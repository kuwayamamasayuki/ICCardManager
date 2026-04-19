# Issue #1285: マイグレーション冪等性ガイドライン実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 非冪等な 5 マイグレーション（002/003/005/006/009）を `MigrationHelpers.AddColumnIfNotExists` 経由で冪等化し、9 マイグレーション全てに二重実行テストを追加する。開発者ガイドと `.claude/rules/migrations.md` で冪等性ガイドラインを文書化。

**Architecture:** `internal static MigrationHelpers` クラスを新設して `PRAGMA table_info()` ベースの列存在チェックを提供。既存 Migration の `ExecuteNonQuery("ALTER TABLE ADD COLUMN ...")` をヘルパー呼び出しに置換する。`IMigration` インターフェースや `MigrationRunner` は変更しない。

**Tech Stack:** C# 10 / .NET Framework 4.8 / System.Data.SQLite / xUnit / FluentAssertions

---

## 事前確認

- ブランチ: `feat/issue-1285-migration-idempotency`（main から分岐、spec commit 済み）
- 対象ファイル:
  - `ICCardManager/src/ICCardManager/Data/Migrations/Migration_002_AddPointRedemption.cs`
  - `ICCardManager/src/ICCardManager/Data/Migrations/Migration_003_AddTripGroupId.cs`
  - `ICCardManager/src/ICCardManager/Data/Migrations/Migration_005_AddStartingPageNumber.cs`
  - `ICCardManager/src/ICCardManager/Data/Migrations/Migration_006_AddRefundedStatus.cs`
  - `ICCardManager/src/ICCardManager/Data/Migrations/Migration_009_AddCarryoverTotals.cs`
- 既存テスト: 全体 2996 件 pass、Migration 系 32 件 pass
- Test コマンド: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~Migration" --nologo --verbosity minimal`

## File Structure

### 新規作成

| パス | 役割 |
|-----|------|
| `ICCardManager/src/ICCardManager/Data/Migrations/MigrationHelpers.cs` | `HasColumn` / `AddColumnIfNotExists` を提供する internal static クラス |
| `ICCardManager/tests/ICCardManager.Tests/Data/Migrations/MigrationHelpersTests.cs` | ヘルパーの単体テスト |
| `ICCardManager/tests/ICCardManager.Tests/Data/Migrations/MigrationIdempotencyTests.cs` | 各マイグレーション（9 個）の二重実行テスト |
| `.claude/rules/migrations.md` | 冪等性チェックリスト（開発規約） |

### 変更

- `Migration_002_AddPointRedemption.cs` (ALTER TABLE を helper 経由に)
- `Migration_003_AddTripGroupId.cs` (同上)
- `Migration_005_AddStartingPageNumber.cs` (同上)
- `Migration_006_AddRefundedStatus.cs` (同上)
- `Migration_009_AddCarryoverTotals.cs` (同上)
- `ICCardManager/docs/manual/開発者ガイド.md` §3.5（自動検出・冪等性ガイドライン）
- `ICCardManager/docs/design/05_クラス設計書.md`（MigrationHelpers 追記、任意）
- `ICCardManager/docs/design/07_テスト設計書.md`（新規テスト追記）
- `ICCardManager/CHANGELOG.md`（Unreleased リファクタリング）
- `CLAUDE.md`（`.claude/rules/migrations.md` への参照追加、任意）

---

## Task 1: Baseline 確認

**Files:** 参照のみ

- [ ] **Step 1: ブランチ確認**

```bash
git branch --show-current
```
Expected: `feat/issue-1285-migration-idempotency`

- [ ] **Step 2: 既存マイグレーションテスト pass 確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~Migration" --nologo --verbosity minimal
```
Expected: `成功! -失敗: 0、合格: 32`（件数は 30〜32 程度）

---

## Task 2: MigrationHelpers クラスと単体テスト

**Files:**
- Create: `ICCardManager/src/ICCardManager/Data/Migrations/MigrationHelpers.cs`
- Create: `ICCardManager/tests/ICCardManager.Tests/Data/Migrations/MigrationHelpersTests.cs`

- [ ] **Step 1: MigrationHelpers 本体を作成**

ファイル `ICCardManager/src/ICCardManager/Data/Migrations/MigrationHelpers.cs`:

```csharp
using System;
using System.Data.SQLite;

