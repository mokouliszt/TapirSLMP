# Freezer HTTP API

[English](http-api.md)

APIはUTF-8 JSON、camelCase property、文字列enum、byte配列のstandard base64を
使用します。version 1のpathは`/v1`です。

`GET /healthz`以外には`Authorization: Bearer TOKEN`が必要です。sealer tokenは
jobの作成・参照、liberator tokenはlease取得・更新にだけ使え、相互利用できません。

| Method・path | Credential | 成功時 |
| --- | --- | --- |
| `GET /healthz` | 不要 | `200` |
| `POST /v1/jobs` | sealer | `201`、重複再送は`200` |
| `GET /v1/jobs/{id}` | sealer | `200` |
| `POST /v1/leases/acquire` | liberator | `200`、空なら`204` |
| `POST /v1/jobs/{id}/executing` | liberator | `204` |
| `POST /v1/jobs/{id}/complete` | liberator | `204` |
| `POST /v1/jobs/{id}/fail` | liberator | `204` |

`404`はjobなし、`409`はlease期限切れまたは不正な状態遷移です。enqueue時の`403`は
通常、状態変更commandが無効になっていることを示します。

## Job登録

```json
{
  "clientRequestId": "request-01991a2b3c4d",
  "routeKey": "plc1",
  "transport": "Tcp",
  "requestFrame": "UAAA//8DAA4AEAABBAIAZAAAAKgAAgA=",
  "expiresAt": "2026-09-05T12:01:00Z"
}
```

`expiresAt`は省略可能です。要求には正しいバイナリ3E/4E frameをちょうど1つだけ
含めます。応答の`id`を`GET /v1/jobs/{id}`で参照し、`Completed`、`Failed`、
`Expired`、`OutcomeUnknown`のいずれかでpollを終了します。
終端jobは`Freezer__TerminalRetentionHours`経過後に削除されます。

`clientRequestId`は論理要求ごとに一意な値が必須です。HTTP結果が不明な場合は同じ
内容・同じIDで再送します。初回は`201`、重複再送は`200`と`replayed: true`を返し、
異なる内容でのID再利用は`409`です。重複排除は終端jobの保持期間中に有効です。

## Lease取得

```json
{
  "workerId": "liberator-01991a2b-0",
  "routeKeys": ["plc1", "plc2"],
  "leaseSeconds": 30
}
```

応答は`leaseToken`、`leaseUntil`、job全体を含みます。lease tokenをlogへ出さないで
ください。PLCへ最初のbyteを送信できる直前に`/executing`を呼び出します。

`/complete`には同じworker/leaseと`responseFrame`を、`/fail`には
`Retry`・`Failed`・`OutcomeUnknown`のdispositionとerrorを渡します。実行中の
状態変更commandに`Retry`を指定しても、storeが安全側に上書きして
`OutcomeUnknown`にします。
