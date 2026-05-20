# Issue #1456: `LedgerRepository.InsertDetailsAsync` のバッチ化（N+1 とトランザクション無しの解消）

- 起票: 2026-05-08 のリポジトリ全体レビュー（性能観点エージェント、Perf H1）
- 設計日: 2026-05-20

## 背景・問題

`LedgerRepository.InsertDetailsAsync` は `foreach (var detail in details) { await InsertDetailAsync(detail, tx); }` の単純ループで実装されている。`InsertDetailAsync` は呼び出しごとに以下を行う:

- **tx == null 経路**: `LeaseConnectionAsync()` を取り直し、独立した autocommit で 1 件ずつ INSERT する。`journal_mode=DELETE` では各 INSERT が rollback journal の作成・fsync・削除を伴う。
- **tx != null 経路**: 接続とトランザクションは共有されるが、`SQLiteCommand` を毎回 `new` して `Parameters.AddWithValue(...)` を 11 個ずつ繰り返す。

`InsertDetailsAsync` は典型的に 5〜100 件を一度に挿入し、以下の経路から呼ばれる:

- `Services/LedgerSplitService.cs:141`（tx=null 経路、分割で新 Ledger 作成時）
- `Services/Import/Builders/NewLedgerFromSegmentsBuilder.cs:106`（tx=null 経路、CSV インポートで新 Ledger 作成時）
- `Services/LendingService.cs` の `InsertDetailsInTransactionAsync` 経由（tx あり経路、貸出/返却フロー）
- `LedgerRepository.ReplaceDetailsAsync` 経由（編集・分割・CSV インポート 3 経路、tx=null）

共有モード（SMB）では SMB の往復遅延 1〜10 ms × 件数 が直線的に効き、ローカルモードでも各 INSERT で rollback journal の fsync が発生するため、**返却処理の体感を支配しうる**遅さになる。

## 設計方針

Issue 本文の主提案に沿ったアプローチA を採用する。

1. `InsertDetailsAsync(int ledgerId, IEnumerable<LedgerDetail> details, SQLiteTransaction transaction)` の実装を **単一トランザクション＋単一 SQLiteCommand 再利用** に置き換える。
2. `transaction == null` の場合は内部で `_dbContext.BeginTransactionAsync()` を開き、メソッド内で commit / 例外時 rollback まで責任を持つ。`transaction != null` の場合は呼び出し元のトランザクションを使い、commit/rollback には介入しない。
3. ループ内では `SQLiteCommand` を 1 個だけ作成し、`Parameters` をあらかじめ宣言（型と名前のみ）、毎回 `param.Value = ...` で再代入してから `ExecuteNonQueryAsync()` を呼ぶ。
4. 1 件の挿入 API である `InsertDetailAsync(detail, tx)` は、`DebugDataService` や `LendingService` の単一明細書込みで広く使われているため触らない。

採用しなかった代替案:

- **値リスト連結 `INSERT … VALUES (…),(…),(…)`**: SQLite の変数上限 999 を考慮したチャンク分割が必要で保守コストが増える割に、N=100 程度では本案 A との実効差が小さい。
- **A + 巨大バッチ時のみ B のハイブリッド**: 早すぎる最適化。

## 詳細仕様

### `InsertDetailsAsync` の新実装（疑似コード）

