// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

namespace Vertex.Transport;

/// <summary>
/// 命名的 transport 注册表。一个进程可以有多个独立 transport（例如 Channel 同时
/// 跑 "provider-bus" 的 Sub 和 "channel-bus" 的 Router）。
/// </summary>
public interface ITransportRegistry
{
    ITransport Get(string name);

    bool TryGet(string name, out ITransport transport);
}
