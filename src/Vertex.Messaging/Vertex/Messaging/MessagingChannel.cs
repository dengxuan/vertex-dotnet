// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Vertex.Messaging.Internal;
using Vertex.Serialization;
using Vertex.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Vertex.Messaging;

/// <summary>
/// 包装一个命名 <see cref="ITransport"/>，同时实现 <see cref="IMessageBus"/> + <see cref="IRpcClient"/>
/// 以及服务端 RPC 分发。一个进程可以有多个命名 channel（典型：channel-bus + provider-bus）。
///
/// 线程模型：
/// <list type="bullet">
///   <item>构造时启动一个后台 receive loop（独立 Task）从 transport 拉消息。</item>
///   <item>分发到订阅者 / pending RPC / RPC handler，每个都用 Task.Run 投递，避免阻塞 receive loop。</item>
///   <item>发送通过 transport.SendAsync — transport 内部已做线程切换。</item>
/// </list>
/// </summary>
internal sealed class MessagingChannel : IMessageBus, IRpcClient, IAsyncDisposable
{
    private readonly string _name;
    private readonly ITransport _transport;
    private readonly IMessageSerializer _serializer;
    private readonly MessageTypeRegistry _typeRegistry;
    private readonly IReadOnlyDictionary<string, RpcHandlerRegistration> _handlers;
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Type, List<Subscription>> _subscriptions = new();

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _receiveLoop;
    private readonly TimeSpan _defaultTimeout;
    private readonly TimeSpan _peerDisconnectGracePeriod;
    private int _disposed;

    public MessagingChannel(
        string name,
        ITransport transport,
        IMessageSerializer serializer,
        MessageTypeRegistry typeRegistry,
        IReadOnlyDictionary<string, RpcHandlerRegistration> handlers,
        IServiceProvider services,
        ILogger<MessagingChannel> logger,
        TimeSpan? defaultTimeout = null,
        TimeSpan? peerDisconnectGracePeriod = null)
    {
        _name = name;
        _transport = transport;
        _serializer = serializer;
        _typeRegistry = typeRegistry;
        _handlers = handlers;
        _services = services;
        _logger = logger;
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(30);
        // Bounds the window between "transport fires Disconnected" and
        // "pending invokes fail". See OnPeerConnectionChanged for why.
        _peerDisconnectGracePeriod = peerDisconnectGracePeriod ?? TimeSpan.FromMilliseconds(200);

        _transport.PeerConnectionChanged += OnPeerConnectionChanged;
        _receiveLoop = Task.Run(() => RunReceiveLoopAsync(_cts.Token));
        _logger.LogInformation(
            "Messaging channel '{Name}' started on transport '{Transport}' with serializer '{Serializer}'.",
            _name, _transport.Name, _serializer.GetType().Name);
    }

    public ValueTask PublishAsync<T>(T @event, PeerId target = default, CancellationToken cancellationToken = default)
        where T : notnull
    {
        var topic = MessageTopic.For<T>().Value;
        var payload = _serializer.Serialize(typeof(T), @event);
        var frames = BuildFrames(topic, MessageKind.Event, requestId: string.Empty, payload);
        return _transport.SendAsync(target, frames, cancellationToken);
    }

    public IDisposable Subscribe<T>(Func<EventContext<T>, CancellationToken, ValueTask> handler)
        where T : notnull
    {
        var list = _subscriptions.GetOrAdd(typeof(T), _ => new List<Subscription>());
        var sub = new Subscription(typeof(T), (ctx, ct) => handler(new EventContext<T>(ctx.From, (T)ctx.Payload), ct));
        lock (list)
        {
            list.Add(sub);
        }
        return new SubscriptionToken(this, typeof(T), sub);
    }

    public async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
        TRequest request,
        PeerId target = default,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        where TRequest : notnull
        where TResponse : notnull
    {
        var topic = MessageTopic.For<TRequest>().Value;
        var requestId = Guid.NewGuid().ToString("N");
        var payload = _serializer.Serialize(typeof(TRequest), request);
        var frames = BuildFrames(topic, MessageKind.Request, requestId, payload);

        var pending = new PendingRequest();
        _pending[requestId] = pending;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout ?? _defaultTimeout);

