// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

namespace Vertex.Transport;

/// <summary>
/// 表示 connect 类 transport（Dealer / Sub）支持运行时动态增删对端端点。
/// 实现必须线程安全；通常把操作投递到 transport 内部的事件循环执行。
/// </summary>
public interface IConnectableTransport
{
    /// <summary>
    /// 连接到一个新端点。重复地址会被忽略。
    /// </summary>
    void Connect(string endpoint);

    /// <summary>
    /// 断开指定端点。未连接的地址会被忽略。
    /// </summary>
    void Disconnect(string endpoint);
}
