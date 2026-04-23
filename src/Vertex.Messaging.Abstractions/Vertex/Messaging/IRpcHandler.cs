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
/// RPC 上下文，包含来源对端信息。
/// </summary>
public readonly record struct RpcContext<T>(PeerId From, T Request);
