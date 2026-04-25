// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

namespace Vertex.Transport.Grpc;

/// <summary>
/// <see cref="GrpcServerTransport"/> 的配置选项。
/// </summary>
public sealed class GrpcServerTransportOptions
{
    /// <summary>
    /// 服务端从客户端 metadata 中读取 PeerId 的 header 名。
    /// 如果客户端未提供，则使用 gRPC 的连接级唯一 ID（<c>connection-id:call-id</c>）兜底。
    /// 默认 <c>x-vertex-peer-id</c>。
    /// </summary>
    public string PeerIdMetadataKey { get; set; } = "x-vertex-peer-id";

    /// <summary>
    /// 当向某 peer 发送消息时，若对应 stream 已断开是否抛 <see cref="TransportSendException"/>。
    /// 默认 <c>true</c>。设为 <c>false</c> 则静默丢弃（用于 fire-and-forget 广播场景）。
    /// </summary>
    public bool ThrowOnUnknownPeer { get; set; } = true;

    /// <summary>
    /// 可选：连接级认证回调（spec/peer-authentication.md）。设了 → 每个新 peer 在 read loop
    /// 启动前会被调一次。返回 <c>Reject(reason)</c> = 该 stream 直接以
    /// <see cref="Grpc.Core.StatusCode.Unauthenticated"/> 关掉，永远不入 inbox / 不发 Connected。
    /// 返回 <c>Accept(state)</c> = state 跟着每条 <see cref="TransportMessage.PeerState"/> 走。
    /// 不设 → 等价于无脑接受，PeerState = <c>null</c>（向后兼容）。
    /// </summary>
    public PeerAuthenticator? Authenticator { get; set; }
}