namespace ICCardManager.Data.Migrations
{
    /// <summary>
    /// マイグレーション実装で冪等な SQL 操作を提供するヘルパー（Issue #1285）。
    /// </summary>
    /// <remarks>
    /// SQLite の <c>ALTER TABLE ADD COLUMN</c> は二重実行時に "duplicate column" エラーを出すため、
    /// <c>PRAGMA table_info()</c> で事前に列の有無を確認する方式で冪等化する。
    /// </remarks>
    internal static class MigrationHelpers
    {
        /// <summary>
        /// 指定テーブルに指定列が存在するかを返す。
        /// </summary>
        public static bool HasColumn(
            SQLiteConnection connection,
            SQLiteTransaction transaction,
            string table,
            string column)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (string.IsNullOrWhiteSpace(table)) throw new ArgumentException("table must be non-empty", nameof(table));
            if (string.IsNullOrWhiteSpace(column)) throw new ArgumentException("column must be non-empty", nameof(column));

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // PRAGMA は識別子のパラメータ化をサポートしないため、単純な検証のみで埋め込む。
            // table はマイグレーション実装が文字列リテラルで渡す前提（外部入力ではない）。
            if (table.IndexOfAny(new[] { '\'', '"', ';', ' ' }) >= 0)
            {
                throw new ArgumentException($"invalid table name: {table}", nameof(table));
            }
            command.CommandText = $"PRAGMA table_info({table})";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                // PRAGMA table_info の 2 列目（index 1）が column name
                var name = reader.GetString(1);
                if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 列が存在しない場合のみ <c>ALTER TABLE ... ADD COLUMN</c> を実行する（冪等）。
        /// </summary>
        /// <param name="typeAndConstraints">例: <c>"INTEGER DEFAULT 0"</c>, <c>"TEXT"</c></param>
        public static void AddColumnIfNotExists(
            SQLiteConnection connection,
            SQLiteTransaction transaction,
            string table,
            string column,
            string typeAndConstraints)
        {
            if (HasColumn(connection, transaction, table, column))
            {
                return;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeAndConstraints}";
            command.ExecuteNonQuery();
        }
    }
}
```

- [ ] **Step 2: MigrationHelpersTests を作成**

ファイル `ICCardManager/tests/ICCardManager.Tests/Data/Migrations/MigrationHelpersTests.cs`:

```csharp
using System;
using System.Data.SQLite;
using FluentAssertions;
using ICCardManager.Data.Migrations;
using Xunit;

namespace ICCardManager.Tests.Data.Migrations
{
    /// <summary>
    /// Issue #1285: MigrationHelpers の冪等 SQL 操作の単体テスト。
    /// </summary>
    public class MigrationHelpersTests : IDisposable
    {
        private readonly SQLiteConnection _connection;

        public MigrationHelpersTests()
        {
            _connection = new SQLiteConnection("Data Source=:memory:");
            _connection.Open();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT)";
            cmd.ExecuteNonQuery();
        }

