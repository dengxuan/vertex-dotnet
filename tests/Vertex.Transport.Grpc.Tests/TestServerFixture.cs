// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Net;
using Grpc.AspNetCore.Server;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vertex.Transport.Grpc.Protocol.V1;

namespace Vertex.Transport.Grpc.Tests;

/// <summary>
/// 测试用 in-memory gRPC server。监听本机随机端口，行为通过 <see cref="BidiBehavior"/>
/// 注入；每个测试可独立配置「服务端怎么处理一条消息」。
/// </summary>
internal sealed class TestServerFixture : IAsyncDisposable
{
    public WebApplication App { get; }
    public BidiBehavior Behavior { get; }
    public Uri Address { get; }

    private TestServerFixture(WebApplication app, BidiBehavior behavior, Uri address)
    {
        App = app;
        Behavior = behavior;
        Address = address;
    }

    public static async Task<TestServerFixture> StartAsync(Action<BidiBehavior>? configure = null)
    {
        var behavior = new BidiBehavior();
        configure?.Invoke(behavior);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(IPAddress.Loopback, 0, lo => lo.Protocols = HttpProtocols.Http2);
        });
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(behavior);

        var app = builder.Build();
        app.MapGrpcService<TestBidiService>();

        await app.StartAsync().ConfigureAwait(false);

        var addresses = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses;
        var address = new Uri(addresses.First());
        return new TestServerFixture(app, behavior, address);
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync().ConfigureAwait(false);
        await App.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// 由测试逐项配置的 gRPC bidi server 行为。
/// </summary>
internal sealed class BidiBehavior
{
    /// <summary>每收到一帧就调用一次。返回值是要发回的帧（null 表示不回）。</summary>
    public Func<TransportFrame, IServerStreamWriter<TransportFrame>, Task>? OnFrame { get; set; }

    /// <summary>读循环开始前的钩子。可用于让 server 主动给 client 推送一些消息。</summary>
    public Func<IServerStreamWriter<TransportFrame>, Task>? OnConnect { get; set; }

    /// <summary>读完客户端流后是否主动关流（自然结束）。默认 true，正常结束 stream。</summary>
    public bool CompleteOnClientFinish { get; set; } = true;

    /// <summary>统计：服务端总共收到多少帧。</summary>
    public int ReceivedFrameCount;

    /// <summary>统计：服务端收到的所有完整 message（按 EndOfMessage 边界划分）。</summary>
    public ConcurrentBag<List<byte[]>> ReceivedMessages { get; } = new();
}

internal sealed class TestBidiService : Bidi.BidiBase
{
    private readonly BidiBehavior _behavior;

    public TestBidiService(BidiBehavior behavior) => _behavior = behavior;

    public override async Task Connect(IAsyncStreamReader<TransportFrame> requestStream, IServerStreamWriter<TransportFrame> responseStream, ServerCallContext context)
    {
        if (_behavior.OnConnect is not null)
        {
            await _behavior.OnConnect(responseStream).ConfigureAwait(false);
        }

        var current = new List<byte[]>();
        try
        {
            while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
            {
                var frame = requestStream.Current;
                Interlocked.Increment(ref _behavior.ReceivedFrameCount);
                current.Add(frame.Payload.ToByteArray());
                if (frame.EndOfMessage)
                {
                    _behavior.ReceivedMessages.Add(current);
                    current = new List<byte[]>();
                }

                if (_behavior.OnFrame is not null)
                {
                    await _behavior.OnFrame(frame, responseStream).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // client cancelled or server shutting down - normal
        }

        if (!_behavior.CompleteOnClientFinish)
        {
            // 让服务端持续 hold 住，以模拟 server side hang 的场景
            try { await Task.Delay(Timeout.Infinite, context.CancellationToken).ConfigureAwait(false); }
            catch { }
        }
    }
}
