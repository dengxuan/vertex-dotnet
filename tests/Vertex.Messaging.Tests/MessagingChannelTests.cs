// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vertex.Serialization.MessagePack;
using Vertex.Transport;

namespace Vertex.Messaging.Tests;

public class MessagingChannelTests
{
    // ============================= Event pub/sub =============================

    [Fact]
    public async Task Publish_DeliversToRemoteSubscriber_WithCorrectTopic()
    {
        var (alice, bob) = InMemoryTransport.CreatePair();
        await using var aliceChannel = CreateChannel("alice", alice,
            reg => reg.RegisterEvent<HelloEvent>());
        await using var bobChannel = CreateChannel("bob", bob,
            reg => reg.RegisterEvent<HelloEvent>());

        var received = new TaskCompletionSource<HelloEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        bobChannel.Subscribe<HelloEvent>((ctx, _) =>
        {
            received.TrySetResult(ctx.Payload);
            return ValueTask.CompletedTask;
        });

        await aliceChannel.PublishAsync(new HelloEvent("world"));

        var evt = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("world", evt.Greeting);
    }

    [Fact]
    public async Task Publish_UnregisteredEventOnReceiver_IsSilentlyDropped()
    {
        var (alice, bob) = InMemoryTransport.CreatePair();
        await using var aliceChannel = CreateChannel("alice", alice,
            reg => reg.RegisterEvent<HelloEvent>());
        await using var bobChannel = CreateChannel("bob", bob);  // bob did NOT register HelloEvent

        await aliceChannel.PublishAsync(new HelloEvent("nobody listens"));

        // Give the dispatcher a moment; assert nothing throws and receiver didn't crash.
        await Task.Delay(100);
    }

    // ============================= RPC invoke =============================

    [Fact]
    public async Task Invoke_RoundTripsSuccessResponse()
    {
        var services = new ServiceCollection();
        services.AddScoped<IRpcHandler<EchoRequest, EchoResponse>>(_ =>
            new LambdaEchoHandler(req => new EchoResponse($"echo: {req.Text}")));
        var serverSp = services.BuildServiceProvider();

        var (alice, bob) = InMemoryTransport.CreatePair();
        await using var aliceChannel = CreateChannel("alice", alice,
            reg => reg.RegisterRequest<EchoRequest, EchoResponse>());
        await using var bobChannel = CreateChannel("bob", bob,
            reg => reg.RegisterRequest<EchoRequest, EchoResponse>(),
            handlers: CreateEchoHandlerRegistration(),
            services: serverSp);

        var resp = await aliceChannel.InvokeAsync<EchoRequest, EchoResponse>(new EchoRequest("hi"));
        Assert.Equal("echo: hi", resp.Text);
    }

