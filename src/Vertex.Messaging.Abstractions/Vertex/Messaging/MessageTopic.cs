// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Reflection;

namespace Vertex.Messaging;

/// <summary>
/// On-wire identifier for a business message type. Both sender and receiver route by this string.
/// <para>
/// Naming rules:
/// <list type="bullet">
///   <item>
///     If <typeparamref name="T"/> is a Protobuf-generated message (duck-typed: has a public static
///     <c>Descriptor</c> property whose value exposes a string <c>FullName</c>), the topic is that
///     <c>FullName</c> — e.g. <c>feivoo.gaming.v1.CreateRoom</c>. This matches exactly what Go / any
///     other language's generated code reports, so cross-language peers align topics automatically.
///   </item>
///   <item>
///     Otherwise the topic is <see cref="Type.FullName"/> (CLR-qualified name). Cross-language interop
///     then requires manual coordination of the topic string.
///   </item>
/// </list>
/// </para>
/// </summary>
public readonly record struct MessageTopic(string Value)
{
    private static readonly ConcurrentDictionary<Type, string> Cache = new();

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    public override string ToString() => Value;

    public static MessageTopic For<T>() => For(typeof(T));

    public static MessageTopic For(Type type)
    {
        var value = Cache.GetOrAdd(type, static t =>
            TryGetProtobufFullName(t, out var protoName) && !string.IsNullOrEmpty(protoName)
                ? protoName!
                : (t.FullName ?? t.Name));
        return new MessageTopic(value);
    }

    // Duck-typed Protobuf detection: avoids a hard dependency on Google.Protobuf in
    // Vertex.Messaging.Abstractions (which would pull protobuf into every consumer).
    // Every protoc-generated C# class has a public static "Descriptor" whose type has a "FullName" property.
    private static bool TryGetProtobufFullName(Type type, out string? fullName)
    {
        fullName = null;
        var descriptorProp = type.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static);
        var descriptor = descriptorProp?.GetValue(null);
        if (descriptor is null) return false;

        var fullNameProp = descriptor.GetType().GetProperty("FullName", BindingFlags.Public | BindingFlags.Instance);
        if (fullNameProp?.GetValue(descriptor) is string name && !string.IsNullOrEmpty(name))
        {
            fullName = name;
            return true;
        }
        return false;
    }
}
