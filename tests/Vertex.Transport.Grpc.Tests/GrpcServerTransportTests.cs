// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using System.Net;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Vertex.Transport.Grpc.Protocol.V1;

namespace Vertex.Transport.Grpc.Tests;

/// <summary>
/// 验证 <see cref="GrpcServerTransport"/> + <see cref="BidiServiceImpl"/>。
/// 测试本身扮演 gRPC client；real server 用受测的 transport。
/// </summary>
public class GrpcServerTransportTests
{
    /// <summary>多帧消息：client 一次性写出多帧，server 端 ReceiveAsync 拿到完整一条 TransportMessage。</summary>
    [Fact]
    public async Task ReceiveAsync_AssemblesMultiFrameMessageFromClient()
    {
        await using var fx = await ServerFixture.StartAsync();
        await using var client = new TestBidiClient(fx.Address, peerId: "peer-A");

        await client.SendAsync(new byte[][] { [0x01], [0x02, 0x02], [0x03, 0x03, 0x03] }, endOfMessage: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var msg in fx.Transport.ReceiveAsync(cts.Token))
        {
            Assert.Equal(new PeerId("peer-A"), msg.From);
            Assert.Equal(3, msg.Frames.Count);
            Assert.Equal(new byte[] { 0x01 }, msg.Frames[0].ToArray());
            Assert.Equal(new byte[] { 0x02, 0x02 }, msg.Frames[1].ToArray());
            Assert.Equal(new byte[] { 0x03, 0x03, 0x03 }, msg.Frames[2].ToArray());
            return;
        }
        Assert.Fail("Server transport did not produce a message.");
    }

