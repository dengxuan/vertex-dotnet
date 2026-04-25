// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

namespace Vertex.Transport;

/// <summary>
/// Transport 层接收到的一帧消息（多帧组合）。
/// </summary>
/// <param name="From">消息来自哪个对端。Pub/Sub 模型下为 <see cref="PeerId.Empty"/>。</param>
/// <param name="Frames">原始字节帧（已剥离 ZMQ identity 帧）。</param>
public readonly record struct TransportMessage(PeerId From, IReadOnlyList<ReadOnlyMemory<byte>> Frames)
{
    /// <summary>
    /// Server-side transport 在 <see cref="PeerAuthenticator"/> Accept 时绑定，跟着该
    /// peer 的每一条消息走。Pub/Sub / 客户端 transport / 没配 authenticator 时为
    /// <c>null</c>。MessagingChannel 把它转手放进 <c>RpcContext.PeerState</c> /
    /// <c>EventContext.PeerState</c>。详见 spec/peer-authentication.md §5。
    /// </summary>
    public object? PeerState { get; init; }
}
