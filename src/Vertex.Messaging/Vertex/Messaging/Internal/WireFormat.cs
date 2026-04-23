// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Text;

namespace Vertex.Messaging.Internal;

/// <summary>
/// Messaging 层在 transport 之上的统一帧格式。固定 4 帧：
/// <list type="bullet">
///   <item>[0] Topic (UTF8) — 同时充当 PUB 主题前缀。</item>
///   <item>[1] Kind (1 byte) — <see cref="MessageKind"/>。</item>
///   <item>[2] RequestId (UTF8) — Event 时为空字符串。</item>
///   <item>[3] Payload — Event/Request 为 MessagePack 字节；
///         Response 成功时为 MessagePack 字节，失败时（kind 仍为 Response，由 status 区分）为 UTF8 错误文本。</item>
/// </list>
/// 错误响应：复用 Response kind，但在 Topic 前缀加 "!"，简单且无需新增帧。
/// 接收方先剥离 "!" 决定是抛 <see cref="RpcRemoteException"/> 还是反序列化。
/// </summary>
internal static class WireFormat
{
    internal const string ErrorTopicPrefix = "!";
    internal const int FrameCount = 4;

    internal const int FrameTopic = 0;
    internal const int FrameKind = 1;
    internal const int FrameRequestId = 2;
    internal const int FramePayload = 3;

    internal static ReadOnlyMemory<byte> EncodeKind(MessageKind kind) => new[] { (byte)kind };

    internal static MessageKind DecodeKind(ReadOnlyMemory<byte> frame)
    {
        if (frame.Length != 1)
        {
            throw new InvalidOperationException($"Kind frame must be 1 byte, got {frame.Length}.");
        }
        return (MessageKind)frame.Span[0];
    }

    internal static ReadOnlyMemory<byte> EncodeString(string value) =>
        string.IsNullOrEmpty(value) ? ReadOnlyMemory<byte>.Empty : Encoding.UTF8.GetBytes(value);

    internal static string DecodeString(ReadOnlyMemory<byte> frame) =>
        frame.IsEmpty ? string.Empty : Encoding.UTF8.GetString(frame.Span);
}
