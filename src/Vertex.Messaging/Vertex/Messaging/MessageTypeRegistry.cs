// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using Vertex.Transport;

namespace Vertex.Messaging;

/// <summary>
/// Per-channel type registry: declares which Event / Request types the channel carries.
/// Deserialization itself is delegated to an <c>IMessageSerializer</c> bound to the channel via DI —
/// this registry only remembers <c>topic → Type</c> mappings and does not know any specific serializer.
/// </summary>
public sealed class MessageTypeRegistry
{
    private readonly Dictionary<string, Type> _events = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RequestDescriptor> _requests = new(StringComparer.Ordinal);

    /// <summary>
    /// Declare an event type. Both publisher and subscriber must register it.
    /// </summary>
    public MessageTypeRegistry RegisterEvent<T>()
        where T : notnull
    {
        _events[MessageTopic.For<T>().Value] = typeof(T);
        return this;
    }

    /// <summary>
    /// Declare an RPC request/response pair. Types only; handlers are bound separately via DI.
    /// </summary>
    public MessageTypeRegistry RegisterRequest<TRequest, TResponse>()
        where TRequest : notnull
        where TResponse : notnull
    {
        _requests[MessageTopic.For<TRequest>().Value] = new RequestDescriptor(typeof(TRequest), typeof(TResponse));
        return this;
    }

    internal bool TryGetEvent(string topic, out Type? type)
        => _events.TryGetValue(topic, out type);

    internal bool TryGetRequest(string topic, out RequestDescriptor descriptor)
        => _requests.TryGetValue(topic, out descriptor);

    internal readonly record struct RequestDescriptor(Type RequestType, Type ResponseType);
}

/// <summary>
/// Server-side RPC handler registration used by the DI layer to route <c>topic → handler</c>.
/// </summary>
internal readonly record struct RpcHandlerRegistration(
    string ChannelName,
    string Topic,
    Type RequestType,
    Type ResponseType,
    Func<IServiceProvider, PeerId, object, CancellationToken, ValueTask<object>> Invoke);
