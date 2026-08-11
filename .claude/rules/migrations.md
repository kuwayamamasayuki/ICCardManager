---
paths:
  - "ICCardManager/src/ICCardManager/Data/Migrations/**"
  - "ICCardManager/tests/ICCardManager.Tests/Data/Migrations/**"
  - "ICCardManager/docs/design/02_DB設計書.md"
---

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

## 冪等性の規約は「マイグレーション本体」だけでなく「適用記録の書き込み」にも掛かる（Issue #1738）

`Up()` をいくら冪等にしても、**適用記録の INSERT が素の `INSERT` なら共有モードの同時起動でアプリが起動不能になる**。`MigrateTo` は冒頭で `GetCurrentVersion()` を 1 回読むだけで、そのスナップショットは他 PC の適用で陳腐化する。しかも `ApplyMigration` の `BeginTransaction()` は `BEGIN IMMEDIATE`（System.Data.SQLite の既定 `IsolationLevel.Serializable`）のため、後発 PC は**先発 PC の COMMIT を待ってから、先発が入れた version を INSERT する**形になる。busy_timeout による待機は衝突を回避するどころか確定させる。

- **`schema_migrations` へ INSERT する箇所は 2 つある**。`MigrationRunner.RecordMigration`（通常のマイグレーション適用）と `DbContext.BackfillLegacyMigrationVersion1`（レガシー DB の version=1 補填）。後者は Issue #1484 で `INSERT OR IGNORE` 化されたが、**runner 側が取り残されて #1738 として再発した**。規約を直すときは同じテーブルを書く箇所を横断で洗うこと
- **`INSERT OR IGNORE` は PRIMARY KEY 衝突以外の制約違反も握りつぶす**。`NOT NULL` / `CHECK` の違反まで無視されると、記録が入らないまま成功扱いになり、`GetCurrentVersion()` が永久に上がらず**毎起動で同じ `Up()` が再適用され続ける無言の劣化**になる。`ExecuteNonQuery()` の戻り値が 0 のときは行の有無を読み戻し、「他 PC が先に記録した（正常）」と「書き込めなかった（異常）」を確定させること（`RecordMigration` が参考実装）
- **`DbContext.InitializeDatabase` のコメントを「PRIMARY KEY 制約により重複適用が防止される」と書かない**。PK が防ぐのは記録の重複であって、重複**適用**そのものではない（後発 PC は `Up()` を実際に再実行する）。誤った前提のコメントが「だから安全」という判断を下流に伝播させた

## `AddColumnIfNotExists` 引数の制約（Issue #1466）

`MigrationHelpers.AddColumnIfNotExists` / `HasColumn` は SQL の識別子をパラメータ化できないため文字列補間で組み立てるが、開発者の事故防止としてホワイトリスト regex による検証層を持つ。受理される値の範囲は以下:

- `table` / `column`: 識別子パターン `^[A-Za-z_][A-Za-z0-9_]*$`（英字または `_` で始まり、英数字または `_` のみ）
- `typeAndConstraints`: 型は `INTEGER` / `TEXT` / `REAL` / `BLOB` / `NUMERIC` のいずれか。制約は `NOT NULL` / `DEFAULT <整数|小数|'literal'|NULL>` / `REFERENCES <table>(<col>)` の組み合わせ。リテラル内の `'` や `;` は禁止

範囲外の値を渡すと `ArgumentException` が即座に投げられる。新しい型/制約（例: `BOOLEAN`、`UNIQUE`、`CHECK`）を使いたい場合は `MigrationHelpers.cs` の `TypeAndConstraintsPattern` を更新し、`MigrationHelpersTests` の Theory に positive/negative を追加すること。

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

- 設計書: `ICCardManager/docs/superpowers/specs/2026-04-19-issue-1285-migration-idempotency-design.md`
- 設計書: `ICCardManager/docs/superpowers/specs/2026-05-22-issue-1466-migration-helpers-validation-design.md`（引数検証）
- ヘルパー実装: `ICCardManager/src/ICCardManager/Data/Migrations/MigrationHelpers.cs`
- テスト例: `ICCardManager/tests/ICCardManager.Tests/Data/Migrations/MigrationIdempotencyTests.cs`
