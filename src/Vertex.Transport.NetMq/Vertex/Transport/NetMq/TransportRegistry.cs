// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using System.Collections.Concurrent;

namespace Vertex.Transport.NetMq;

/// <summary>
/// 默认 <see cref="ITransportRegistry"/> 实现：DI 注册的所有 transport 按 Name 索引。
/// </summary>
internal sealed class TransportRegistry : ITransportRegistry
{
    private readonly ConcurrentDictionary<string, ITransport> _transports;

    public TransportRegistry(IEnumerable<ITransport> transports)
    {
        _transports = new ConcurrentDictionary<string, ITransport>(StringComparer.Ordinal);
        foreach (var t in transports)
        {
            if (!_transports.TryAdd(t.Name, t))
            {
                throw new InvalidOperationException($"Duplicate transport name registered: {t.Name}");
            }
        }
    }

    public ITransport Get(string name) =>
        _transports.TryGetValue(name, out var t)
            ? t
            : throw new KeyNotFoundException($"Transport '{name}' not registered.");

    public bool TryGet(string name, out ITransport transport)
    {
        if (_transports.TryGetValue(name, out var t))
        {
            transport = t;
            return true;
        }
        transport = null!;
        return false;
    }
}
