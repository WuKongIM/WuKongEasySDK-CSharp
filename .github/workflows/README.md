# Workflows

`ci.yml` automatically builds, tests, and packs this SDK on pushes and pull
requests. It has read-only repository permissions, uses hosted runners, and
does not publish NuGet packages, create releases, or provision infrastructure.

`interop.yml` runs the bounded real-process C#/JS fixture on pull requests and
pushes to `main`, with optional manual dispatch. Its released and candidate lanes
use a pinned real server on a hosted Linux runner; they create only temporary
loopback resources and upload sanitized result JSON. See
[`docs/interoperability.md`](../../docs/interoperability.md) for pins and scope.

All transports install the exact public npm `easyjssdk 2.0.5` package with an
empty fixture cache. The browser jobs also test native Node recovery. Chromium runs with normal TLS validation and an ephemeral NSS trust store;
C# uses a child-process CA bundle. Negative CA and hostname tests are required.
Reports record the npm tarball URL and integrity alongside the installed version.

Two additional `cluster` jobs exercise the approved public SDK boundaries against
three owned server processes using the exact npm package and native Node. They
cover cross-node person/group delivery, killed ingress nodes, explicit application
address replacement, and rejoin. No paid infrastructure is provisioned.
