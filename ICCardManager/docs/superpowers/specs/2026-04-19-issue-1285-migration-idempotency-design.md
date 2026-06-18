# Issue #1285: マイグレーション冪等性ガイドライン策定と既存 Migration 検査 設計書

作成日: 2026-04-19
対象 Issue: [#1285](https://github.com/kuwayamamasayuki/ICCardManager/issues/1285)

## 背景と問題

`MigrationRunner` はバージョン管理テーブル `schema_migrations` を介して各マイグレーションを 1 回だけ適用するよう設計されているが、**個々の `Migration.Up()` 実装が冪等（二重実行安全）である保証がない**。

### 既存マイグレーション 9 個の冪等性ステータス

| # | クラス | 主な操作 | 冪等性 |
|---|--------|---------|-------|
| 001 | Initial | `CREATE TABLE IF NOT EXISTS` + `CREATE INDEX IF NOT EXISTS` + `INSERT OR IGNORE` | ✓ |
| 002 | AddPointRedemption | `ALTER TABLE ADD COLUMN` | ✗ |
| 003 | AddTripGroupId | `ALTER TABLE ADD COLUMN` | ✗ |
| 004 | AddPerformanceIndexes | `CREATE INDEX IF NOT EXISTS` | ✓ |
| 005 | AddStartingPageNumber | `ALTER TABLE ADD COLUMN` | ✗ |
| 006 | AddRefundedStatus | `ALTER TABLE ADD COLUMN` x2 | ✗ |
| 007 | AddMergeHistory | `CREATE TABLE IF NOT EXISTS` + `CREATE INDEX IF NOT EXISTS` | ✓ |
| 008 | AddCardTypeNumberUniqueIndex | `CREATE UNIQUE INDEX IF NOT EXISTS` + 重複解消 UPDATE | ✓（重複解消も冪等） |
| 009 | AddCarryoverTotals | `ALTER TABLE ADD COLUMN` x3 | ✗ |

### 実害シナリオ

- **共有モード**: 複数 PC が初回起動で同一 DB に対し同時にマイグレーション適用 → 片方が中途失敗後、もう一方が部分適用済み状態に遭遇してエラー
- **DB 部分破損/手動編集**: `schema_migrations` テーブルが失われた状態で再適用するとエラー
- **テスト環境**: マイグレーションを段階的に手動検証するときに再適用したくなる

### ドキュメントの負債

- `docs/manual/開発者ガイド.md §3.5`: `GetAllMigrations()` の手動登録と書かれているが実装は Reflection で自動検出 → **内容が古い**
- 新規マイグレーション作成時のテンプレートや冪等性チェックリストが存在しない

## スコープ

### 含む

1. `MigrationHelpers` 静的クラス新設（`AddColumnIfNotExists` / `HasColumn`）
2. 非冪等の 5 マイグレーション（002, 003, 005, 006, 009）の修正
3. `MigrationHelpers` 単体テスト
4. 全 9 マイグレーションに対する二重実行テスト
5. 開発者ガイド §3.5 改訂（自動検出ロジック反映、冪等性ガイドライン追加）
6. `.claude/rules/migrations.md` 新規作成（開発規約）

### 含まない

- `MigrationRunner` 本体の refactor
- `IMigration` インターフェース変更
- Migration_008 の重複解消ロジック改良
- 過去に適用済みの DB への影響（`schema_migrations` で既に弾かれる）

## 設計

### MigrationHelpers API

```csharp
namespace ICCardManager.Data.Migrations
{
    /// <summary>
    /// マイグレーション実装で冪等な SQL 操作を提供するヘルパー。
    /// Issue #1285: ALTER TABLE ADD COLUMN の二重実行安全化など。
    /// </summary>
    internal static class MigrationHelpers
    {
        /// <summary>
        /// PRAGMA table_info を使って列の存在を確認する。
        /// </summary>
        public static bool HasColumn(
            SQLiteConnection conn, SQLiteTransaction tx,
            string table, string column);

        /// <summary>
        /// 列が存在しない場合のみ ADD COLUMN を実行する（冪等）。
        /// </summary>
        /// <param name="typeAndConstraints">例: "INTEGER DEFAULT 0 NOT NULL"</param>
        public static void AddColumnIfNotExists(
            SQLiteConnection conn, SQLiteTransaction tx,
            string table, string column, string typeAndConstraints);
    }
}
```

### 既存マイグレーションの修正パターン

**Before (Migration_002):**
```csharp
var cmd = connection.CreateCommand();
cmd.Transaction = transaction;
cmd.CommandText = @"
    ALTER TABLE ledger_detail ADD COLUMN is_point_redemption INTEGER DEFAULT 0 NOT NULL
";
cmd.ExecuteNonQuery();
```

**After:**
```csharp
MigrationHelpers.AddColumnIfNotExists(
    connection, transaction,
    "ledger_detail", "is_point_redemption", "INTEGER DEFAULT 0 NOT NULL");
```

### なぜ `PRAGMA table_info()` 方式か

- SQLite 3.35.0+ は `ALTER TABLE ADD COLUMN IF NOT EXISTS` をサポートしないバージョンがまだ多い（`System.Data.SQLite` が同梱するバージョン依存）
- `PRAGMA table_info(<table>)` は全バージョンで利用可能
- エラーハンドリング型（try/catch "duplicate column"）より意図が明示的

### テスト戦略

#### `MigrationHelpersTests` (~6 件)
- `HasColumn` が存在する列で true を返す
- `HasColumn` が存在しない列で false を返す
- `HasColumn` が存在しないテーブルでも例外を投げず false を返す（あるいは実装の契約に応じて決定）
- `AddColumnIfNotExists` が新規列を追加
- `AddColumnIfNotExists` が既存列に対し no-op
- `AddColumnIfNotExists` を 2 回連続実行しても例外なし

#### `MigrationIdempotencyTests` (9 件)
各マイグレーション（001〜009）に対し:
```csharp
[Fact]
public void Migration_00X_Up_IsIdempotent()
{
    // 1 回目適用
    migration.Up(conn, tx1); tx1.Commit();
    // 2 回目適用（例外が出ないことが合格）
    using var tx2 = conn.BeginTransaction();
    Action act = () => migration.Up(conn, tx2);
    act.Should().NotThrow();
    tx2.Commit();
    // スキーマが正しく保たれていることを検証（任意の SELECT or PRAGMA）
}
```

Migration_008 は重複解消 UPDATE が 2 回目も実行されるが、べき等（同じ入力に対し同じ結果）。テストでは UPDATE 実行回数ではなく「最終状態が変わらない」ことを検証。

### 開発者ガイド §3.5 改訂方針

1. 自動検出ロジック（`DiscoverMigrations()`, Reflection）を正しく記述
2. 冪等性チェックリスト追加:
   - `CREATE TABLE IF NOT EXISTS` を使う
   - `CREATE INDEX IF NOT EXISTS` を使う
   - `ALTER TABLE ADD COLUMN` は `MigrationHelpers.AddColumnIfNotExists` 経由で
   - `INSERT` は `INSERT OR IGNORE` か `INSERT ... WHERE NOT EXISTS`
   - データ更新は「同じ入力に対し同じ結果」を意識
3. `.claude/rules/migrations.md` への参照

### `.claude/rules/migrations.md` 新設

- ファイル作成: 上記の冪等性チェックリストを開発規約として格納
- 既存の `testing.md` / `git-workflow.md` / `error-messages.md` と同じ体裁

## リスクと対策

| リスク | 対策 |
|-------|-----|
| 既存 DB（本番）への影響 | `schema_migrations` で既に適用済みの場合スキップされるので影響なし。新規インストールと部分適用時のみ挙動変化 |
| `PRAGMA table_info()` が transaction scope で動作するか | SQLite は read 系 PRAGMA を transaction 内で実行可能。テストで確認 |
| Migration_008 の重複解消が再実行される | 既に冪等な実装。テストで明示的に検証 |
| `.claude/rules/migrations.md` を hook が検出しない | hook はコード変更に対する自問なので問題なし |

## 非対象（別 Issue 候補）

- `MigrationRunner` のリファクタ（402 行で適切なサイズ）
- マイグレーション UP/DOWN の並列実行サポート
- マイグレーション失敗時のロールバック自動化（既にトランザクション境界で対応済み）
