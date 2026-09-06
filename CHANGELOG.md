# Changelog

All notable changes will be documented in this file.

From 1.0.0 the public API follows semantic versioning.

## [1.0.0]

### Added

- Initial TapirSLMP sealer, freezer, and liberator implementation.
- Binary SLMP 3E/4E raw-frame validation and correlation.
- SQLite and PostgreSQL job stores.
- Conservative write protection and outcome-unknown handling.
- Docker, CI, unit tests, and compatibility test assets.
- `IJobSubmissionGateway` and `IJobWorkerGateway`, with an HTTP implementation in
  `TapirSLMP.Client` and an in-process implementation in `TapirSLMP.Storage`, so a
  sealer or liberator can run either across a network boundary or in the same
  process as the job store.
- `JobAdmissionPolicy` in `TapirSLMP.Contracts`, holding frame validation, the
  read-only command allow-list, TTL clamping, lease-identity checks, and response
  correlation. Both gateways run through it, so the in-process path enforces the
  same safety defaults as the HTTP endpoints.
- Dependency-injection entry points: `AddTapirSealer`, `AddTapirSealerRelay`,
  `AddTapirLiberator`, `AddTapirFreezerClient`, `AddTapirFreezer`, and
  `MapTapirFreezer`.
- `ISlmpRelay`, so an application can relay frames it generates itself without
  routing them through a local socket.
- `TapirSLMP` metapackage covering the protocol, contracts, client, sealer, and
  liberator. Freezer hosting and the storage providers stay separate so that
  applications which do not host the freezer need neither the ASP.NET Core shared
  framework nor a database driver.
- Reference host applications for four deployment shapes under `tests/e2e/hosts/`.
- Package metadata for NuGet: repository and project URLs, tags, README, SourceLink,
  and symbol packages.
- `SealerOptions.Listeners`, so one sealer process can expose one port per PLC. Clients
  select a PLC by choosing the port they connect to. The flat `Port`, `ListenAddress`,
  `ListenMode`, and `RouteKey` configuration keys still configure a single listener.
- `JobPolicyOptions.KnownRouteKeys`, bound from `Freezer:KnownRouteKeys`. When populated,
  a submission or a lease request naming a route key the freezer does not serve is
  rejected with `unknown_route_key` instead of stalling until the job's TTL elapses.
  Leaving it empty preserves the previous permissive behaviour.

### Changed

- The sealer, liberator, and freezer are libraries rather than service executables.
  Host them from your own application; see the reference hosts for minimal examples.
- `TapirSLMP.FreezerClient` is renamed `TapirSLMP.Client`, removing the namespace and
  type name collision.
- `TapirSLMP.Freezer` is renamed `TapirSLMP.Freezer.Hosting`.
- `FreezerApiException` derives from `JobGatewayException`; retry decisions use its
  `IsTransient` property instead of inspecting HTTP status codes.
- Configuration section names are parameters on `FromConfiguration` and the
  `AddTapir*` overloads, so an embedding application can avoid key collisions.
- A liberator that starts before its freezer logs a warning while waiting instead of
  an error on every poll, and logs once at information level when it first connects.
- `ISlmpRelay.RelayAsync` takes the route key as a parameter, so one relay can serve
  several PLCs. Callers that drove the relay directly must pass the key they previously
  set through `SealerOptions.RouteKey`.
- A liberator backs off for fifteen seconds after a non-transient gateway rejection, such
  as an unknown route key or a bad token, rather than retrying at the poll interval.

### Fixed

- A PLC response timeout on the TCP path recorded "The operation was canceled."
  instead of naming the route. It now raises `Timed out exchanging SLMP with PLC
  route '<key>'.`, matching the UDP path. Retry and `OutcomeUnknown` behaviour is
  unchanged.
- Missing `using System.Text.RegularExpressions;` in the freezer and missing
  `using Xunit;` in every test file prevented the solution from compiling. The
  worker-id validator no longer uses a regular expression at all.

### Removed

- `Action<TOptions>` overloads of the `AddTapir*` methods. `SealerOptions`,
  `LiberatorOptions`, and `FreezerOptions` use `required` and `init` members, which a
  configuring lambda cannot set. `Validate()` is public instead, so options built with
  an object initializer can still be checked.
- `Microsoft.Extensions.Hosting.WindowsServices` and `.Systemd` references. Service
  registration belongs to the hosting application.
