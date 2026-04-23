// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

namespace Vertex.Messaging;

/// <summary>
/// Messaging 层在 transport 帧之上叠加的消息类型。
/// </summary>
public enum MessageKind : byte
{
    /// <summary>单向广播消息（IMessageBus 发布/订阅）。</summary>
    Event = 0,

    /// <summary>RPC 请求（等待对应 <see cref="Response"/>）。</summary>
    Request = 1,

    /// <summary>RPC 响应（与某个 <see cref="Request"/> 通过 RequestId 配对）。</summary>
    Response = 2,
}
