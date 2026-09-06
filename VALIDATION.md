# Validation status

Version: 1.0.0
Last verified: 2026-09-05

This snapshot has been built, unit tested, and exercised end to end against a
simulated PLC. It has **not** been run against physical hardware.

## Verified in this environment

Toolchain: .NET SDK 10.0.400, Python 3.12, PostgreSQL 16.15, Ubuntu 24.04.

- NuGet restore and C# compilation of the full solution in `Release`, with
  `TreatWarningsAsErrors=true` and zero warnings.
- 54 unit tests across `Protocol`, `Contracts`, `Client`, `Sealer`, `Freezer.Hosting`, and
  `Storage`, including the PostgreSQL integration tests (`TAPIRSLMP_TEST_POSTGRES`
  supplied against a live server).
- `tests/e2e/slmp_roundtrip.py`: raw 3E and 4E round-trips through
  sealer to freezer to liberator to a pinned VirtualMelsecR build.
- `tests/e2e/PlcCommSmoke`: round-trip through the third-party `PlcComm.Slmp`
  client at the CI-pinned commit.
- Additional protocol probes: bulk 960-word read and write, bit-unit access,
  random read `0x0403`, 4E serial echo, two requests on one TCP connection,
  verbatim relay of a PLC error end code (`0xC051`, byte-compared against a
  direct connection), rejection of malformed and length-mismatched frames, and
  eight concurrent clients without response crosstalk.
- HTTP contract: idempotent replay, conflicting replay, token separation between
  the sealer and liberator roles, and unknown-job handling.
- Transport matrix: TCP and UDP client listeners, and TCP and UDP PLC routes.
  UDP PLC coverage used a UDP-to-TCP shim in front of the TCP-only simulator.
- Failure semantics: an unresponsive PLC yields `OutcomeUnknown` for a
  state-changing job after one attempt and `Failed` for a read-only job after the
  configured maximum attempts; an unreachable PLC yields `Failed`; a job whose
  route key no liberator serves stays `Queued` until its TTL and then `Expired`.
- Read-only allow-list: `0x0401`, `0x0101`, `0x0403`, and `0x0406` pass while
  `0x1401`, `0x1402`, remote run/stop/reset, password unlock, and self-test are
  refused when `Freezer__AllowStateChangingCommands` is false. Verified both
  through the HTTP endpoints and through `InProcessJobGateway` with no HTTP layer.
- Multiple listeners: one sealer process exposing two ports against two simulators
  confirmed that each port reaches only its own PLC, verified by reading the target
  device directly from each simulator, and that both routes progress in parallel.
- Known route keys: with `Freezer__KnownRouteKeys` populated, a mistyped key is rejected
  with HTTP 400 `unknown_route_key` on both submission and lease acquisition, while an
  empty list leaves the previous behaviour unchanged.
- Both storage providers: the full end-to-end suite produced identical results on
  SQLite and PostgreSQL.
- Concurrency: two liberator processes (three workers) on one route completed 24
  jobs with one attempt each and no double execution.
- Packaging: `dotnet pack` produces ten packages and ten symbol packages. Consuming
  `TapirSLMP` from a clean console application resolves `Microsoft.NETCore.App`
  only; consuming `TapirSLMP.Freezer.Hosting` additionally resolves
  `Microsoft.AspNetCore.App`, as intended.

## Not verified

- Physical MELSEC hardware and GX Simulator3.
- Docker image builds. The `Dockerfile` base image tags were confirmed to exist and
  its `dotnet publish` step was run locally, but no container was built.
- TLS termination, long-running soak tests, and sustained high-rate polling.

## Notes

The optional `tests/e2e/PlcCommSmoke` project references externally checked-out
source through the `PlcCommSource` MSBuild property. CI supplies that checkout;
the external source is intentionally absent from this archive and from the
main solution. See `.github/workflows/ci.yml` for the pinned commits and setup.

PostgreSQL retains route rows after pruning terminal jobs so cleanup cannot
race with a concurrent enqueue that references the same route.
