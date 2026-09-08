# Initial source validation

Date: 2026-09-08. These are source and locally packed artifact results, not a
nuget.org release receipt.

- Protocol reference: `WuKongIM/WuKongEasySDK-JS` `v2.0.4`, commit
  `9c03c98c725982fac224cd1d3b52456eae983975`.
- Real server: `WuKongIM/WuKongIM`, commit
  `132e46209d98fa0425cc0f88e7a97080cdad044d`.
- Client source: the SDK implementation committed with this initial record;
  run `git rev-parse HEAD` when reproducing and retain that full revision.
- Local runtime: macOS arm64, .NET SDK `8.0.424`, runtime `8.0.30`.
- Release build: passed with zero warnings and errors, including the console
  example and real-server smoke executable.
- `dotnet test -c Release`: 35 protocol/lifecycle tests passed. They use a real
  loopback WebSocket peer to cover wire encoding, fragmented reception, Unicode,
  exact integer IDs/sequences, out-of-order concurrent responses, rejection and
  error codes, cancellation, deadlines, reconnect exhaustion, kicked sessions,
  heartbeat response variants/timeouts, pending-work cleanup, bounded queues,
  and redacted error/model/log output.
- `dotnet pack`: produced `WuKongEasySDK.1.0.0.nupkg` in the local artifacts feed.
- A separate consumer restores that local `.nupkg`, compiles against its public
  API, instantiates the client, and releases it successfully.
- `scripts/smoke.py`: passed against one owned single-node cluster with 256 hash
  slots and Token authentication enabled. It proves authenticated Alice/Bob
  Unicode messaging in both directions, matching SENDACK/RECV identifiers and
  sequence numbers, manual disconnect/reconnect, correlated heartbeat,
  invalid-token rejection, and cleanup of the owned client/server processes.

The mock-peer suite covers custom events and failure/reconnect behavior; the
real-server smoke does not claim server-originated custom event execution.
WSS proxies, physical mobile devices, Unity, browser WebAssembly, offline sync,
capacity, multi-node deployment, and long-duration stability remain outside
these results. CI checks Windows/Linux/macOS independently; consult its exact
commit run before describing those platforms as runtime-verified.

Reproduction:

```bash
dotnet build -c Release
dotnet test -c Release
dotnet pack src/WuKongEasySDK -c Release -o artifacts
WUKONGIM_BINARY=/absolute/path/to/wukongim python3 scripts/smoke.py
```
