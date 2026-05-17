# Issue #1479: operation_log の OFFSET → keyset pagination 化

## 1. 背景

`OperationLogRepository.SearchAsync` は `LIMIT @pageSize OFFSET @offset` 形式で実装されている。SQLite の OFFSET は線形コストのため、深いページではスキップ行数に比例した遅延が発生する。

`operation_log` は 6 年分（最大 5,000 行/月 × 72 ヶ月 = 数十万行）蓄積し得るため、最終ページや末尾近傍へのジャンプで体感できる遅延が顕在化する見込み。

一方 `ledger.GetPagedAsync` はカード ID m と日付範囲のフィルタが効くため通常運用では深いページに到達しにくく、本 Issue のスコープからは外す（別 Issue で再検討）。

## 2. スコープ

- **対象**: `OperationLogRepository.SearchAsync` および呼び出し元 `OperationLogSearchViewModel`
- **対象外**: `LedgerRepository.GetPagedAsync`（別 Issue 分離）

## 3. 設計判断

### 3.1 UI ポリシー: 「最初／前／次／最後」すべて keyset 化

UI 側のページャーには `最初 / 前 / 次 / 最後` の 4 ボタンしかなく、ページ番号直接ジャンプ機能は無い。よって OFFSET をフォールバックとして残す必要が無いため、4 操作すべてを keyset で実装する。

| 操作 | 実現方法 |
| ---- | -------- |
| 最初へ | `ORDER BY (timestamp ASC, id ASC) LIMIT n` |
| 前へ | `WHERE (timestamp < @ts) OR (timestamp = @ts AND id < @id)` ＋ `ORDER BY DESC LIMIT n` ＋ アプリ側で reverse |
| 次へ | `WHERE (timestamp > @ts) OR (timestamp = @ts AND id > @id)` ＋ `ORDER BY ASC LIMIT n` |
| 最後へ | `ORDER BY (timestamp DESC, id DESC) LIMIT n` ＋ アプリ側で reverse |

### 3.2 既存 OFFSET 版 `SearchAsync` の扱い

`SearchAsync(criteria, page, pageSize)` は同 PR で **削除**する。呼び出し元は `OperationLogSearchViewModel` のみであり、Repository テストおよびそれ自体のテストも合わせて keyset 版に移行する。後方互換のための `[Obsolete]` ステージは省略（外部 API ではなくアプリ内専用のため）。

### 3.3 並び順は変更しない

並び順は既存どおり `(timestamp ASC, id ASC)`。タイブレークの `id` で安定ソートが保証される。

## 4. API 追加

### 4.1 カーソル型

```csharp
namespace ICCardManager.Data.Repositories;

public sealed class OperationLogCursor
{
    public DateTime Timestamp { get; }
    public int Id { get; }
    public OperationLogCursor(DateTime timestamp, int id) { Timestamp = timestamp; Id = id; }
}
```

イミュータブル。`null` を「カーソル無し（先頭／末尾）」を意味する境界として ViewModel から扱う。

### 4.2 ページ結果型

```csharp
public sealed class OperationLogKeysetPage
{
    public IReadOnlyList<OperationLog> Items { get; init; } = Array.Empty<OperationLog>();
    public int TotalCount { get; init; }
    public OperationLogCursor FirstCursor { get; init; }  // 先頭行（Prev 取得時の起点）
    public OperationLogCursor LastCursor { get; init; }   // 末尾行（Next 取得時の起点）
    public bool HasPrevious { get; init; }
    public bool HasNext { get; init; }
}
```

`TotalCount` は引き続き「○件中 X〜Y 件を表示」表示で利用するため keyset 化後も取得する。

### 4.3 リポジトリインターフェイス

```csharp
Task<OperationLogKeysetPage> SearchFirstPageAsync(OperationLogSearchCriteria criteria, int pageSize);
Task<OperationLogKeysetPage> SearchNextPageAsync(OperationLogSearchCriteria criteria, OperationLogCursor afterCursor, int pageSize);
Task<OperationLogKeysetPage> SearchPreviousPageAsync(OperationLogSearchCriteria criteria, OperationLogCursor beforeCursor, int pageSize);
Task<OperationLogKeysetPage> SearchLastPageAsync(OperationLogSearchCriteria criteria, int pageSize);
```

