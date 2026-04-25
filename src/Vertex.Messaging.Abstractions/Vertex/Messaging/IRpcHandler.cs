// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using Vertex.Transport;

namespace Vertex.Messaging;

/// <summary>
/// RPC 服务端 handler：处理某种请求，返回对应响应。
/// 通过 DI 注册，宿主在收到匹配 topic 的请求时调用。
/// </summary>
public interface IRpcHandler<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : notnull
{
    ValueTask<TResponse> HandleAsync(RpcContext<TRequest> context, CancellationToken cancellationToken);
}

/// <summary>
/// RPC 上下文，包含来源对端信息 + opaque <see cref="PeerState"/>。
/// PeerState 来自 transport 的 <see cref="PeerAuthenticator"/> Accept；没配 authenticator
/// / 客户端入站请求时为 <c>null</c>。用 <see cref="PeerStateAs{TState}"/> 强转。
/// </summary>
public readonly record struct RpcContext<T>(PeerId From, T Request)
{
    public object? PeerState { get; init; }

    /// <summary>
    /// 把 opaque <see cref="PeerState"/> 强转成业务定义的会话类型。失败返回 <c>null</c>。
    /// </summary>
    public TState? PeerStateAs<TState>() where TState : class => PeerState as TState;
}