```csharp
public Task<bool> InsertDetailsAsync(int ledgerId, IEnumerable<LedgerDetail> details)
    => InsertDetailsAsync(ledgerId, details, transaction: null);

public async Task<bool> InsertDetailsAsync(int ledgerId, IEnumerable<LedgerDetail> details, SQLiteTransaction transaction)
{
    var list = details as IList<LedgerDetail> ?? details.ToList();
    if (list.Count == 0) return true;

    if (transaction != null)
    {
        return await InsertDetailsCore(ledgerId, list, transaction.Connection, transaction).ConfigureAwait(false);
    }

    using var scope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false);
    try
    {
        var ok = await InsertDetailsCore(ledgerId, list, scope.Lease.Connection, scope.Transaction).ConfigureAwait(false);
        if (ok) scope.Commit();
        else    scope.Rollback();
        return ok;
    }
    catch
    {
        scope.Rollback();
        throw;
    }
}

private static async Task<bool> InsertDetailsCore(
    int ledgerId, IList<LedgerDetail> list, SQLiteConnection connection, SQLiteTransaction transaction)
{
    using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = @"INSERT INTO ledger_detail (ledger_id, use_date, entry_station, exit_station,
                               bus_stops, amount, balance, is_charge, is_point_redemption, is_bus, group_id)
VALUES (@ledgerId, @useDate, @entryStation, @exitStation,
       @busStops, @amount, @balance, @isCharge, @isPointRedemption, @isBus, @groupId)";

    var pLedgerId        = command.Parameters.Add("@ledgerId",         DbType.Int32);
    var pUseDate         = command.Parameters.Add("@useDate",          DbType.String);
    var pEntryStation    = command.Parameters.Add("@entryStation",     DbType.String);
    var pExitStation     = command.Parameters.Add("@exitStation",      DbType.String);
    var pBusStops        = command.Parameters.Add("@busStops",         DbType.String);
    var pAmount          = command.Parameters.Add("@amount",           DbType.Int32);
    var pBalance         = command.Parameters.Add("@balance",          DbType.Int32);
    var pIsCharge        = command.Parameters.Add("@isCharge",         DbType.Int32);
    var pIsPointRedemption = command.Parameters.Add("@isPointRedemption", DbType.Int32);
    var pIsBus           = command.Parameters.Add("@isBus",            DbType.Int32);
    var pGroupId         = command.Parameters.Add("@groupId",          DbType.Int32);

    foreach (var detail in list)
    {
        detail.LedgerId = ledgerId;

        pLedgerId.Value          = detail.LedgerId;
        pUseDate.Value           = detail.UseDate.HasValue ? (object)detail.UseDate.Value.ToString("yyyy-MM-dd HH:mm:ss") : DBNull.Value;
        pEntryStation.Value      = (object)detail.EntryStation ?? DBNull.Value;
        pExitStation.Value       = (object)detail.ExitStation  ?? DBNull.Value;
        pBusStops.Value          = (object)detail.BusStops     ?? DBNull.Value;
        pAmount.Value            = detail.Amount.HasValue  ? (object)detail.Amount.Value  : DBNull.Value;
        pBalance.Value           = detail.Balance.HasValue ? (object)detail.Balance.Value : DBNull.Value;
        pIsCharge.Value          = detail.IsCharge ? 1 : 0;
        pIsPointRedemption.Value = detail.IsPointRedemption ? 1 : 0;
        pIsBus.Value             = detail.IsBus ? 1 : 0;
        pGroupId.Value           = detail.GroupId.HasValue ? (object)detail.GroupId.Value : DBNull.Value;

        if (await command.ExecuteNonQueryAsync().ConfigureAwait(false) <= 0)
            return false;
    }
    return true;
}
```

設計上のポイント:

- **不変条件の維持**: 既存の挙動と一致するように、`detail.LedgerId = ledgerId` の上書き、`(object)?? DBNull.Value` の NULL 表現、`Date` の `"yyyy-MM-dd HH:mm:ss"` フォーマット、bool→int 変換ルールはすべて踏襲する。
- **空コレクションの扱い**: 既存実装は空入力で `foreach` が回らず `true` を返す。新実装も `list.Count == 0` で早期 `return true`（接続も tx も取らない）。
- **ループ内失敗時**: `ExecuteNonQueryAsync` が 0 行を返した場合は `false` を返し、自分が開けた tx はロールバックする。呼び出し元 tx は呼び出し元が rollback する責務（既存 `MergeLedgersAsync` 等のパターンと一致）。
- **例外伝播**: SQLiteException が出た場合は自分が開けた tx を rollback してから再スロー。

### API 互換性

- `ILedgerRepository` の宣言は変更しない（4 シグネチャ維持）。
- 既存呼び出し元（`LedgerSplitService`, `LendingService`, `NewLedgerFromSegmentsBuilder`, `ReplaceDetailsAsync`）はソース変更不要。
- モック化されている `Mock<ILedgerRepository>` のテスト（`CsvImportServiceTests` 等）も追加修正不要。

### 並行性・スレッド安全

- `BeginTransactionAsync()` は `_semaphore.WaitAsync()` を取るため、tx=null 経路でも DB 操作の直列化は保たれる。
- 呼び出し元 tx 経路は既に呼び出し元が `BeginTransactionAsync()` でセマフォを取得済み（既存パターン）。
- 並列起動禁止規約（Issue #1452）は本変更で影響なし。`InsertDetailsAsync` は 1 接続上の直列ループのまま。

### 失敗モードと回復

| ケース | 挙動 |
|--------|------|
| tx=null・途中で SQLiteException | 開いた tx を rollback して再スロー。既存挙動より明確に「ALL OR NOTHING」が保証される（以前は途中までの行が autocommit されていた） |
| tx 指定あり・途中で SQLiteException | 例外を再スロー。呼び出し元の tx スコープが Rollback で全体を破棄する |
| 0 件入力 | true を返し、DB 操作は一切行わない |
| ExecuteNonQueryAsync が 0 を返す | false を返す（tx=null の場合は rollback） |

