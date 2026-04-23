// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using Grpc.Net.Client;

namespace Vertex.Transport.Grpc;

/// <summary>
/// <see cref="GrpcTransport"/> 的配置选项。
/// </summary>
public sealed class GrpcTransportOptions
{
    /// <summary>
    /// 服务端地址。必填。例如 <c>https://api.example.com</c>。
    /// </summary>
    public Uri? ServerAddress { get; set; }

    /// <summary>
    /// 覆盖 HTTP/2 <c>:authority</c> 头。通常不需要设置；常见用法是
    /// 透过反向代理时把 <c>:authority</c> 强制为后端真实主机名。
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>
    /// 每次发起 bidi 调用时附带的 metadata（HTTP/2 头）。常用来携带鉴权信息，
    /// 例如 <c>{ "authorization", "Bearer ..." }</c>。
    /// 该集合在每次重连时会被重新读取一次（允许实现按需轮换 token）。
    /// </summary>
    public IList<KeyValuePair<string, string>> Metadata { get; } = new List<KeyValuePair<string, string>>();

    /// <summary>
    /// 保留字段：未来用于显式的连接超时控制。
    /// 当前实现把「<see cref="GrpcTransport"/> 已构造、bidi call 对象已创建」视为已连接，
    /// 真实的网络连通性会在第一次 <c>SendAsync</c> 时由 HTTP/2 stream 建立过程暴露。
    /// 默认 10 秒。
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 重连策略。默认开启，指数退避。详见 <see cref="ReconnectPolicy"/>。
    /// </summary>
    public ReconnectPolicy Reconnect { get; set; } = ReconnectPolicy.Default;

    /// <summary>
    /// 允许调用方进一步定制 <see cref="GrpcChannelOptions"/>，例如：
    /// <list type="bullet">
    ///   <item>替换 <c>HttpHandler</c> 以接入自定义 HTTPS 校验</item>
    ///   <item>设置 <c>Credentials</c> / <c>UnsafeUseInsecureChannelCallCredentials</c></item>
    ///   <item>设置 <c>MaxReceiveMessageSize</c> / <c>MaxSendMessageSize</c></item>
    /// </list>
    /// </summary>
    public Action<GrpcChannelOptions>? ConfigureChannel { get; set; }
}
