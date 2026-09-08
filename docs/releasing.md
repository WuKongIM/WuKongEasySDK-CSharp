# NuGet releases

The package ID is `WuKongEasySDK`, owned and released from
`WuKongIM/WuKongEasySDK-CSharp`. The current candidate is `1.0.0`.
A prepared package, Git tag, or successful validation-only run does not mean
the version is available on nuget.org. The public installation check is the
publication gate. Never overwrite a published version with different bytes.

## One-time account setup

Sign into the NuGet account that should publish the package. In
[Trusted Publishing](https://www.nuget.org/account/trustedpublishing), create
a GitHub policy with these exact values:

| Field | Value |
| --- | --- |
| Repository owner | `WuKongIM` |
| Repository | `WuKongEasySDK-CSharp` |
| Workflow file | `release.yml` |
| Environment | `nuget` |
| Package glob | `WuKongEasySDK` |
| Scopes | Push new packages and new package versions |

Select the intended NuGet package owner (the WuKongIM organization if available).
Set repository Actions variable `NUGET_USER` to the policy creator's NuGet profile
username, not an email address or organization name. No long-lived API key is
stored. The workflow obtains a temporary key with `NuGet/login`.
The NuGet owner and username must come from the actual signed-in account.

See Microsoft's [Trusted Publishing setup](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
for policy ownership and activation behavior. A public 404 does not guarantee
that an ID is unreserved; NuGet's first push is authoritative for availability.

## Prepare and validate

1. Set the stable version in `src/WuKongEasySDK/WuKongEasySDK.csproj` and move
   applicable changelog entries into exactly one `## [x.y.z]` section.
2. Merge the reviewed release source into `main`.
3. Run **Release NuGet** on `main` with **publish = false**. It builds, runs
   protocol/lifecycle tests, packs, and installs the package in a fresh consumer
   on Windows, Linux, and macOS. The Linux package is retained as an artifact.

The same package check can run locally after `dotnet build -c Release`:

```bash
python3 scripts/release.py metadata
dotnet pack src/WuKongEasySDK -c Release --no-build -p:RepositoryCommit="$(git rev-parse HEAD)" -o artifacts
python3 scripts/release.py local --commit "$(git rev-parse HEAD)"
```

## Publish and verify

Run **Release NuGet** on `main` with **publish = true**. Alternatively, push
`vX.Y.Z` at an exact commit already on `main`; its version must match the project.
The run validates all three platforms before requesting NuGet credentials.
It publishes the exact tested Linux artifact, waits up to ten minutes for the
public version, compares every package entry except NuGet's added signature,
and restores, compiles, loads, and disposes the client in an isolated consumer
using only nuget.org and an empty package cache. Only then does it create the
GitHub Release and attach that same package.

After success, update the SDK README and bilingual `docs-site` installation
instructions to the exact public version and source commit. Verify both public
documentation pages after their normal deployment pipeline finishes.

If a push times out or verification fails, inspect nuget.org and the failing
step before retrying. Re-running the same source may skip an existing package,
but the comparison must still pass. If another source owns that version, choose
a new version; do not delete or unlist a public package to replace its contents.
