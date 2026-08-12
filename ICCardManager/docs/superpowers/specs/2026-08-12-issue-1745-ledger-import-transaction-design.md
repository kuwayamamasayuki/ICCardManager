# 履歴CSVインポートの原子性（Issue #1745）設計書

作成日: 2026-08-12
対象 Issue: #1745「履歴CSVインポートだけトランザクション無しで部分コミットされる」

## 1. 背景と問題

`CsvImportService` の 3 つのインポート経路のうち、**利用履歴（ledger）だけがトランザクションを持たない**。

| 経路 | 実装 | 原子性 |
|------|------|--------|
| `ImportCardsInternalAsync`（Card.cs:157-） | `BeginTransactionAsync` で囲み、`errors > 0` なら `Rollback` して `importedCount = 0` | あり |
| `ImportStaffInternalAsync`（Staff.cs:151-） | 同上 | あり |
| `ImportLedgersAsync`（Ledger.cs:191-） | コメント「履歴はトランザクションなしで直接インポート」のとおり `InsertAsync(ledger)` / `UpdateAsync(ledger)` を逐次実行 | **なし** |

`LedgerRepository.InsertAsync(Ledger)` は `InsertAsync(ledger, transaction: null)` へ委譲し、tx が null のときは `LeaseConnectionAsync()` で接続を借りて **autocommit** で INSERT する。したがって N 行目で例外（共有モードの `SQLITE_BUSY`、接続断等）が出ると、1..N-1 行は DB にコミット済みのまま残る。

### 実害

300 行の履歴 CSV を取り込み中、150 行目で他 PC のバックアップ処理により `SQLITE_BUSY` が発生すると、1〜149 行目だけが台帳に残る。事前の `ValidateBalanceConsistencyForLedgers` は「全行が入る前提」で残高チェーンを検証しているため、途中で切れた状態は検証されていない状態として確定する。

### Issue 本文の付随主張は不成立（本設計では扱わない）

Issue 本文が挙げた次の 2 点は、Issue 自身の検証欄で反証済み。本修正の目的には含めない。

- 「外側 catch で `importedCount` が捨てられ `ImportedCount=0` になる」— foreach 本体は行ごとの `try/catch` で完全に包まれており、ループ内の DB 例外は外側 catch へ到達しない
- 「監査ログは `ImportedCount > 0` の分岐でのみ記録＝残らない」— 逆で、部分成功時こそ `DataExportImportViewModel` が記録する（Issue #1302）

## 2. 方針

`ImportLedgersAsync` の投入ループを `BeginTransactionAsync` で囲み、Card / Staff と**同一の形**にする。

```csharp
using var scope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false);
try
{
    foreach (var (lineNumber, ledger, isUpdate) in validRecords)
    {
        // 行ごとの try/catch は持たない（Card / Staff と同じ）
        if (isUpdate) { … UpdateAsync(ledger, scope.Transaction) … }
        else          { … InsertAsync(ledger, scope.Transaction) … }
    }

    if (errors.Count == 0) { scope.Commit(); }
    else { scope.Rollback(); importedCount = 0; updatedCount = 0; }
}
catch (SQLiteException ex)
{
    scope.Rollback();
    _logger?.LogError(ex, "履歴CSVインポートのトランザクション中に SQLite エラーが発生しロールバック");
    throw DatabaseException.QueryFailed("CSV import transaction", ex);
}
catch (Exception ex)
{
    scope.Rollback();
    _logger?.LogError(ex, "履歴CSVインポートのトランザクション中に想定外の例外が発生しロールバック");
    throw;
}
```

### 2.1 tx は必ず明示的に引き渡す（`development-conventions.md` の「①」経路）

`InsertAsync(Ledger)` / `UpdateAsync(Ledger)` の 1 引数版は使わない。`BeginTransactionAsync` は `SemaphoreSlim(1,1)` を取るため、入れ子で開くと自己デッドロックする（Issue #1575）。`SQLiteTransaction` を受ける 2 引数オーバーロード（`LedgerRepository.cs:175, 229`）は既に存在するため、技術的制約はない。

`HasActiveTransactionScope` による暗黙参加（「②」経路）に頼らないこと。②は `DbContext` のプロセス全体カウンタで「自分のスコープか他フローのスコープか」を区別できず、backstop であって正規手段ではない（Issue #1737）。

### 2.2 行ごとの `try/catch` は撤去する

現行のループ内 `catch` は例外を `errors` へ積んで**続行**する。これを残したままトランザクションで囲むと:

- 一度 `SQLITE_BUSY` でロックが取れなければ後続行も同じ理由で失敗し、**同一のエラーが行数分だけ並ぶ**（300 行なら 300 件）
- `Message = $"…エラーが発生しました: {ex.Message}"` が生の `ex.Message`（英語の SQLite メッセージ）を UI のエラー一覧へ出す（`error-messages.md` / Issue #1614 違反）

撤去すると例外は外側 catch へ抜け、`ImportLedgersAsync` 冒頭の `try` が `ToUserFacingErrorMessage(ex)` で 3 要素文言へ変換する。`DatabaseException` は `AppException` なので整備済みの `UserFriendlyMessage` が使われる。**「例外 → 文言」の対応表を 1 か所に集約する**という Issue #1744 の規約にも沿う。

