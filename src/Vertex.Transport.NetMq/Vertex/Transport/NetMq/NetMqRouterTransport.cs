// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;
using NetMQ;
using NetMQ.Sockets;

namespace Vertex.Transport.NetMq;

/// <summary>
/// Router socket 实现：bind 端口接收多个 Dealer，入站消息带 identity 帧；
/// 发送时必须指定目标 <see cref="PeerId"/>。
/// </summary>
internal sealed class NetMqRouterTransport : NetMqTransportBase
{
    private readonly ILogger _logger;

    public NetMqRouterTransport(string name, RouterSocket socket, ILogger logger)
        : base(name, socket, logger, enableMonitor: true)
    {
        _logger = logger;
    }

    protected override void OnSocketReceiveReady(object? sender, NetMQSocketEventArgs e)
    {
        var message = new NetMQMessage();
        while (e.Socket.TryReceiveMultipartMessage(TimeSpan.Zero, ref message))
        {
            if (message.FrameCount < 1)
            {
                continue;
            }

            var peerId = new PeerId(message[0].ConvertToString());
            var rest = new NetMQMessage();
            for (var i = 1; i < message.FrameCount; i++)
            {
                rest.Append(message[i]);
            }

            EnqueueIncoming(peerId, rest);
            message = new NetMQMessage();
        }
    }

    protected override NetMQMessage? BuildOutbound(in OutboundEnvelope envelope)
    {
        if (envelope.Target.IsEmpty)
        {
            _logger.LogWarning("Router transport requires explicit target PeerId; dropping message.");
            return null;
        }

        var msg = new NetMQMessage(envelope.Frames.Length + 1);
        msg.Append(envelope.Target.Value);
        foreach (var frame in envelope.Frames)
        {
            msg.Append(frame);
        }
        return msg;
    }
}
