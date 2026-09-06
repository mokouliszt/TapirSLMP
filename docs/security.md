# Security and failure semantics

[日本語](security.ja.md)

## Trust boundaries

- The sealer accepts unauthenticated SLMP by design. Bind it to loopback or a
  tightly controlled client network and protect it with a host firewall.
- The freezer is the durable security boundary. Put TLS in front of it, keep it
  off the public Internet, and restrict ingress to known sealer/liberator hosts.
- The liberator can reach the PLC. Run it with minimum OS privileges and a route
  map containing only intended targets.

The application requires separate URL-safe bearer tokens of 32-512 characters.
It compares hashed tokens in fixed time, disables HTTP redirects in clients,
and refuses non-HTTPS freezer URLs except an explicitly enabled loopback test.
Tokens are credentials: rotate them, inject them from a secret manager, and
never put them in URLs or logs.

## Command safety

The allow-list, TTL clamping, and lease validation live in `JobAdmissionPolicy`
(`TapirSLMP.Contracts`), below both job gateways rather than inside the HTTP endpoints.
An embedded, single-process deployment using `InProcessJobGateway` therefore enforces the
same rules as one going through the freezer's HTTP API. Bearer-token authentication is the
exception: it applies to the HTTP path only, since the in-process path crosses no network
boundary. Treat the in-process configuration as a single trust zone.

Writes are disabled by default. Only commands `0x0101`, `0x0401`, `0x0403`, and
`0x0406` are classified read-only. All other and unknown commands are treated as
state-changing. This is intentionally conservative and is not a substitute for
PLC-side access control.

Enabling state-changing commands permits more than device writes; it may permit
remote run/stop/reset or vendor-specific operations contained in raw frames.
Use a dedicated PLC account/profile where available and test the exact command
set offline first.

## Route addressing

A client cannot name a PLC. It names nothing at all: the sealer stamps every job with the
route key configured for the listener the client connected to, and the liberator resolves
that key to a host and port from its own route map. A sealer reachable by an untrusted
client therefore cannot be turned into a path to an arbitrary device on the PLC network.

`Freezer:KnownRouteKeys` optionally restricts which route keys the freezer will accept at
all. Its purpose is catching configuration mistakes early rather than defence: the route
map on the liberator is what actually bounds reachable targets.

## Ambiguous execution

A network failure has three materially different points:

| Failure point | Stored outcome | Automatic retry |
| --- | --- | --- |
| Before PLC connection/send boundary | `Queued` or `Failed` | safe when policy allows |
| After send boundary, read-only request | `Queued` or `Failed` | allowed |
| After send boundary, state-changing request | `OutcomeUnknown` | never |

The `Executing` transition happens after target resolution/connection and just
before send. A send call can partially succeed before reporting an error, so
every state-changing failure from that point is ambiguous.

Likewise, an SLMP error returned by the sealer after its wait deadline does not
prove that a state-changing job never reached the PLC. A client that reconnects
and sends the same raw write is a new logical request with a new idempotency ID.
Disable automatic write retries at the SLMP client and inspect the freezer/PLC
state before issuing another write.

## Data protection

Request and response frames may contain operational values, passwords, labels,
or proprietary process data. Database files, backups, logs, and observability
systems should be treated as sensitive. TapirSLMP does not log raw frames or
tokens by default. Configure database encryption, disk encryption, retention,
and access control appropriate to your deployment.

## Operational checklist

- Use HTTPS and reject client-controlled forwarding headers at the edge.
- Bind the Docker example only to loopback until a trusted proxy is configured.
- Keep SQLite on a persistent local filesystem; do not use network filesystems
  without validating SQLite locking semantics.
- Back up the freezer and test restore procedures.
- Alert on `OutcomeUnknown`, repeated lease expiry, authentication failures, and
  queue age.
- Synchronize system clocks; TTL and lease decisions use UTC timestamps.
- Pin dependency versions and review updates before deployment.
