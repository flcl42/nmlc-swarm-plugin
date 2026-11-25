# Swarm plugin for Nethermind Light Client

This build-time plugin adds read-only JSON-RPC access to public Swarm content.
It downloads content from an operator-configured [Bee HTTP API](https://docs.ethswarm.org/api/).
Use a Bee node you trust to retrieve the content for a reference.

Build a plugin-enabled light client by selecting `flcl42/nmlc-swarm-plugin/swarm@<commit>`
in the light client's plugin build instructions. The project references the
Nethermind source used to build the light client.

Set `Swarm__BeeUrl` in the light client's environment to the Bee base URL, such
as `http://127.0.0.1:1633` for a local Bee node. In Docker, use a Bee URL reachable
from the light-client container and pass it with `-e Swarm__BeeUrl=...`.

| JSON-RPC method | Bee request |
| --- | --- |
| `swarm_getBytes` | `GET /bytes/{reference}` for raw data |
| `swarm_getFile` | `GET /bzz/{reference}/` for a file or a collection's index document |

Each method accepts one 32-byte hexadecimal reference with an optional `0x`
prefix. The result is the response body as a `0x`-prefixed hexadecimal string.
The plugin accepts up to 1 MiB per response and four concurrent downloads.
Each download has a 15-second deadline, including reading the response body.

```sh
curl http://127.0.0.1:8545 \
  -H 'Content-Type: application/json' \
  --data '{"jsonrpc":"2.0","id":1,"method":"swarm_getFile","params":["22cbb9cedca08ca8d50b0319a32016174ceb8fbaa452ca5f0a77b804109baa00"]}'
```

| Error code | Meaning |
| --- | --- |
| `-32602` | Invalid reference |
| `-32001` | Bee returned HTTP 404 |
| `-32002` | Bee configuration, connection, or HTTP failure |
| `-32005` | Size or concurrency limit |
| `-32016` | Download deadline exceeded |

Run the HTTP integration checks against a published client with Python 3:

```sh
python test-swarm.py /path/to/publish/Nethermind.LightClient.dll
```

The checks use loopback fixtures to exercise Bee responses and RPC dispatch
before Ethereum sync.
