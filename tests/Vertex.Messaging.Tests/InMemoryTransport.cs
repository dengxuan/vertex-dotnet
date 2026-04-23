// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Vertex.Transport;

namespace Vertex.Messaging.Tests;

/// <summary>
/// In-memory loopback <see cref="ITransport"/> pair used by messaging-layer tests.
/// Sending from one side enqueues a <see cref="TransportMessage"/> on the other side's
/// receive channel. No real I/O, no threading surprises — ideal for verifying
/// <c>MessagingChannel</c> semantics (topic routing, request/response pairing,
/// serializer round-trips, error responses) deterministically.
/// </summary>
internal sealed class InMemoryTransport : ITransport
{
    public static (InMemoryTransport Alice, InMemoryTransport Bob) CreatePair(
        string aliceName = "alice",
        string bobName = "bob")
    {
        var aliceInbound = Channel.CreateUnbounded<TransportMessage>();
        var bobInbound = Channel.CreateUnbounded<TransportMessage>();
        var alice = new InMemoryTransport(aliceName, aliceInbound.Reader, bobInbound.Writer);
        var bob = new InMemoryTransport(bobName, bobInbound.Reader, aliceInbound.Writer);
        return (alice, bob);
    }

    private readonly string _name;
    private readonly ChannelReader<TransportMessage> _inbound;
    private readonly ChannelWriter<TransportMessage> _outboundToPeer;
    private int _disposed;

    private InMemoryTransport(
        string name,
        ChannelReader<TransportMessage> inbound,
        ChannelWriter<TransportMessage> outboundToPeer)
    {
        _name = name;
        _inbound = inbound;
        _outboundToPeer = outboundToPeer;
    }

    public string Name => _name;

    public event EventHandler<PeerConnectionEvent>? PeerConnectionChanged;

    public ValueTask SendAsync(PeerId target, IReadOnlyList<ReadOnlyMemory<byte>> frames, CancellationToken cancellationToken = default)
    {
        if (_disposed == 1) throw new ObjectDisposedException(nameof(InMemoryTransport));
        cancellationToken.ThrowIfCancellationRequested();

        // Copy frames so the buffer's lifetime is independent of the caller's.
        var snapshot = new ReadOnlyMemory<byte>[frames.Count];
        for (var i = 0; i < frames.Count; i++)
        {
            snapshot[i] = frames[i].ToArray();
        }

        // The peer sees this message as coming from *us*.
        var msg = new TransportMessage(new PeerId(_name), snapshot);
        if (!_outboundToPeer.TryWrite(msg))
        {
            throw new InvalidOperationException("Peer inbound channel is closed.");
        }
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<TransportMessage> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await _inbound.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_inbound.TryRead(out var msg))
            {
                yield return msg;
            }
        }
    }

    /// <summary>Simulate a connection-state change on this side (for tests that verify peer-lifecycle handling).</summary>
    public void RaisePeerConnection(PeerConnectionState state, string peer)
        => PeerConnectionChanged?.Invoke(this, new PeerConnectionEvent(new PeerId(peer), state));

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _outboundToPeer.TryComplete();
        }
        return ValueTask.CompletedTask;
    }
}