    /// <summary>SendAsync(peer, ...): server 推一条多帧消息给指定 client。</summary>
    [Fact]
    public async Task SendAsync_DeliversMessageToTargetedPeer()
    {
        await using var fx = await ServerFixture.StartAsync();
        await using var client = new TestBidiClient(fx.Address, peerId: "peer-X");

        await WaitFor(() => fx.ConnectedPeers.Contains(new PeerId("peer-X")));

        await fx.Transport.SendAsync(
            new PeerId("peer-X"),
            [(ReadOnlyMemory<byte>)new byte[] { 0xAA }, new byte[] { 0xBB }]);

        var received = await client.ReceiveOneMessageAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, received.Count);
        Assert.Equal(new byte[] { 0xAA }, received[0]);
        Assert.Equal(new byte[] { 0xBB }, received[1]);
    }

    /// <summary>未注册的 peer：SendAsync 抛 <see cref="TransportSendException"/>。</summary>
    [Fact]
    public async Task SendAsync_UnknownPeer_Throws()
    {
        await using var fx = await ServerFixture.StartAsync();

        await Assert.ThrowsAsync<TransportSendException>(async () =>
            await fx.Transport.SendAsync(new PeerId("nope"), [(ReadOnlyMemory<byte>)new byte[] { 1 }]));
    }

    /// <summary>多 peer 隔离：不同 client 之间不会串扰。</summary>
    [Fact]
    public async Task MultiplePeers_AreIsolated()
    {
        await using var fx = await ServerFixture.StartAsync();
        await using var clientA = new TestBidiClient(fx.Address, peerId: "A");
        await using var clientB = new TestBidiClient(fx.Address, peerId: "B");

        await WaitFor(() =>
            fx.ConnectedPeers.Contains(new PeerId("A")) &&
            fx.ConnectedPeers.Contains(new PeerId("B")));

        await fx.Transport.SendAsync(new PeerId("A"), [(ReadOnlyMemory<byte>)new byte[] { 0xA1 }]);
        await fx.Transport.SendAsync(new PeerId("B"), [(ReadOnlyMemory<byte>)new byte[] { 0xB1 }]);

        var ra = await clientA.ReceiveOneMessageAsync(TimeSpan.FromSeconds(5));
        var rb = await clientB.ReceiveOneMessageAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 0xA1 }, ra[0]);
        Assert.Equal(new byte[] { 0xB1 }, rb[0]);
    }

    /// <summary>
    /// 铁律 #4：客户端断开 → server 触发 Disconnected 事件，且 peer 被移除。
    /// </summary>
    [Fact]
    public async Task PeerConnectionChanged_FiresDisconnected_OnClientDisconnect()
    {
        await using var fx = await ServerFixture.StartAsync();
        var client = new TestBidiClient(fx.Address, peerId: "ephemeral");

        await WaitFor(() => fx.ConnectedPeers.Contains(new PeerId("ephemeral")));
        await client.DisposeAsync();
        await WaitFor(() => !fx.ConnectedPeers.Contains(new PeerId("ephemeral")));

        Assert.Contains(fx.Events, e => e.Peer.Value == "ephemeral" && e.State == PeerConnectionState.Connected);
        Assert.Contains(fx.Events, e => e.Peer.Value == "ephemeral" && e.State == PeerConnectionState.Disconnected);
    }

    private static async Task WaitFor(
        Func<bool> condition,
        double timeoutSec = 15,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail($"WaitFor timed out after {timeoutSec}s waiting for: {expr}");
    }

    /// <summary>启动一个真实 Kestrel + gRPC server，挂上受测 transport。</summary>
    private sealed class ServerFixture : IAsyncDisposable
    {
        public WebApplication App { get; }
        public GrpcServerTransport Transport { get; }
        public Uri Address { get; }
        public List<PeerConnectionEvent> Events { get; } = new();
        public HashSet<PeerId> ConnectedPeers { get; } = new();

        private ServerFixture(WebApplication app, GrpcServerTransport transport, Uri address)
        {
            App = app;
            Transport = transport;
            Address = address;
            transport.PeerConnectionChanged += (_, e) =>
            {
                lock (Events)
                {
                    Events.Add(e);
                    if (e.State == PeerConnectionState.Connected) ConnectedPeers.Add(e.Peer);
                    else ConnectedPeers.Remove(e.Peer);
                }
            };
        }

        public static async Task<ServerFixture> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o =>
            {
                o.Listen(IPAddress.Loopback, 0, lo => lo.Protocols = HttpProtocols.Http2);
            });
            builder.Services.AddGrpc();
            builder.Services.AddGrpcServerTransport("server-test");

            var app = builder.Build();
            app.MapGrpcService<BidiServiceImpl>();

            await app.StartAsync().ConfigureAwait(false);

            var address = new Uri(app.Services
                .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
                .Addresses.First());

            var transport = app.Services.GetRequiredService<GrpcServerTransport>();
            return new ServerFixture(app, transport, address);
        }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync().ConfigureAwait(false);
            await App.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>测试用客户端：直接用生成的 BidiClient 调用 server。</summary>
    private sealed class TestBidiClient : IAsyncDisposable
    {
        private readonly GrpcChannel _channel;
        private readonly AsyncDuplexStreamingCall<TransportFrame, TransportFrame> _call;
        private readonly CancellationTokenSource _cts = new();

        public TestBidiClient(Uri serverAddress, string peerId)
        {
            _channel = GrpcChannel.ForAddress(serverAddress);
            var client = new Bidi.BidiClient(_channel);
            var headers = new Metadata { { "x-skywalker-peer-id", peerId } };
            _call = client.Connect(new CallOptions(headers: headers, cancellationToken: _cts.Token));
        }

        public async Task SendAsync(byte[][] frames, bool endOfMessage)
        {
            for (var i = 0; i < frames.Length; i++)
            {
                await _call.RequestStream.WriteAsync(new TransportFrame
                {
                    Payload = ByteString.CopyFrom(frames[i]),
                    EndOfMessage = endOfMessage && i == frames.Length - 1,
                }).ConfigureAwait(false);
            }
        }

        public async Task<List<byte[]>> ReceiveOneMessageAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            var msg = new List<byte[]>();
            await foreach (var frame in _call.ResponseStream.ReadAllAsync(cts.Token).ConfigureAwait(false))
            {
                msg.Add(frame.Payload.ToByteArray());
                if (frame.EndOfMessage) return msg;
            }
            throw new TimeoutException("No message received within " + timeout);
        }

        public async ValueTask DisposeAsync()
        {
            try { await _call.RequestStream.CompleteAsync().ConfigureAwait(false); } catch { }
            try { _cts.Cancel(); } catch { }
            try { _call.Dispose(); } catch { }
            try { await _channel.ShutdownAsync().ConfigureAwait(false); } catch { }
            _channel.Dispose();
            _cts.Dispose();
        }
    }
}
