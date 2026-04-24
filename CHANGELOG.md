# Changelog

所有重大变更记录于此。格式遵循 [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)，版本号遵循 [Semantic Versioning](https://semver.org/spec/v2.0.0.html)。

## [1.0.1] - 2026-04-24

基于 v1.0.0 的关键修复和测试补强，API 无变化，下游直接升级。

### Fixed

- **messaging**: `MessagingChannel` 在 transport `Disconnected` 时会立即把所有 pending `InvokeAsync` 标记失败；对于服务端主动发起的 reverse-RPC，如果客户端恰好在这一瞬断线（优雅关闭或网络抖动），会导致正在飞行中的响应来不及回写就被标记 `ChannelDisconnectedException`。新实现在切到 Disconnected 后给一个短暂 grace period 等 in-flight 响应落地，过期后再把未完成的 pending invoke 失败。与 `vertex-go` 侧的 `Channel.Close` drain 语义对齐。(`1f5b013`)
- **di**: `AddGrpcServerTransport` 未注册 `ProtobufMessageSerializer`，导致 gRPC 传输侧序列化器缺失。现已在 DI extension 中补全。(`28f93f7`)

### Tests

- `Vertex.Transport.NetMq.Tests`：6 个集成测试覆盖 Router/Dealer、Pub/Sub、自动重连、并发 Invoke。(`1884773`)
- `Vertex.Transport.Grpc.Tests`：E2E 回归 `MessagingChannel` + `GrpcServerTransport` 双向流场景。(`96cc5ee`)

### Bench

- `Vertex.Messaging.Bench`：BenchmarkDotNet 基线（1v1 invoke latency、fan-out publish throughput）用于后续性能回归监控。(`d997581`)

### Docs

- README 状态升到 v1.0.0 GA，补交付语义（at-most-once / at-least-once）与生产环境 checklist。(`62faf70`)

## [1.0.0] - 2026-04-23

初次 GA 发布。从 Skywalker.Messaging / Skywalker.Transport 重构的轻量双向消息内核，与 `vertex-go 1.0.0` 配套，wire 协议 4-frame envelope。

### Added

- **Vertex.Messaging**：`MessagingChannel` + `IRpcClient` + `IMessageBus` + `IRpcHandler<TReq, TResp>` + `MessageTypeRegistry`。按 topic 注册处理器，按 `PeerId` 寻址调用。
- **Vertex.Messaging.Abstractions**：`PeerId`、`RpcContext<T>`、`PeerConnectionChanged`、`ChannelDisconnectedException` 等公共契约。
- **Vertex.Transport.Abstractions**：`ITransport` / `TransportFrame` / `ConnectionState` 基础抽象。
- **Vertex.Transport.Grpc**：基于 gRPC 双向流的 transport，支持 PerRPCCredentials、自动重连、指数退避。`AddGrpcTransport` / `AddGrpcServerTransport` DI extensions。
- **Vertex.Transport.NetMq**：基于 NetMQ 的 transport（Router/Dealer、Pub/Sub），支持 mDNS 服务发现和 libzmq 自动重连。
- **Vertex.Serialization.Abstractions** / **Vertex.Serialization.MessagePack** / **Vertex.Serialization.Protobuf**：可插拔序列化器；默认 protobuf，MessagePack 可用于非 proto 类型。

### Known caveats

- NetMQ transport 的 `Disconnected` 不可靠（libzmq 自动重连会屏蔽 peer 丢失）；`InvokeAsync` 应自带超时，不要依赖 `PeerDisconnectedError`。
