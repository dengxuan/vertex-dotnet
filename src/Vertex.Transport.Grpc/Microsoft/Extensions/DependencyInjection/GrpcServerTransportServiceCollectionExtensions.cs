// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Vertex.Transport;
using Vertex.Transport.Grpc;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI 扩展：注册命名的 gRPC server transport。需要配合用户自己的
/// <c>app.MapGrpcService&lt;BidiServiceImpl&gt;()</c>。
/// </summary>
public static class GrpcServerTransportServiceCollectionExtensions
{
    /// <summary>
    /// 注册一个命名的 gRPC server transport。<paramref name="name"/> 同时充当上层 messaging channel 的 transport key。
    /// </summary>
    public static IServiceCollection AddGrpcServerTransport(
        this IServiceCollection services,
        string name,
        Action<GrpcServerTransportOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var options = new GrpcServerTransportOptions();
        configure?.Invoke(options);

        services.AddKeyedSingleton(name, options);

        // 创建为单例：同时作为 ITransport（按 name 注册到 transport registry）和具体类型（给 BidiServiceImpl 注入）。
        services.AddSingleton<GrpcServerTransport>(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<GrpcServerTransport>();
            logger.LogInformation("gRPC server transport '{Name}' starting.", name);
            return new GrpcServerTransport(name, options, logger);
        });
        services.AddSingleton<ITransport>(sp => sp.GetRequiredService<GrpcServerTransport>());

        // BidiServiceImpl 由 ASP.NET gRPC framework 在每次调用时实例化（scoped/transient 都行），
        // 注册为 singleton 即可，因为它只持有 transport 引用。
        services.TryAddSingleton<BidiServiceImpl>();

        // 提供轻量 fallback registry（同 client 端）。
        services.TryAddSingleton<ITransportRegistry, DefaultTransportRegistry>();
        return services;
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
