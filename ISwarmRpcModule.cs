// SPDX-FileCopyrightText: 2026 flcl42
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nmlc.Swarm.Plugin;

/// <summary>Reads content by public Swarm reference through a Bee API.</summary>
[RpcModule("swarm")]
public interface ISwarmRpcModule : IRpcModule
{
    /// <summary>Retrieves raw content at the supplied Swarm reference.</summary>
    Task<ResultWrapper<byte[]>> swarm_getBytes(string reference, CancellationToken cancellationToken);

    /// <summary>Retrieves a file or a collection's index document at the supplied Swarm reference.</summary>
    Task<ResultWrapper<byte[]>> swarm_getFile(string reference, CancellationToken cancellationToken);
}
