# vertex-dotnet

> .NET implementation of [Vertex](https://github.com/dengxuan/Vertex) — a lightweight, cross-language bidi messaging kernel.

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

## Status: ✅ v1.0.0 GA

Migrated out of [`Skywalker.Messaging.*`](https://github.com/dengxuan/Skywalker) with a new serializer abstraction, transport-forced Protobuf on gRPC, and proto-FullName topic alignment to [vertex-go](https://github.com/dengxuan/vertex-go). Cross-language interop verified end-to-end via [Vertex/compat/hello](https://github.com/dengxuan/Vertex/tree/main/compat/hello).

## Package layout

| NuGet package | Role |
|---|---|
| `Vertex.Serialization.Abstractions` | `IMessageSerializer` interface |
| `Vertex.Serialization.Protobuf` | `ProtobufMessageSerializer` (required by `Vertex.Transport.Grpc`) |
| `Vertex.Serialization.MessagePack` | `MessagePackMessageSerializer` (default for `Vertex.Transport.NetMq`) |
| `Vertex.Transport.Abstractions` | `ITransport`, peer / frame primitives, the 4-invariant contract |
| `Vertex.Transport.NetMq` | ZeroMQ transport; user-supplied serializer (MessagePack default) |
| `Vertex.Transport.Grpc` | gRPC transport (client + server); Protobuf enforced |
| `Vertex.Messaging.Abstractions` | `IMessageBus`, `IRpcClient`, `IRpcHandler<,>` |
| `Vertex.Messaging` | `MessagingChannel` |

Namespaces mirror the package names (`namespace Vertex.Messaging { ... }`, etc.).

Target framework: **net8.0** initially.

## Getting started

### Install

```bash
dotnet add package Vertex.Messaging
dotnet add package Vertex.Transport.Grpc       # cross-language scenarios
# or
dotnet add package Vertex.Transport.NetMq      # intra-cluster scenarios
```

Packages are published to this repo's GitHub Packages feed. Configure a `nuget.config` with a `<packageSource>` pointing at `https://nuget.pkg.github.com/dengxuan/index.json` and a PAT with `read:packages`.

### Cross-language path (gRPC + Protobuf)

Define business messages in `.proto`:

```proto
// protos/gaming.proto
syntax = "proto3";
package gaming.v1;

message CreateRoom  { string room_name = 1; }
message RoomCreated { string room_id   = 1; }
```

Wire it up:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddVertexGrpcTransport("main", o =>
{
    o.ServerAddress = new Uri("https://api.example.com");
});

builder.Services.AddVertexMessaging("main", reg =>
{
    reg.RegisterEvent<gaming.v1.GameStateChanged>();
    reg.RegisterRequest<gaming.v1.CreateRoom, gaming.v1.RoomCreated>();
});
```

Use it:

```csharp
public class RoomService(IRpcClient rpc)
{
    public Task<gaming.v1.RoomCreated> CreateAsync(string name, CancellationToken ct) =>
        rpc.InvokeAsync<gaming.v1.CreateRoom, gaming.v1.RoomCreated>(
            new() { RoomName = name }, cancellationToken: ct).AsTask();
}
```

### Intra-cluster path (ZeroMQ + MessagePack, .NET only)

```csharp
[MessagePackObject]
public class CreateRoom { [Key(0)] public string RoomName { get; set; } = ""; }

builder.Services.AddVertexNetMqTransport("internal", o =>
{
    o.BindEndpoint = "tcp://*:5555";
    // o.Serializer defaults to MessagePackMessageSerializer;
    // pass any IMessageSerializer to opt into Protobuf / JSON / etc.
});
```

### Server-side RPC handler

```csharp
public class CreateRoomHandler
    : IRpcHandler<gaming.v1.CreateRoom, gaming.v1.RoomCreated>
{
    public ValueTask<gaming.v1.RoomCreated> HandleAsync(
        gaming.v1.CreateRoom req, RpcContext ctx, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        return ValueTask.FromResult(new gaming.v1.RoomCreated { RoomId = id });
    }
}

// in Startup:
builder.Services.AddScoped<
    IRpcHandler<gaming.v1.CreateRoom, gaming.v1.RoomCreated>,
    CreateRoomHandler>();
```

## Building (once code lands)

```bash
dotnet restore Vertex.sln
dotnet test
dotnet pack
```

MinVer-based versioning: git tags of the form `v<major>.<minor>.<patch>` drive the NuGet version (see the Skywalker setup for precedent).

## Delivery semantics — READ BEFORE PRODUCTION

Vertex is **transport-layer messaging**, not a durable broker. The distinction matters:

### `PublishAsync` = fire-and-forget, at-most-once

`IMessageBus.PublishAsync<T>(event)` sends an `EVENT` envelope over the current stream. **No ACK, no retry, no persistence.** Lost when:

- network drops bytes mid-flight
- server crashes between receive and dispatch
- a subscriber handler throws (logged, but event not reprocessed)
- the gRPC stream resets for any reason

`PublishAsync` returns once frames are on the wire. It cannot tell you whether the subscriber ran.

**Use for**: real-time notifications, cache invalidations, telemetry.
**Don't use for**: orders, payments, audit log — anything with a correctness requirement.

### `InvokeAsync` = request/response, at-most-once with error signalling

`IRpcClient.InvokeAsync<TReq, TResp>(request)` sends a `REQUEST` and awaits the matching `RESPONSE`. **The caller learns about every failure path:**

| Failure mode | What `InvokeAsync` does |
|---|---|
| Transport fails to send | `throw` (transport exception) |
| Server has no handler for this type | `throw RpcRemoteException("No RPC handler registered…")` |
| Server handler throws | `throw RpcRemoteException(<message>)` |
| Connection drops before response arrives | `throw RpcPeerDisconnectedException` |
| Response doesn't arrive within the timeout | `throw RpcTimeoutException` |
| CancellationToken cancelled | `throw OperationCanceledException` |

The app decides retry / idempotency / fallback — Vertex only guarantees you **know** whether it worked.

**Use for**: anything with a business correctness requirement.

### Decision table

| Your flow | Recommended |
|---|---|
| Real-time UI update | `PublishAsync` |
| Cache invalidation | `PublishAsync` |
| Payment confirmation | `InvokeAsync` or broker |
| Provision a resource | `InvokeAsync` |
| Audit log entry | `InvokeAsync`, or a broker/outbox for durability |
| Periodic heartbeat | `PublishAsync` |

### Truly durable (at-least-once, crash-safe)

Out of scope. Use Kafka / RabbitMQ / NATS JetStream. Vertex sits above the transport; it cannot add persistence on its own.

---

## Production checklist

Before pointing real traffic at a Vertex-based client:

- [ ] **`CancellationToken` with deadline on every PublishAsync / InvokeAsync call** — a stuck server should not block the caller indefinitely.
- [ ] **Logger wired**: `ILoggerFactory` from the host is used automatically by `MessagingChannel` (via standard DI) — make sure your logging backend captures Warn-level lines (those are the ones that fire on dropped events / handler exceptions).
- [ ] **`InvokeAsync` timeout ≤ server handler's worst-case latency** — callers that bail early while the handler still completes leave orphan work.
- [ ] **Dispose the host gracefully** — the built-in `MessagingChannelStarter` IHostedService drains the channel on `IHost.StopAsync()`; `Ctrl+C` on a console app does this automatically, Kubernetes `SIGTERM` → `IHostApplicationLifetime.ApplicationStopping` also works.
- [ ] **Use `InvokeAsync`, not `PublishAsync`, for must-deliver flows** — see the decision table above.

---

## Spec

The authoritative wire format and transport contract live in the [Vertex spec repo](https://github.com/dengxuan/Vertex). **Any wire-spec change lands there first**, with a companion PR here.

Key documents:

- [Wire format](https://github.com/dengxuan/Vertex/blob/main/spec/wire-format.md)
- [Transport contract (4 invariants)](https://github.com/dengxuan/Vertex/blob/main/spec/transport-contract.md)
- Cross-language interop test: [Vertex / compat / hello](https://github.com/dengxuan/Vertex/tree/main/compat/hello)

## Contributing

See [`CONTRIBUTING.md`](./CONTRIBUTING.md).

## License

MIT — see [`LICENSE`](./LICENSE).
