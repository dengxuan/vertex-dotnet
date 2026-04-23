// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vertex.Messaging;
using Vertex.Serialization;
using Vertex.Serialization.MessagePack;
using Vertex.Transport;

namespace Vertex.Messaging.Bench;

/// <summary>
/// Baseline micro-benchmarks for the messaging channel (MessagePack + InMemoryTransport).
///
/// Paths measured:
///   1. Publish — producer-only. Serialize + envelope + Send; peer receive
///      loop silently drops (matches at-most-once semantics).
///   2. Invoke — full round-trip. Serialize request + send + handler +
///      serialize response + unmarshal. Sequential.
///
/// Reference numbers only — not a gate. Use to spot regressions across
/// commits.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
public class MessagingChannelBenchmarks
{
    private ServiceProvider _aliceSp = null!;
    private ServiceProvider _bobSp = null!;
    private IMessageBus _alicePublisher = null!;
    private IRpcClient _aliceRpcClient = null!;

    private readonly HelloEvent _event = new() { Greeting = "hello" };
    private readonly EchoRequest _echoRequest = new() { Text = "hi" };

    [GlobalSetup]
    public void Setup()
    {
        var (alice, bob) = InMemoryTransport.CreatePair();

        // Alice: publisher + rpc client (no handler).
        _aliceSp = BuildSide("alice", alice, handlers: null);
        _alicePublisher = _aliceSp.GetRequiredKeyedService<IMessageBus>("alice");
        _aliceRpcClient = _aliceSp.GetRequiredKeyedService<IRpcClient>("alice");

        // Bob: handler for EchoRequest, subscriber is not needed (publish drops).
        _bobSp = BuildSide("bob", bob, handlers: sp =>
        {
            sp.AddSingleton<IRpcHandler<EchoRequest, EchoResponse>, EchoHandler>();
        });
        // Resolve bob's channel so its receive loop is running before benchmarks tick.
        _ = _bobSp.GetRequiredKeyedService<IMessageBus>("bob");
    }

    private static ServiceProvider BuildSide(
        string name,
        ITransport transport,
        Action<IServiceCollection>? handlers)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Register transport in a trivial registry so AddMessagingChannel resolves it.
        services.AddSingleton<ITransportRegistry>(new SingletonTransportRegistry(transport));
        services.AddKeyedSingleton<IMessageSerializer>(name, (_, _) => MessagePackMessageSerializer.Instance);

        services.AddMessagingChannel(name, reg =>
        {
            reg.RegisterEvent<HelloEvent>();
            reg.RegisterRequest<EchoRequest, EchoResponse>();
        });

        if (handlers is not null)
        {
            handlers(services);
            services.AddRpcHandler<EchoRequest, EchoResponse, EchoHandler>(name);
        }

        var sp = services.BuildServiceProvider();

        // Run the hosted-service warm-up so the channel starts its receive loop.
        foreach (var h in sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>())
        {
            h.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        return sp;
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _aliceSp.DisposeAsync();
        await _bobSp.DisposeAsync();
    }

    [Benchmark]
    public async ValueTask Publish_ProducerPath()
    {
        await _alicePublisher.PublishAsync(_event);
    }

    [Benchmark]
    public async ValueTask<EchoResponse> Invoke_Sequential()
    {
        return await _aliceRpcClient.InvokeAsync<EchoRequest, EchoResponse>(
            _echoRequest, timeout: TimeSpan.FromSeconds(5));
    }

    // ── fixtures ──────────────────────────────────────────────────────────

    private sealed class SingletonTransportRegistry : ITransportRegistry
    {
        private readonly ITransport _t;
        public SingletonTransportRegistry(ITransport t) => _t = t;
        public ITransport Get(string name) => _t;
        public bool TryGet(string name, out ITransport transport)
        {
            transport = _t;
            return true;
        }
    }

    private sealed class EchoHandler : IRpcHandler<EchoRequest, EchoResponse>
    {
        public ValueTask<EchoResponse> HandleAsync(RpcContext<EchoRequest> ctx, CancellationToken ct)
            => ValueTask.FromResult(new EchoResponse { Text = ctx.Request.Text });
    }

    [MessagePackObject]
    public sealed class HelloEvent
    {
        [Key(0)] public string Greeting { get; set; } = "";
    }

    [MessagePackObject]
    public sealed class EchoRequest
    {
        [Key(0)] public string Text { get; set; } = "";
    }

    [MessagePackObject]
    public sealed class EchoResponse
    {
        [Key(0)] public string Text { get; set; } = "";
    }
}
