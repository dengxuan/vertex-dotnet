// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

namespace Vertex.Transport.NetMq;

internal sealed class BindEndpointInfo : IBindEndpointInfo
{
    private readonly NetMqBindOptions _options;

    public BindEndpointInfo(string transportName, NetMqBindOptions options)
    {
        TransportName = transportName;
        _options = options;
    }

    public string TransportName { get; }

    public int Port => _options.ActualPort;
}
