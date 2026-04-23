// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

namespace Vertex.Transport;

/// <summary>
/// Transport 层接收到的一帧消息（多帧组合）。
/// </summary>
/// <param name="From">消息来自哪个对端。Pub/Sub 模型下为 <see cref="PeerId.Empty"/>。</param>
/// <param name="Frames">原始字节帧（已剥离 ZMQ identity 帧）。</param>
public readonly record struct TransportMessage(PeerId From, IReadOnlyList<ReadOnlyMemory<byte>> Frames);
