// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;
using NetMQ;
using NetMQ.Sockets;

namespace Vertex.Transport.NetMq;

/// <summary>
/// Dealer socket 实现：connect 到对端，带 identity；入站消息没有 identity 帧（已被 ZMQ 剥离）；
/// 发送时通常忽略 target（连到单个 Router 时无歧义）。
/// </summary>
internal sealed class NetMqDealerTransport : NetMqTransportBase, IConnectableTransport
{
    public NetMqDealerTransport(string name, DealerSocket socket, ILogger logger)
        : base(name, socket, logger, enableMonitor: true)
    {
    }

    public void Connect(string endpoint) => Schedule(s => s.Connect(endpoint));

    public void Disconnect(string endpoint) => Schedule(s => s.Disconnect(endpoint));

    protected override void OnSocketReceiveReady(object? sender, NetMQSocketEventArgs e)
    {
        var message = new NetMQMessage();
        while (e.Socket.TryReceiveMultipartMessage(TimeSpan.Zero, ref message))
        {
            if (message.FrameCount == 0)
            {
                continue;
            }
            EnqueueIncoming(PeerId.Empty, message);
            message = new NetMQMessage();
        }
    }

    protected override NetMQMessage? BuildOutbound(in OutboundEnvelope envelope)
    {
        // Dealer 不需要 identity 帧；连到单个 Router 时 target 被忽略。
        var msg = new NetMQMessage(envelope.Frames.Length);
        foreach (var frame in envelope.Frames)
        {
            msg.Append(frame);
        }
        return msg;
    }
}