既存 `SearchAsync(criteria, page, pageSize)` は削除。

## 5. SQL 実装

### 5.1 次ページ（ASC 方向）

```sql
SELECT id, timestamp, operator_idm, operator_name, target_table,
       target_id, action, before_data, after_data
FROM operation_log
WHERE <criteria_clause>
  AND (
        timestamp > @afterTs
     OR (timestamp = @afterTs AND id > @afterId)
      )
ORDER BY timestamp ASC, id ASC
LIMIT @pageSizePlusOne
```

`LIMIT pageSize + 1` で 1 行余分に取得し、超過していれば `HasNext = true`（最終行は破棄）。

### 5.2 前ページ（DESC → reverse）

```sql
SELECT ...
FROM operation_log
WHERE <criteria_clause>
  AND (
        timestamp < @beforeTs
     OR (timestamp = @beforeTs AND id < @beforeId)
      )
ORDER BY timestamp DESC, id DESC
LIMIT @pageSizePlusOne
```

取得後アプリ側で `Reverse()`。`HasPrevious` は同様に「超過判定」で得る。

### 5.3 最初ページ

```sql
SELECT ... FROM operation_log
WHERE <criteria_clause>
ORDER BY timestamp ASC, id ASC
LIMIT @pageSizePlusOne
```

`HasPrevious = false`、`HasNext` は超過判定。

### 5.4 最後ページ

```sql
SELECT ... FROM operation_log
WHERE <criteria_clause>
ORDER BY timestamp DESC, id DESC
LIMIT @pageSizePlusOne
```

取得後 reverse。`HasNext = false`、`HasPrevious` は超過判定。

### 5.5 COUNT クエリ

既存と同じ：

```sql
SELECT COUNT(*) FROM operation_log WHERE <criteria_clause>
```

`TotalCount` 表示用に各ページ取得時に同時実行する。

### 5.6 タイムスタンプ書式

ISO 8601 `yyyy-MM-dd HH:mm:ss`（プロジェクト規約に準拠、`development-conventions.md`）。

## 6. ViewModel 変更（`OperationLogSearchViewModel`）

### 6.1 状態の変化

| プロパティ | 変更 |
| ---------- | ---- |
| `CurrentPage` | 維持（表示用に手動で増減） |
| `TotalCount` | 維持 |
| `TotalPages` | 維持（`COUNT/pageSize` から計算） |
| `PageSize` | 維持 |
| `HasPreviousPage` | keyset 結果から反映 |
| `HasNextPage` | keyset 結果から反映 |
| `PageInfo` | 維持（CurrentPage 値ベース） |
| `_firstCursor` | **追加**（private field、現在ページの先頭行） |
| `_lastCursor` | **追加**（private field、現在ページの末尾行） |

### 6.2 各コマンド

```csharp
SearchAsync()          → SearchFirstPageAsync, CurrentPage = 1
LastPageAsync()        → SearchLastPageAsync, CurrentPage = TotalPages
NextPageAsync()        → SearchNextPageAsync(_lastCursor), CurrentPage++
PreviousPageAsync()    → SearchPreviousPageAsync(_firstCursor), CurrentPage--
FirstPageAsync()       → SearchFirstPageAsync, CurrentPage = 1
OnPageSizeChanged()    → SearchAsync()（再検索＝先頭ページ）
```

### 6.3 エッジケース

- 空結果: `Items=[]`, `TotalCount=0`, `HasPrevious=false`, `HasNext=false`, cursors = null。`CurrentPage = 1`, `TotalPages = 0`。
- 1 ページのみ: `FirstCursor == LastCursor`、`HasPrevious = HasNext = false`、ボタン全 disable。
- 同一 `timestamp` 多発: `id` のタイブレークにより順序確定。
- `PageSize` 変更: カーソル無効化し先頭ページから再取得。
- 検索条件変更: 同上。
- ページ取得直後に別 PC で挿入が発生: `TotalCount` と表示中件数に瞬時的なずれが起こり得るが、`(timestamp, id)` カーソルは絶対値なので「表示中行が消える／重複する」事故は起きない。

