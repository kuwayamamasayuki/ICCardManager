# Issue #1458 — OperationLogger を ledger 操作と同一トランザクション化する設計

## 背景

Ledger 行の追加・編集・削除・統合・分割を行う際、現状は以下のように 2 つの独立したトランザクションが走っている。

1. `_ledgerRepository.UpdateAsync(ledger)` — Ledger 本体の更新（接続リース + 暗黙トランザクション + fsync）
2. `_operationLogger.LogLedgerUpdateAsync(beforeLedger, ledger)` — 内部で `_operationLogRepository.InsertAsync` を呼び、別の接続リース + 暗黙トランザクション + fsync

共有モード（SMB 上の SQLite）では fsync 1 回が数 ms〜数十 ms のラウンドトリップになるため、各操作で 1 RTT 分の遅延が積み増しされる。Issue #1458 はこの 2 RTT を 1 RTT に削減することを目的とする。

## 関連 Issue / 規約

- Issue #1458（本 Issue）
- Issue #1456 (`LedgerRepository.InsertDetailsAsync` バッチ化) — 同じ「同一トランザクション化」パターンの先例
- `.claude/rules/async-configureawait.md` — Service 層では `.ConfigureAwait(false)`、ViewModel 層では付けない
- `.claude/rules/business-logic.md` — 共有モードの journal_mode/busy_timeout 設定

## 調査結果（重要）

Issue 本文の認識と実コードに以下の差異がある。

| Issue 本文の記述 | 実コードの状況 |
|------------------|----------------|
| 「貸出 / 返却の度に `LogLedgerXxxAsync` を await している」 | `LendingService` は `OperationLogger` を**呼んでいない** |
| 「MainViewModel.cs:1603, 1657」 | これらは `ShowDialogAsync<LedgerRowEditDialog>` 呼出。`LogLedger*Async` の実行箇所ではない |
| 「LedgerRowEditViewModel.cs:681」 | 正確（`LogLedgerUpdateAsync` 呼出） |

実際に `LogLedger*Async` を呼んでいるのは以下 7 箱所:

1. `LedgerRowEditViewModel.cs:610` — `LogLedgerInsertAsync` (履歴の追加保存)
2. `LedgerRowEditViewModel.cs:681` — `LogLedgerUpdateAsync` (履歴の編集保存)
3. `MainViewModel.cs:1644` — `LogLedgerDeleteAsync` (履歴行の削除)
4. `MainViewModel.cs:1698` — `LogLedgerDeleteAsync` (編集フロー内の削除)
5. `LedgerDetailViewModel.cs:592` — `LogLedgerUpdateAsync` (詳細編集ダイアログ)
6. `LedgerMergeService.cs:306` — `LogLedgerMergeAsync` (履歴統合)
7. `LedgerSplitService.cs:148` — `LogLedgerSplitAsync` (履歴分割)

`DbContext` は SQLite 接続を単一インスタンス共有し `SemaphoreSlim(1,1)` で直列化している。このため Issue 本文の「選択肢 2: Channel ベースバックグラウンドキュー」の並行性メリットは構造的に得られない。「選択肢 1: 同一トランザクション相乗り」を採用する。

スコープは Ledger 関連 7 箱所に限定。Card/Staff/Backup/Restore/Import/Export の `Log*Async` 呼出は本 Issue では変更しない。

## アーキテクチャ

### 既存インフラの活用

- `LedgerRepository.InsertAsync(Ledger, SQLiteTransaction)` — 既存
- `LedgerRepository.UpdateAsync(Ledger, SQLiteTransaction)` — 既存
- `DbContext.BeginTransactionAsync()` — 既存（`using` で auto rollback、`Commit()` で確定）

### 新規追加 API

#### Repository 層

```csharp
// OperationLogRepository
public Task<int> InsertAsync(OperationLog log, SQLiteTransaction transaction);

// LedgerRepository
public Task<bool> DeleteAsync(int id, SQLiteTransaction transaction);
public Task<bool> MergeLedgersAsync(
    int targetLedgerId,
    IEnumerable<int> sourceLedgerIds,
    Ledger updatedTarget,
    SQLiteTransaction transaction);
```

既存パラメータレス版は新 API に委譲する形に統一する（二重実装ドリフト防止）。

