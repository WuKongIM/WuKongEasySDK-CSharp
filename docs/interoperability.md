# C# / JavaScript interoperability

`scripts/interop.py` runs two independent SDK processes against a real, owned
single-node cluster. The TCP relay forwards bytes unchanged and injects faults
by closing sockets. The fixture uses public SDK APIs and enables Token
authentication with 256 hash slots.

## Reviewed dependency baseline

| Component | Version / source |
| --- | --- |
| Released C# consumer | Public NuGet `WuKongEasySDK` **1.0.0**, restored into an empty package cache |
| Candidate C# consumer | Project reference to the checkout under test |
| JavaScript consumer | npm `easyjssdk` **2.0.4** |
| JavaScript transport | Node **24**, explicitly using npm `ws` **8.21.3** |
| WuKongIM | `v3.0.0-beta.9`, commit `734166e0ec30fc0f6f10fef6f6d1889d079ab636` |

The C# release source is `02ea7d60cd94feef1996f41bca35ffc3b8e18ea6`.
`tests/interop/pins.json` records the baseline; the npm lockfile also pins
transitive dependencies and their integrity hashes. The fixture verifies the
resolved NuGet version and dependency type, JS version, and the server binary's
clean Git revision. Updating a baseline requires reviewing these pins and
running both lanes again.

## Scenarios

1. Bidirectional Chinese/emoji messages and nested custom JSON payloads. Verify
   exact string message IDs above JavaScript's safe integer range, matching
   SENDACK/RECV sequence numbers, successful reason code `1`, sender UID, and
   unchanged JSON types and values. This server omits `clientMsgNo` in SENDACK;
   RECV must preserve the supplied correlation value.
2. Both SDKs reject an invalid Token through an authentication/RPC rejection,
   remain disconnected, and make no retry during the observation window.
3. Both established connections lose the network. Keep the fault active long
   enough for a retry to fail, restore the relay, and require automatic
   reconnection followed by bidirectional delivery.
4. Kill the owned server process, restart with the same configuration and data,
   and require automatic reconnection and bidirectional delivery.
5. Explicitly disconnect both clients. Observe for 3.2 seconds with no connection
   attempts, reject offline SEND, then explicitly reconnect and exchange messages.

The two lanes each exchange ten messages across five scenario groups. They are
separate from the initial source smoke and the NuGet installation receipt in
[validation records](validation.md).

## Reproduce

Prerequisites: Go compatible with the pinned server's `go.mod` (toolchain
`go1.25.11`), .NET SDK 8, Node 24/npm, Python 3.11 or later, and Git. Run each lane
sequentially in a checkout; they share the consumer's build output directory.

Build the server in its own clean clone. A nested Git worktree can make Go stamp
the outer repository's revision into the binary; the fixture rejects that stamp.

```bash
git clone https://github.com/WuKongIM/WuKongIM.git /tmp/wukong-interop-server
git -C /tmp/wukong-interop-server checkout 734166e0ec30fc0f6f10fef6f6d1889d079ab636
cd /tmp/wukong-interop-server
GOWORK=off go build -o /tmp/wukongim-interop ./cmd/wukongim
go version -m /tmp/wukongim-interop

cd /absolute/path/to/WuKongEasySDK-CSharp
WUKONGIM_BINARY=/tmp/wukongim-interop python3 scripts/interop.py
WUKONGIM_BINARY=/tmp/wukongim-interop python3 scripts/interop.py --candidate
```

Optional `DOTNET` and `NODE` environment variables select executable paths.
The default lane uses only nuget.org, with an exact `PackageReference`; the
`--candidate` lane deliberately uses the checkout's `ProjectReference`.
The fixture installs locked npm dependencies, creates temporary credentials and
storage, and cleans up its own clients, proxies, and server even on failure.
The scenario run has a 150-second deadline in addition to bounded setup and
cleanup waits. Fast assertion tests run separately:

```bash
python3 -m unittest discover -s scripts -p 'test_interop.py'
```

## CI and evidence

[CSharp and JS interoperability](https://github.com/WuKongIM/WuKongEasySDK-CSharp/actions/workflows/interop.yml)
runs both lanes on Linux for pull requests and pushes to `main`; it can also be
dispatched manually. Each job builds the exact server checkout and uploads only
`artifacts/interop-released.json` or `artifacts/interop-candidate.json`.
Reports contain status, scenario results, dependency pins, harness revision and
dirty state, server binary SHA-256, connection/event counts, and a bounded failure
category. They exclude raw server/client logs, credentials, and payloads.
Use the exact workflow run and its artifacts as the committed CI receipt.
The ordinary CI separately builds and tests C# on Windows, Linux, and macOS.

These jobs protect changes to this SDK against the pinned baseline. They do not
automatically follow new server or JS releases.

## Scope and observed limitation

Local macOS runs passed both lanes on 2026-09-08 with .NET SDK `8.0.424`, Node
`24.3.0`, and the versions above. During transport comparison, Node 24.3.0's
native WebSocket stalled after a failed JS reconnect handshake in this fixture.
The same scenarios passed with the explicitly selected `ws 8.21.3` constructor;
the precise native-transport cause remains unverified. No JS SDK lifecycle
methods or state are patched by this test.

The verified JS scope is Node with `ws`, not native Node WebSocket or browsers.
This short, loopback test does not establish WSS/proxy compatibility, Unity or
mobile support, offline synchronization, server-originated custom events,
multi-node behavior, capacity, or long-duration stability.
