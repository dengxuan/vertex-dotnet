// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

namespace Vertex.Transport.Grpc;

/// <summary>
/// gRPC transport 重连策略。指数退避 + 抖动。
/// </summary>
public sealed class ReconnectPolicy
{
    /// <summary>
    /// 是否允许在底层 stream 断开后自动重连。默认 <c>true</c>。
    /// 设为 <c>false</c> 时一旦读循环退出，transport 即关闭、<see cref="GrpcTransport.ReceiveAsync"/>
    /// 的迭代结束、后续 <see cref="GrpcTransport.SendAsync"/> 抛出 <see cref="TransportSendException"/>。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 第一次重连前等待的时长。默认 1 秒。
    /// </summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 退避时长上限。默认 30 秒。
    /// </summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 退避增长因子。默认 2.0（每次失败后等待时长翻倍，直到 <see cref="MaxBackoff"/>）。
    /// </summary>
    public double Multiplier { get; set; } = 2.0;

    /// <summary>
    /// 抖动比例 [0, 1]。默认 0.2，表示在 ±20% 范围内随机扰动。用于避免雷鸣群效应。
    /// </summary>
    public double Jitter { get; set; } = 0.2;

    /// <summary>
    /// 默认策略：1s → 2s → 4s → … → 30s 上限，±20% 抖动。
    /// </summary>
    public static ReconnectPolicy Default => new();

    /// <summary>
    /// 不重连：一次断开即 transport 终结。常用于测试或一次性脚本。
    /// </summary>
    public static ReconnectPolicy Disabled => new() { Enabled = false };
}
