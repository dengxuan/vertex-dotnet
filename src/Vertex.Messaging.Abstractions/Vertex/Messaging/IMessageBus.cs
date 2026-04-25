// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using Vertex.Transport;

namespace Vertex.Messaging;

/// <summary>
/// 单向事件总线（基于 transport 之上）。一条 transport 可承载多种事件类型，按 <see cref="MessageTopic"/> 区分。
/// 实现负责序列化、加上 Envelope、写到底层 transport，以及反向解码后分发到订阅者。
/// </summary>
public interface IMessageBus
{
    /// <summary>
    /// 发布事件到 transport。
    /// </summary>
    /// <param name="event">业务事件对象。Topic 默认取 <c>typeof(T).Name</c>。</param>
    /// <param name="target">目标对端；为空时走广播 / Dealer 默认路由。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask PublishAsync<T>(T @event, PeerId target = default, CancellationToken cancellationToken = default)
        where T : notnull;

    /// <summary>
    /// 订阅指定类型的事件。返回值用于取消订阅。
    /// </summary>
    /// <param name="handler">事件处理回调，包含来源对端。</param>
    IDisposable Subscribe<T>(Func<EventContext<T>, CancellationToken, ValueTask> handler)
        where T : notnull;
}

/// <summary>
/// 事件上下文，包含来源对端信息 + opaque <see cref="PeerState"/>。
/// PeerState 来自 transport 的 <see cref="PeerAuthenticator"/> Accept；没配 authenticator
/// / 客户端入站事件时为 <c>null</c>。用 <see cref="PeerStateAs{TState}"/> 强转。
/// </summary>
public readonly record struct EventContext<T>(PeerId From, T Payload)
{
    public object? PeerState { get; init; }

    /// <summary>
    /// 把 opaque <see cref="PeerState"/> 强转成业务定义的会话类型。失败返回 <c>null</c>。
    /// </summary>
    public TState? PeerStateAs<TState>() where TState : class => PeerState as TState;
}
