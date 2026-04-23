// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

namespace Vertex.Serialization;

/// <summary>
/// Converts application messages to/from bytes at the Vertex messaging layer.
/// <para>
/// An <see cref="IMessageSerializer"/> is bound to a named messaging channel through DI;
/// every peer talking on the same channel MUST use the same serializer — mixing serializers
/// on one channel is undefined behavior (the receiver will not be able to decode the other's bytes).
/// </para>
/// <para>
/// Transports pick their default serializer by convention:
/// <list type="bullet">
///   <item><c>Vertex.Transport.Grpc</c> forces <c>Vertex.Serialization.Protobuf</c> (gRPC's whole point is Protobuf).</item>
///   <item><c>Vertex.Transport.NetMq</c> defaults to <c>Vertex.Serialization.MessagePack</c>; any <see cref="IMessageSerializer"/> can be plugged in via options.</item>
/// </list>
/// </para>
/// </summary>
public interface IMessageSerializer
{
    /// <summary>
    /// Serialize a runtime <paramref name="value"/> of the given <paramref name="type"/> to a fresh byte array.
    /// Implementations MAY throw if <paramref name="value"/> is not compatible with the serializer's expected shape
    /// (e.g. Protobuf requires <c>Google.Protobuf.IMessage</c>).
    /// </summary>
    byte[] Serialize(Type type, object value);

    /// <summary>
    /// Deserialize <paramref name="payload"/> to an instance of <paramref name="type"/>.
    /// </summary>
    object Deserialize(Type type, ReadOnlyMemory<byte> payload);
}
