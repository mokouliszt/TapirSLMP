# Freezer HTTP API

[日本語](http-api.ja.md)

The API uses JSON, UTF-8, camel-case property names, string enum values, and
standard base64 for byte arrays. Version 1 paths begin with `/v1`.

All endpoints except `GET /healthz` require `Authorization: Bearer TOKEN`.
Sealer credentials can create/read jobs. Liberator credentials can acquire and
mutate leases. Tokens are deliberately not interchangeable.

## Endpoints

| Method and path | Credential | Success |
| --- | --- | --- |
| `GET /healthz` | none | `200` |
| `POST /v1/jobs` | sealer | `201`, or `200` for an idempotent replay |
| `GET /v1/jobs/{id}` | sealer | `200` |
| `POST /v1/leases/acquire` | liberator | `200`, or `204` when empty |
| `POST /v1/jobs/{id}/executing` | liberator | `204` |
| `POST /v1/jobs/{id}/complete` | liberator | `204` |
| `POST /v1/jobs/{id}/fail` | liberator | `204` |

`404` means the job does not exist. `409` means a lease token is stale or the
requested state transition is no longer valid. `403` on enqueue normally means
the command was classified as state-changing while writes are disabled.

## Submit a job

```json
{
  "clientRequestId": "request-01991a2b3c4d",
  "routeKey": "plc1",
  "transport": "Tcp",
  "requestFrame": "UAAA//8DAA4AEAABBAIAZAAAAKgAAgA=",
  "expiresAt": "2026-09-05T12:01:00Z"
}
```

`expiresAt` is optional. The freezer applies its default TTL and rejects an
expiry beyond its configured maximum. A request must contain exactly one valid
binary 3E or 4E frame.

`clientRequestId` is required and must be unique for a logical request. If the
HTTP outcome is uncertain, resend identical data with the same ID. The first
submission returns `201`; a replay returns `200` and `"replayed": true`. Reuse
with different request data returns `409`. Deduplication lasts for the terminal
job retention period.

```json
{
  "id": "job_01991a2b3c4d7ef089abcdef01234567",
  "createdAt": "2026-09-05T12:00:00Z",
  "replayed": false
}
```

## Read a job

The response includes `status`, exact request and optional response frames,
command metadata, attempt count, timestamps, and terminal error text. Clients
should stop polling on `Completed`, `Failed`, `Expired`, or `OutcomeUnknown`.
Terminal jobs are deleted after `Freezer__TerminalRetentionHours`.

## Acquire a lease

```json
{
  "workerId": "liberator-01991a2b-0",
  "routeKeys": ["plc1", "plc2"],
  "leaseSeconds": 30
}
```

The response contains a secret, single-job `leaseToken`, `leaseUntil`, and a
complete job view. A worker must not log the lease token.

## Begin execution

Call immediately before the first byte can be sent to the PLC:

```json
{
  "workerId": "liberator-01991a2b-0",
  "leaseToken": "opaque-lease-token"
}
```

## Complete

```json
{
  "workerId": "liberator-01991a2b-0",
  "leaseToken": "opaque-lease-token",
  "responseFrame": "0AAA//8DAAQAAAA0Eg=="
}
```

The freezer validates the response format, length, route, and 4E serial before
committing it.

## Fail

```json
{
  "workerId": "liberator-01991a2b-0",
  "leaseToken": "opaque-lease-token",
  "disposition": "Retry",
  "error": "connection failed before send"
}
```

Disposition is `Retry`, `Failed`, or `OutcomeUnknown`. The store overrides an
unsafe `Retry`: an executing state-changing command always becomes
`OutcomeUnknown`.
