# Issue #1478: LedgerRepository.GetByIdAsync / GetLentRecordAsync の 1 RTT 化

## 背景

`LedgerRepository.GetByIdAsync` および `GetLentRecordAsync` は、レジャー本体 (`ledger`) と詳細 (`ledger_detail`) を **別々の SELECT** で取得しており、同一接続内とはいえ 2 RTT 発生する。SMB 共有モードで詳細編集ダイアログ表示時に体感できるレイテンシを引き起こす。

- 該当箇所: `ICCardManager/src/ICCardManager/Data/Repositories/LedgerRepository.cs`
  - `GetByIdAsync(int id)` (74-96 行)
  - `GetLentRecordAsync(string cardIdm)` (99-123 行)
- 出典: 2026-05-08 のリポジトリ全体レビュー (性能観点エージェント、Perf M1)

## ゴール

両メソッドを **1 RTT** に集約し、SMB 共有モードでのレスポンスを 5〜20ms × 該当呼び出し頻度ぶん改善する。Local モードでは誤差レベルだが、コード可読性は同等以上を保つ。

## 採用方針: 複数結果セット (`NextResultAsync`)

1 つの `CommandText` に SELECT 2 本をセミコロンで連結し、`ExecuteReaderAsync()` で 1 つ目を読み、`reader.NextResultAsync()` で 2 つ目に進む。SQLite ADO.NET (`System.Data.SQLite`) の標準機能で動作する。

### 検討した代替案

| 案                              | 評価                                                                                                                                                  |
| ------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------- |
| `GetDetailsByLedgerIdsAsync` 流用 | Issue 提案 1。既存メソッドも内部で別途 `LeaseConnectionAsync` を呼ぶため、RTT 数は実質変わらない。コード重複の解消のみで本質的な性能改善にならない。 |
| `UNION ALL` で 1 SELECT 化        | Issue 提案 2。`ledger` と `ledger_detail` の列構造が異なるため `NULL` パディングが必要となり SQL が肥大化・型混在で可読性が低下。                       |
| **複数結果セット (採用)**         | 1 RTT 化を達成しつつ、各 SELECT のマッピングコードは既存のまま (`MapToLedger` / `MapToLedgerDetail`) 流用でき、可読性も保持。                          |

## 実装方針

### `GetByIdAsync`

```csharp
public async Task<Ledger> GetByIdAsync(int id)
{
    using var lease = await _dbContext.LeaseConnectionAsync();
    var connection = lease.Connection;

    using var command = connection.CreateCommand();
    command.CommandText = @"SELECT id, card_idm, lender_idm, date, summary, income, expense, balance,
       staff_name, note, returner_idm, lent_at, returned_at, is_lent_record
FROM ledger
WHERE id = @id;

SELECT ledger_id, use_date, entry_station, exit_station,
       bus_stops, amount, balance, is_charge, is_point_redemption, is_bus, group_id, rowid
FROM ledger_detail
WHERE ledger_id = @id
ORDER BY use_date ASC, is_charge DESC, is_point_redemption DESC, rowid DESC";

    command.Parameters.AddWithValue("@id", id);

    using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        return null;
    }

    var ledger = MapToLedger(reader);

    // 詳細結果セットに移動
    await reader.NextResultAsync();
    var details = new List<LedgerDetail>();
    while (await reader.ReadAsync())
    {
        details.Add(MapToLedgerDetail(reader));
    }
    ledger.Details = Common.LedgerDetailChronologicalSorter
        .Sort(details, preserveOrderOnFailure: true).ToList();

    return ledger;
}
```

### `GetLentRecordAsync`

本体 SELECT は `card_idm` で検索して `lent_at DESC LIMIT 1` を取るため、ledger_id が SELECT 段階では確定していない。詳細側のサブクエリで同じ条件を再評価する。

