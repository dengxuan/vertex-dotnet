// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

namespace Vertex.Transport;

/// <summary>
/// 对端连接状态变化事件。由 <see cref="ITransport.PeerConnectionChanged"/> 触发。
/// 上层（例如 RPC 层）可借此在对端断线时立刻 fail-fast 所有 pending 请求，
/// 而不必等到协议层超时。
/// </summary>
/// <remarks>
/// 实现说明：底层 socket（如 NetMQ Router/Dealer）感知断线的方式可能是 socket monitor、
/// 应用层心跳或对端首次发送。具体语义由实现文档约定。
/// </remarks>
public readonly record struct PeerConnectionEvent(PeerId Peer, PeerConnectionState State);

public enum PeerConnectionState
{
    Connected,
    Disconnected
}