        public void Dispose()
        {
            _connection.Dispose();
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void HasColumn_ExistingColumn_ReturnsTrue()
        {
            using var tx = _connection.BeginTransaction();
            MigrationHelpers.HasColumn(_connection, tx, "t", "name").Should().BeTrue();
        }

        [Fact]
        public void HasColumn_MissingColumn_ReturnsFalse()
        {
            using var tx = _connection.BeginTransaction();
            MigrationHelpers.HasColumn(_connection, tx, "t", "nope").Should().BeFalse();
        }

        [Fact]
        public void HasColumn_CaseInsensitive()
        {
            using var tx = _connection.BeginTransaction();
            MigrationHelpers.HasColumn(_connection, tx, "t", "NAME").Should().BeTrue();
        }

        [Fact]
        public void HasColumn_InvalidTableName_Throws()
        {
            using var tx = _connection.BeginTransaction();
            Action act = () => MigrationHelpers.HasColumn(_connection, tx, "t; DROP TABLE t;", "x");
            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void AddColumnIfNotExists_NewColumn_IsAdded()
        {
            using var tx = _connection.BeginTransaction();
            MigrationHelpers.AddColumnIfNotExists(_connection, tx, "t", "extra", "INTEGER DEFAULT 0");
            tx.Commit();

            using var tx2 = _connection.BeginTransaction();
            MigrationHelpers.HasColumn(_connection, tx2, "t", "extra").Should().BeTrue();
        }

        [Fact]
        public void AddColumnIfNotExists_ExistingColumn_NoOp()
        {
            using var tx = _connection.BeginTransaction();
            MigrationHelpers.AddColumnIfNotExists(_connection, tx, "t", "extra", "INTEGER DEFAULT 0");
            tx.Commit();

            using var tx2 = _connection.BeginTransaction();
            Action act = () =>
                MigrationHelpers.AddColumnIfNotExists(_connection, tx2, "t", "extra", "INTEGER DEFAULT 0");
            act.Should().NotThrow();
            tx2.Commit();
        }

        [Fact]
        public void AddColumnIfNotExists_TwiceInSameCall_NoOp()
        {
            using var tx = _connection.BeginTransaction();
            MigrationHelpers.AddColumnIfNotExists(_connection, tx, "t", "extra2", "TEXT");
            Action act = () =>
                MigrationHelpers.AddColumnIfNotExists(_connection, tx, "t", "extra2", "TEXT");
            act.Should().NotThrow();
            tx.Commit();
        }
    }
}
```

- [ ] **Step 3: ビルド + テスト**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~MigrationHelpersTests" --nologo --verbosity minimal 2>&1 | tail -3
```
Expected: 合格 7 件

- [ ] **Step 4: コミット**

```bash
git add ICCardManager/src/ICCardManager/Data/Migrations/MigrationHelpers.cs \
       ICCardManager/tests/ICCardManager.Tests/Data/Migrations/MigrationHelpersTests.cs
git commit -m "$(cat <<'EOF'
feat: MigrationHelpers に冪等 ALTER TABLE ヘルパーを追加 (Issue #1285)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Migration_002 冪等化

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Data/Migrations/Migration_002_AddPointRedemption.cs`

- [ ] **Step 1: Up() を helper 呼び出しに置換**

L21-26 の `Up` メソッド本体を以下に置換:

```csharp
        public void Up(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // Issue #1285: AddColumnIfNotExists で冪等化
            MigrationHelpers.AddColumnIfNotExists(
                connection, transaction,
                "ledger_detail", "is_point_redemption", "INTEGER DEFAULT 0");
        }
```

不要になった `ExecuteNonQuery` ヘルパーは Down() で使い続けるため残す。using は既にある想定。

- [ ] **Step 2: 既存テスト pass 確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~Migration" --nologo --verbosity minimal 2>&1 | tail -3
```
Expected: 失敗 0

- [ ] **Step 3: コミット**

```bash
git add ICCardManager/src/ICCardManager/Data/Migrations/Migration_002_AddPointRedemption.cs
git commit -m "$(cat <<'EOF'
refactor: Migration_002 を冪等化 (Issue #1285)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Migration_003 冪等化

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Data/Migrations/Migration_003_AddTripGroupId.cs`

- [ ] **Step 1: Up() を helper 呼び出しに置換**

`Up` メソッド本体を以下に置換:

```csharp
        public void Up(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // Issue #1285: AddColumnIfNotExists で冪等化
            // NULL = 自動判定、同じ値 = 同一グループ（乗り継ぎ）として扱う
            MigrationHelpers.AddColumnIfNotExists(
                connection, transaction,
                "ledger_detail", "group_id", "INTEGER");
        }
```

- [ ] **Step 2: テスト**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~Migration" --nologo --verbosity minimal 2>&1 | tail -3
```
Expected: 失敗 0

- [ ] **Step 3: コミット**

```bash
git add ICCardManager/src/ICCardManager/Data/Migrations/Migration_003_AddTripGroupId.cs
git commit -m "$(cat <<'EOF'
refactor: Migration_003 を冪等化 (Issue #1285)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Migration_005 冪等化

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Data/Migrations/Migration_005_AddStartingPageNumber.cs`

- [ ] **Step 1: Up() を helper 呼び出しに置換**

```csharp
        public void Up(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // Issue #1285: AddColumnIfNotExists で冪等化
            MigrationHelpers.AddColumnIfNotExists(
                connection, transaction,
                "ic_card", "starting_page_number", "INTEGER DEFAULT 1");
        }
```

- [ ] **Step 2: テスト + コミット**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~Migration" --nologo --verbosity minimal 2>&1 | tail -3
# Expected: 失敗 0

git add ICCardManager/src/ICCardManager/Data/Migrations/Migration_005_AddStartingPageNumber.cs
git commit -m "$(cat <<'EOF'
refactor: Migration_005 を冪等化 (Issue #1285)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: Migration_006 冪等化

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Data/Migrations/Migration_006_AddRefundedStatus.cs`

- [ ] **Step 1: Up() を helper 呼び出しに置換**

```csharp
        public void Up(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // Issue #1285: AddColumnIfNotExists で冪等化
            MigrationHelpers.AddColumnIfNotExists(
                connection, transaction,
                "ic_card", "is_refunded", "INTEGER DEFAULT 0");
            MigrationHelpers.AddColumnIfNotExists(
                connection, transaction,
                "ic_card", "refunded_at", "TEXT");
        }
```

- [ ] **Step 2: テスト + コミット**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~Migration" --nologo --verbosity minimal 2>&1 | tail -3

git add ICCardManager/src/ICCardManager/Data/Migrations/Migration_006_AddRefundedStatus.cs
git commit -m "$(cat <<'EOF'
refactor: Migration_006 を冪等化 (Issue #1285)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: Migration_009 冪等化

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Data/Migrations/Migration_009_AddCarryoverTotals.cs`

- [ ] **Step 1: Up() を helper 呼び出しに置換**

```csharp
        public void Up(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // Issue #1285: AddColumnIfNotExists で冪等化
            MigrationHelpers.AddColumnIfNotExists(
                connection, transaction,
                "ic_card", "carryover_income_total", "INTEGER DEFAULT 0");
            MigrationHelpers.AddColumnIfNotExists(
                connection, transaction,
                "ic_card", "carryover_expense_total", "INTEGER DEFAULT 0");
            MigrationHelpers.AddColumnIfNotExists(
                connection, transaction,
                "ic_card", "carryover_fiscal_year", "INTEGER");
        }
```

- [ ] **Step 2: テスト + コミット**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~Migration" --nologo --verbosity minimal 2>&1 | tail -3

git add ICCardManager/src/ICCardManager/Data/Migrations/Migration_009_AddCarryoverTotals.cs
git commit -m "$(cat <<'EOF'
refactor: Migration_009 を冪等化 (Issue #1285)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: 全マイグレーション二重実行テスト

**Files:**
- Create: `ICCardManager/tests/ICCardManager.Tests/Data/Migrations/MigrationIdempotencyTests.cs`

- [ ] **Step 1: テストファイルを作成**

```csharp
using System;
using System.Data.SQLite;
using FluentAssertions;
using ICCardManager.Data.Migrations;
using Xunit;

namespace ICCardManager.Tests.Data.Migrations
{
    /// <summary>
    /// Issue #1285: 全マイグレーションの Up() が二重実行に対して安全（冪等）であることを検証。
    /// </summary>
    /// <remarks>
    /// MigrationRunner は schema_migrations でバージョン管理するが、共有モードでの競合や
    /// 部分適用状態を想定し、各マイグレーション自体の Up() が二重適用でも例外を投げないことを担保する。
    /// テストは MigrationRunner を介さず、Migration のインスタンスを作って直接 Up() を 2 回呼ぶ。
    /// </remarks>
    public class MigrationIdempotencyTests : IDisposable
    {
        private readonly SQLiteConnection _connection;

        public MigrationIdempotencyTests()
        {
            _connection = new SQLiteConnection("Data Source=:memory:");
            _connection.Open();
            // Initial migration で土台となる全テーブルを作成
            RunMigrationOnce(new Migration_001_Initial());
        }

        public void Dispose()
        {
            _connection.Dispose();
            GC.SuppressFinalize(this);
        }

        private void RunMigrationOnce(IMigration migration)
        {
            using var tx = _connection.BeginTransaction();
            migration.Up(_connection, tx);
            tx.Commit();
        }

        private Action RunMigrationTwice(IMigration migration)
        {
            return () =>
            {
                using (var tx1 = _connection.BeginTransaction())
                {
                    migration.Up(_connection, tx1);
                    tx1.Commit();
                }
                using (var tx2 = _connection.BeginTransaction())
                {
                    migration.Up(_connection, tx2);
                    tx2.Commit();
                }
            };
        }

        [Fact]
        public void Migration_001_Initial_Up_IsIdempotent()
        {
            // 001 は base class でも既に一度実行済み。2 回目を実行しても例外が出ないことを確認
            using var tx = _connection.BeginTransaction();
            Action act = () => new Migration_001_Initial().Up(_connection, tx);
            act.Should().NotThrow();
            tx.Commit();
        }

        [Fact]
        public void Migration_002_AddPointRedemption_Up_IsIdempotent()
        {
            Action act = RunMigrationTwice(new Migration_002_AddPointRedemption());
            act.Should().NotThrow();
        }

        [Fact]
        public void Migration_003_AddTripGroupId_Up_IsIdempotent()
        {
            RunMigrationOnce(new Migration_002_AddPointRedemption());
            Action act = RunMigrationTwice(new Migration_003_AddTripGroupId());
            act.Should().NotThrow();
        }

        [Fact]
        public void Migration_004_AddPerformanceIndexes_Up_IsIdempotent()
        {
            RunMigrationOnce(new Migration_002_AddPointRedemption());
            RunMigrationOnce(new Migration_003_AddTripGroupId());
            Action act = RunMigrationTwice(new Migration_004_AddPerformanceIndexes());
            act.Should().NotThrow();
        }

        [Fact]
        public void Migration_005_AddStartingPageNumber_Up_IsIdempotent()
        {
            RunMigrationOnce(new Migration_002_AddPointRedemption());
            RunMigrationOnce(new Migration_003_AddTripGroupId());
            RunMigrationOnce(new Migration_004_AddPerformanceIndexes());
            Action act = RunMigrationTwice(new Migration_005_AddStartingPageNumber());
            act.Should().NotThrow();
        }

        [Fact]
        public void Migration_006_AddRefundedStatus_Up_IsIdempotent()
        {
            RunMigrationOnce(new Migration_002_AddPointRedemption());
            RunMigrationOnce(new Migration_003_AddTripGroupId());
            RunMigrationOnce(new Migration_004_AddPerformanceIndexes());
            RunMigrationOnce(new Migration_005_AddStartingPageNumber());
            Action act = RunMigrationTwice(new Migration_006_AddRefundedStatus());
            act.Should().NotThrow();
        }

        [Fact]
        public void Migration_007_AddMergeHistory_Up_IsIdempotent()
        {
            RunMigrationOnce(new Migration_002_AddPointRedemption());
            RunMigrationOnce(new Migration_003_AddTripGroupId());
            RunMigrationOnce(new Migration_004_AddPerformanceIndexes());
            RunMigrationOnce(new Migration_005_AddStartingPageNumber());
            RunMigrationOnce(new Migration_006_AddRefundedStatus());
            Action act = RunMigrationTwice(new Migration_007_AddMergeHistory());
            act.Should().NotThrow();
        }

        [Fact]
        public void Migration_008_AddCardTypeNumberUniqueIndex_Up_IsIdempotent()
        {
            RunMigrationOnce(new Migration_002_AddPointRedemption());
            RunMigrationOnce(new Migration_003_AddTripGroupId());
            RunMigrationOnce(new Migration_004_AddPerformanceIndexes());
            RunMigrationOnce(new Migration_005_AddStartingPageNumber());
            RunMigrationOnce(new Migration_006_AddRefundedStatus());
            RunMigrationOnce(new Migration_007_AddMergeHistory());
            Action act = RunMigrationTwice(new Migration_008_AddCardTypeNumberUniqueIndex());
            act.Should().NotThrow();
        }

        [Fact]
        public void Migration_009_AddCarryoverTotals_Up_IsIdempotent()
        {
            RunMigrationOnce(new Migration_002_AddPointRedemption());
            RunMigrationOnce(new Migration_003_AddTripGroupId());
            RunMigrationOnce(new Migration_004_AddPerformanceIndexes());
            RunMigrationOnce(new Migration_005_AddStartingPageNumber());
            RunMigrationOnce(new Migration_006_AddRefundedStatus());
            RunMigrationOnce(new Migration_007_AddMergeHistory());
            RunMigrationOnce(new Migration_008_AddCardTypeNumberUniqueIndex());
            Action act = RunMigrationTwice(new Migration_009_AddCarryoverTotals());
            act.Should().NotThrow();
        }
    }
}
```

- [ ] **Step 2: テスト実行**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~MigrationIdempotencyTests" --nologo --verbosity minimal 2>&1 | tail -5
```

Expected: 9 件 pass

- [ ] **Step 3: コミット**

```bash
git add ICCardManager/tests/ICCardManager.Tests/Data/Migrations/MigrationIdempotencyTests.cs
git commit -m "$(cat <<'EOF'
test: 全マイグレーションの二重実行テストを追加 (Issue #1285)

Migration_001〜009 の Up() が冪等であることを検証。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: 開発規約ファイルと開発者ガイド

**Files:**
- Create: `.claude/rules/migrations.md`
- Modify: `ICCardManager/docs/manual/開発者ガイド.md` §3.5（L586-629 付近）

- [ ] **Step 1: `.claude/rules/migrations.md` を作成**

```markdown
# マイグレーション作成規約

## 冪等性（二重実行安全）の必須化

新規マイグレーション (`IMigration` 実装) の `Up()` は**必ず冪等**に書くこと。共有モードで複数 PC が同時起動した場合や、`schema_migrations` テーブルが部分破損した場合に備える。

## 冪等パターン

| 操作 | 書き方 |
|------|--------|
| テーブル作成 | `CREATE TABLE IF NOT EXISTS ...` |
| インデックス作成 | `CREATE INDEX IF NOT EXISTS ...` / `CREATE UNIQUE INDEX IF NOT EXISTS ...` |
| 列追加 | `MigrationHelpers.AddColumnIfNotExists(conn, tx, "table", "column", "TYPE DEFAULT ...")` |
| 行追加 | `INSERT OR IGNORE INTO ...` |
| 行更新 | `UPDATE ... WHERE <条件が同じ入力で同じ結果を出す>` |

## 禁止パターン

- 素の `ALTER TABLE ... ADD COLUMN`（SQLite では IF NOT EXISTS がサポートされない）
- 素の `CREATE TABLE` / `CREATE INDEX`（IF NOT EXISTS を付ける）
- 素の `INSERT`（二重実行で PK 重複エラー）

## 新規マイグレーション追加手順

1. `ICCardManager/src/ICCardManager/Data/Migrations/Migration_0NN_<説明>.cs` を作成
2. `IMigration` を実装（`Version` は既存最大 +1、`Description` は日本語で要約）
3. `Up()` は冪等パターンで書く
4. `Down()` は可能ならロールバック、不可能なら空実装 + コメント
5. `MigrationIdempotencyTests` に `Migration_0NN_<name>_Up_IsIdempotent` を追加（先行マイグレーションを順に `RunMigrationOnce` してから `RunMigrationTwice`）
6. `docs/design/02_DB設計書.md` に列追加を反映

## 自動検出

`MigrationRunner.DiscoverMigrations()` が Reflection で `IMigration` 実装を自動検出するため、**手動登録は不要**。

## 参考

- 設計書: `docs/superpowers/specs/2026-04-19-issue-1285-migration-idempotency-design.md`
- ヘルパー: `ICCardManager/src/ICCardManager/Data/Migrations/MigrationHelpers.cs`
```

- [ ] **Step 2: 開発者ガイド §3.5 を更新**

`ICCardManager/docs/manual/開発者ガイド.md` の §3.5 マイグレーション節（L586-629 付近）を読み、以下を反映:

1. 「`GetAllMigrations()` に手動登録」の記述を「自動検出 (`DiscoverMigrations()` via Reflection)」に修正
2. 新規マイグレーション作成時の冪等性チェックリスト追加（`.claude/rules/migrations.md` への参照含む）
3. `MigrationHelpers.AddColumnIfNotExists` の使い方を例示

具体的には、§3.5 の末尾に以下のような節を追加:

```markdown
#### 冪等性ガイドライン（Issue #1285）

全マイグレーションの `Up()` は二重実行しても例外を出さないこと（共有モード運用や部分適用状態に備える）。

- `ALTER TABLE ADD COLUMN` は `MigrationHelpers.AddColumnIfNotExists(...)` を使う
- `CREATE TABLE` / `CREATE INDEX` は `IF NOT EXISTS` を付ける
- `INSERT` は `INSERT OR IGNORE` を使う

詳細は `.claude/rules/migrations.md` 参照。全マイグレーションは `MigrationIdempotencyTests` で二重実行安全性を検証している。

登録は手動ではなく、`MigrationRunner.DiscoverMigrations()` が Reflection で自動検出する。
```

- [ ] **Step 3: コミット**

```bash
git add .claude/rules/migrations.md ICCardManager/docs/manual/開発者ガイド.md
git commit -m "$(cat <<'EOF'
docs: マイグレーション冪等性ガイドラインを追加 (Issue #1285)

- .claude/rules/migrations.md 新設
- 開発者ガイド §3.5 を自動検出ロジックに更新し、冪等性チェックリストを追加

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 10: 全体ビルド・テスト + CHANGELOG + 設計書

**Files:**
- Modify: `ICCardManager/CHANGELOG.md`
- Modify: `ICCardManager/docs/design/07_テスト設計書.md`

- [ ] **Step 1: 全体ビルドとテスト**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal 2>&1 | tail -5
```
Expected: エラー 0

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --nologo --verbosity minimal 2>&1 | tail -3
```
Expected: 失敗 0、合格は 2996 + 新規 7 + 9 = 3012 件程度

- [ ] **Step 2: CHANGELOG 更新**

`ICCardManager/CHANGELOG.md` の `[Unreleased]` の **リファクタリング** セクションに追記:

```markdown
- DB マイグレーションの冪等性（二重実行安全性）を担保。`MigrationHelpers.AddColumnIfNotExists` を新設し、非冪等だった 5 つの `ALTER TABLE ADD COLUMN` 型マイグレーション（#002/#003/#005/#006/#009）を冪等化。共有モードで複数 PC が初回起動時にマイグレーション競合した場合や、`schema_migrations` テーブル部分破損時の再適用エラーを防止。全 9 マイグレーションの二重実行テスト (`MigrationIdempotencyTests`) と `MigrationHelpers` 単体テスト (7 件) を追加。`.claude/rules/migrations.md` に冪等性チェックリストを新設し、開発者ガイド §3.5 を自動検出ロジック (`DiscoverMigrations()`) に合わせて更新（#1285）
```

- [ ] **Step 3: 07_テスト設計書.md を更新**

`ICCardManager/docs/design/07_テスト設計書.md` のマイグレーション関連セクションを探し（`grep -n "Migration" ICCardManager/docs/design/07_テスト設計書.md`）、以下のテスト追加を記載:

```markdown
#### UT-MIG-IDEMPOTENCY: マイグレーション二重実行テスト（Issue #1285）

| No | テストケース | 期待結果 |
|----|-------------|---------|
| 1 | Migration_001〜009 の各 Up() を 2 回連続実行 | 例外なし |
| 2 | MigrationHelpers.HasColumn（既存列） | true |
| 3 | MigrationHelpers.HasColumn（存在しない列） | false |
| 4 | MigrationHelpers.HasColumn（大小文字違い） | true |
| 5 | MigrationHelpers.HasColumn（不正テーブル名） | ArgumentException |
| 6 | AddColumnIfNotExists（新規列） | 列が追加される |
| 7 | AddColumnIfNotExists（既存列） | no-op、例外なし |
| 8 | AddColumnIfNotExists（同 tx 内で 2 回呼び出し） | 2 回目は no-op |

**テストクラス**: `MigrationHelpersTests` / `MigrationIdempotencyTests`
```

- [ ] **Step 4: コミット**

```bash
git add ICCardManager/CHANGELOG.md ICCardManager/docs/design/07_テスト設計書.md
git commit -m "$(cat <<'EOF'
docs: CHANGELOG とテスト設計書を Issue #1285 で更新

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 11: Push と PR 作成

- [ ] **Step 1: push**

```bash
git push -u origin feat/issue-1285-migration-idempotency
```

- [ ] **Step 2: PR 作成**

```bash
gh pr create --title "feat: マイグレーション冪等性の強化と二重実行テスト追加 (Issue #1285)" --body "$(cat <<'EOF'
## Summary
- 非冪等だった 5 マイグレーション (`#002`, `#003`, `#005`, `#006`, `#009`) を `MigrationHelpers.AddColumnIfNotExists` 経由で冪等化
- `PRAGMA table_info()` ベースの列存在判定で SQLite 全バージョンに対応
- 全 9 マイグレーションの二重実行テスト (`MigrationIdempotencyTests`, 9 件) と `MigrationHelpers` 単体テスト (7 件) を追加
- `.claude/rules/migrations.md` 新設 + 開発者ガイド §3.5 を自動検出ロジックに合わせて更新

## Related
- Closes #1285

## 冪等化した Migration
| # | Migration | 対象 |
|---|-----------|------|
| 002 | AddPointRedemption | `ledger_detail.is_point_redemption` |
| 003 | AddTripGroupId | `ledger_detail.group_id` |
| 005 | AddStartingPageNumber | `ic_card.starting_page_number` |
| 006 | AddRefundedStatus | `ic_card.is_refunded` / `refunded_at` |
| 009 | AddCarryoverTotals | `ic_card.carryover_income_total` / `carryover_expense_total` / `carryover_fiscal_year` |

## Test plan
- [x] `MigrationHelpersTests` 7 件 pass
- [x] `MigrationIdempotencyTests` 9 件 pass
- [x] 既存 `MigrationRunnerTests` / `Migration001InitialTests` / `DbContextMigrationTests` 全 pass
- [x] ソリューション全体テスト pass、ビルド 0 error
- [ ] 手動テスト: 既存 DB に対しアプリを起動し、マイグレーションが問題なく完了することを確認

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

Expected: PR URL

---

## 手動テスト依頼

1. **既存 DB でアプリ起動**: 既に v2.7 相当のマイグレーション適用済み DB でアプリを起動 → マイグレーションが発動せず正常起動
2. **新規 DB 初期化**: 空 DB（`ProgramData\ICCardManager\app.db` を削除）でアプリ起動 → 全 9 マイグレーションが 1 回で完走
3. **共有モード**: UNC 共有フォルダに DB を置き、2 台の PC でほぼ同時にアプリを起動 → 片方が適用中にもう片方が起動してもエラーにならない（トランザクション競合は MigrationRunner 側の既存実装で処理）

## リスクと対策

| リスク | 対策 |
|-------|-----|
| `PRAGMA table_info` がトランザクション内で動かない | Task 2 Step 3 テストで検証。SQLite は read 系 PRAGMA を tx 内で許容 |
| 既存 DB の既適用カラムに対し `AddColumnIfNotExists` が no-op → ユーザー影響 | 実装は idempotent なので no-op 自体が正しい動作 |
| `Migration_008` の重複解消 UPDATE が再実行で無駄に走る | 実装上既に冪等（同じ入力で同じ結果）。テストで検証 |
| `Down()` の非冪等性が残る | Down() は本 Issue スコープ外。管理者が手動で適用する場面のみで使われる想定 |

## 非対象

- `MigrationRunner` のリファクタ
- `IMigration` インターフェース変更
- Migration_002/003 の Down() における `CREATE TABLE backup` の冪等化（下位互換のため別 Issue 推奨）
