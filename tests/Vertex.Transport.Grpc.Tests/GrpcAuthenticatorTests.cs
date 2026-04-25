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
using Microsoft.Extensions.Logging;
using Vertex.Transport.Grpc.Protocol.V1;

namespace Vertex.Transport.Grpc.Tests;

/// <summary>
/// 验证 <see cref="GrpcServerTransport"/> 的 <see cref="GrpcServerTransportOptions.Authenticator"/>
/// 行为，对齐 spec/peer-authentication.md 与 vertex-cpp 的同名测试。
/// </summary>
public class GrpcAuthenticatorTests
{
    private sealed record TenantState(string TenantId, string Token);

    [Fact]
    public async Task NoAuthenticator_YieldsNullPeerState()
    {
        await using var fx = await ServerFixture.StartAsync();
        await using var client = new TestBidiClient(fx.Address, peerId: "peer-X");

        await client.SendAsync(new byte[][] { [0x01] }, endOfMessage: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var msg in fx.Transport.ReceiveAsync(cts.Token))
        {
            Assert.Null(msg.PeerState);
            return;
        }
        Assert.Fail("No message received.");
    }

    [Fact]
    public async Task Accept_PropagatesPeerStateToInboundMessage()
    {
        var state = new TenantState("tenant-42", "secret");
        await using var fx = await ServerFixture.StartAsync(opts =>
        {
            opts.Authenticator = (_, _) => ValueTask.FromResult(PeerAuthenticationResult.Accept(state));
        });
        await using var client = new TestBidiClient(fx.Address, peerId: "peer-X");

        await client.SendAsync(new byte[][] { [0x01] }, endOfMessage: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var msg in fx.Transport.ReceiveAsync(cts.Token))
        {
            var recovered = Assert.IsType<TenantState>(msg.PeerState);
            Assert.Equal("tenant-42", recovered.TenantId);
            Assert.Equal("secret",    recovered.Token);
            return;
        }
        Assert.Fail("No message received.");
    }

    [Fact]
    public async Task Authenticator_SeesLowercaseMetadataKeys()
    {
        IReadOnlyDictionary<string, string>? captured = null;
        await using var fx = await ServerFixture.StartAsync(opts =>
        {
            opts.Authenticator = (ctx, _) =>
            {
                captured = ctx.Metadata;
                return ValueTask.FromResult(PeerAuthenticationResult.Accept());
            };
        });

        await using var client = new TestBidiClient(
            fx.Address,
            peerId: "peer-X",
            extraHeaders: new Metadata
            {
                { "authorization", "Bearer abc" },
                { "x-tenant-id",   "T1" },
            });
        await client.SendAsync(new byte[][] { [0x01] }, endOfMessage: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var _ in fx.Transport.ReceiveAsync(cts.Token)) break;

        Assert.NotNull(captured);
        Assert.Equal("Bearer abc", captured!["authorization"]);
        Assert.Equal("T1",         captured!["x-tenant-id"]);
    }

    [Fact]
    public async Task Reject_ClosesStreamWithUnauthenticated_AndSuppressesInbox()
    {
        await using var fx = await ServerFixture.StartAsync(opts =>
        {
            opts.Authenticator = (_, _) =>
                ValueTask.FromResult(PeerAuthenticationResult.Reject("nope"));
        });
        await using var client = new TestBidiClient(fx.Address, peerId: "peer-X");

        // 客户端 send/read 任意一端会拿到 UNAUTHENTICATED；服务端 inbox 必须不入。
        var rpcEx = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            await client.SendAsync(new byte[][] { [0x01] }, endOfMessage: true);
            // server 关 stream 后客户端 ResponseStream.MoveNext 会抛
            await client.ReceiveOneMessageAsync(TimeSpan.FromSeconds(2));
        });
        Assert.Equal(StatusCode.Unauthenticated, rpcEx.StatusCode);

        await AssertInboxStaysEmptyAsync(fx);
        Assert.DoesNotContain(fx.Events, e => e.State == PeerConnectionState.Connected);
    }

    [Fact]
    public async Task ThrowingAuthenticator_TreatedAsReject()
    {
        await using var fx = await ServerFixture.StartAsync(opts =>
        {
            opts.Authenticator = (_, _) => throw new InvalidOperationException("boom");
        });
        await using var client = new TestBidiClient(fx.Address, peerId: "peer-X");

        var rpcEx = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            await client.SendAsync(new byte[][] { [0x01] }, endOfMessage: true);
            await client.ReceiveOneMessageAsync(TimeSpan.FromSeconds(2));
        });
        Assert.Equal(StatusCode.Unauthenticated, rpcEx.StatusCode);

        await AssertInboxStaysEmptyAsync(fx);
    }

    /// <summary>等 500ms，断言 transport 的 inbox 在此期间没收到任何消息。</summary>
    private static async Task AssertInboxStaysEmptyAsync(ServerFixture fx)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            await foreach (var _ in fx.Transport.ReceiveAsync(cts.Token))
            {
                Assert.Fail("Inbox must stay empty for rejected/throwing authenticator.");
            }
        }
        catch (OperationCanceledException)
        {
            // 期望路径：500ms 没收到消息 → cancellation 触发 → 退出循环
        }
    }

    // ---- fixture ----
    private sealed class ServerFixture : IAsyncDisposable
    {
        public WebApplication App { get; }
        public GrpcServerTransport Transport { get; }
        public Uri Address { get; }
        public List<PeerConnectionEvent> Events { get; } = new();

        private ServerFixture(WebApplication app, GrpcServerTransport transport, Uri address)
        {
            App = app;
            Transport = transport;
            Address = address;
            transport.PeerConnectionChanged += (_, e) =>
            {
                lock (Events) { Events.Add(e); }
            };
        }

        public static async Task<ServerFixture> StartAsync(Action<GrpcServerTransportOptions>? configure = null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o =>
            {
                o.Listen(IPAddress.Loopback, 0, lo => lo.Protocols = HttpProtocols.Http2);
            });
            builder.Services.AddGrpc();
            builder.Services.AddGrpcServerTransport("auth-test", configure);

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

    private sealed class TestBidiClient : IAsyncDisposable
    {
        private readonly GrpcChannel _channel;
        private readonly AsyncDuplexStreamingCall<TransportFrame, TransportFrame> _call;
        private readonly CancellationTokenSource _cts = new();

        public TestBidiClient(Uri serverAddress, string peerId, Metadata? extraHeaders = null)
        {
            _channel = GrpcChannel.ForAddress(serverAddress);
            var client = new Bidi.BidiClient(_channel);
            var headers = new Metadata { { "x-vertex-peer-id", peerId } };
            if (extraHeaders is not null)
            {
                foreach (var entry in extraHeaders)
                {
                    headers.Add(entry);
                }
            }
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
