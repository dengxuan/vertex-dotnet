// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using MsgPack = MessagePack.MessagePackSerializer;

namespace Vertex.Serialization.MessagePack;

/// <summary>
/// <see cref="IMessageSerializer"/> backed by MessagePack-CSharp.
/// <para>
/// Thin wrapper around <see cref="MsgPack"/>; the library handles <c>[MessagePackObject]</c> annotations,
/// <c>[Union]</c> polymorphism, and resolver configuration. Use <c>MessagePackSerializer.DefaultOptions</c>
/// to tune globally if needed — this class does not own configuration.
/// </para>
/// </summary>
public sealed class MessagePackMessageSerializer : IMessageSerializer
{
    /// <summary>Shared singleton instance. Safe to reuse: no mutable state.</summary>
    public static readonly MessagePackMessageSerializer Instance = new();

    /// <inheritdoc/>
    public byte[] Serialize(Type type, object value) => MsgPack.Serialize(type, value);

    /// <inheritdoc/>
    public object Deserialize(Type type, ReadOnlyMemory<byte> payload)
        => MsgPack.Deserialize(type, payload)
           ?? throw new InvalidOperationException(
               $"MessagePack returned null when deserializing '{type.FullName}'. Nullable payloads are not allowed at this layer.");
}
