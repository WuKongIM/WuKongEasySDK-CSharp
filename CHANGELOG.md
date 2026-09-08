# Changelog

## [Unreleased]

- Add real C#/JS interoperability CI for the public NuGet release and candidate source, covering Unicode/custom payloads, exact ACK/RECV correlation, invalid tokens, network recovery, server restart, and explicit disconnect.

## [1.0.0]

### Added

- Initial C# SDK source (`1.0.0`) targeting .NET 8 with no third-party runtime dependencies.
- WebSocket JSON-RPC authentication, online SEND/RECV and ACK, custom events, heartbeat, bounded retries, typed events, cancellation, and async cleanup.
- Bounded pending requests, event dispatch, and wire messages; sanitized opt-in diagnostics.
- Console chat, protocol/lifecycle tests, real-server smoke fixture, and bilingual documentation.
- NuGet trusted publishing with three-platform validation and isolated public package installation verification.

Registry availability is confirmed by the successful public installation step in the release workflow.