#### Service 層 (OperationLogger)

```csharp
public Task LogLedgerInsertAsync(Ledger ledger, SQLiteTransaction transaction);
public Task LogLedgerUpdateAsync(Ledger before, Ledger after, SQLiteTransaction transaction);
public Task LogLedgerDeleteAsync(Ledger ledger, SQLiteTransaction transaction);
public Task LogLedgerMergeAsync(IReadOnlyList<Ledger> sources, Ledger merged, SQLiteTransaction transaction);
public Task LogLedgerSplitAsync(Ledger original, IReadOnlyList<Ledger> splits, SQLiteTransaction transaction);
```

既存の非 tx 版は他のフロー（Card/Staff 等）で使用中のためそのまま残す。

### Callsite 変換パターン

#### パターン A: 単一 Ledger 操作 + 単一ログ（5 箱所）

```csharp
// Before
var result = await _ledgerRepository.UpdateAsync(ledger);
if (result)
{
    await _operationLogger.LogLedgerUpdateAsync(beforeLedger, ledger);
}

// After
using var scope = await _dbContext.BeginTransactionAsync();
var result = await _ledgerRepository.UpdateAsync(ledger, scope.Transaction);
if (!result) { /* StatusMessage 設定して return */ }
await _operationLogger.LogLedgerUpdateAsync(beforeLedger, ledger, scope.Transaction);
scope.Commit();
```

該当箇所:
- `LedgerRowEditViewModel.cs:610` (Insert)
- `LedgerRowEditViewModel.cs:681` (Update)
- `MainViewModel.cs:1644` (Delete)
- `MainViewModel.cs:1698` (Delete)
- `LedgerDetailViewModel.cs:592` (Update)

ViewModel/Service 層の `.ConfigureAwait(false)` 規約は既存コードに合わせて付与する。

#### パターン B: 既存内部 txn を呼び出し側に巻き上げ（2 箱所）

`LedgerMergeService.cs:306` と `LedgerSplitService.cs:148`。

```csharp
// LedgerMergeService.MergeAsync (改修後の核心部)
using var scope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false);
var success = await _ledgerRepository.MergeLedgersAsync(target.Id, sourceIds, target, scope.Transaction).ConfigureAwait(false);
if (!success) return /* failure */;
await _operationLogger.LogLedgerMergeAsync(beforeLedgers, target, scope.Transaction).ConfigureAwait(false);
scope.Commit();
```

Split 側は既に `UpdateAsync(SQLiteTransaction)` / `InsertAsync(SQLiteTransaction)` を使う準備が整っているため、`SplitAsync` の最外周に `BeginTransactionAsync` を被せ、最後にログ挿入を同じ tx で行って commit する形に変更する。

## エラーハンドリング

| シナリオ | 現状（2 txn） | 変更後（1 txn） |
|----------|---------------|------------------|
| Ledger 成功 + log 成功 | 両方残る | 両方残る（commit） |
| Ledger 成功 + log 失敗 | Ledger だけ残り **監査ログ欠落** | `using` dispose で自動 rollback → **両方消える** |
| Ledger 失敗 | log なし | log なし（commit に到達しない） |

監査ログとデータが原子的にコミットされるため、性能改善の副次効果としてデータ整合性が向上する。

`using var scope = await _dbContext.BeginTransactionAsync()` は `Commit()` を呼ばずに dispose されると自動 rollback されるため、既存の try/catch 構造は変更不要。

## テスト戦略

### 単体テスト（新規）

1. **`OperationLogRepositoryTransactionTests`** — `InsertAsync(log, tx)` オーバーロード
   - 既存 `InsertAsync(log)` と等価な書き込みを検証
   - tx を rollback すると log 行が残らないこと（in-memory SQLite）

2. **`OperationLoggerTransactionTests`** — 5 つの `LogLedger*Async(..., tx)`
   - `Mock<IOperationLogRepository>` で `InsertAsync(It.IsAny<OperationLog>(), It.IsAny<SQLiteTransaction>())` の呼出を検証
   - 渡された tx が repository まで届くこと

