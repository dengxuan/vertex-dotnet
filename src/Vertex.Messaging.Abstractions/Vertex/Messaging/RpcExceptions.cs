// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

namespace Vertex.Messaging;

/// <summary>
/// 远端 handler 抛出异常时，封装并发回客户端，由 <see cref="IRpcClient"/> 重新抛出。
/// </summary>
public sealed class RpcRemoteException : Exception
{
    public RpcRemoteException(string message) : base(message) { }

    public RpcRemoteException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// 等待响应超时。
/// </summary>
public sealed class RpcTimeoutException : Exception
{
    public RpcTimeoutException(string message) : base(message) { }
}

/// <summary>
/// 对端在响应到达前断开连接。
/// </summary>
public sealed class RpcPeerDisconnectedException : Exception
{
    public RpcPeerDisconnectedException(string message) : base(message) { }
}
