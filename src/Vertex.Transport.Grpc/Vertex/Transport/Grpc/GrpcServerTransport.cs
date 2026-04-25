// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Threading.Channels;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vertex.Transport.Grpc.Protocol.V1;

namespace Vertex.Transport.Grpc;

/// <summary>
/// 服务端 <see cref="ITransport"/> 实现。等价于 NetMQ Router：
/// 一个 transport 同时承接多个客户端 bidi stream，每个 stream 是一个 <see cref="PeerId"/>。
///
/// 工作模型：
/// <list type="bullet">
///   <item>由 ASP.NET Core gRPC pipeline（用户用 <c>MapGrpcService&lt;BidiServiceImpl&gt;()</c> 注册）
///         把每条 <c>Bidi.Connect</c> RPC 转发给 <see cref="HandleConnectAsync"/>。</item>
///   <item><see cref="HandleConnectAsync"/> 负责：注册 <see cref="PeerSession"/> → 触发 Connected →
///         跑读循环 → 退出时触发 Disconnected → 反注册。</item>
///   <item><see cref="SendAsync"/> 通过 <see cref="PeerId"/> 在 session 字典里查找目标 peer，
///         在该 peer 的写锁内写出多帧。</item>
/// </list>
///
/// 满足 4 条 transport 铁律（详见 <c>docs/modules/transport.md</c>）：
/// 见每个相关方法上的中文注释。
/// </summary>
public sealed class GrpcServerTransport : ITransport
{
    private readonly GrpcServerTransportOptions _options;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<PeerId, PeerSession> _peers = new();
    private readonly Channel<TransportMessage> _inChannel = Channel.CreateUnbounded<TransportMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private int _disposed;

    public GrpcServerTransport(string name, GrpcServerTransportOptions options, ILogger<GrpcServerTransport>? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(options);
        Name = name;
        _options = options;
        _logger = (ILogger?)logger ?? NullLogger<GrpcServerTransport>.Instance;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public event EventHandler<PeerConnectionEvent>? PeerConnectionChanged;

    /// <inheritdoc />
    public ValueTask SendAsync(PeerId target, IReadOnlyList<ReadOnlyMemory<byte>> frames, CancellationToken cancellationToken = default)
    {
        if (_disposed != 0)
        {
            return ValueTask.FromException(new ObjectDisposedException(nameof(GrpcServerTransport)));
        }
        if (target.IsEmpty)
        {
            return ValueTask.FromException(new ArgumentException("Server transport requires explicit target PeerId.", nameof(target)));
        }
        if (frames is null || frames.Count == 0)
        {
            return ValueTask.FromException(new ArgumentException("frames must not be empty", nameof(frames)));
        }

        return SendCoreAsync(target, frames, cancellationToken);
    }

    private async ValueTask SendCoreAsync(PeerId target, IReadOnlyList<ReadOnlyMemory<byte>> frames, CancellationToken ct)
    {
        if (!_peers.TryGetValue(target, out var session))
        {
            if (_options.ThrowOnUnknownPeer)
            {
                // 铁律 #2：抛 TransportSendException，不发 Disconnected（peer 早就 Disconnected 过了）。
                throw new TransportSendException($"Peer '{target}' is not connected.");
            }
            return;
        }

        // ===== Pre-wire：抢 peer 的写锁（铁律 #3 允许 CT 在此阶段生效）=====
        await session.WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = new TransportFrame
                {
                    Payload = ByteString.CopyFrom(frames[i].Span),
                    EndOfMessage = (i == frames.Count - 1),
                };
                try
                {
                    // 铁律 #3：开始写之后不再传 ct，避免 RST_STREAM 撕掉同一连接上其他写。
                    await session.Response.WriteAsync(frame).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // 铁律 #2：单次写失败只通知调用方，不主动 Disconnected。
                    // peer 真正断开会由该 peer 的 read loop 在 MoveNext 抛错时统一发出。
                    throw new TransportSendException($"Failed to write frame to peer '{target}'.", ex);
                }
            }
        }
        finally
        {
            session.WriteLock.Release();
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<TransportMessage> ReceiveAsync(CancellationToken cancellationToken = default)
        => _inChannel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>
    /// 由 <see cref="BidiServiceImpl"/> 调用：处理一个进来的 bidi stream，从开始到结束。
    /// 这是该 peer 的「连接生命周期」，也是该 peer 的「断开判定」唯一来源（铁律 #4）。
    /// </summary>
    internal async Task HandleConnectAsync(IAsyncStreamReader<TransportFrame> requestStream, IServerStreamWriter<TransportFrame> responseStream, ServerCallContext context)
    {
        if (_disposed != 0)
        {
            return;
        }

        var peerId = ResolvePeerId(context);

        // ===== 认证（spec/peer-authentication.md）=====
        // 先于 peers 表注册 / Connected 事件 —— Reject 的 stream 永远进不到
        // bus 视图，业务侧看不到这种 peer。
        object? peerState = null;
        if (_options.Authenticator is { } authenticator)
        {
            PeerAuthenticationResult result;
            try
            {
                var authCtx = new PeerAuthenticationContext(peerId, ExtractClientMetadata(context));
                result = await authenticator(authCtx, context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PeerAuthenticator threw for {Peer}; treating as Reject.", peerId);
                throw new RpcException(new Status(StatusCode.Unauthenticated, $"authenticator threw: {ex.Message}"));
            }
            if (!result.Accepted)
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated,
                    string.IsNullOrEmpty(result.RejectReason) ? "rejected" : result.RejectReason));
            }
            peerState = result.PeerState;
        }

