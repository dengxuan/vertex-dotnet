# vertex-dotnet

> .NET implementation of [Vertex](https://github.com/dengxuan/Vertex) — a lightweight, cross-language bidi messaging kernel.

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

## Status: 🚧 bootstrapping

Code is being ported from [`Skywalker.Messaging.*`](https://github.com/dengxuan/Skywalker/tree/main/src/Skywalker.Messaging) and [`Skywalker.Transport.*`](https://github.com/dengxuan/Skywalker/tree/main/src/Skywalker.Transport.Grpc). Tracking: [Skywalker spin-out design doc](https://github.com/dengxuan/Skywalker/blob/main/docs/architecture/messaging-spin-out.md).

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

> Planned API — code not functional until the Skywalker port lands.

### Install

```bash
dotnet add package Vertex.Messaging
dotnet add package Vertex.Transport.Grpc       # cross-language scenarios
# or
dotnet add package Vertex.Transport.NetMq      # intra-cluster scenarios
```

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

## Spec

The authoritative wire format and transport contract live in the [Vertex spec repo](https://github.com/dengxuan/Vertex). **Any wire-spec change lands there first**, with a companion PR here.

Key documents:

- [Wire format](https://github.com/dengxuan/Vertex/blob/main/spec/wire-format.md)
- [Transport contract (4 invariants)](https://github.com/dengxuan/Vertex/blob/main/spec/transport-contract.md)

## Contributing

See [`CONTRIBUTING.md`](./CONTRIBUTING.md).

## License

MIT — see [`LICENSE`](./LICENSE).