        try
        {
            await _transport.SendAsync(target, frames, cancellationToken).ConfigureAwait(false);
            byte[] resBytes;
            try
            {
                resBytes = await pending.Tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new RpcTimeoutException($"RPC '{topic}' timed out after {(timeout ?? _defaultTimeout).TotalSeconds}s.");
            }

            if (pending.IsError)
            {
                throw new RpcRemoteException(System.Text.Encoding.UTF8.GetString(resBytes));
            }
            return (TResponse)_serializer.Deserialize(typeof(TResponse), resBytes);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private static IReadOnlyList<ReadOnlyMemory<byte>> BuildFrames(string topic, MessageKind kind, string requestId, byte[] payload)
        => new ReadOnlyMemory<byte>[]
        {
            WireFormat.EncodeString(topic),
            WireFormat.EncodeKind(kind),
            WireFormat.EncodeString(requestId),
            payload,
        };

    private async Task RunReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var msg in _transport.ReceiveAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    DispatchOneMessage(msg);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Dispatch failed on channel '{Name}'.", _name);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Receive loop crashed on channel '{Name}'.", _name);
        }
    }

    private void DispatchOneMessage(TransportMessage msg)
    {
        if (msg.Frames.Count < WireFormat.FrameCount)
        {
            _logger.LogWarning("Dropping malformed message on '{Name}' (frames={Count}).", _name, msg.Frames.Count);
            return;
        }

        var topic = WireFormat.DecodeString(msg.Frames[WireFormat.FrameTopic]);
        var kind = WireFormat.DecodeKind(msg.Frames[WireFormat.FrameKind]);
        var requestId = WireFormat.DecodeString(msg.Frames[WireFormat.FrameRequestId]);
        var payload = msg.Frames[WireFormat.FramePayload];

        switch (kind)
        {
            case MessageKind.Event:
                DispatchEvent(msg.From, topic, payload);
                break;
            case MessageKind.Request:
                _ = DispatchRequestAsync(msg.From, topic, requestId, payload);
                break;
            case MessageKind.Response:
                DispatchResponse(topic, requestId, payload);
                break;
            default:
                _logger.LogWarning("Unknown MessageKind {Kind} on channel '{Name}'.", kind, _name);
                break;
        }
    }

    private void DispatchEvent(PeerId from, string topic, ReadOnlyMemory<byte> payload)
    {
        if (!_typeRegistry.TryGetEvent(topic, out var payloadType) || payloadType is null)
        {
            return; // unregistered event — subscribers don't care
        }

        var obj = _serializer.Deserialize(payloadType, payload);
        if (!_subscriptions.TryGetValue(payloadType, out var subs))
        {
            return;
        }

        Subscription[] snapshot;
        lock (subs)
        {
            snapshot = subs.ToArray();
        }
        foreach (var sub in snapshot)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await sub.Invoke(new EventContext<object>(from, obj), _cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Event subscriber threw for {Topic} on '{Name}'.", topic, _name);
                }
            });
        }
    }

    private async Task DispatchRequestAsync(PeerId from, string topic, string requestId, ReadOnlyMemory<byte> payload)
    {
        if (!_handlers.TryGetValue(topic, out var registration))
        {
            await SendErrorAsync(from, topic, requestId, $"No RPC handler registered for '{topic}' on channel '{_name}'.").ConfigureAwait(false);
            return;
        }

        try
        {
            if (!_typeRegistry.TryGetRequest(topic, out var descriptor))
            {
                await SendErrorAsync(from, topic, requestId, $"Request type '{topic}' not registered on channel '{_name}'.").ConfigureAwait(false);
                return;
            }
            var requestObj = _serializer.Deserialize(descriptor.RequestType, payload);
            using var scope = (_services as IServiceProvider)?.CreateScope();
            var sp = scope?.ServiceProvider ?? _services;
            var responseObj = await registration.Invoke(sp, from, requestObj, _cts.Token).ConfigureAwait(false);
            var resBytes = _serializer.Serialize(registration.ResponseType, responseObj);
            var frames = BuildFrames(topic, MessageKind.Response, requestId, resBytes);
            await _transport.SendAsync(from, frames, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RPC handler failed for {Topic} on '{Name}'.", topic, _name);
            await SendErrorAsync(from, topic, requestId, ex.Message).ConfigureAwait(false);
        }
    }

    private ValueTask SendErrorAsync(PeerId target, string topic, string requestId, string message)
    {
        var errorBytes = System.Text.Encoding.UTF8.GetBytes(message);
        // 用 "!" 前缀的 topic 表示错误响应
        var frames = BuildFrames(WireFormat.ErrorTopicPrefix + topic, MessageKind.Response, requestId, errorBytes);
        return _transport.SendAsync(target, frames, _cts.Token);
    }

    private void DispatchResponse(string topic, string requestId, ReadOnlyMemory<byte> payload)
    {
        if (!_pending.TryRemove(requestId, out var pending))
        {
            _logger.LogDebug("Response with unknown requestId {ReqId} on '{Name}'.", requestId, _name);
            return;
        }
        pending.IsError = topic.StartsWith(WireFormat.ErrorTopicPrefix, StringComparison.Ordinal);
        pending.Tcs.TrySetResult(payload.ToArray());
    }

    private void OnPeerConnectionChanged(object? sender, PeerConnectionEvent e)
    {
        if (e.State != PeerConnectionState.Disconnected)
        {
            return;
        }

        // The transport's read loop (e.g. GrpcServerTransport.HandleConnectAsync)
        // enqueues inbound messages onto a shared inbound channel, then raises
        // this event from its finally block. Our receive loop (on a separate
        // thread) may not yet have drained the last message that arrived just
        // before disconnect — a response for a still-pending Invoke could be
        // sitting in that queue right now.
        //
        // If we fail pending invokes synchronously here, that racing response
        // will be consumed by the receive loop *after* the TCS has been
        // exceptioned, and the caller sees RpcPeerDisconnectedException even
        // though the server-side actually responded successfully.
        //
        // Defer the failure by a short grace period. Any Invoke whose response
        // arrives during the window completes via TrySetResult and the
        // subsequent TrySetException below is a no-op.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_peerDisconnectGracePeriod).ConfigureAwait(false);
                foreach (var (_, pending) in _pending)
                {
                    pending.Tcs.TrySetException(
                        new RpcPeerDisconnectedException($"Peer {e.Peer} disconnected before response."));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while failing pending invokes after peer '{Peer}' disconnect.", e.Peer);
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _transport.PeerConnectionChanged -= OnPeerConnectionChanged;
        _cts.Cancel();
        try
        {
            await _receiveLoop.ConfigureAwait(false);
        }
        catch
        {
            // ignored
        }
        _cts.Dispose();
    }

    internal void RemoveSubscription(Type type, Subscription sub)
    {
        if (_subscriptions.TryGetValue(type, out var list))
        {
            lock (list)
            {
                list.Remove(sub);
            }
        }
    }

    internal sealed class Subscription
    {
        public Type Type { get; }
        public Func<EventContext<object>, CancellationToken, ValueTask> Invoke { get; }

        public Subscription(Type type, Func<EventContext<object>, CancellationToken, ValueTask> invoke)
        {
            Type = type;
            Invoke = invoke;
        }
    }

    private sealed class SubscriptionToken : IDisposable
    {
        private readonly MessagingChannel _channel;
        private readonly Type _type;
        private readonly Subscription _sub;
        private int _disposed;

        public SubscriptionToken(MessagingChannel channel, Type type, Subscription sub)
        {
            _channel = channel;
            _type = type;
            _sub = sub;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            _channel.RemoveSubscription(_type, _sub);
        }
    }

    private sealed class PendingRequest
    {
        public TaskCompletionSource<byte[]> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsError { get; set; }
    }
}
