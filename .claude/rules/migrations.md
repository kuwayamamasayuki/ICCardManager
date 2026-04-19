# マイグレーション作成規約

## 冪等性（二重実行安全）の必須化

新規マイグレーション (`IMigration` 実装) の `Up()` は**必ず冪等**に書くこと。共有モードで複数 PC が同時起動した場合や、`schema_migrations` テーブルが部分破損した場合に備える（Issue #1285）。

## 冪等パターン

| 操作 | 書き方 |
|------|--------|
| テーブル作成 | `CREATE TABLE IF NOT EXISTS ...` |
| インデックス作成 | `CREATE INDEX IF NOT EXISTS ...` / `CREATE UNIQUE INDEX IF NOT EXISTS ...` |
| 列追加 | `MigrationHelpers.AddColumnIfNotExists(conn, tx, "table", "column", "TYPE DEFAULT ...")` |
| 行追加 | `INSERT OR IGNORE INTO ...` |
| 行更新 | `UPDATE ... WHERE <条件が同じ入力で同じ結果を出す>` |

## 禁止パターン

- 素の `ALTER TABLE ... ADD COLUMN`（SQLite では `IF NOT EXISTS` が一部バージョンで非サポート）
- 素の `CREATE TABLE` / `CREATE INDEX`（必ず `IF NOT EXISTS` を付ける）
- 素の `INSERT`（主キー重複で失敗するため `INSERT OR IGNORE` を使う）

## 新規マイグレーション追加手順

1. `ICCardManager/src/ICCardManager/Data/Migrations/Migration_0NN_<説明>.cs` を作成
2. `IMigration` を実装
   - `Version` は既存最大 +1
   - `Description` は日本語で要約
3. `Up()` は上記「冪等パターン」に従って書く
4. `Down()` は可能ならロールバック、不可能なら空実装 + 理由コメント
5. `ICCardManager/tests/ICCardManager.Tests/Data/Migrations/MigrationIdempotencyTests.cs` に `Migration_0NN_<name>_Up_IsIdempotent` を追加
   - 先行マイグレーションを順に `RunMigrationOnce` して DB を準備し、対象を `RunMigrationTwice` で実行
6. スキーマに列追加した場合は `docs/design/02_DB設計書.md` の該当テーブルに反映

## 自動検出

`MigrationRunner.DiscoverMigrations()` が Reflection で `IMigration` 実装クラスを自動検出するため、**手動登録は不要**。`Version` プロパティ順に適用される。

## 参考

- 設計書: `docs/superpowers/specs/2026-04-19-issue-1285-migration-idempotency-design.md`
- ヘルパー実装: `ICCardManager/src/ICCardManager/Data/Migrations/MigrationHelpers.cs`
- テスト例: `ICCardManager/tests/ICCardManager.Tests/Data/Migrations/MigrationIdempotencyTests.cs`
