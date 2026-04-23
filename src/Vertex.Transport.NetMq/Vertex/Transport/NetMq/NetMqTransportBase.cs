// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NetMQ;
using NetMQ.Monitoring;

namespace Vertex.Transport.NetMq;

/// <summary>
/// NetMQ transport 实现的公共线程模型。
///
/// 线程职责：
/// <list type="bullet">
///   <item>所有 socket 操作只在 <see cref="NetMQPoller"/> 线程上执行（NetMQ socket 非线程安全）。</item>
///   <item><see cref="SendAsync"/> 仅把出站消息投递到 <see cref="NetMQQueue{T}"/>，由 poller 调度真实发送。</item>
///   <item>入站消息从 socket 读出后写入 <see cref="System.Threading.Channels.Channel{T}"/>，业务侧通过
///         <see cref="ReceiveAsync"/> 用 <c>await foreach</c> 消费。</item>
/// </list>
/// </summary>
internal abstract class NetMqTransportBase : ITransport
{
    private readonly NetMQSocket _socket;
    private readonly NetMQPoller _poller;
    private readonly NetMQQueue<OutboundEnvelope> _outQueue = new();
    private readonly NetMQQueue<Action<NetMQSocket>> _controlQueue = new();
    private readonly Channel<TransportMessage> _inChannel = Channel.CreateUnbounded<TransportMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly NetMQMonitor? _monitor;
    private readonly ILogger _logger;
    private int _disposed;

    protected NetMqTransportBase(string name, NetMQSocket socket, ILogger logger, bool enableMonitor)
    {
        Name = name;
        _socket = socket;
        _logger = logger;

        _poller = new NetMQPoller { _socket, _outQueue, _controlQueue };
        _socket.ReceiveReady += OnSocketReceiveReady;
        _outQueue.ReceiveReady += OnOutQueueReceiveReady;
        _controlQueue.ReceiveReady += OnControlQueueReceiveReady;

        if (enableMonitor)
        {
            var endpoint = $"inproc://monitor.{Guid.NewGuid():N}";
            _monitor = new NetMQMonitor(_socket, endpoint, SocketEvents.Accepted | SocketEvents.Disconnected | SocketEvents.Connected);
            _monitor.Accepted += (_, e) => RaisePeerEvent(e, PeerConnectionState.Connected);
            _monitor.Connected += (_, e) => RaisePeerEvent(e, PeerConnectionState.Connected);
            _monitor.Disconnected += (_, e) => RaisePeerEvent(e, PeerConnectionState.Disconnected);
            _monitor.AttachToPoller(_poller);
        }

        _poller.RunAsync();
    }

    public string Name { get; }

    public event EventHandler<PeerConnectionEvent>? PeerConnectionChanged;

    public ValueTask SendAsync(PeerId target, IReadOnlyList<ReadOnlyMemory<byte>> frames, CancellationToken cancellationToken = default)
    {
        if (_disposed != 0)
        {
            return ValueTask.FromException(new ObjectDisposedException(GetType().Name));
        }

        if (frames is null || frames.Count == 0)
        {
            return ValueTask.FromException(new ArgumentException("frames must not be empty", nameof(frames)));
        }

        // 复制为独立 byte[]：调用方的 ReadOnlyMemory 可能引用池化 buffer，到 poller 线程消费时已被回收。
        var copies = new byte[frames.Count][];
        for (var i = 0; i < frames.Count; i++)
        {
            copies[i] = frames[i].ToArray();
        }

        // 满足 ITransport.SendAsync 契约（铁律 #3）：入队后即不再响应 CT，
        // 真正的 socket 发送在 poller 线程上由 OnOutQueueReceiveReady 完成，
        // 不会因调用方取消 CT 而把已入队的多帧消息撕成两半。
        _outQueue.Enqueue(new OutboundEnvelope(target, copies));
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<TransportMessage> ReceiveAsync(CancellationToken cancellationToken = default)
        => _inChannel.Reader.ReadAllAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _inChannel.Writer.TryComplete();
            if (_poller.IsRunning)
            {
                await Task.Run(() => _poller.Stop()).ConfigureAwait(false);
            }
            _monitor?.Stop();
            _monitor?.Dispose();
            _outQueue.Dispose();
            _controlQueue.Dispose();
            _socket.Dispose();
            _poller.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while disposing transport {Name}", Name);
        }
    }

    /// <summary>
    /// 入站可读：派生类负责从 socket 读完整一条多帧消息，并解析出 (PeerId, frames)。
    /// </summary>
    protected abstract void OnSocketReceiveReady(object? sender, NetMQSocketEventArgs e);

    /// <summary>
    /// 出队一批消息：派生类把 <see cref="OutboundEnvelope"/> 转成 NetMQMessage，
    /// 然后由基类用 socket 发送。仅在 poller 线程调用。
    /// </summary>
    private void OnOutQueueReceiveReady(object? sender, NetMQQueueEventArgs<OutboundEnvelope> e)
    {
        while (e.Queue.TryDequeue(out var env, TimeSpan.Zero))
        {
            try
            {
                var msg = BuildOutbound(env);
                if (msg is not null)
                {
                    _socket.SendMultipartMessage(msg);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send outbound message on transport {Name}", Name);
            }
        }
    }

    /// <summary>
    /// 派生类根据 socket 类型把出站 envelope 转换为 NetMQMessage。返回 null 表示该消息丢弃。
    /// </summary>
    protected abstract NetMQMessage? BuildOutbound(in OutboundEnvelope envelope);

    /// <summary>
    /// 把一个 socket 操作投递到 poller 线程执行。用于 Connect/Disconnect/Subscribe 等
    /// 不能跨线程的 socket API。
    /// </summary>
    protected internal void Schedule(Action<NetMQSocket> action)
    {
        if (_disposed != 0)
        {
            return;
        }
        _controlQueue.Enqueue(action);
    }

    private void OnControlQueueReceiveReady(object? sender, NetMQQueueEventArgs<Action<NetMQSocket>> e)
    {
        while (e.Queue.TryDequeue(out var action, TimeSpan.Zero))
        {
            if (action is null)
            {
                continue;
            }
            try
            {
                action(_socket);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Control action failed on transport {Name}", Name);
            }
        }
    }

    /// <summary>
    /// 帮助派生类把一条解析好的多帧消息推到入站 channel。
    /// </summary>
    protected void EnqueueIncoming(PeerId from, NetMQMessage message)
    {
        var frames = new ReadOnlyMemory<byte>[message.FrameCount];
        for (var i = 0; i < message.FrameCount; i++)
        {
            frames[i] = message[i].ToByteArray();
        }
        _inChannel.Writer.TryWrite(new TransportMessage(from, frames));
    }

    private void RaisePeerEvent(NetMQMonitorSocketEventArgs e, PeerConnectionState state)
    {
        var peer = new PeerId(e.Address ?? string.Empty);
        try
        {
            PeerConnectionChanged?.Invoke(this, new PeerConnectionEvent(peer, state));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PeerConnectionChanged handler threw for {Peer}", peer);
        }
    }
}

/// <summary>
/// 内部出站消息容器。
/// </summary>
internal readonly record struct OutboundEnvelope(PeerId Target, byte[][] Frames);