```csharp
public async Task<Ledger> GetLentRecordAsync(string cardIdm)
{
    using var lease = await _dbContext.LeaseConnectionAsync();
    var connection = lease.Connection;

    using var command = connection.CreateCommand();
    command.CommandText = @"SELECT id, card_idm, lender_idm, date, summary, income, expense, balance,
       staff_name, note, returner_idm, lent_at, returned_at, is_lent_record
FROM ledger
WHERE card_idm = @cardIdm AND is_lent_record = 1
ORDER BY lent_at DESC
LIMIT 1;

SELECT ledger_id, use_date, entry_station, exit_station,
       bus_stops, amount, balance, is_charge, is_point_redemption, is_bus, group_id, rowid
FROM ledger_detail
WHERE ledger_id = (
    SELECT id FROM ledger
    WHERE card_idm = @cardIdm AND is_lent_record = 1
    ORDER BY lent_at DESC
    LIMIT 1
)
ORDER BY use_date ASC, is_charge DESC, is_point_redemption DESC, rowid DESC";

    command.Parameters.AddWithValue("@cardIdm", cardIdm);

    using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        return null;
    }

    var ledger = MapToLedger(reader);

    await reader.NextResultAsync();
    var details = new List<LedgerDetail>();
    while (await reader.ReadAsync())
    {
        details.Add(MapToLedgerDetail(reader));
    }
    ledger.Details = Common.LedgerDetailChronologicalSorter
        .Sort(details, preserveOrderOnFailure: true).ToList();

    return ledger;
}
```

## 境界・エラーケース

- 本体行なし → 即 `return null`（2 つ目の結果セットは読まずに破棄。`using` で reader が破棄される際に内部的に消費される）
- 本体あり・詳細 0 件 → `Details` は空リスト
- パラメータ共有: `System.Data.SQLite` は同一コマンド内の複数 SELECT で同名パラメータを共有可能。万が一動作しない場合はパラメータ名を分けて両方に同値をバインドする（実装時に確認）
- `Common.LedgerDetailChronologicalSorter.Sort` は既存実装と同じく `preserveOrderOnFailure: true` で呼び、フォールバック時は SQL ORDER BY 結果を維持する

## 互換性への配慮

- 既存 `private GetDetailsAsync(int)` はリポジトリ内で `GetByIdAsync` / `GetLentRecordAsync` 以外で参照されないが、テスタビリティと将来の汎用呼び出しを考慮して **削除せず残置**する
- `ILedgerRepository` インターフェースは変更しない（呼び出し側への影響なし）

## テスト計画

`ICCardManager/tests/ICCardManager.Tests/Data/Repositories/LedgerRepositoryTests.cs` に追加。

| #   | テスト名                                                                | 検証内容                                                                              |
| --- | ----------------------------------------------------------------------- | ------------------------------------------------------------------------------------- |
| T1  | `GetByIdAsync_LedgerAndDetailsExist_ReturnsLedgerWithDetails`           | 本体 + 詳細 N 件が両方マップされる                                                    |
| T2  | `GetByIdAsync_NoDetails_ReturnsLedgerWithEmptyDetails`                  | 本体あり・詳細 0 件で `Details` が空リスト                                              |
| T3  | `GetByIdAsync_NotFound_ReturnsNull`                                     | 存在しない id で `null`                                                                |
| T4  | `GetByIdAsync_DetailsAreChronologicallySorted`                          | 残高チェーンソートが既存挙動通り適用される（既存テストで間接検証されていれば不要）    |
| T5  | `GetLentRecordAsync_HasLentRecord_ReturnsLatestWithDetails`             | 複数貸出中レコードのうち `lent_at` 最新が選ばれ、詳細が取得される                       |
| T6  | `GetLentRecordAsync_NoLentRecord_ReturnsNull`                           | 貸出中なしで `null`                                                                    |
| T7  | `GetLentRecordAsync_MultipleLentRecords_SubqueryReturnsCorrectDetails`  | サブクエリが「`lent_at DESC LIMIT 1`」と同じ id を解決し、その詳細のみを返す           |

DB セットアップは既存 `LedgerRepositoryTests` のパターンに合わせる（一時ファイル SQLite + `MigrationRunner` 実行）。

## 期待される改善

- SMB 共有モード: 詳細編集ダイアログ表示時のレスポンスが 5〜20ms 短縮
- 貸出中判定（`GetLentRecordAsync`）が頻繁に呼ばれる場合、累積で体感速度向上
- Local モード: 誤差レベル（測定可能だが体感差なし）

## 関連

- Issue: #1478
- 同時期の性能改善 Issue: Perf M シリーズ
- 参照リポジトリ層パターン: `LedgerRepository.GetDetailsByLedgerIdsAsync` (IN 句版・別用途)
