// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;
using NetMQ;
using NetMQ.Sockets;

namespace Vertex.Transport.NetMq;

/// <summary>
/// Pub socket 实现：bind 端口广播；不接收。
/// 第一帧约定为"主题"（NetMQ 的订阅过滤基于此前缀）；其余为 payload。
/// 我们的 <see cref="OutboundEnvelope.Target"/> 在这里被作为主题前缀帧使用。
/// </summary>
internal sealed class NetMqPubTransport : NetMqTransportBase
{
    public NetMqPubTransport(string name, PublisherSocket socket, ILogger logger)
        : base(name, socket, logger, enableMonitor: false)
    {
    }

    protected override void OnSocketReceiveReady(object? sender, NetMQSocketEventArgs e)
    {
        // Publisher 不接收，永远不应触发；保险起见排空丢弃。
        var message = new NetMQMessage();
        while (e.Socket.TryReceiveMultipartMessage(TimeSpan.Zero, ref message))
        {
            message = new NetMQMessage();
        }
    }

    protected override NetMQMessage BuildOutbound(in OutboundEnvelope envelope)
    {
        var msg = new NetMQMessage(envelope.Frames.Length + 1);
        // PUB 的 topic 前缀帧；空字符串也合法（订阅方对 "" 前缀订阅则全收）。
        msg.Append(envelope.Target.IsEmpty ? string.Empty : envelope.Target.Value);
        foreach (var frame in envelope.Frames)
        {
            msg.Append(frame);
        }
        return msg;
    }
}