## 7. インデックス

既存 `idx_log_timestamp ON operation_log(timestamp)` を活用。

`(timestamp, id)` の複合インデックスを追加するとカバリングインデックスとしてさらに効率化できるが、`id` は `INTEGER PRIMARY KEY` のため SQLite では rowid であり、`timestamp` インデックスのリーフから rowid 経由でアクセスはほぼ無料。**本 Issue ではマイグレーション追加を見送る**（将来必要なら別 Issue で）。

## 8. テスト計画

### 8.1 Repository（xUnit + 一時 SQLite DB）

`tests/ICCardManager.Tests/Repositories/OperationLogRepositoryTests.cs` に追加。

| テスト | 目的 |
| ------ | ---- |
| `SearchFirstPageAsync_ReturnsFirstNItems_OrderedByTimestampIdAsc` | 基本動作 |
| `SearchFirstPageAsync_EmptyTable_ReturnsEmptyPage` | 空テーブル |
| `SearchFirstPageAsync_LessThanPageSize_HasNextFalse` | 末尾判定 |
| `SearchNextPageAsync_NavigatesForwardCorrectly` | カーソル前進 |
| `SearchNextPageAsync_HandlesTimestampTies_ByIdOrder` | タイブレーク |
| `SearchPreviousPageAsync_NavigatesBackwardCorrectly` | カーソル後退 |
| `SearchLastPageAsync_ReturnsLastNItems` | 末尾取得 |
| `SearchLastPageAsync_PartialLastPage` | 端数 |
| `Search_AppliesAllCriteriaFilters` | 条件適用 |
| `SequentialNavigation_FirstToLast_ReachesAllRows` | 往復走査の網羅性 |
| `Search_ReportsTotalCount` | TotalCount 正確性 |

### 8.2 ViewModel（xUnit + Moq）

`tests/ICCardManager.Tests/ViewModels/OperationLogSearchViewModelTests.cs` に追加／更新。

| テスト | 目的 |
| ------ | ---- |
| `SearchAsync_StartsAtFirstPage_WithCursors` | 初期取得 |
| `NextPageAsync_AdvancesPageWithCursor` | 次ページ |
| `PreviousPageAsync_RetreatsPageWithCursor` | 前ページ |
| `LastPageAsync_GoesToLastPageAndUpdatesCurrentPage` | 最終ページ |
| `FirstPageAsync_ResetsToFirstPage` | 先頭復帰 |
| `OnPageSizeChanged_ResetsToFirstPage` | サイズ変更時のリセット |
| `EmptyResult_DisablesAllNavigation` | 空結果 |

## 9. ドキュメント同期

- `docs/design/07_テスト設計書.md`：新規テストの件数スナップショットを `dotnet test --list-tests` 実測値で同期。
- 設計書（`docs/design/`）に keyset 戦略の概略を追加するかは差分が大きい場合のみ判断（今回は API 追加のみで本ファイルが Single Source of Truth）。

## 10. 非互換変更

- `OperationLogRepository.SearchAsync(criteria, page, pageSize)` 削除
- `OperationLogSearchResult` クラス削除（`OperationLogKeysetPage` で置換）

外部依存はアプリ内のみで、PR スコープ内で完結。

## 11. 期待される改善

- 最終ページへのジャンプ: 数十万行で 0.1〜数秒 → 数 ms 程度
- 次ページ／前ページ: 浅いページでも線形オフセットを排除（軽微）
- 先頭ページ近辺: 改善は限定的（OFFSET=0 と同等のコスト）

## 12. リスクと緩和

| リスク | 緩和 |
| ------ | ---- |
| カーソル `(timestamp, id)` のシリアル化漏れで状態破綻 | ViewModel は memory-only。Window 閉じれば破棄 |
| 別 PC で同時挿入 → TotalCount と Items の瞬時的不整合 | keyset の特性で「表示中行の消失／重複」は発生しない。TotalCount のずれは再検索で解消 |
| 既存 SearchAsync を参照する潜在的外部コード | grep 済み、参照は ViewModel と Repository テストのみ |
