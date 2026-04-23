// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;
using NetMQ;
using NetMQ.Sockets;

namespace Vertex.Transport.NetMq;

/// <summary>
/// Sub socket 实现：connect 到 Pub，按主题前缀订阅；不发送。
/// 入站第一帧（PUB 主题，由 Pub 端用 target.Value 写入）被剥离，作为 <see cref="PeerId"/> 暴露给上层；
/// 后续帧原样上传。这样 Messaging 层看到的帧布局与 Router/Dealer 一致（4 帧 WireFormat）。
/// </summary>
internal sealed class NetMqSubTransport : NetMqTransportBase, IConnectableTransport
{
    public NetMqSubTransport(string name, SubscriberSocket socket, ILogger logger)
        : base(name, socket, logger, enableMonitor: false)
    {
    }

    public void Connect(string endpoint) => Schedule(s => s.Connect(endpoint));

    public void Disconnect(string endpoint) => Schedule(s => s.Disconnect(endpoint));

    protected override void OnSocketReceiveReady(object? sender, NetMQSocketEventArgs e)
    {
        var message = new NetMQMessage();
        while (e.Socket.TryReceiveMultipartMessage(TimeSpan.Zero, ref message))
        {
            if (message.FrameCount < 1)
            {
                continue;
            }

            var pubTopic = new PeerId(message[0].ConvertToString());
            var rest = new NetMQMessage();
            for (var i = 1; i < message.FrameCount; i++)
            {
                rest.Append(message[i]);
            }
            EnqueueIncoming(pubTopic, rest);
            message = new NetMQMessage();
        }
    }

    protected override NetMQMessage? BuildOutbound(in OutboundEnvelope envelope)
    {
        throw new NotSupportedException("Subscriber transport cannot send.");
    }
}