失われるのは「例外が起きた行番号」の UI 表示だが、`_logger.LogError(ex, …)` に痕跡が残る。またリポジトリが `false` / `0` を返す（例外ではない）失敗は従来どおり行番号付きで `errors` へ積まれる。

### 2.3 キャッシュ無効化は行わない（Card / Staff に倣わない箇所）

Card / Staff はコミット後に `_cacheService.InvalidateByPrefix(...)` を呼ぶ。**ledger は呼ばない。**
`LedgerRepository` は `ICacheService` を一切参照しておらず、`CacheKeys` にも ledger 系プレフィックスが存在しないため、無効化すべきキャッシュが無い。「倣う」ことを理由に存在しないキーを足さない。

### 2.4 `skippedCount` はロールバック時も維持する

スキップ行はそもそも書き込み対象ではないため、ロールバックしても件数の意味は変わらない。Card / Staff も同じ扱い。ゼロにするのは `importedCount` と `updatedCount`（`ImportedCount = importedCount + updatedCount` のため両方）。

## 3. UI への影響（`DataExportImportViewModel` は変更しない）

| 状況 | 変更前 | 変更後 |
|------|--------|--------|
| 途中行で例外 | `Success=false` / `ImportedCount=N-1` → 「インポート完了（一部エラー）: N-1件を登録」。DB には N-1 行が残る | 例外が `ErrorMessage` へ変換され「インポートに失敗しました。〜」。DB は無変更 |
| リポジトリが `false` / `0` を返す | 同上（部分コミット） | `ImportedCount=0` / `ErrorCount>0` → 「インポート完了（一部エラー）: 0件を登録」＋ `BuildPartialImportGuidance(0)`「登録が確定した行はありません。…」 |
| 全行成功 | 変更なし | 変更なし |

`BuildPartialImportGuidance(0)`（Issue #1781）は `importedCount == 0` の分岐を既に持ち、二重登録に言及しない文言を返す。Card / Staff のロールバック時と同じ経路であり、ViewModel 側の追加変更は不要。

プレビューの破棄判定 `importCommitted = result.Success || result.ImportedCount > 0`（Issue #1781/#1782）も、ロールバック時は `false` となりプレビューが残る。取り込みが確定していない以上、そのまま再実行できるのが正しい。

## 4. スコープ外

- **`ImportLedgerDetailsAsync`（Detail.cs）**: 同様にトランザクションを持たないが、本 Issue の対象ではない。明細インポートは `NewLedgerFromSegmentsBuilder` 経由で ledger 行も作るため独立した検討が要る。別 Issue で扱う
- **`DbContext.ExecuteWithRetryAsync` によるリトライ**: Card / Staff インポートも掛けていない。原子性の欠如とは別問題のため本 PR では扱わない（Issue #1727 で `LendingService.ImportHistoryForRegistrationAsync` には適用済み）
- **ViewModel の「インポート完了（一部エラー）」というダイアログ表題**: `ImportedCount=0` のとき表題がやや実態と離れるが、Card / Staff で既にそう振る舞っており、本文の `BuildPartialImportGuidance(0)` が正しく案内する。本 PR では変えない

## 5. テスト

ロールバックが効いたかは「モックが呼ばれたか」では観測できない。**実 `LedgerRepository` を噛ませて実際の行数を表明する**（`LendingServiceHistoryImportTests` が参考実装、`development-conventions.md` Issue #1727）。

新規テストクラス `CsvImportServiceLedgerTransactionTests`（実 DB = `TestDbContextFactory.Create()`）:

| No. | テスト | 表明 |
|-----|--------|------|
| 1 | 途中行の INSERT が例外 → 全行ロールバック | `SELECT COUNT(*) FROM ledger` が 0。既に成功した行も残らない |
| 2 | 全行成功 → まとめてコミット | 行数が CSV の行数と一致、`ImportedCount` も一致 |
| 3 | リポジトリが `0` を返す（例外ではない失敗）→ ロールバック | 行数 0、`ImportedCount == 0`、`ErrorCount > 0` |
| 4 | 更新行の UPDATE が例外 → 同一トランザクション内の INSERT 行も戻る | 行数が事前投入分のまま、内容も変更前 |

失敗の注入には `InvalidOperationException` を使う（`SQLiteException(Busy)` はリトライ待機を挟む経路があるため、1 回で確定する例外を使う）。

既存の `CsvImportServiceTests` のうち `ImportLedgersAsync_*` 7 件は、モック設定を 2 引数オーバーロード（`It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()`）へ変更する。Card / Staff インポートの既存テストが既にこの形であり、アサーション（`Times.Once` / `Times.Never` 等）は変更しない。

## 6. ドキュメント更新

- `ICCardManager/CHANGELOG.md` の `### Unreleased` に追記
- `ICCardManager/docs/design/05_クラス設計書.md` — CSV インポートのトランザクション境界の記述を更新
- `ICCardManager/docs/design/04_機能設計書.md` — 履歴インポートの失敗時挙動（全か無か）を記述
