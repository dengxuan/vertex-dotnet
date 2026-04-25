// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

namespace Vertex.Transport;

/// <summary>
/// 投给 <see cref="PeerAuthenticator"/> 的入参。<paramref name="Peer"/> 是 transport
/// 已分配好的 PeerId；<paramref name="Metadata"/> 是建链时附带的 key-value
/// （已 lowercase 归一化，spec/peer-authentication.md §4）。
/// </summary>
public readonly record struct PeerAuthenticationContext(
    PeerId Peer,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>
/// <see cref="PeerAuthenticator"/> 的结果。<see cref="Accepted"/>=true 时
/// <see cref="PeerState"/> 是用户数据（opaque object）；false 时
/// <see cref="RejectReason"/> 给到 transport 用作关流时的 status message。
/// 用静态工厂 <see cref="Accept"/> / <see cref="Reject"/> 构造。
/// </summary>
public readonly record struct PeerAuthenticationResult
{
    public bool    Accepted     { get; init; }
    public string? RejectReason { get; init; }
    public object? PeerState    { get; init; }

    public static PeerAuthenticationResult Accept(object? state = null)
        => new() { Accepted = true, PeerState = state };

    public static PeerAuthenticationResult Reject(string reason)
        => new() { Accepted = false, RejectReason = reason };
}

/// <summary>
/// 连接级认证回调 —— 实现 spec/peer-authentication.md。
/// Server-side transport 在新 peer 连上时调一次（read loop 之前）。
/// 实现 MUST 是可重入的；多 peer 并发可能并行调进来。
/// 抛异常 == Reject（transport 把异常消息当 reject reason）。
/// </summary>
public delegate ValueTask<PeerAuthenticationResult> PeerAuthenticator(
    PeerAuthenticationContext context,
    CancellationToken cancellationToken);
