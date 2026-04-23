// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using Vertex.Transport;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vertex.Messaging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI 扩展：注册 messaging channel 与 RPC handler。每个 channel 是命名 keyed singleton，
/// 同一个 key 同时暴露 <see cref="IMessageBus"/> + <see cref="IRpcClient"/>。
/// </summary>
public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// 注册一个命名 messaging channel。channel 的 transport 名等于其 channel 名（约定）。
    /// </summary>
    public static IServiceCollection AddMessagingChannel(
        this IServiceCollection services,
        string name,
        Action<MessageTypeRegistry>? configure = null)
        => services.AddMessagingChannel(name, transportName: name, configure);

    /// <summary>
    /// 注册一个命名 messaging channel，使用指定 transport 名。
    /// </summary>
    public static IServiceCollection AddMessagingChannel(
        this IServiceCollection services,
        string name,
        string transportName,
        Action<MessageTypeRegistry>? configure = null)
    {
        var registry = new MessageTypeRegistry();
        configure?.Invoke(registry);
        services.AddKeyedSingleton(name, registry);

        services.AddKeyedSingleton<MessagingChannel>(name, (sp, key) =>
        {
            var transport = sp.GetRequiredService<ITransportRegistry>().Get(transportName);
            var typeRegistry = sp.GetRequiredKeyedService<MessageTypeRegistry>(key);
            var handlers = sp.GetServices<RpcHandlerRegistrationHolder>()
                .Where(h => h.Registration.ChannelName == (string)key!)
                .ToDictionary(h => h.Registration.Topic, h => h.Registration, StringComparer.Ordinal);
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<MessagingChannel>();
            return new MessagingChannel((string)key!, transport, typeRegistry, handlers, sp, logger);
        });

        services.AddKeyedSingleton<IMessageBus>(name, (sp, key) => sp.GetRequiredKeyedService<MessagingChannel>(key));
        services.AddKeyedSingleton<IRpcClient>(name, (sp, key) => sp.GetRequiredKeyedService<MessagingChannel>(key));

        // 收集所有 channel 名，启动时统一 warm up。
        services.AddSingleton(new MessagingChannelName(name));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MessagingChannelStarter>());
        return services;
    }

    /// <summary>
    /// 在指定 channel 上注册 RPC handler。需在 <see cref="AddMessagingChannel"/> 中通过
    /// <see cref="MessageTypeRegistry.RegisterRequest{TRequest, TResponse}"/> 注册类型。
    /// </summary>
    public static IServiceCollection AddRpcHandler<TRequest, TResponse, THandler>(
        this IServiceCollection services,
        string channelName)
        where TRequest : notnull
        where TResponse : notnull
        where THandler : class, IRpcHandler<TRequest, TResponse>
    {
        services.AddScoped<IRpcHandler<TRequest, TResponse>, THandler>();
        var registration = new RpcHandlerRegistration(
            channelName,
            MessageTopic.For<TRequest>().Value,
            typeof(TRequest),
            typeof(TResponse),
            async (sp, peerId, requestObj, ct) =>
            {
                var handler = sp.GetRequiredService<IRpcHandler<TRequest, TResponse>>();
                var ctx = new RpcContext<TRequest>(peerId, (TRequest)requestObj);
                var res = await handler.HandleAsync(ctx, ct).ConfigureAwait(false);
                return res!;
            });
        services.AddSingleton(new RpcHandlerRegistrationHolder(registration));
        return services;
    }
}

internal sealed record MessagingChannelName(string Name);

internal sealed record RpcHandlerRegistrationHolder(RpcHandlerRegistration Registration);

/// <summary>
/// 启动时遍历所有命名 channel 并解析一次，触发它们的 receive loop。
/// </summary>
internal sealed class MessagingChannelStarter : IHostedService
{
    private readonly IServiceProvider _sp;
    private readonly IEnumerable<MessagingChannelName> _names;
    private readonly ILogger<MessagingChannelStarter> _logger;

    public MessagingChannelStarter(
        IServiceProvider sp,
        IEnumerable<MessagingChannelName> names,
        ILogger<MessagingChannelStarter> logger)
    {
        _sp = sp;
        _names = names;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var n in _names)
        {
            var ch = _sp.GetRequiredKeyedService<MessagingChannel>(n.Name);
            _logger.LogDebug("Warmed up messaging channel '{Name}': {Type}", n.Name, ch.GetType().Name);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
