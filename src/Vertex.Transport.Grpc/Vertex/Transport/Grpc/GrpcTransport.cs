// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using System.Threading.Channels;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vertex.Transport.Grpc.Protocol.V1;

namespace Vertex.Transport.Grpc;

/// <summary>
/// 基于 gRPC bidi stream 的 <see cref="ITransport"/> 实现（client side）。
///
/// 设计要点（必须满足 <c>docs/modules/transport.md</c> 中描述的「Transport 实现者四条铁律」）：
/// <list type="number">
///   <item>读循环只把帧写入 inbound channel；不阻塞、不调用业务 handler（铁律 #1）。</item>
///   <item><see cref="SendAsync"/> 失败只抛 <see cref="TransportSendException"/>，
///         不主动触发 <see cref="PeerConnectionChanged"/>（铁律 #2）。</item>
///   <item><see cref="SendAsync"/> 的 <c>cancellationToken</c> 仅用于 pre-wire（等连接、等写锁），
///         一旦开始 <c>RequestStream.WriteAsync</c> 即不再传 CT 给底层（铁律 #3）。</item>
///   <item>唯一的「连接挂掉」判定 = 读循环 <c>ResponseStream</c> 抛异常 / 自然结束（铁律 #4）。</item>
/// </list>
///
/// 对端模型：本 transport 视服务端 <see cref="GrpcTransportOptions.ServerAddress"/> 为唯一对端，
/// <see cref="PeerId"/> = ServerAddress 字符串。Connected/Disconnected 事件在
/// 读循环开始/结束（含每一次重连周期）时触发。
/// </summary>
public sealed class GrpcTransport : ITransport
{
    private readonly GrpcTransportOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Channel<TransportMessage> _inChannel = Channel.CreateUnbounded<TransportMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly TaskCompletionSource<bool> _firstConnectTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly PeerId _serverPeer;
    private readonly Task _runLoop;

    private GrpcChannel? _channel;
    private AsyncDuplexStreamingCall<TransportFrame, TransportFrame>? _call;
    private int _disposed;

    /// <summary>
    /// 创建一个 gRPC transport。构造完成后立刻在后台启动连接 + 读循环。
    /// </summary>
    public GrpcTransport(string name, GrpcTransportOptions options, ILogger<GrpcTransport>? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(options);
        if (options.ServerAddress is null)
        {
            throw new ArgumentException($"{nameof(GrpcTransportOptions.ServerAddress)} is required.", nameof(options));
        }

        Name = name;
        _options = options;
        _logger = (ILogger?)logger ?? NullLogger<GrpcTransport>.Instance;
        _serverPeer = new PeerId(options.ServerAddress.ToString());
        _runLoop = Task.Run(RunAsync);
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
            return ValueTask.FromException(new ObjectDisposedException(nameof(GrpcTransport)));
        }
        if (frames is null || frames.Count == 0)
        {
            return ValueTask.FromException(new ArgumentException("frames must not be empty", nameof(frames)));
        }

