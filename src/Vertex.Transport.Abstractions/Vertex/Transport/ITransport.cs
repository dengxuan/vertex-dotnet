// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

namespace Vertex.Transport;

/// <summary>
/// 字节级网络传输抽象。不关心消息语义、不关心序列化。
/// 实现可以是 NetMQ 的 Pub/Sub/Router/Dealer，也可以是 InProc 测试桩。
/// </summary>
/// <remarks>
/// 同一个 transport 实例同时支持发送和接收（即使底层是单向 socket，
/// 不支持的方向应抛出 <see cref="NotSupportedException"/>）。
/// 实现必须线程安全：业务可以从任意线程调用 <see cref="SendAsync"/>。
/// </remarks>
public interface ITransport : IAsyncDisposable
{
    /// <summary>
    /// 该 transport 的逻辑名（DI 注册时指定，用于多实例隔离）。
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 发送一条多帧消息。实现必须将 <paramref name="frames"/> 作为一个原子单元写出
    /// （要么全部成功，要么全部失败），不允许跨帧穿插其他消息。
    /// </summary>
    /// <param name="target">目标对端。Router 必须指定；Dealer/Pub 通常忽略；Sub 不支持发送。</param>
    /// <param name="frames">要发送的帧序列。</param>
    /// <param name="cancellationToken">
    /// <para><b>语义契约（实现者必读）：</b></para>
    /// <para>
    /// <paramref name="cancellationToken"/> 仅用于<b>「写入操作真正落到底层 wire 之前」</b>的取消，
    /// 例如：等待写锁、等待连接建立、等待 backpressure 让步。
    /// </para>
    /// <para>
    /// <b>一旦写入已经开始落到底层 wire（例如已写入 HTTP/2 stream、已入队到 socket 发送队列），
    /// 实现必须忽略后续 CT 取消，把当次写入完成或抛出 transport 级异常。</b>
    /// </para>
    /// <para>
    /// 违反此契约会导致协议级灾难。以 gRPC bidi stream 为例：CT 在
    /// <c>RequestStream.WriteAsync</c> 执行中触发会发出 HTTP/2 RST_STREAM，
    /// 整条 stream 上所有 in-flight 请求一起失败、连接被强制重建。
    /// 真实事故案例见 <c>gaming-dotnet-sdk</c> v1.3.0 CHANGELOG 中
    /// <c>GamingSession.SendAsync</c> 的修复，以及 <c>docs/modules/transport.md</c>
    /// 「Transport 实现者四条铁律」。
    /// </para>
    /// </param>
    ValueTask SendAsync(PeerId target, IReadOnlyList<ReadOnlyMemory<byte>> frames, CancellationToken cancellationToken = default);

    /// <summary>
    /// 异步消费入站消息流。每个调用方应只创建一个消费循环。
    /// </summary>
    IAsyncEnumerable<TransportMessage> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 对端连接状态变化（Connected / Disconnected）。
    /// 仅在能区分对端的 transport 上有意义（典型：Router、Dealer）。Pub / Sub 实现可不触发。
    /// 触发线程不保证；订阅方需自行加锁或转发到本地队列。
    /// </summary>
    event EventHandler<PeerConnectionEvent>? PeerConnectionChanged;
}