3. **`LedgerLogAtomicityTests` (新規ファイル)** — Ledger op + log op の原子性
   - in-memory SQLite で実 txn を開き、UPDATE + log INSERT を同一 tx で実行 → 両方コミット
   - 意図的に rollback → 両方とも書き込まれていない
   - Ledger UPDATE 失敗時 → log も書き込まれない

### 既存テスト修正

- `LedgerMergeServiceTests` — `_ledgerRepository.MergeLedgersAsync` のモックを `(int, IEnumerable<int>, Ledger, SQLiteTransaction)` 形に更新（既存非 tx 版が tx 版に委譲する実装にして、テスト側は新 API のみ mock すれば動くようにする）
- `LedgerSplitServiceTests` — 同様

### 手動テスト依頼項目

単体テストでは確認できないため、後でユーザーに依頼する:

1. **ローカルモード機能確認**: 履歴行の追加/編集/削除/統合/分割を各 1 回実行し、UI とログ表示が正しいこと
2. **共有モード性能体感**: UNC パスを設定して同じ操作を実行し、保存ボタン押下→ダイアログクローズまでの体感速度が改善されたこと
3. **エラー復旧確認**: ネットワーク切断中に保存操作を試み、エラーで Ledger/log どちらも書き込まれていないこと（次回起動時に矛盾なし）

### ドキュメント同期

- `docs/design/07_テスト設計書.md` §1.1a 件数表に追加クラス数を反映
- `docs/design/07_テスト設計書.md` §8.1 テスト一覧に新規テスト追記
- `CHANGELOG.md` `### Unreleased` セクションに変更内容を追記

## 実装順序

1. **Repository 層 API 追加**
   - `OperationLogRepository.InsertAsync(OperationLog, SQLiteTransaction)`
   - `LedgerRepository.DeleteAsync(int, SQLiteTransaction)`
   - `LedgerRepository.MergeLedgersAsync(..., SQLiteTransaction)`
2. **Service 層 API 追加**
   - `OperationLogger` の 5 つの `LogLedger*Async(..., SQLiteTransaction)` オーバーロード
3. **テスト追加**
   - 上記 3 種の新規テストクラス
4. **Callsite 変換 (7 箱所)**
   - パターン A の 5 箱所 → パターン B の 2 箱所の順
5. **既存テスト修正**
   - `LedgerMergeServiceTests`, `LedgerSplitServiceTests`
6. **ドキュメント同期**
   - テスト設計書、CHANGELOG
7. **検証**
   - `dotnet build` 警告ゼロ
   - `dotnet test --configuration Release` で全件パス
   - テスト件数を §1.1a に反映

## リスクと対策

| リスク | 対策 |
|--------|------|
| `using var scope` の `Commit()` 忘れ → サイレント rollback でデータが消える | レビューチェック。テストで実 DB 書き込みを検証 |
| 既存非 tx 版を呼ぶ別箇所 (Card/Staff/Backup) の挙動変化 | 非 tx 版の実装を変えない方針。新オーバーロード追加のみ |
| `LedgerMergeService` の既存モックが破損 | 「非 tx 版 → tx 版に委譲」設計で、テスト側は片方の mock だけで動かす |
| txn 内で長時間処理が走り SMB ロック競合 | `SyncBusStopsFromSummaryAsync` 等の補助処理は txn 外のまま。今回は「Ledger 本体 + ログ」のみを atomically commit する |
| `LedgerSplitService.SplitAsync` の元コードは複数 Ledger を順次更新→挿入する。1 txn 化で commit 失敗時に部分書き込みが消える挙動変化 | これは整合性向上として歓迎。既存ロジックの一部失敗時の挙動を明示テストで固定 |

## スコープ外

- Card/Staff/Backup/Restore/Import/Export の `Log*Async` 呼出（別 Issue 候補）
- Channel ベースのバックグラウンドキュー化（Issue #1458 選択肢 2）
- `LendingService` への監査ログ追加（Issue 本文の認識誤りを別 Issue として整理する余地あり）
- `LedgerRepository.UnmergeLedgersAsync` 周辺（今回触らない）

## 期待される改善

- SMB 共有モードで Ledger 関連 1 操作あたり fsync 1 回（数 ms〜数十 ms）短縮
- データ整合性: Ledger と監査ログが原子的にコミットされる（副次効果）