        return SendCoreAsync(frames, cancellationToken);
    }

    private async ValueTask SendCoreAsync(IReadOnlyList<ReadOnlyMemory<byte>> frames, CancellationToken ct)
    {
        // ===== Pre-wire #1：等待首次连接成功（铁律 #3 允许 CT 在此阶段生效）=====
        if (!_firstConnectTcs.Task.IsCompletedSuccessfully)
        {
            await _firstConnectTcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        // ===== Pre-wire #2：争抢写锁（铁律 #3 允许 CT 在此阶段生效）=====
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var call = _call;
            if (call is null)
            {
                throw new TransportSendException("gRPC transport is not connected.");
            }

            // ===== 已开写：CT 不再生效（铁律 #3）=====
            // 整组帧必须原子写出，最后一帧 EndOfMessage=true。
            // ⚠️ 即使调用方取消 ct，下面的 WriteAsync 也不会传 ct——
            // 否则会触发 HTTP/2 RST_STREAM，把整条 bidi stream 上其他 in-flight 请求一起拖死。
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = new TransportFrame
                {
                    Payload = ByteString.CopyFrom(frames[i].Span),
                    EndOfMessage = (i == frames.Count - 1),
                };
                try
                {
                    await call.RequestStream.WriteAsync(frame).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // 铁律 #2：单次写失败只通知调用方，不主动断连。
                    throw new TransportSendException("Failed to write frame to gRPC stream.", ex);
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<TransportMessage> ReceiveAsync(CancellationToken cancellationToken = default)
        => _inChannel.Reader.ReadAllAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try { _lifetimeCts.Cancel(); } catch { /* ignore */ }

        // 重要：必须先把当前的 streaming call dispose 掉，才能保证 ReadLoopAsync
        // 里的 `await foreach` 立刻抛 OperationCanceledException 退出；
        // 仅 cancel _lifetimeCts 不一定能撕开 HTTP/2 stream（取决于网络 IO 状态）。
        await CleanupCallAsync().ConfigureAwait(false);

        try { await _runLoop.ConfigureAwait(false); } catch { /* run loop swallows, but be safe */ }

        // 阻塞中的 SendAsync 必须能从 _firstConnectTcs 中醒来。
        _firstConnectTcs.TrySetException(new ObjectDisposedException(nameof(GrpcTransport)));

        _writeLock.Dispose();
        _lifetimeCts.Dispose();
    }

    /// <summary>
    /// 主循环：连接 → 读循环 → 断开 → 退避 → 重连。是本 transport 中**唯一**
    /// 的「连接挂掉」判定源（铁律 #4）。
    /// </summary>
    private async Task RunAsync()
    {
        var attempt = 0;
        while (!_lifetimeCts.IsCancellationRequested)
        {
            try
            {
                await ConnectOnceAsync().ConfigureAwait(false);
                attempt = 0; // 成功重置退避
                _firstConnectTcs.TrySetResult(true);
                RaisePeerEvent(PeerConnectionState.Connected);

                await ReadLoopAsync(_call!).ConfigureAwait(false);
                // 服务端正常 close stream：当作一次断开，进入重连退避。
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "gRPC transport '{Name}' connection error.", Name);
            }

            await CleanupCallAsync().ConfigureAwait(false);

            // 仅在「曾经成功连接过」之后才发 Disconnected；否则只在退避结束后下一轮再 Connected。
            if (_firstConnectTcs.Task.IsCompletedSuccessfully)
            {
                RaisePeerEvent(PeerConnectionState.Disconnected);
            }

            if (!_options.Reconnect.Enabled)
            {
                break;
            }

            attempt++;
            try
            {
                await Task.Delay(ComputeBackoff(attempt), _lifetimeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _inChannel.Writer.TryComplete();
    }

    private async Task ConnectOnceAsync()
    {
        var channelOptions = new GrpcChannelOptions();
        _options.ConfigureChannel?.Invoke(channelOptions);

        _channel = GrpcChannel.ForAddress(_options.ServerAddress!, channelOptions);
        var client = new Bidi.BidiClient(_channel);
        if (!string.IsNullOrEmpty(_options.Authority))
        {
            client = client.WithHost(_options.Authority);
        }

        var headers = new Metadata();
        foreach (var kv in _options.Metadata)
        {
            headers.Add(kv.Key, kv.Value);
        }

        var callOptions = new CallOptions(headers: headers, cancellationToken: _lifetimeCts.Token);

        _call = client.Connect(callOptions);

        // 注意：不能用 _call.ResponseHeadersAsync 作为「已连接」的判断信号——
        // 在我们的协议里，服务端只在业务代码主动写响应时才会发送 response headers，
        // 一个空闲的 bidi stream 客户端会因此永远拿不到 headers。
        // gRPC 自身只在「第一次写」时才真正建立 HTTP/2 流，因此 client.Connect 返回即视为「已连接」，
        // 真实的网络错误会在后续 RequestStream.WriteAsync / ResponseStream.MoveNext 时抛出。
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(AsyncDuplexStreamingCall<TransportFrame, TransportFrame> call)
    {
        var buffer = new List<ReadOnlyMemory<byte>>();
        await foreach (var frame in call.ResponseStream.ReadAllAsync(_lifetimeCts.Token).ConfigureAwait(false))
        {
            buffer.Add(frame.Payload.ToByteArray());
            if (frame.EndOfMessage)
            {
                _inChannel.Writer.TryWrite(new TransportMessage(_serverPeer, buffer.ToArray()));
                buffer.Clear();
            }
        }
        // 流自然结束：相当于服务端关流；上层把它视为一次断开。
    }

    private async Task CleanupCallAsync()
    {
        var call = Interlocked.Exchange(ref _call, null);
        if (call is not null)
        {
            // 注意：不 await RequestStream.CompleteAsync()——若 stream 已被对端撕掉，
            // 该调用会无限阻塞。直接 Dispose() 会立即取消底层 HTTP/2 stream。
            try { call.Dispose(); } catch { /* ignore */ }
        }

        var channel = Interlocked.Exchange(ref _channel, null);
        if (channel is not null)
        {
            try { await channel.ShutdownAsync().ConfigureAwait(false); } catch { /* ignore */ }
            channel.Dispose();
        }
    }

    private TimeSpan ComputeBackoff(int attempt)
    {
        var p = _options.Reconnect;
        var baseMs = p.InitialBackoff.TotalMilliseconds * Math.Pow(p.Multiplier, attempt - 1);
        baseMs = Math.Min(p.MaxBackoff.TotalMilliseconds, baseMs);
        var jitterRange = baseMs * Math.Clamp(p.Jitter, 0, 1);
        var jitter = (Random.Shared.NextDouble() * 2 - 1) * jitterRange;
        return TimeSpan.FromMilliseconds(Math.Max(0, baseMs + jitter));
    }

    private void RaisePeerEvent(PeerConnectionState state)
    {
        try
        {
            PeerConnectionChanged?.Invoke(this, new PeerConnectionEvent(_serverPeer, state));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PeerConnectionChanged handler threw for {Peer}", _serverPeer);
        }
    }
}
