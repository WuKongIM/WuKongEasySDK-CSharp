# C# / JavaScript interoperability

`scripts/interop.py` runs independent SDK processes against an owned, real
single-node cluster with Token authentication and 256 hash slots. Its TCP relay
forwards application bytes unchanged; WSS terminates TLS at the fixture proxy
before forwarding WebSocket traffic to the server. Network faults close sockets,
and server recovery kills and restarts the actual server process with its data.

## Reviewed matrix

Every transport tests both the public **NuGet WuKongEasySDK 1.0.0** package,
restored into an empty package cache, and an explicit **candidate source** project
reference. The public package source remains
`02ea7d60cd94feef1996f41bca35ffc3b8e18ea6`.

| Transport | JavaScript dependency | Scope |
| --- | --- | --- |
| `ws` | npm `easyjssdk 2.0.5`, `ws 8.21.3` | Node with the optional `ws` transport |
| `native` | npm `easyjssdk 2.0.5` | Node 24.3.0 native WebSocket, including the handshake repair |
| `browser-wss` | npm `easyjssdk 2.0.5` | Chromium from Playwright 1.62.1, HTTPS page and WSS, normal certificate validation |

All rows pin WuKongIM `v3.0.0-beta.9`, source
`734166e0ec30fc0f6f10fef6f6d1889d079ab636`. `tests/interop/pins.json` records
reviewed versions and source revisions. The npm lockfile pins Playwright, esbuild,
and other fixture dependencies and their integrity hashes. Browser reports record
the actual Chromium version, Playwright version, and secure-context state.

Every default lane installs npm `2.0.5` from the public registry with an empty
fixture cache and the checked-in lockfile. Reports record its tarball URL and
SHA-512 integrity. Version `2.0.4` predates the handshake fix. The historical
[source acceptance](https://github.com/WuKongIM/WuKongEasySDK-CSharp/actions/runs/34195913516)
used JS commit `5e5dfb727fb0ea08294939962ae799e998b7ca5c`; reports distinguish
`npm` and `source` dependency kinds. Review and rerun the full matrix when
changing a pin; CI does not silently follow upstream.

## Scenarios

1. Bidirectional Chinese/emoji and nested custom JSON payloads. Verify exact
   string message IDs above JavaScript's safe integer range, matching ACK/RECV
   sequence numbers, reason code `1`, sender UID, and unchanged JSON types and
   values. The pinned server omits `clientMsgNo` in SENDACK; RECV must retain it.
2. Invalid Token rejection through authentication/RPC errors, disconnected state,
   and no automatic retry during the observation window.
3. Drop both established connections, require a retry while the fault remains,
   restore the relay, and require automatic reconnection and bidirectional delivery.
4. Kill the server, restart with its existing config/storage, and require both
   clients to automatically reconnect and resume delivery.
5. Explicit disconnect with no connection attempts for 3.2 seconds, rejected
   offline SEND, and successful explicit reconnect and bidirectional delivery.

Each lane exchanges ten messages across these five groups. The browser/WSS lane
adds a sixth group before messaging: both C# and Chromium must reject an untrusted
CA and a mismatched hostname, with no decrypted client application bytes forwarded
to the WebSocket server. A TLS connection alone is not proof of certificate
acceptance; some clients finish TLS before rejecting its certificate.

## Certificate validation

The WSS fixture creates a one-day temporary CA and certificates for loopback
endpoints. The stock C# `ClientWebSocket` validates using a child-process
`SSL_CERT_FILE`; Chromium uses an isolated child home with an NSS trust database.
No machine trust store is changed. The browser opens a real HTTPS page before
constructing its unmodified native WebSocket. `ignoreHTTPSErrors` stays false;
there are no certificate-ignore switches or C# validation callbacks.

The positive exchange and both negative certificate controls must pass together.
This establishes validation for the fixture CA and loopback TLS proxy, not every
public CA, reverse proxy, TLS policy, or production network. Chromium's process
sandbox is disabled in this owned CI/container fixture; that switch does not
relax TLS validation and is not a recommendation for production browser hosting.

## Reproduce

Use Go compatible with the pinned server's `go.mod` (toolchain `go1.25.11`),
.NET SDK 8, Node **24.3.0**/npm, Python 3.11+, Git, and OpenSSL. Browser/WSS also
requires **Linux**, `certutil` (`libnss3-tools`), and Playwright's Chromium system
dependencies. Run lanes sequentially: they share consumer build output.

Build the server in its own clean clone. A nested Git worktree can cause Go to
stamp an outer repository's revision into the binary; the fixture rejects an
incorrect or dirty stamp.

```bash
git clone https://github.com/WuKongIM/WuKongIM.git /tmp/wukong-interop-server
git -C /tmp/wukong-interop-server checkout 734166e0ec30fc0f6f10fef6f6d1889d079ab636
cd /tmp/wukong-interop-server
GOWORK=off go build -o /tmp/wukongim-interop ./cmd/wukongim

cd /absolute/path/to/WuKongEasySDK-CSharp
npm ci --prefix tests/interop/js --ignore-scripts --no-audit --no-fund
# Linux: install libnss3-tools with your package manager first.
# This browser installation may install system packages; use an isolated host/container.
export PLAYWRIGHT_BROWSERS_PATH=/absolute/path/to/fixture-browser-cache
tests/interop/js/node_modules/.bin/playwright install --with-deps chromium
export WUKONGIM_BINARY=/tmp/wukongim-interop

python3 scripts/interop.py
python3 scripts/interop.py --transport native
python3 scripts/interop.py --transport browser-wss
# Repeat each command with --candidate to test the C# checkout instead of NuGet.
```

Optional `DOTNET` and `NODE` select executable paths. For historical source
reproduction only, `--javascript-source PATH` requires a clean checkout at the
recorded repair commit and reports it as source. Default CI needs no JS source
checkout. The script checks actual npm/NuGet resolution and dependency types,
and owns only temporary credentials, data, certificates, child clients,
proxies, and server processes. Setup has bounded timeouts; scenarios have a
180-second deadline followed by cleanup. Keys and raw logs are never uploaded.

## CI and receipts

[CSharp and JS interoperability](https://github.com/WuKongIM/WuKongEasySDK-CSharp/actions/workflows/interop.yml)
runs on pull requests and `main` pushes, with optional manual dispatch. Four
Linux jobs test released/candidate C# against public npm `2.0.5` with either `ws`
or both native and browser/WSS transports. Reports are:

- `artifacts/interop-{released,candidate}.json` for npm/WS.
- `artifacts/interop-{released,candidate}-native.json` for npm/native Node.
- `artifacts/interop-{released,candidate}-browser-wss.json` for npm/Chromium.

Reports contain status, pins, dependency kinds, actual harness commit and dirty
state, server binary SHA-256, runtime versions, certificate controls, scenario
results, connection/event counts, and bounded fixture failure categories. They
exclude credentials, payloads, certificates' private keys, and raw logs. Use the
exact workflow run and its six artifacts as the committed receipt. Ordinary C#
unit/build CI remains separate on Windows, Linux, and macOS.

The original WS result and package-publication receipts remain in
[validation records](validation.md). The former Node native reconnect limitation
was traced to an `error` event without a subsequent `close`; the source repair
settles that attempt and continues bounded retries. Its minimal TCP regression
and CI are documented in the
[JS repair](https://github.com/WuKongIM/WuKongEasySDK-JS/pull/10).

This short fixture does not establish Firefox/WebKit, physical devices, Unity,
browser WebAssembly for C#, offline synchronization, custom server events,
multi-node behavior, capacity, or long-duration stability.
