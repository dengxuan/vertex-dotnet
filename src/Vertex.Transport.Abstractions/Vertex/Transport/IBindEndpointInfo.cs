// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

namespace Vertex.Transport;

/// <summary>
/// 暴露 bind 类 transport（Router / Pub）实际绑定的本地端口，给 mDNS 公告等需要端口的组件使用。
/// 通过命名 keyed singleton 注册：<c>[FromKeyedServices("name")] IBindEndpointInfo</c>。
/// </summary>
public interface IBindEndpointInfo
{
    string TransportName { get; }
    int Port { get; }
}
