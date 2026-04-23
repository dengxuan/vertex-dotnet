// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

namespace Vertex.Transport;

/// <summary>
/// 对端标识。
/// 对 Router 而言，是它看见的连接进来的客户端 identity。
/// 对 Dealer / Pub / Sub 而言，可能为空（点对点单端、广播无对端概念）。
/// </summary>
public readonly record struct PeerId(string Value)
{
    public static readonly PeerId Empty = new(string.Empty);

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    public override string ToString() => Value;
}
