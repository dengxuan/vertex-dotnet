// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

namespace Vertex.Transport.NetMq;

/// <summary>
/// 角色为 bind 的 transport（Router / Pub）的配置。
/// </summary>
public sealed class NetMqBindOptions
{
    /// <summary>
    /// 显式 bind 地址；为空时使用 <see cref="BindRandomPortOnAllInterfaces"/>。
    /// 例: <c>tcp://*:5601</c> 或 <c>tcp://127.0.0.1:5601</c>。
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// 若为 true 且 <see cref="Endpoint"/> 为空，自动 <c>BindRandomPort("tcp://*")</c>，
    /// 端口将在 <see cref="ActualPort"/> 中暴露。
    /// </summary>
    public bool BindRandomPortOnAllInterfaces { get; set; } = true;

    /// <summary>
    /// 实际绑定的端口（启动后由 transport 填回，便于 mDNS 公告或日志）。
    /// </summary>
    public int ActualPort { get; internal set; }
}

/// <summary>
/// 角色为 connect 的 transport（Dealer / Sub）的配置。
/// </summary>
public sealed class NetMqConnectOptions
{
    /// <summary>
    /// 静态端点列表；通常为空，由 mDNS 等运行时发现机制后续追加。
    /// 例: <c>tcp://127.0.0.1:5601</c>。
    /// </summary>
    public IList<string> Endpoints { get; } = new List<string>();

    /// <summary>
    /// Dealer 的 socket identity；为空表示由 ZMQ 随机分配（Router 看不到稳定身份）。
    /// </summary>
    public string? Identity { get; set; }
}