        var session = new PeerSession(responseStream);

        if (!_peers.TryAdd(peerId, session))
        {
            // 同名 peer 重连：覆盖（旧的早已断了或正在断），并踢掉旧 session 的写锁。
            _peers[peerId] = session;
            _logger.LogInformation("Peer '{Peer}' reconnected; replacing previous session.", peerId);
        }

        RaisePeerEvent(peerId, PeerConnectionState.Connected);

        try
        {
            // ===== 读循环：唯一的「连接还活着」判定（铁律 #1 + #4）=====
            var buffer = new List<ReadOnlyMemory<byte>>();
            while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
            {
                var frame = requestStream.Current;
                buffer.Add(frame.Payload.ToByteArray());
                if (frame.EndOfMessage)
                {
                    // 铁律 #1：只入队，不走业务 handler。
                    _inChannel.Writer.TryWrite(
                        new TransportMessage(peerId, buffer.ToArray()) { PeerState = peerState });
                    buffer = new List<ReadOnlyMemory<byte>>();
                }
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // client cancelled / server shutting down — 视为正常断开
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Read loop for peer '{Peer}' faulted.", peerId);
        }
        finally
        {
            _peers.TryRemove(new KeyValuePair<PeerId, PeerSession>(peerId, session));
            session.Dispose();
            RaisePeerEvent(peerId, PeerConnectionState.Disconnected);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _inChannel.Writer.TryComplete();
        // 注意：不在此 dispose 各 peer 的 SemaphoreSlim——它们的 lifetime
        // 与 ServerCallContext 一致，gRPC pipeline 关停时会取消相应的 cancellation token，
        // 让 read loop 退出，由 finally 块统一清理。
        return ValueTask.CompletedTask;
    }

    private PeerId ResolvePeerId(ServerCallContext context)
    {
        var entry = context.RequestHeaders?.FirstOrDefault(e =>
            string.Equals(e.Key, _options.PeerIdMetadataKey, StringComparison.OrdinalIgnoreCase));
        if (entry is { Value: { Length: > 0 } v })
        {
            return new PeerId(v);
        }
        // 兜底：用 gRPC 给的 peer 字符串（含 host:port）+ 调用唯一 id
        return new PeerId($"{context.Peer}#{Guid.NewGuid():N}");
    }

    /// <summary>
    /// 把 gRPC client metadata 拷成 lowercase string map（spec §4 命名约定）。
    /// 二进制 header（"-bin" 后缀）跳过 —— authenticator 看的是文本凭证。
    /// </summary>
    private static IReadOnlyDictionary<string, string> ExtractClientMetadata(ServerCallContext context)
    {
        var headers = context.RequestHeaders;
        if (headers is null || headers.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        var dict = new Dictionary<string, string>(headers.Count, StringComparer.Ordinal);
        foreach (var entry in headers)
        {
            if (entry.IsBinary) continue;
            // gRPC 标准化保证 header key 已是 lowercase；显式再 ToLowerInvariant 防御。
            dict[entry.Key.ToLowerInvariant()] = entry.Value;
        }
        return dict;
    }

    private void RaisePeerEvent(PeerId peer, PeerConnectionState state)
    {
        try
        {
            PeerConnectionChanged?.Invoke(this, new PeerConnectionEvent(peer, state));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PeerConnectionChanged handler threw for {Peer}", peer);
        }
    }

    private sealed class PeerSession : IDisposable
    {
        public IServerStreamWriter<TransportFrame> Response { get; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);

        public PeerSession(IServerStreamWriter<TransportFrame> response) => Response = response;

        public void Dispose() => WriteLock.Dispose();
    }
}
