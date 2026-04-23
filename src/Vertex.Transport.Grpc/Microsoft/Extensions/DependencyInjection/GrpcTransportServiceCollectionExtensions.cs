// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Vertex.Transport;
using Vertex.Transport.Grpc;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI 扩展：注册命名的 gRPC transport。
/// </summary>
public static class GrpcTransportServiceCollectionExtensions
{
    /// <summary>
    /// 注册一个命名的 gRPC client transport。一个进程可注册多个，按 <paramref name="name"/> 区分。
    /// 同一个 <paramref name="name"/> 同时充当上层 messaging channel 的 transport key。
    /// </summary>
    public static IServiceCollection AddGrpcTransport(
        this IServiceCollection services,
        string name,
        Action<GrpcTransportOptions> configure)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(configure);

        EnsureRegistry(services);

        var options = new GrpcTransportOptions();
        configure(options);
        if (options.ServerAddress is null)
        {
            throw new ArgumentException(
                $"{nameof(GrpcTransportOptions.ServerAddress)} must be set when registering gRPC transport '{name}'.",
                nameof(configure));
        }

        services.AddKeyedSingleton(name, options);
        services.AddSingleton<ITransport>(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<GrpcTransport>();
            logger.LogInformation("gRPC transport '{Name}' starting; server={Server}", name, options.ServerAddress);
            return new GrpcTransport(name, options, logger);
        });

        return services;
    }

    private static void EnsureRegistry(IServiceCollection services)
    {
        // 与 NetMq 保持一致：复用 Vertex.Transport.NetMq 内的 TransportRegistry 实现。
        // 该类型为 internal，不能在此处直接 new。约定：调用方至少要先调用一次任意
        // AddNetMq* 或在自己的 composition root 里注册一个 ITransportRegistry。
        // 为了让 AddGrpcTransport 单独使用也能工作，提供一个轻量回退实现。
        services.TryAddSingleton<ITransportRegistry, DefaultTransportRegistry>();
    }

    private sealed class DefaultTransportRegistry : ITransportRegistry
    {
        private readonly Dictionary<string, ITransport> _transports;

        public DefaultTransportRegistry(IEnumerable<ITransport> transports)
        {
            _transports = new Dictionary<string, ITransport>(StringComparer.Ordinal);
            foreach (var t in transports)
            {
                if (!_transports.TryAdd(t.Name, t))
                {
                    throw new InvalidOperationException($"Duplicate transport name registered: {t.Name}");
                }
            }
        }

        public ITransport Get(string name) =>
            _transports.TryGetValue(name, out var t)
                ? t
                : throw new KeyNotFoundException($"Transport '{name}' not registered.");

        public bool TryGet(string name, out ITransport transport)
        {
            if (_transports.TryGetValue(name, out var t))
            {
                transport = t;
                return true;
            }
            transport = null!;
            return false;
        }
    }
}
