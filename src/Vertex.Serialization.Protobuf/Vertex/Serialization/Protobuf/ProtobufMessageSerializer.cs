// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Reflection;
using Google.Protobuf;

namespace Vertex.Serialization.Protobuf;

/// <summary>
/// <see cref="IMessageSerializer"/> backed by Google.Protobuf.
/// <para>
/// Every message type is expected to be generated from a <c>.proto</c> file, which makes it implement
/// <see cref="IMessage"/> and expose a <c>public static Parser</c> property. Deserialization caches the
/// <see cref="MessageParser"/> per <see cref="Type"/> via a <see cref="ConcurrentDictionary{TKey,TValue}"/>;
/// the per-call reflection cost is amortized after the first message of each type.
/// </para>
/// </summary>
public sealed class ProtobufMessageSerializer : IMessageSerializer
{
    /// <summary>Shared singleton instance. Safe to reuse: cache is thread-safe.</summary>
    public static readonly ProtobufMessageSerializer Instance = new();

    private static readonly ConcurrentDictionary<Type, MessageParser> ParserCache = new();

    /// <inheritdoc/>
    public byte[] Serialize(Type type, object value)
    {
        if (value is not IMessage message)
        {
            throw new InvalidOperationException(
                $"Type '{type.FullName}' is not a Protobuf-generated message (does not implement Google.Protobuf.IMessage). " +
                $"Vertex.Transport.Grpc requires Protobuf payloads — define '{type.Name}' in a .proto file.");
        }
        return message.ToByteArray();
    }

    /// <inheritdoc/>
    public object Deserialize(Type type, ReadOnlyMemory<byte> payload)
    {
        var parser = ParserCache.GetOrAdd(type, static t =>
        {
            var prop = t.GetProperty("Parser", BindingFlags.Public | BindingFlags.Static);
            if (prop?.GetValue(null) is not MessageParser p)
            {
                throw new InvalidOperationException(
                    $"Type '{t.FullName}' is not a Protobuf-generated message (no public static Parser property). " +
                    $"Generated Protobuf classes expose 'public static MessageParser<T> Parser'.");
            }
            return p;
        });

        return parser.ParseFrom(payload.Span);
    }
}
