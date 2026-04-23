// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using Vertex.Transport;

namespace Vertex.Messaging;

/// <summary>
/// 单个 messaging channel 的类型注册表：声明该 channel 上跑哪些 Event / Request 类型，
/// 以及如何反序列化 + 调用 handler。客户端只需要"会发什么"，服务端还需要"怎么处理"。
/// </summary>
public sealed class MessageTypeRegistry
{
    private readonly Dictionary<string, EventDescriptor> _events = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RequestDescriptor> _requests = new(StringComparer.Ordinal);

    /// <summary>
    /// 声明一个事件类型。订阅 / 发布双方都需要注册。
    /// </summary>
    public MessageTypeRegistry RegisterEvent<T>()
        where T : notnull
    {
        var topic = MessageTopic.For<T>().Value;
        _events[topic] = new EventDescriptor(
            typeof(T),
            payload => MessagePack.MessagePackSerializer.Deserialize<T>(payload));
        return this;
    }

    /// <summary>
    /// 声明一个 RPC 请求/响应对类型（仅声明，不绑定 handler；handler 通过 DI 在 RpcServer 侧注入）。
    /// </summary>
    public MessageTypeRegistry RegisterRequest<TRequest, TResponse>()
        where TRequest : notnull
        where TResponse : notnull
    {
        var topic = MessageTopic.For<TRequest>().Value;
        _requests[topic] = new RequestDescriptor(
            typeof(TRequest),
            typeof(TResponse),
            payload => MessagePack.MessagePackSerializer.Deserialize<TRequest>(payload),
            obj => MessagePack.MessagePackSerializer.Deserialize<TResponse>((byte[])obj));
        return this;
    }

    internal bool TryGetEvent(string topic, out EventDescriptor descriptor)
        => _events.TryGetValue(topic, out descriptor!);

    internal bool TryGetRequest(string topic, out RequestDescriptor descriptor)
        => _requests.TryGetValue(topic, out descriptor!);

    internal readonly record struct EventDescriptor(
        Type PayloadType,
        Func<byte[], object> Deserialize);

    internal readonly record struct RequestDescriptor(
        Type RequestType,
        Type ResponseType,
        Func<byte[], object> DeserializeRequest,
        Func<object, object> DeserializeResponse);
}

/// <summary>
/// 服务端 RPC handler 的注册项，宿主用它知道某 topic 应该走哪个 handler。
/// </summary>
internal readonly record struct RpcHandlerRegistration(
    string ChannelName,
    string Topic,
    Type RequestType,
    Type ResponseType,
    Func<IServiceProvider, PeerId, object, CancellationToken, ValueTask<object>> Invoke);
