// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using Vertex.Transport;

namespace Vertex.Messaging;

/// <summary>
/// RPC 客户端：向对端发起请求并等待响应。基于 transport 之上，
/// 通过 RequestId 多路复用，在同一 transport 上并发多个请求/响应。
/// </summary>
public interface IRpcClient
{
    /// <summary>
    /// 发起 RPC 请求并等待响应。
    /// </summary>
    /// <typeparam name="TRequest">请求类型。Topic 取 <c>typeof(TRequest).Name</c>。</typeparam>
    /// <typeparam name="TResponse">响应类型。</typeparam>
    /// <param name="request">请求对象。</param>
    /// <param name="target">目标对端；空表示让 transport 决定（Dealer 模式下无需指定）。</param>
    /// <param name="timeout">本次请求的超时时间。null 表示使用全局默认。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
        TRequest request,
        PeerId target = default,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        where TRequest : notnull
        where TResponse : notnull;
}