**重要な挙動変更**: tx=null 経路で途中失敗した場合、これまでは「最初の N-1 件はコミット済み、N 件目で例外」という不整合状態が残り得た。新実装では tx ロールバックにより 0 件残る。これは呼び出し元（`LedgerSplitService`, `NewLedgerFromSegmentsBuilder`, `ReplaceDetailsAsync` 経由のインポート）の意図に **より忠実**であり、改善とみなす。

## テスト方針

`tests/ICCardManager.Tests/Data/Repositories/LedgerRepositoryTransactionTests.cs` に追加するか、新ファイル `LedgerRepositoryBatchInsertTests.cs` を作成する。既存 `LedgerRepositoryTransactionTests` の組み立てを真似て実 SQLite で検証する。

追加するテストケース:

1. **`InsertDetailsAsync_LargeBatch_TxNull_AllPersisted`** — 100 件を tx=null で渡し、すべて DB に入ることを確認。N+1 解消で挙動が変わっていないことの機能保証（最も基本的なリグレッション守備網）。
2. **`InsertDetailsAsync_LargeBatch_WithCallerTransaction_RollbackDiscardsAll`** — 100 件を呼び出し元 tx 経由で挿入し、呼び出し元が `Rollback` すると 1 件も残らないこと。これにより以下を保証する: (a) `InsertDetailsAsync` が呼び出し元 tx に介入していない（自分で commit していない）、(b) 100 件分のループが同一 tx 内で実行されている。
3. **`InsertDetailsAsync_EmptyCollection_ReturnsTrue_NoSideEffect`** — 空コレクションで true、テーブル件数に変化なし。早期 return の経路を固定。
4. **`InsertDetailsAsync_OverwritesLedgerId_OnEachRow`** — 各 `detail.LedgerId` に異なる値（例: -1）を事前に入れた状態で呼び、すべての行が引数の `ledgerId` で書き換えられて DB に入る（既存挙動の固定）。
5. **`InsertDetailsAsync_TxNull_OnSqliteException_DoesNotLeakSemaphore`** — `ledgerId=999999` のような不正な FK で例外が出る経路を呼んだ直後でも、次の `BeginTransactionAsync()` がタイムアウトせずに取れることを確認（内部 tx の rollback と lease 解放が正しく行われている証明）。

**測定不能なテストの省略**: 「tx=null 経路で途中行が失敗したら ALL OR NOTHING」を直接示す単体テストは、`detail.LedgerId = ledgerId` の上書きが全行同一値を強制するため、現実的なシナリオでは作りにくい（FK 失敗は常に 1 行目で起こる）。代わりに、テスト #2（caller-tx Rollback で 100 件全消滅）が「同一 tx 内ループ」の不変条件を、テスト #5 が「例外時のリソース解放」を担うため、ALL OR NOTHING 保証は両者の組み合わせで間接的に確認する。

`LedgerRepositoryTests.InsertDetailsAsync_MultipleDetails_SavesAll` は既存のリグレッション守備網としてそのまま機能する（修正不要）。

テスト設計書 `docs/design/07_テスト設計書.md` の「LedgerRepository テスト」セクションに上記 5 件を追記する。件数表 §1.1a の自動同期は CI で検証されるため、`dotnet test --list-tests` の出力に合わせて数値を更新する。

## 観測可能な改善

ベンチマーク数値は本 PR には含めない（合意済み）。理論的にはローカルで N×fsync → 1×fsync、SMB で N往復 → 1往復＋commit 同期 になる。100 件の `InsertDetailsAsync` 1 回あたりで、SMB 環境では返却処理の体感 5〜30 倍高速化が見込まれる（Issue 本文記載の見立て）。

## ロールバック計画

万一リグレッションが発生した場合は、`InsertDetailsAsync` の本体を旧 `foreach (var detail in details) { await InsertDetailAsync(detail, transaction); }` に戻すだけで原状回復できる。`InsertDetailAsync` は触らないため revert は単一ファイル単位で済む。

## CHANGELOG への記載予定

`ICCardManager/CHANGELOG.md` の Unreleased セクションに以下を追加する（リリース時の更新を後続作業とする想定）:

```
### 改善

- `LedgerRepository.InsertDetailsAsync` を単一トランザクション＋SQLiteCommand 再利用に変更し、複数明細を一括書込みする際の I/O を大幅に削減（Issue #1456）。
  共有モード（SMB）で返却処理・分割・CSV インポートが体感で高速化されます。
```

## 関連 Issue・参考

- Issue #1456 本文の提案
- Issue #1481（`SQLiteTransaction` 受け取りオーバーロード導入）— このオーバーロードを前提に最小差分で実現
- Issue #1452（並列起動禁止）— `BeginTransactionAsync` のセマフォ規約と一致
- Issue #1107（共有モードの journal_mode=DELETE）— 本改善が最も効く環境
