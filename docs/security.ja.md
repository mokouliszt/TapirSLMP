# Securityと障害時動作

[English](security.md)

## Trust boundary

- sealerは設計上、認証なしのSLMPを受けます。loopbackまたは厳しく制御したclient
  networkだけにbindし、host firewallで保護してください。
- freezerが永続化と認証の境界です。TLSの背後に置き、public Internetへ直接公開
  せず、既知のsealer/liberator hostだけを許可します。
- liberatorはPLCへ到達できます。最小OS権限で実行し、意図したPLCだけをroute mapへ
  設定してください。要求内のhost/portへ転送する機能はありません。

sealerとliberatorには別々の32-512文字URL-safe bearer tokenが必要です。tokenは
hash化して固定時間比較し、HTTP redirectは無効です。平文HTTPは明示的に許可した
loopback試験だけで使用できます。tokenはsecret managerから注入し、URLやlogへ
含めないでください。

## Command safety

書き込み許可リスト、TTLクランプ、lease検証は `JobAdmissionPolicy`（`TapirSLMP.Contracts`）に
あり、HTTP endpointの内側ではなく両job gatewayの下位に位置します。したがって
`InProcessJobGateway` を使う1プロセス構成でも、freezerのHTTP APIを経由する場合と同一の
規則が適用されます。例外はbearer token認証で、これはHTTP経路にのみ適用されます。in-process
経路はネットワーク境界をまたがないためです。in-process構成は単一の信頼ゾーンとして扱ってください。

状態変更は既定で無効です。`0x0101`、`0x0401`、`0x0403`、`0x0406`だけを読出しとし、
それ以外と未知commandを状態変更扱いにします。有効化するとdevice writeだけでなく、
remote run/stop/resetやvendor固有操作も通る可能性があります。PLC側access controlの
代替にはなりません。

## routeの指定

クライアントはPLCを指定できません。そもそも何も指定しません。sealerは接続先listenerに設定された
route keyを全ジョブに刻み、liberatorが自身のroute mapでhost/portへ解決します。したがって、
信頼できないクライアントから到達可能なsealerがあっても、PLCネットワーク上の任意機器への
経路にはなりません。

`Freezer:KnownRouteKeys` を設定すると、freezerが受け付けるroute keyを限定できます。ただし
これは設定ミスを早期に検出するためのものであり、防御機構ではありません。到達可能な対象を
実際に限定しているのはliberator側のroute mapです。

## 曖昧な実行結果

| Failure位置 | 保存結果 | 自動retry |
| --- | --- | --- |
| PLC接続・送信境界より前 | `Queued`または`Failed` | policy範囲で可能 |
| 送信境界より後の読出し | `Queued`または`Failed` | 可能 |
| 送信境界より後の状態変更 | `OutcomeUnknown` | 不可 |

`Executing`への変更は名前解決・接続の後、送信直前です。send APIが一部送信後にerrorを
返す可能性があるため、それ以降の状態変更failureはすべて曖昧とみなします。

sealerの待機期限後にSLMP errorが返っても、状態変更jobがPLCへ届かなかった証明には
なりません。SLMP clientが再接続して同じwriteを送ると、別のidempotency IDを持つ
新規要求になります。client側のwrite自動retryを無効にし、freezerとPLCの状態を
確認してから次のwriteを発行してください。

## Data保護

伝文には設備値、password、label、機密process dataが含まれる場合があります。DB、
backup、log、observability systemも機密として扱ってください。TapirSLMPは既定で
生伝文とtokenをlogに出しません。暗号化、保持期間、access controlを環境に合わせて
設定してください。

本番前に、HTTPS、network分離、永続DB、backup/restore、時刻同期、token rotation、
`OutcomeUnknown`とlease期限切れのalertを確認してください。
