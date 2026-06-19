# Issue #1283: LendingService の巨大メソッド責務分割 設計書

作成日: 2026-04-19
対象 Issue: [#1283](https://github.com/kuwayamamasayuki/ICCardManager/issues/1283)
対象ファイル: `ICCardManager/src/ICCardManager/Services/LendingService.cs`

## 背景と問題

`LendingService.cs`（1222 行）の 2 つの主要メソッドが複数責務を抱え込んでおり、部分的なユニットテストが書きにくい。

- `LendAsync()`: 121 行（ロック / 検証 / 残高 fallback / DB 更新 / 状態記録 / エラー変換）
- `ReturnAsync()`: 182 行（ロック / 検証 / 履歴フィルタ / DB 更新 / 残高解決 / 警告判定 / 状態記録 / エラー変換）
- `CreateUsageLedgersAsync()`: 380 行（本 Issue の対象外。別 Issue で対応）

## スコープ

### 含む

- `LendAsync()` / `ReturnAsync()` の責務を private ヘルパーメソッドへ抽出
- 抽出したヘルパーの単体テスト追加
- public API は一切変更しない（シグネチャ・戻り値・例外・副作用を維持）

### 含まない

- `CreateUsageLedgersAsync()` のさらなる分割（別 Issue とする）
- 動作仕様の変更
- ログメッセージ・エラーメッセージの文言変更

## 抽出するヘルパーメソッド

| 新メソッド | 責務 | 元の位置 | 推定行数 |
|---------|------|--------|---------|
| `ValidateLendPreconditionsAsync(staffIdm, cardIdm)` | カード存在・未貸出・職員存在の検証。タプル `(Card?, Staff?, string? ErrorMessage)` を返す | LendAsync L315-336 | ~25 |
| `ResolveInitialBalanceAsync(cardIdm, balance)` | `balance=null` 時に直近 ledger から残高補完（Issue #656） | LendAsync L340-352 | ~15 |
| `InsertLendLedgerAsync(cardIdm, staffIdm, staffName, balance, now)` | トランザクション内で ledger 挿入 + カード is_lent 更新 | LendAsync L356-390 | ~35 |
| `ValidateReturnPreconditionsAsync(staffIdm, cardIdm)` | カード存在・貸出中・職員存在の検証 | ReturnAsync L461-482 | ~25 |
| `ResolveLentRecordAsync(cardIdm)` | 貸出レコード取得 + null チェック。タプル `(Ledger?, string? ErrorMessage)` | ReturnAsync L484-490 | ~10 |
| `FilterUsageSinceLent(detailList, lentRecord, now)` | 貸出日の 7 日前以降の履歴を抽出（貸出タッチ忘れ対応） | ReturnAsync L500-506 | ~15 |
| `ResolveReturnBalanceAsync(detailList, createdLedgers, cardIdm)` | 残高解決カスケード（カード → ledger → DB） | ReturnAsync L560-587 | ~30 |
| `CalculateBalanceWarningAsync(balance)` | `AppSettings.WarningBalance` 取得 + 判定 | ReturnAsync L590-592 | ~10 |

抽出後の `LendAsync` / `ReturnAsync` はそれぞれ ~50-60 行の orchestration 層になる。

## 設計判断

### なぜ private ヘルパー（静的 util クラスや別サービスではない）

- メソッド群は `_dbContext`, `_cardRepository`, `_staffRepository`, `_ledgerRepository`, `_settingsRepository`, `_logger` などインスタンスフィールドを多用するため、別クラスへ移すと DI 構築のコストが上がる
- 既に静的ユーティリティへ切り出し可能なロジック（履歴解析）は `LendingHistoryAnalyzer` へ移管済み
- 今回は「巨大メソッドの可読性改善」が目的であり、責務の集約先を変えるリファクタではない

### なぜ ValidateXxx は「例外スロー」ではなくタプル返却

- 既存の `LendAsync`/`ReturnAsync` はキャッチ節でユーザー向けメッセージへ変換するため、ビジネスバリデーション層では例外を投げないのが自然
- タプル `(Card?, Staff?, string? ErrorMessage)` で "ErrorMessage 非 null なら失敗" と表現し、呼び出し側の早期 return をシンプルに保つ

### なぜ InsertLendLedgerAsync は transaction を内包する

- `_dbContext.ExecuteWithRetryAsync` と `BeginTransactionAsync` は「ひと塊の DB 書込み」として常にセットで使われており、途中で分断するとリトライ境界が壊れる
- LendAsync 本体からは「貸出レコード挿入という原子的操作」として見えればよい

## テスト戦略

### ベースライン保全

既存のテストファイル群（合計 ~5800 行）が全件 pass することをもって振る舞い不変性を担保する。

- `LendingServiceTests.cs` (4326 行)
- `LendingServiceEdgeCaseTests.cs` (140 行)
- `LendingServiceErrorMessageTests.cs` (75 行)
- `LendingServiceInsufficientBalanceTests.cs` (303 行)
- `LendingServiceRetouchTimeoutTests.cs` (392 行)

### 新規テスト（抽出ヘルパー向け）

private なので `InternalsVisibleTo`（既設）を利用し、必要に応じてヘルパーを `internal` に昇格させる。各ヘルパーに対し 3-5 ケースの単体テスト追加:

- `ValidateLendPreconditionsAsync`: カード未登録 / 貸出中 / 職員未登録 / 正常
- `ValidateReturnPreconditionsAsync`: カード未登録 / 未貸出 / 職員未登録 / 正常
- `ResolveLentRecordAsync`: 貸出レコードなし / 正常
- `ResolveInitialBalanceAsync`: balance=null で直近 ledger あり / balance=null で ledger なし / balance 指定あり
- `FilterUsageSinceLent`: 貸出前の履歴除外 / 貸出日と同日 / 7 日前境界値
- `ResolveReturnBalanceAsync`: カード残高優先 / ledger fallback / DB fallback
- `CalculateBalanceWarningAsync`: 閾値未満 / 閾値同値 / 閾値超過

テストファイル名: `LendingServiceHelperTests.cs`（新規）

## 進め方

1. **baseline**: 現状のテストが全件 pass することを確認
2. **抽出**: ヘルパーメソッドを 1 つずつ抽出し、その都度 `dotnet test` を実行
3. **単体テスト追加**: 抽出完了後、新規ヘルパー向けテストを追加
4. **最終確認**: build + test を実行し、PR 作成

## リスクと対策

| リスク | 対策 |
|-------|-----|
| トランザクション境界を壊して atomic 性が崩れる | transaction 内のコードは `InsertLendLedgerAsync` の中に閉じ込める。transaction を跨ぐヘルパーは作らない |
| ログ順序が変わることで既存テストが falsely fail する | ログメッセージの文言・順序を変更しない。抽出は呼び出し位置のみ変える |
| lock 取得 / release の流れが分断される | `try-finally` 構造は残し、内側だけを抽出する |

## 非対象（別 Issue 推奨）

- `CreateUsageLedgersAsync` (380 行) の細分化 — 残高不足パターン処理 / チャージ処理 / ポイント還元処理 / 利用グループ処理を segment type ごとに分割可能
