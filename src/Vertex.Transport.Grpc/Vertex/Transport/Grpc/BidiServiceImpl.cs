// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using Grpc.Core;
using Vertex.Transport.Grpc.Protocol.V1;

namespace Vertex.Transport.Grpc;

/// <summary>
/// 用户用 <c>app.MapGrpcService&lt;BidiServiceImpl&gt;()</c> 把本类挂到 gRPC pipeline。
/// 每条 <c>Bidi.Connect</c> 调用都被 forward 给 <see cref="GrpcServerTransport.HandleConnectAsync"/>。
///
/// 一个进程内可以有多个 <see cref="GrpcServerTransport"/>（按 name 区分），但每个 BidiService 实现
/// 只对应一个 transport。如需多个 transport 端点，在不同端口/路由上注册多个派生类。
/// </summary>
public class BidiServiceImpl : Bidi.BidiBase
{
    private readonly GrpcServerTransport _transport;

    public BidiServiceImpl(GrpcServerTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
    }

    public override Task Connect(IAsyncStreamReader<TransportFrame> requestStream, IServerStreamWriter<TransportFrame> responseStream, ServerCallContext context)
        => _transport.HandleConnectAsync(requestStream, responseStream, context);
}