    [Fact]
    public async Task Invoke_HandlerThrows_PropagatesAsRpcRemoteException()
    {
        var services = new ServiceCollection();
        services.AddScoped<IRpcHandler<EchoRequest, EchoResponse>>(_ =>
            new LambdaEchoHandler(_ => throw new InvalidOperationException("boom")));
        var serverSp = services.BuildServiceProvider();

        var (alice, bob) = InMemoryTransport.CreatePair();
        await using var aliceChannel = CreateChannel("alice", alice,
            reg => reg.RegisterRequest<EchoRequest, EchoResponse>());
        await using var bobChannel = CreateChannel("bob", bob,
            reg => reg.RegisterRequest<EchoRequest, EchoResponse>(),
            handlers: CreateEchoHandlerRegistration(),
            services: serverSp);

        var ex = await Assert.ThrowsAsync<RpcRemoteException>(
            () => aliceChannel.InvokeAsync<EchoRequest, EchoResponse>(new EchoRequest("ignored")).AsTask());
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task Invoke_NoHandlerRegistered_ReturnsErrorResponse()
    {
        var (alice, bob) = InMemoryTransport.CreatePair();
        await using var aliceChannel = CreateChannel("alice", alice,
            reg => reg.RegisterRequest<EchoRequest, EchoResponse>());
        await using var bobChannel = CreateChannel("bob", bob,
            reg => reg.RegisterRequest<EchoRequest, EchoResponse>());  // no handler bound

        var ex = await Assert.ThrowsAsync<RpcRemoteException>(
            () => aliceChannel.InvokeAsync<EchoRequest, EchoResponse>(new EchoRequest("x")).AsTask());
        Assert.Contains("No RPC handler", ex.Message);
    }

    [Fact]
    public async Task Invoke_TimeoutFires_WhenHandlerHangs()
    {
        // Bob has a handler registered, but it blocks until its CancellationToken trips.
        // Alice's per-call timeout should fire before that.
        var hangingHandler = new HangingEchoHandler();
        var services = new ServiceCollection();
        services.AddScoped<IRpcHandler<EchoRequest, EchoResponse>>(_ => hangingHandler);
        var serverSp = services.BuildServiceProvider();

        var (alice, bob) = InMemoryTransport.CreatePair();
        await using var aliceChannel = CreateChannel("alice", alice,
            reg => reg.RegisterRequest<EchoRequest, EchoResponse>());
        await using var bobChannel = CreateChannel("bob", bob,
            reg => reg.RegisterRequest<EchoRequest, EchoResponse>(),
            handlers: CreateEchoHandlerRegistration(),
            services: serverSp);

        await Assert.ThrowsAsync<RpcTimeoutException>(
            () => aliceChannel.InvokeAsync<EchoRequest, EchoResponse>(
                new EchoRequest("please hang"),
                timeout: TimeSpan.FromMilliseconds(200)).AsTask());
    }

    /// <summary>
    /// Regression guard: when the transport raises Disconnected, the channel must
    /// wait a brief grace period before failing pending invokes. If a response
    /// arrives during that window, the Invoke must complete successfully — not
    /// surface <see cref="RpcPeerDisconnectedException"/>.
    ///
    /// Without this fix, production ran into: a Go client handler returns its
    /// response, then the bidi stream tears down; .NET's transport read loop
    /// fires Disconnected while the response is still sitting in the inbound
    /// queue; the synchronous event handler exceptioned every pending Invoke
    /// before the receive loop could drain the racing response.
    /// </summary>
    [Fact]
    public async Task Invoke_ResponseArrivesDuringDisconnectGrace_CompletesSuccessfully()
    {
        var services = new ServiceCollection();
        services.AddScoped<IRpcHandler<EchoRequest, EchoResponse>>(_ =>
            new LambdaEchoHandler(req => new EchoResponse($"echo: {req.Text}")));
        var serverSp = services.BuildServiceProvider();

        var (alice, bob) = InMemoryTransport.CreatePair();
        // Long grace period so we can deterministically interleave disconnect
        // and response: we fire Disconnected BEFORE the response lands, then
        // show that the receive loop drains the response inside the grace
        // and the Invoke completes successfully.
        await using var aliceChannel = CreateChannel("alice", alice,
            reg => reg.RegisterRequest<EchoRequest, EchoResponse>(),
            peerDisconnectGracePeriod: TimeSpan.FromSeconds(2));
        await using var bobChannel = CreateChannel("bob", bob,
            reg => reg.RegisterRequest<EchoRequest, EchoResponse>(),
            handlers: CreateEchoHandlerRegistration(),
            services: serverSp);

        // Start an Invoke; it's waiting for a response from bob.
        var invokeTask = aliceChannel.InvokeAsync<EchoRequest, EchoResponse>(
            new EchoRequest("hi"),
            timeout: TimeSpan.FromSeconds(10)).AsTask();

        // Fire a disconnect on alice BEFORE the response has arrived. Under
        // the old (buggy) behavior, this synchronously exceptioned the
        // pending invoke. Under the fix, the failure is deferred, and the
        // response handler (which runs on the receive loop) unblocks the
        // invoke with a real EchoResponse before the grace period expires.
        alice.RaisePeerConnection(PeerConnectionState.Disconnected, "bob");

        var resp = await invokeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("echo: hi", resp.Text);
    }

    // ============================= Topic naming =============================

    [Fact]
    public void MessageTopic_For_NonProtoType_UsesFullName()
    {
        var topic = MessageTopic.For<HelloEvent>();
        // POCO: expect CLR full name, not just short name.
        Assert.Equal(typeof(HelloEvent).FullName, topic.Value);
    }

    [Fact]
    public void MessageTopic_For_DuckTypedProtoLikeType_UsesDescriptorFullName()
    {
        // Fake a proto-generated shape: public static Descriptor with public instance FullName.
        var topic = MessageTopic.For<FakeProtoMessage>();
        Assert.Equal("fake.v1.FakeMessage", topic.Value);
    }

    // ============================= Helpers =============================

    private static MessagingChannel CreateChannel(
        string name,
        ITransport transport,
        Action<MessageTypeRegistry>? configureTypes = null,
        IReadOnlyDictionary<string, RpcHandlerRegistration>? handlers = null,
        IServiceProvider? services = null,
        TimeSpan? defaultTimeout = null,
        TimeSpan? peerDisconnectGracePeriod = null)
    {
        var registry = new MessageTypeRegistry();
        configureTypes?.Invoke(registry);
        handlers ??= new Dictionary<string, RpcHandlerRegistration>(StringComparer.Ordinal);
        services ??= new ServiceCollection().BuildServiceProvider();
        return new MessagingChannel(
            name,
            transport,
            MessagePackMessageSerializer.Instance,
            registry,
            handlers,
            services,
            NullLogger<MessagingChannel>.Instance,
            defaultTimeout,
            peerDisconnectGracePeriod);
    }

    private static Dictionary<string, RpcHandlerRegistration> CreateEchoHandlerRegistration()
    {
        var reg = new RpcHandlerRegistration(
            ChannelName: "bob",
            Topic: MessageTopic.For<EchoRequest>().Value,
            RequestType: typeof(EchoRequest),
            ResponseType: typeof(EchoResponse),
            Invoke: async (sp, _, requestObj, peerState, ct) =>
            {
                var handler = sp.GetRequiredService<IRpcHandler<EchoRequest, EchoResponse>>();
                var ctx = new RpcContext<EchoRequest>(PeerId.Empty, (EchoRequest)requestObj) { PeerState = peerState };
                var res = await handler.HandleAsync(ctx, ct).ConfigureAwait(false);
                return (object)res;
            });
        return new Dictionary<string, RpcHandlerRegistration>(StringComparer.Ordinal)
        {
            [reg.Topic] = reg,
        };
    }

    // ============================= Test fixtures =============================

    [MessagePackObject]
    public sealed class HelloEvent
    {
        [Key(0)] public string Greeting { get; set; } = "";
        public HelloEvent() { }
        public HelloEvent(string g) { Greeting = g; }
    }

    [MessagePackObject]
    public sealed class EchoRequest
    {
        [Key(0)] public string Text { get; set; } = "";
        public EchoRequest() { }
        public EchoRequest(string t) { Text = t; }
    }

    [MessagePackObject]
    public sealed class EchoResponse
    {
        [Key(0)] public string Text { get; set; } = "";
        public EchoResponse() { }
        public EchoResponse(string t) { Text = t; }
    }

    [MessagePackObject]
    public sealed class HushRequest { public HushRequest() { } }

    [MessagePackObject]
    public sealed class HushResponse { public HushResponse() { } }

    private sealed class LambdaEchoHandler : IRpcHandler<EchoRequest, EchoResponse>
    {
        private readonly Func<EchoRequest, EchoResponse> _f;
        public LambdaEchoHandler(Func<EchoRequest, EchoResponse> f) { _f = f; }
        public ValueTask<EchoResponse> HandleAsync(RpcContext<EchoRequest> ctx, CancellationToken ct)
            => ValueTask.FromResult(_f(ctx.Request));
    }

    private sealed class HangingEchoHandler : IRpcHandler<EchoRequest, EchoResponse>
    {
        public async ValueTask<EchoResponse> HandleAsync(RpcContext<EchoRequest> ctx, CancellationToken ct)
        {
            // Hang until cancelled (channel disposal cancels the handler's CT).
            var tcs = new TaskCompletionSource<EchoResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            return await tcs.Task.ConfigureAwait(false);
        }
    }

    /// <summary>A type that quacks like a proto-generated message for <see cref="MessageTopic"/>'s duck-typing.</summary>
    public sealed class FakeProtoMessage
    {
        public static FakeDescriptor Descriptor { get; } = new();

        public sealed class FakeDescriptor
        {
            public string FullName => "fake.v1.FakeMessage";
        }
    }
}
