// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

namespace Vertex.Messaging;

/// <summary>
/// 业务消息类型在 wire 上的标识符。约定使用 CLR 短类型名（不含命名空间）。
/// 同一条 transport 上承载多种业务消息时，订阅方按 topic 路由到对应 handler。
/// </summary>
public readonly record struct MessageTopic(string Value)
{
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    public override string ToString() => Value;

    public static MessageTopic For<T>() => new(typeof(T).Name);

    public static MessageTopic For(Type type) => new(type.Name);
}
