// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vertex.Messaging;
using Vertex.Tests.Echo.V1;
using Vertex.Transport.Grpc;
using Vertex.Transport.Grpc.Protocol.V1;

namespace Vertex.Transport.Grpc.Tests;

/// <summary>
/// End-to-end regression test: stacks the full production DI chain
/// <c>AddGrpcServerTransport</c> + <c>AddMessagingChannel</c> + <c>AddRpcHandler</c>
/// on a real Kestrel server, then drives it with a production-path <c>AddGrpcTransport</c>
/// client in the same process.
///
/// This is the test that would have caught the missing-<see cref="Serialization.IMessageSerializer"/>
/// bug in <c>AddGrpcServerTransport</c> — the unit tests didn't, because they built transports
/// directly without going through the DI extensions.
/// </summary>
public class MessagingChannelE2ETests
{
    [Fact]
    public async Task Invoke_EndToEnd_FullDIWiring_RoundTripsProtoResponse()
    {
        // ── server: AddGrpc + AddGrpcServerTransport + AddMessagingChannel + AddRpcHandler
        var serverBuilder = WebApplication.CreateSlimBuilder();
        serverBuilder.Logging.ClearProviders();
        serverBuilder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.IncludeScopes = false; });
        serverBuilder.Logging.SetMinimumLevel(LogLevel.Warning);
        serverBuilder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(IPAddress.Loopback, 0, lo => lo.Protocols = HttpProtocols.Http2);
        });

        serverBuilder.Services.AddGrpc();
        serverBuilder.Services.AddGrpcServerTransport("echo");
        serverBuilder.Services.AddMessagingChannel("echo",
            reg => reg.RegisterRequest<EchoRequest, EchoResponse>());
        serverBuilder.Services.AddRpcHandler<EchoRequest, EchoResponse, EchoHandler>("echo");

        var server = serverBuilder.Build();
        server.MapGrpcService<BidiServiceImpl>();

        await server.StartAsync();
        try
        {
            var serverAddress = new Uri(server.Services
                .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
                .Addresses.First());

            // ── client: AddGrpcTransport + AddMessagingChannel (no handlers — caller-only).
            var clientServices = new ServiceCollection();
            clientServices.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
            clientServices.AddGrpcTransport("echo", o =>
            {
                o.ServerAddress = serverAddress;
                o.ConnectTimeout = TimeSpan.FromSeconds(5);
                o.Reconnect = ReconnectPolicy.Disabled;
            });
            clientServices.AddMessagingChannel("echo",
                reg => reg.RegisterRequest<EchoRequest, EchoResponse>());

            await using var clientSp = clientServices.BuildServiceProvider();

            // Run the hosted-service warm-up that the real composition root does.
            foreach (var h in clientSp.GetServices<IHostedService>())
            {
                await h.StartAsync(CancellationToken.None);
            }

            var channel = clientSp.GetRequiredKeyedService<IRpcClient>("echo");

            var response = await channel.InvokeAsync<EchoRequest, EchoResponse>(
                new EchoRequest { Text = "hello" },
                timeout: TimeSpan.FromSeconds(10));

            Assert.Equal("echo: hello", response.Text);
        }
        finally
        {
            await server.StopAsync();
            await server.DisposeAsync();
        }
    }

    /// <summary>
    /// Regression guard for the IMessageSerializer-not-registered bug.
    /// <see cref="GrpcTransportServiceCollectionExtensions.AddGrpcTransport"/>
    /// and <see cref="GrpcServerTransportServiceCollectionExtensions.AddGrpcServerTransport"/>
    /// must each register a keyed <see cref="Serialization.IMessageSerializer"/> so
    /// <see cref="MessagingServiceCollectionExtensions.AddMessagingChannel"/>'s factory
    /// can resolve one. Regressing this drops the failure onto whoever first
    /// resolves the channel — typically at request time, deep in CI.
    /// </summary>
    [Fact]
    public void AddGrpcServerTransport_RegistersKeyedIMessageSerializer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGrpcServerTransport("srv");

        using var sp = services.BuildServiceProvider();
        var serializer = sp.GetKeyedService<Serialization.IMessageSerializer>("srv");
        Assert.NotNull(serializer);
        Assert.IsType<Serialization.Protobuf.ProtobufMessageSerializer>(serializer);
    }

    [Fact]
    public void AddGrpcTransport_RegistersKeyedIMessageSerializer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGrpcTransport("cli", o => o.ServerAddress = new Uri("http://127.0.0.1:1"));

        using var sp = services.BuildServiceProvider();
        var serializer = sp.GetKeyedService<Serialization.IMessageSerializer>("cli");
        Assert.NotNull(serializer);
        Assert.IsType<Serialization.Protobuf.ProtobufMessageSerializer>(serializer);
    }

    public class EchoHandler : IRpcHandler<EchoRequest, EchoResponse>
    {
        public ValueTask<EchoResponse> HandleAsync(RpcContext<EchoRequest> ctx, CancellationToken ct)
            => ValueTask.FromResult(new EchoResponse { Text = $"echo: {ctx.Request.Text}" });
    }
}
