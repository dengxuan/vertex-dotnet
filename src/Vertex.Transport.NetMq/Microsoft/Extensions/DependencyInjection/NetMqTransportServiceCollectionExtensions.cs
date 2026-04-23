// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NetMQ.Sockets;
using Vertex.Serialization;
using Vertex.Serialization.MessagePack;
using Vertex.Transport;
using Vertex.Transport.NetMq;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI 扩展：注册命名的 NetMQ transport（Router / Dealer / Pub / Sub）。
/// </summary>
public static class NetMqTransportServiceCollectionExtensions
{
    /// <summary>
    /// 注册一个 Router transport（bind，多客户端，带 identity 路由）。
    /// <paramref name="serializer"/> 为 null 时默认 <see cref="MessagePackMessageSerializer"/>。
    /// </summary>
    public static IServiceCollection AddNetMqRouterTransport(
        this IServiceCollection services,
        string name,
        Action<NetMqBindOptions> configure,
        IMessageSerializer? serializer = null)
    {
        EnsureRegistry(services);
        var options = new NetMqBindOptions();
        configure(options);
        services.AddKeyedSingleton(name, options);
        services.AddKeyedSingleton<IBindEndpointInfo>(name, (_, _) => new BindEndpointInfo(name, options));
        services.AddSingleton<ITransport>(sp =>
        {
            var socket = new RouterSocket();
            socket.Options.RouterMandatory = false; // 未知 identity 的消息丢弃而非抛异常
            BindSocket(socket, options);

            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger($"Transport.{name}.Router");
            logger.LogInformation("NetMQ Router '{Name}' bound to port {Port}", name, options.ActualPort);
            return new NetMqRouterTransport(name, socket, logger);
        });
        RegisterSerializer(services, name, serializer);
        return services;
    }

    /// <summary>
    /// 注册一个 Dealer transport（connect，可带 identity）。
    /// <paramref name="serializer"/> 为 null 时默认 <see cref="MessagePackMessageSerializer"/>。
    /// </summary>
    public static IServiceCollection AddNetMqDealerTransport(
        this IServiceCollection services,
        string name,
        Action<NetMqConnectOptions> configure,
        IMessageSerializer? serializer = null)
    {
        EnsureRegistry(services);
        var options = new NetMqConnectOptions();
        configure(options);
        services.AddKeyedSingleton(name, options);
        services.AddSingleton<ITransport>(sp =>
        {
            var socket = new DealerSocket();
            if (!string.IsNullOrEmpty(options.Identity))
            {
                socket.Options.Identity = System.Text.Encoding.UTF8.GetBytes(options.Identity);
            }
            foreach (var ep in options.Endpoints)
            {
                socket.Connect(ep);
            }

            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger($"Transport.{name}.Dealer");
            logger.LogInformation("NetMQ Dealer '{Name}' identity={Identity} connected to {Count} endpoint(s)",
                name, options.Identity ?? "(auto)", options.Endpoints.Count);
            return new NetMqDealerTransport(name, socket, logger);
        });
        services.AddKeyedSingleton<IConnectableTransport>(name, (sp, key) =>
            (IConnectableTransport)sp.GetRequiredService<ITransportRegistry>().Get((string)key!));
        RegisterSerializer(services, name, serializer);
        return services;
    }

    /// <summary>
    /// 注册一个 Pub transport（bind，单向广播）。
    /// <paramref name="serializer"/> 为 null 时默认 <see cref="MessagePackMessageSerializer"/>。
    /// </summary>
    public static IServiceCollection AddNetMqPubTransport(
        this IServiceCollection services,
        string name,
        Action<NetMqBindOptions> configure,
        IMessageSerializer? serializer = null)
    {
        EnsureRegistry(services);
        var options = new NetMqBindOptions();
        configure(options);
        services.AddKeyedSingleton(name, options);
        services.AddKeyedSingleton<IBindEndpointInfo>(name, (_, _) => new BindEndpointInfo(name, options));
        services.AddSingleton<ITransport>(sp =>
        {
            var socket = new PublisherSocket();
            BindSocket(socket, options);

            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger($"Transport.{name}.Pub");
            logger.LogInformation("NetMQ Pub '{Name}' bound to port {Port}", name, options.ActualPort);
            return new NetMqPubTransport(name, socket, logger);
        });
        RegisterSerializer(services, name, serializer);
        return services;
    }

    /// <summary>
    /// 注册一个 Sub transport（connect，按主题订阅；默认订阅全部）。
    /// <paramref name="serializer"/> 为 null 时默认 <see cref="MessagePackMessageSerializer"/>。
    /// </summary>
    public static IServiceCollection AddNetMqSubTransport(
        this IServiceCollection services,
        string name,
        Action<NetMqConnectOptions>? configure = null,
        IMessageSerializer? serializer = null,
        params string[] subscriptions)
    {
        EnsureRegistry(services);
        var options = new NetMqConnectOptions();
        configure?.Invoke(options);
        services.AddKeyedSingleton(name, options);
        services.AddSingleton<ITransport>(sp =>
        {
            var socket = new SubscriberSocket();
            foreach (var ep in options.Endpoints)
            {
                socket.Connect(ep);
            }

            if (subscriptions is null || subscriptions.Length == 0)
            {
                socket.SubscribeToAnyTopic();
            }
            else
            {
                foreach (var sub in subscriptions)
                {
                    socket.Subscribe(sub);
                }
            }

            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger($"Transport.{name}.Sub");
            logger.LogInformation("NetMQ Sub '{Name}' connected to {Count} endpoint(s) with {SubCount} subscription(s)",
                name, options.Endpoints.Count, subscriptions?.Length ?? 0);
            return new NetMqSubTransport(name, socket, logger);
        });
        services.AddKeyedSingleton<IConnectableTransport>(name, (sp, key) =>
            (IConnectableTransport)sp.GetRequiredService<ITransportRegistry>().Get((string)key!));
        RegisterSerializer(services, name, serializer);
        return services;
    }

    /// <summary>
    /// 把 NetMq transport 的 serializer 绑定到命名 channel。
    /// 允许用户注入任意 <see cref="IMessageSerializer"/>（Protobuf / 自定义 JSON / 等）；
    /// 不指定则用 MessagePack 默认（<see cref="MessagePackMessageSerializer"/>）。
    /// </summary>
    private static void RegisterSerializer(IServiceCollection services, string name, IMessageSerializer? serializer)
    {
        var effective = serializer ?? MessagePackMessageSerializer.Instance;
        services.AddKeyedSingleton<IMessageSerializer>(name, (_, _) => effective);
    }

    private static void EnsureRegistry(IServiceCollection services)
    {
        services.TryAddSingleton<ITransportRegistry, TransportRegistry>();
    }

    private static void BindSocket(NetMQ.NetMQSocket socket, NetMqBindOptions options)
    {
        if (!string.IsNullOrEmpty(options.Endpoint))
        {
            socket.Bind(options.Endpoint);
            // 解析端口：支持 tcp://host:port 或 tcp://*:port
            var idx = options.Endpoint.LastIndexOf(':');
            if (idx > 0 && int.TryParse(options.Endpoint.AsSpan(idx + 1), out var port))
            {
                options.ActualPort = port;
            }
        }
        else if (options.BindRandomPortOnAllInterfaces)
        {
            options.ActualPort = socket.BindRandomPort("tcp://*");
        }
        else
        {
            throw new InvalidOperationException("Either Endpoint or BindRandomPortOnAllInterfaces must be set.");
        }
    }
}
