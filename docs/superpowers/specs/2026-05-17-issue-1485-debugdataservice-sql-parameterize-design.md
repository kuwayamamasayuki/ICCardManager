# Issue #1485 設計書: DebugDataService の SQL 文字列連結（DEBUG 限定）をパラメータ化する

- **Issue**: [#1485](https://github.com/kuwayamamasayuki/ICCardManager/issues/1485) — DebugDataService の SQL 文字列連結（DEBUG限定）をパラメータ化する
- **作成日**: 2026-05-17
- **対象ブランチ**: `fix/issue-1485-debugdataservice-parameterize-sql`
- **優先度**: low（衛生改善）
- **種別**: enhancement / area: data

## 1. 背景と目的

`DebugDataService.CleanExistingTestDataAsync` は、テスト用 IDm リストを `string.Join(",", ... $"'{idm}'")` で構築し、SQL の `IN (...)` 句に文字列補間で埋め込んでいる。

```csharp
var testCardIdms = string.Join(",", TestCardList.Select(c => $"'{c.CardIdm}'"));
cmd.CommandText = $"DELETE FROM ledger WHERE card_idm IN ({testCardIdms})";
```

該当箇所は `#if DEBUG` ガードに囲まれており、テスト用の固定 IDm（16進数文字列）のみが埋め込まれるため、**Release ビルドの出荷物には含まれず、実害となる攻撃面は存在しない**。

しかし、コーディング標準として「**SQL に外部値を直接埋め込まない**」を一貫させる意義は高い。本 Issue は 2026-05-08 のリポジトリ全体レビュー（セキュリティ観点エージェント、Sec L3）で抽出された衛生改善項目である。

## 2. 該当箇所

| ファイル | 行 | 操作 |
|---|---|---|
| `ICCardManager/src/ICCardManager/Services/DebugDataService.cs` | 115–116 | `testCardIdms` / `testStaffIdms` の文字列連結構築 |
| 同上 | 121 | `DELETE FROM ledger_detail ... IN ({testCardIdms})` |
| 同上 | 128 | `DELETE FROM ledger ... IN ({testCardIdms})` |
| 同上 | 135 | `DELETE FROM ic_card ... IN ({testCardIdms})` |
| 同上 | 142 | `DELETE FROM staff ... IN ({testStaffIdms})` |

## 3. 設計

### 3.1 採用方針

Issue 提案の **案 1（パラメータ化）** を採用する。案 2（コメント追記のみ）は「SQL に外部値を含めない」原則の体現にならないため不採用。

### 3.2 実装

`CleanExistingTestDataAsync` 内に **ローカル関数** `BuildInClause` を導入する。

```csharp
internal async Task CleanExistingTestDataAsync()
{
    using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
    var connection = lease.Connection;

    // IN 句のプレースホルダ列 (@p0, @p1, ...) を組み立て、対応する値をパラメータとして登録する。
    // SQL に値を直接埋め込まないコーディング標準（DEBUG 限定コードでも一貫適用）。
    static string BuildInClause(SQLiteCommand cmd, IEnumerable<string> values, string prefix)
    {
        var placeholders = new List<string>();
        var index = 0;
        foreach (var value in values)
        {
            var name = $"@{prefix}{index}";
            placeholders.Add(name);
            cmd.Parameters.AddWithValue(name, value);
            index++;
        }
        return string.Join(",", placeholders);
    }

    var testCardIdms = TestCardList.Select(c => c.CardIdm).ToArray();
    var testStaffIdms = TestStaffList.Select(s => s.StaffIdm).ToArray();

    // 台帳詳細を削除（外部キー制約のため先に削除）
    using (var cmd = connection.CreateCommand())
    {
        var placeholders = BuildInClause(cmd, testCardIdms, "c");
        cmd.CommandText =
            $"DELETE FROM ledger_detail WHERE ledger_id IN (SELECT id FROM ledger WHERE card_idm IN ({placeholders}))";
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    // 同様に ledger / ic_card / staff を削除
    // ...
}
```

ローカル関数を採用する理由（YAGNI）:

- 他箇所からの再利用が現状ない
- `#if DEBUG` 限定コードであり、本番経路からは絶対に呼ばれない
- メソッド内に閉じることで影響範囲が読み取りやすい

### 3.3 動作・契約の不変条件

- 削除対象は `TestCardList` / `TestStaffList` で定義された IDm の行のみ
- 本番データ（テスト IDm に該当しない行）は削除しない
- 既存トランザクション境界（`_dbContext.BeginTransactionAsync` 内）は維持
- `ConfigureAwait(false)` は Service 層規約に従い継続して付与
- ログ出力 `[DEBUG] 既存テストデータを削除しました` は変更しない

## 4. テスト

既存の `DebugDataServiceTests.cs`（インメモリ SQLite ベース）に **2 件** のテストを追加する。

### 4.1 `CleanExistingTestDataAsync_RemovesTestRecordsAndPreservesNonTestRecords`

**目的**: パラメータ化後も「テスト IDm の行のみ削除し、それ以外を残す」契約が保たれること。

- **Arrange**: `_connection` に対し、テスト IDm（`TestCardList[0].CardIdm`）と非テスト IDm（`"AABBCCDD11223344"`）の `ic_card` / `ledger` / `ledger_detail` を直接 INSERT
- **Act**: `_service.CleanExistingTestDataAsync()` を呼ぶ（`internal` を `InternalsVisibleTo` 経由で参照）
- **Assert**:
  - テスト IDm の `ic_card`, `ledger`, `ledger_detail` がすべて 0 件
  - 非テスト IDm の `ic_card`, `ledger`, `ledger_detail` が残存

### 4.2 `CleanExistingTestDataAsync_DoesNotInjectFromQuotedIdm`

**目的**: パラメータ化が回帰でなくなったことを将来検出する防御テスト。

- **Arrange**: テーブルに無関係なレコード（例: `"normal_idm"`）を INSERT。一時的に `TestCardList` 相当を `private const string MaliciousIdm = "'; DROP TABLE ic_card; --"` のような IDm を含むデータで構成することは不可能（`TestCardList` は静的）であるため、**`BuildInClause` ヘルパーに相当する間接テスト**として、パラメータ化後の SQL 文がプレースホルダ列を含むことを `cmd.CommandText` キャプチャで検証
- 簡略化: 実際は `CleanExistingTestDataAsync` 内のヘルパー（ローカル関数）はテストから直接呼べないため、**動作テストとして「悪意ある引用符を含む IDm を持つレコードが事前に存在しても、テーブル自体は破壊されない」** を検証する形に変更する
- **代替案**: テスト用に `'; DROP TABLE ic_card; --` を `card_idm` に持つレコードを直接 INSERT した上で `CleanExistingTestDataAsync` を呼び、`ic_card` テーブルが依然存在し（`PRAGMA table_info(ic_card)` でカラムが取得可能）、該当レコードは削除されない（テスト IDm ではないため）ことを確認する

## 5. ドキュメント同期

| ファイル | 変更内容 |
|---|---|
| `ICCardManager/CHANGELOG.md` | `[Unreleased]` の `Security` セクションに「DebugDataService の DELETE 文を IN 句パラメータ化（DEBUG 限定、影響なし）」を追記 |
| `ICCardManager/docs/design/07_テスト設計書.md` | §8.1 のテスト件数スナップショットを `dotnet test --list-tests` 実測値で更新、新規テスト 2 件を該当セクションに記載 |

`.claude/rules/development-conventions.md` への記載追加は本 PR のスコープ外。コーディング標準の明文化が必要であれば別 Issue で対応する。

## 6. リスクと不在事項

- **機能変更**: なし（DELETE 結果集合は不変）
- **パフォーマンス**: 影響なし（SQLite では IN 句のパラメータ化はプリペアド扱いになりやすく、悪化要素なし）
- **後方互換**: テストデータ生成の挙動・スキーマ・呼び出し元（`MainViewModel` 等）に変更なし
- **マイグレーション**: なし（スキーマ無変更）
- **設計書（`docs/design/01〜08`）への影響**: 07_テスト設計書のみ（§8.1 スナップショット同期）

## 7. 完了基準

- [ ] `DebugDataService.cs` の 4 箇所の DELETE で IN 句がパラメータ化されている
- [ ] `dotnet build -c Debug` がエラー 0 / 警告増加なしで成功
- [ ] `dotnet test` で既存テスト + 新規 2 件がすべて pass
- [ ] CHANGELOG.md / 07_テスト設計書.md が同期更新済み
- [ ] PR が `main` を base としてオープン
