// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Vertex.Transport.Grpc.Protocol.V1;

namespace Vertex.Transport.Grpc.Tests;

/// <summary>
/// 验证 <see cref="GrpcTransport"/> 满足 4 条铁律及基础语义。
/// 「服务端 close stream → reconnect」端到端验证更适合放在 integration test。
/// </summary>
public class GrpcTransportTests
{
    private static GrpcTransport CreateTransport(Uri serverAddress, Action<GrpcTransportOptions>? configure = null)
    {
        var options = new GrpcTransportOptions
        {
            ServerAddress = serverAddress,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            Reconnect = ReconnectPolicy.Disabled,
        };
        configure?.Invoke(options);
        return new GrpcTransport("test", options, NullLogger<GrpcTransport>.Instance);
    }

    private static IReadOnlyList<ReadOnlyMemory<byte>> Frames(params byte[][] payloads)
        => payloads.Select(p => (ReadOnlyMemory<byte>)p).ToArray();

    /// <summary>多帧消息：3 帧按序到达服务端，且服务端识别出「一条 3 帧消息」。</summary>
    [Fact]
    public async Task SendAsync_MultiFrame_ArrivesAsSingleMessage()
    {
        await using var server = await TestServerFixture.StartAsync();
        await using var transport = CreateTransport(server.Address);

        await transport.SendAsync(PeerId.Empty, Frames([1], [2, 2], [3, 3, 3]));

        await WaitFor(() => !server.Behavior.ReceivedMessages.IsEmpty);

        Assert.Single(server.Behavior.ReceivedMessages);
        var msg = server.Behavior.ReceivedMessages.First();
        Assert.Equal(3, msg.Count);
        Assert.Equal(new byte[] { 1 }, msg[0]);
        Assert.Equal(new byte[] { 2, 2 }, msg[1]);
        Assert.Equal(new byte[] { 3, 3, 3 }, msg[2]);
    }

    /// <summary>铁律 #3：写入开始后取消 CT，写入仍必须完成、stream 不断。</summary>
    [Fact]
    public async Task SendAsync_CancelAfterWriteStarted_DoesNotTearDownStream()
    {
        await using var server = await TestServerFixture.StartAsync();
        await using var transport = CreateTransport(server.Address);

        await transport.SendAsync(PeerId.Empty, Frames([0]));
        await WaitFor(() => server.Behavior.ReceivedMessages.Count >= 1);

        using var cts = new CancellationTokenSource();
        var sendTask = transport.SendAsync(PeerId.Empty, Frames(new byte[10], new byte[10], new byte[10]), cts.Token);
        cts.Cancel();
        await sendTask;

        await transport.SendAsync(PeerId.Empty, Frames([42]));

        await WaitFor(() => server.Behavior.ReceivedMessages.Count >= 3);
        Assert.True(server.Behavior.ReceivedMessages.Count >= 3,
            $"Expected at least 3 messages, got {server.Behavior.ReceivedMessages.Count}");
    }

    /// <summary>铁律 #4：PeerConnectionChanged 在首次连接成功后触发 Connected。</summary>
    [Fact]
    public async Task PeerConnectionChanged_FiresConnectedOnFirstConnect()
    {
        await using var server = await TestServerFixture.StartAsync();
        var events = new List<PeerConnectionEvent>();
        await using var transport = CreateTransport(server.Address);
        transport.PeerConnectionChanged += (_, e) =>
        {
            lock (events) events.Add(e);
        };

        await transport.SendAsync(PeerId.Empty, Frames([1]));
        await WaitFor(() => { lock (events) return events.Count >= 1; });

        lock (events)
        {
            Assert.NotEmpty(events);
            Assert.Equal(PeerConnectionState.Connected, events[0].State);
            Assert.Equal(server.Address.ToString(), events[0].Peer.Value);
        }
    }

    /// <summary>服务端推一帧给 client，client 必须能在 ReceiveAsync 上读到。</summary>
    [Fact]
    public async Task ReceiveAsync_DeliversServerPushedMessage()
    {
        await using var server = await TestServerFixture.StartAsync(b =>
        {
            b.OnConnect = async response =>
            {
                await response.WriteAsync(new TransportFrame { Payload = ByteString.CopyFrom([0x10]), EndOfMessage = false });
                await response.WriteAsync(new TransportFrame { Payload = ByteString.CopyFrom([0x20]), EndOfMessage = true });
            };
        });

        await using var transport = CreateTransport(server.Address);
        await transport.SendAsync(PeerId.Empty, Frames([0]));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var msg in transport.ReceiveAsync(cts.Token))
        {
            Assert.Equal(2, msg.Frames.Count);
            Assert.Equal(new byte[] { 0x10 }, msg.Frames[0].ToArray());
            Assert.Equal(new byte[] { 0x20 }, msg.Frames[1].ToArray());
            return;
        }
        Assert.Fail("Did not receive any message from the server.");
    }

    /// <summary>Disposed 后再发送应抛 <see cref="ObjectDisposedException"/>。</summary>
    [Fact]
    public async Task SendAsync_OnDisposedTransport_ThrowsObjectDisposedException()
    {
        await using var server = await TestServerFixture.StartAsync();
        var transport = CreateTransport(server.Address);
        await transport.SendAsync(PeerId.Empty, Frames([1]));

        await transport.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await transport.SendAsync(PeerId.Empty, Frames([2])));
    }

    private static async Task WaitFor(Func<bool> condition, double timeoutSec = 10)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
    }
}
