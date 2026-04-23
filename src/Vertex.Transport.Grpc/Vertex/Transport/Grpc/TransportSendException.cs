// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

namespace Vertex.Transport.Grpc;

/// <summary>
/// 单次 <see cref="ITransport.SendAsync"/> 失败时抛出。
/// 抛出此异常**不**意味着连接已断开（参见 <c>docs/modules/transport.md</c> 铁律 #2）：
/// transport 会保持当前 stream，调用方可以稍后重试。
/// 真正的断连判定只发生在读循环（铁律 #4）。
/// </summary>
public sealed class TransportSendException : Exception
{
    public TransportSendException(string message) : base(message) { }
    public TransportSendException(string message, Exception innerException) : base(message, innerException) { }
}
