# Issue #1458 — OperationLogger と Ledger 操作の同一トランザクション化 実装プラン

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ledger 関連の 7 つの callsite で Ledger 操作と `operation_log` INSERT を同一トランザクションに統一し、SMB 共有モードで fsync 1 RTT を削減する。

**Architecture:** 既存の `LedgerRepository.InsertAsync(Ledger, SQLiteTransaction)` パターンを `OperationLogRepository.InsertAsync` と `OperationLogger.LogLedger*Async` に拡張。各 callsite は `using var scope = await _dbContext.BeginTransactionAsync()` で外側 txn を持ち、Ledger 操作とログ挿入を同じ tx で実行して `scope.Commit()` する。

**Tech Stack:** C# 10 / .NET Framework 4.8 / SQLite (`System.Data.SQLite`) / xUnit + FluentAssertions + Moq

**Build/Test commands (WSL2):**
- Build: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`
- Test: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --configuration Release`

**Branch:** `feat/issue-1458-operation-log-cotransaction` (既に作成済み)

**設計書参照:** `ICCardManager/docs/superpowers/specs/2026-05-21-issue-1458-operation-log-cotransaction-design.md`

---

## File Structure

**Source:**
- `ICCardManager/src/ICCardManager/Data/Repositories/IOperationLogRepository.cs` — 追加: tx 受入 `InsertAsync` 宣言
- `ICCardManager/src/ICCardManager/Data/Repositories/OperationLogRepository.cs` — 追加: tx 受入 `InsertAsync` 実装、既存版が新版に委譲
- `ICCardManager/src/ICCardManager/Data/Repositories/ILedgerRepository.cs` — 追加: tx 受入 `DeleteAsync`, `MergeLedgersAsync`, `ReplaceDetailsAsync` 宣言
- `ICCardManager/src/ICCardManager/Data/Repositories/LedgerRepository.cs` — 追加: 同上の実装。既存版が新版に委譲する形に統一
- `ICCardManager/src/ICCardManager/Services/OperationLogger.cs` — 追加: 5 つの `LogLedger*Async(..., SQLiteTransaction)` オーバーロード
- `ICCardManager/src/ICCardManager/ViewModels/LedgerRowEditViewModel.cs` — 修正: line 610 (Insert), 681 (Update)
- `ICCardManager/src/ICCardManager/ViewModels/MainViewModel.cs` — 修正: line 1644, 1698 (Delete)
- `ICCardManager/src/ICCardManager/ViewModels/LedgerDetailViewModel.cs` — 修正: line 592 (Update)
- `ICCardManager/src/ICCardManager/Services/LedgerMergeService.cs` — 修正: line 290-306 (Merge + Log を同 tx)
- `ICCardManager/src/ICCardManager/Services/LedgerSplitService.cs` — 修正: line 86-160 (Split 全体を 1 tx)

**Tests:**
- `ICCardManager/tests/ICCardManager.Tests/Repositories/OperationLogRepositoryTransactionTests.cs` — 新規
- `ICCardManager/tests/ICCardManager.Tests/Services/OperationLoggerTransactionTests.cs` — 新規
- `ICCardManager/tests/ICCardManager.Tests/Integration/LedgerLogAtomicityTests.cs` — 新規
- `ICCardManager/tests/ICCardManager.Tests/Services/LedgerMergeServiceTests.cs` — 修正（モック更新）
- `ICCardManager/tests/ICCardManager.Tests/Services/LedgerSplitServiceTests.cs` — 修正（モック更新）

**Docs:**
- `ICCardManager/docs/design/07_テスト設計書.md` — §1.1a 件数表、§8.1 テスト一覧
- `ICCardManager/CHANGELOG.md` — `### Unreleased` セクション

---

## Task 1: OperationLogRepository — tx 受入 `InsertAsync` オーバーロード

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Data/Repositories/IOperationLogRepository.cs`
- Modify: `ICCardManager/src/ICCardManager/Data/Repositories/OperationLogRepository.cs:24-47`
- Create: `ICCardManager/tests/ICCardManager.Tests/Repositories/OperationLogRepositoryTransactionTests.cs`

- [ ] **Step 1.1: 失敗するテストを書く**

Create `ICCardManager/tests/ICCardManager.Tests/Repositories/OperationLogRepositoryTransactionTests.cs`:

```csharp
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using System;
using System.Threading.Tasks;
using Xunit;

namespace ICCardManager.Tests.Repositories;

/// <summary>
/// OperationLogRepository.InsertAsync(OperationLog, SQLiteTransaction) のテスト (Issue #1458)
/// </summary>
public class OperationLogRepositoryTransactionTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly OperationLogRepository _repository;

    public OperationLogRepositoryTransactionTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();
        _repository = new OperationLogRepository(_dbContext);
    }

    public void Dispose()
    {
        _dbContext?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task InsertAsync_WithTransactionCommitted_RowIsVisible()
    {
        // Arrange
        var log = new OperationLog
        {
            Timestamp = DateTime.Now,
            OperatorIdm = "1111111111111111",
            OperatorName = "テスト操作者",
            TargetTable = "ledger",
            TargetId = "1",
            Action = "INSERT",
            BeforeData = null,
            AfterData = "{\"foo\":1}"
        };

        // Act
        int id;
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            id = await _repository.InsertAsync(log, scope.Transaction);
            scope.Commit();
        }

        // Assert
        id.Should().BeGreaterThan(0);
        var logs = await _repository.GetByDateRangeAsync(DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1));
        logs.Should().ContainSingle(l => l.Id == id);
    }

    [Fact]
    public async Task InsertAsync_WithTransactionRolledBack_RowIsNotVisible()
    {
        // Arrange
        var log = new OperationLog
        {
            Timestamp = DateTime.Now,
            OperatorIdm = "2222222222222222",
            OperatorName = "テスト操作者",
            TargetTable = "ledger",
            TargetId = "1",
            Action = "INSERT",
            BeforeData = null,
            AfterData = "{\"foo\":1}"
        };

        // Act
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            await _repository.InsertAsync(log, scope.Transaction);
            // Commit せず scope を dispose → 自動 rollback
        }

        // Assert
        var logs = await _repository.GetByDateRangeAsync(DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1));
        logs.Should().BeEmpty();
    }
}
```

- [ ] **Step 1.2: テストを実行して失敗を確認**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter "FullyQualifiedName~OperationLogRepositoryTransactionTests"`
Expected: コンパイルエラー (`InsertAsync(log, transaction)` オーバーロードが未定義)

- [ ] **Step 1.3: インターフェースにオーバーロード追加**

In `IOperationLogRepository.cs`, after the existing `Task<int> InsertAsync(OperationLog log);` declaration (line 87), add:

```csharp
        /// <summary>
        /// 操作ログを既存トランザクション内で記録する (Issue #1458)。
        /// </summary>
        /// <param name="log">記録するログ</param>
        /// <param name="transaction">既存トランザクション</param>
        /// <returns>挿入されたログのID</returns>
        Task<int> InsertAsync(OperationLog log, System.Data.SQLite.SQLiteTransaction transaction);
```

- [ ] **Step 1.4: OperationLogRepository に実装追加**

In `OperationLogRepository.cs`, replace the existing `InsertAsync` (lines 24-47) with:

```csharp
        /// <inheritdoc/>
        public Task<int> InsertAsync(OperationLog log) => InsertAsync(log, transaction: null);

        /// <inheritdoc/>
        public async Task<int> InsertAsync(OperationLog log, SQLiteTransaction transaction)
        {
            ConnectionLease lease = null;
            try
            {
                SQLiteConnection connection;
                if (transaction != null)
                {
                    connection = (SQLiteConnection)transaction.Connection;
                }
                else
                {
                    lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
                    connection = lease.Connection;
                }

                using var command = connection.CreateCommand();
                if (transaction != null)
                {
                    command.Transaction = transaction;
                }
                command.CommandText = @"INSERT INTO operation_log (timestamp, operator_idm, operator_name, target_table,
                           target_id, action, before_data, after_data)
VALUES (@timestamp, @operatorIdm, @operatorName, @targetTable,
       @targetId, @action, @beforeData, @afterData);
SELECT last_insert_rowid();";

                command.Parameters.AddWithValue("@timestamp", log.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"));
                command.Parameters.AddWithValue("@operatorIdm", log.OperatorIdm);
                command.Parameters.AddWithValue("@operatorName", log.OperatorName);
                command.Parameters.AddWithValue("@targetTable", (object)log.TargetTable ?? DBNull.Value);
                command.Parameters.AddWithValue("@targetId", (object)log.TargetId ?? DBNull.Value);
                command.Parameters.AddWithValue("@action", (object)log.Action ?? DBNull.Value);
                command.Parameters.AddWithValue("@beforeData", (object)log.BeforeData ?? DBNull.Value);
                command.Parameters.AddWithValue("@afterData", (object)log.AfterData ?? DBNull.Value);

                var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
                return Convert.ToInt32(result);
            }
            finally
            {
                lease?.Dispose();
            }
        }
```

Also ensure `ConnectionLease` namespace is available (it's in `ICCardManager.Data`, already imported at top).

- [ ] **Step 1.5: テストを実行してパスを確認**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter "FullyQualifiedName~OperationLogRepositoryTransactionTests"`
Expected: PASS (2 tests)

- [ ] **Step 1.6: 既存 OperationLogRepositoryTests がパスすることを確認**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter "FullyQualifiedName~OperationLogRepositoryTests"`
Expected: 既存テストもすべて PASS

- [ ] **Step 1.7: Commit**

```bash
git add ICCardManager/src/ICCardManager/Data/Repositories/IOperationLogRepository.cs \
        ICCardManager/src/ICCardManager/Data/Repositories/OperationLogRepository.cs \
        ICCardManager/tests/ICCardManager.Tests/Repositories/OperationLogRepositoryTransactionTests.cs
git commit -m "$(cat <<'EOF'
feat: OperationLogRepository.InsertAsync に tx 受入オーバーロード追加 (Issue #1458)

Ledger 操作と同一トランザクション内で監査ログを INSERT できるよう
オーバーロードを追加。既存 InsertAsync(log) は新版に transaction=null で委譲する。

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: LedgerRepository.DeleteAsync — tx 受入オーバーロード

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Data/Repositories/ILedgerRepository.cs`
- Modify: `ICCardManager/src/ICCardManager/Data/Repositories/LedgerRepository.cs:279-297`

- [ ] **Step 2.1: 失敗するテストを書く**

In `ICCardManager/tests/ICCardManager.Tests/Repositories/LedgerRepositoryTests.cs`（既存ファイル）にテストを追加。ファイル末尾の `}` 直前に挿入:

```csharp
    #region DeleteAsync(int, SQLiteTransaction) テスト (Issue #1458)

    [Fact]
    public async Task DeleteAsync_WithTransactionCommitted_RemovesRow()
    {
        // Arrange
        var ledger = CreateValidLedger(cardIdm: "1111111111111111");
        var id = await _repository.InsertAsync(ledger);

        // Act
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            await _repository.DeleteAsync(id, scope.Transaction);
            scope.Commit();
        }

        // Assert
        var result = await _repository.GetByIdAsync(id);
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_WithTransactionRolledBack_RowRemains()
    {
        // Arrange
        var ledger = CreateValidLedger(cardIdm: "2222222222222222");
        var id = await _repository.InsertAsync(ledger);

        // Act
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            await _repository.DeleteAsync(id, scope.Transaction);
            // Commit せず → 自動 rollback
        }

        // Assert
        var result = await _repository.GetByIdAsync(id);
        result.Should().NotBeNull();
    }

    #endregion
```

注: `CreateValidLedger` ヘルパは既存テストで定義済みのはず。なければ既存パターンに合わせて作成。

- [ ] **Step 2.2: テストを実行して失敗を確認**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter "FullyQualifiedName~LedgerRepositoryTests.DeleteAsync_WithTransaction"`
Expected: コンパイルエラー (`DeleteAsync(int, SQLiteTransaction)` 未定義)

- [ ] **Step 2.3: インターフェースにオーバーロード追加**

`ILedgerRepository.cs` の既存 `Task<bool> DeleteAsync(int id);` の宣言の直後に追加:

```csharp
        /// <summary>
        /// 履歴を既存トランザクション内で削除する (Issue #1458)。
        /// </summary>
        /// <param name="id">履歴ID</param>
        /// <param name="transaction">既存トランザクション</param>
        /// <returns>削除に成功した場合true</returns>
        Task<bool> DeleteAsync(int id, System.Data.SQLite.SQLiteTransaction transaction);
```

- [ ] **Step 2.4: LedgerRepository に実装追加**

`LedgerRepository.cs` line 279-297 の既存 `DeleteAsync` を置き換え:

```csharp
        /// <inheritdoc/>
        public Task<bool> DeleteAsync(int id) => DeleteAsync(id, transaction: null);

        /// <inheritdoc/>
        public async Task<bool> DeleteAsync(int id, SQLiteTransaction transaction)
        {
            ConnectionLease lease = null;
            try
            {
                SQLiteConnection connection;
                if (transaction != null)
                {
                    connection = (SQLiteConnection)transaction.Connection;
                }
                else
                {
                    lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
                    connection = lease.Connection;
                }

                // 詳細レコードを先に削除
                using (var deleteDetailCommand = connection.CreateCommand())
                {
                    if (transaction != null) deleteDetailCommand.Transaction = transaction;
                    deleteDetailCommand.CommandText = "DELETE FROM ledger_detail WHERE ledger_id = @id";
                    deleteDetailCommand.Parameters.AddWithValue("@id", id);
                    await deleteDetailCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                // メインレコードを削除
                using var command = connection.CreateCommand();
                if (transaction != null) command.Transaction = transaction;
                command.CommandText = "DELETE FROM ledger WHERE id = @id";
                command.Parameters.AddWithValue("@id", id);

                var result = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                return result > 0;
            }
            finally
            {
                lease?.Dispose();
            }
        }
```

- [ ] **Step 2.5: テスト実行**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter "FullyQualifiedName~LedgerRepositoryTests"`
Expected: 既存テスト + 2 new tests PASS

- [ ] **Step 2.6: Commit**

```bash
git add ICCardManager/src/ICCardManager/Data/Repositories/ILedgerRepository.cs \
        ICCardManager/src/ICCardManager/Data/Repositories/LedgerRepository.cs \
        ICCardManager/tests/ICCardManager.Tests/Repositories/LedgerRepositoryTests.cs
git commit -m "$(cat <<'EOF'
feat: LedgerRepository.DeleteAsync に tx 受入オーバーロード追加 (Issue #1458)

履歴削除と監査ログ INSERT を同一トランザクションで行えるよう拡張。
既存 DeleteAsync(id) は新版に transaction=null で委譲。

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: LedgerRepository.MergeLedgersAsync — tx 受入オーバーロード

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Data/Repositories/ILedgerRepository.cs`
- Modify: `ICCardManager/src/ICCardManager/Data/Repositories/LedgerRepository.cs:979-1029`

- [ ] **Step 3.1: 失敗するテストを書く**

`LedgerRepositoryTests.cs` 末尾に追加:

```csharp
    #region MergeLedgersAsync(..., SQLiteTransaction) テスト (Issue #1458)

    [Fact]
    public async Task MergeLedgersAsync_WithTransactionCommitted_MergesLedgers()
    {
        // Arrange
        var target = CreateValidLedger(cardIdm: "1111111111111111");
        target.Summary = "ターゲット";
        var targetId = await _repository.InsertAsync(target);

        var source = CreateValidLedger(cardIdm: "1111111111111111");
        source.Summary = "ソース";
        var sourceId = await _repository.InsertAsync(source);

        target.Id = targetId;
        target.Summary = "統合後";

        // Act
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            var success = await _repository.MergeLedgersAsync(targetId, new[] { sourceId }, target, scope.Transaction);
            success.Should().BeTrue();
            scope.Commit();
        }

        // Assert
        var afterTarget = await _repository.GetByIdAsync(targetId);
        afterTarget.Should().NotBeNull();
        afterTarget!.Summary.Should().Be("統合後");
        var afterSource = await _repository.GetByIdAsync(sourceId);
        afterSource.Should().BeNull();
    }

    [Fact]
    public async Task MergeLedgersAsync_WithTransactionRolledBack_LeavesLedgersUnchanged()
    {
        // Arrange
        var target = CreateValidLedger(cardIdm: "2222222222222222");
        target.Summary = "ターゲット";
        var targetId = await _repository.InsertAsync(target);

        var source = CreateValidLedger(cardIdm: "2222222222222222");
        source.Summary = "ソース";
        var sourceId = await _repository.InsertAsync(source);

        target.Id = targetId;
        target.Summary = "統合後（rollback されるべき）";

        // Act
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            await _repository.MergeLedgersAsync(targetId, new[] { sourceId }, target, scope.Transaction);
            // Commit せず → 自動 rollback
        }

        // Assert
        var afterTarget = await _repository.GetByIdAsync(targetId);
        afterTarget!.Summary.Should().Be("ターゲット");
        var afterSource = await _repository.GetByIdAsync(sourceId);
        afterSource.Should().NotBeNull();
    }

    #endregion
```

- [ ] **Step 3.2: テストを実行して失敗を確認**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter "FullyQualifiedName~LedgerRepositoryTests.MergeLedgersAsync_WithTransaction"`
Expected: コンパイルエラー

- [ ] **Step 3.3: インターフェース更新**

`ILedgerRepository.cs` の既存 `MergeLedgersAsync` 宣言の直後に追加:

```csharp
        /// <summary>
        /// 履歴を既存トランザクション内で統合する (Issue #1458)。
        /// </summary>
        Task<bool> MergeLedgersAsync(
            int targetLedgerId,
            IEnumerable<int> sourceLedgerIds,
            Ledger updatedTarget,
            System.Data.SQLite.SQLiteTransaction transaction);
```

- [ ] **Step 3.4: LedgerRepository に実装追加**

`LedgerRepository.cs` line 979-1029 の既存 `MergeLedgersAsync` を以下に置き換え:

```csharp
        /// <inheritdoc/>
        public async Task<bool> MergeLedgersAsync(int targetLedgerId, IEnumerable<int> sourceLedgerIds, Ledger updatedTarget)
        {
            using var scope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false);
            var result = await MergeLedgersAsync(targetLedgerId, sourceLedgerIds, updatedTarget, scope.Transaction).ConfigureAwait(false);
            if (result)
            {
                scope.Commit();
            }
            return result;
        }

        /// <inheritdoc/>
        public async Task<bool> MergeLedgersAsync(
            int targetLedgerId,
            IEnumerable<int> sourceLedgerIds,
            Ledger updatedTarget,
            SQLiteTransaction transaction)
        {
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            var sourceIds = sourceLedgerIds.ToList();
            var connection = (SQLiteConnection)transaction.Connection;

            // 1. ソースの詳細をターゲットに移動（UPDATEでrowid保持）
            foreach (var sourceId in sourceIds)
            {
                using var moveCommand = connection.CreateCommand();
                moveCommand.Transaction = transaction;
                moveCommand.CommandText = "UPDATE ledger_detail SET ledger_id = @targetId WHERE ledger_id = @sourceId";
                moveCommand.Parameters.AddWithValue("@targetId", targetLedgerId);
                moveCommand.Parameters.AddWithValue("@sourceId", sourceId);
                await moveCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            // 2. ターゲットLedgerを更新
            using (var updateCommand = connection.CreateCommand())
            {
                updateCommand.Transaction = transaction;
                updateCommand.CommandText = @"UPDATE ledger
SET summary = @summary, income = @income, expense = @expense,
    balance = @balance, note = @note
WHERE id = @id";
                updateCommand.Parameters.AddWithValue("@summary", updatedTarget.Summary);
                updateCommand.Parameters.AddWithValue("@income", updatedTarget.Income);
                updateCommand.Parameters.AddWithValue("@expense", updatedTarget.Expense);
                updateCommand.Parameters.AddWithValue("@balance", updatedTarget.Balance);
                updateCommand.Parameters.AddWithValue("@note", (object)updatedTarget.Note ?? DBNull.Value);
                updateCommand.Parameters.AddWithValue("@id", targetLedgerId);
                await updateCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            // 3. ソースLedgerを削除（detailsは既に移動済み）
            foreach (var sourceId in sourceIds)
            {
                using var deleteCommand = connection.CreateCommand();
                deleteCommand.Transaction = transaction;
                deleteCommand.CommandText = "DELETE FROM ledger WHERE id = @id";
                deleteCommand.Parameters.AddWithValue("@id", sourceId);
                await deleteCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            return true;
        }
```

- [ ] **Step 3.5: テスト実行**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter "FullyQualifiedName~LedgerRepositoryTests.MergeLedgersAsync"`
Expected: 既存 + 新規 2 件 PASS

- [ ] **Step 3.6: Commit**

```bash
git add ICCardManager/src/ICCardManager/Data/Repositories/ILedgerRepository.cs \
        ICCardManager/src/ICCardManager/Data/Repositories/LedgerRepository.cs \
        ICCardManager/tests/ICCardManager.Tests/Repositories/LedgerRepositoryTests.cs
git commit -m "$(cat <<'EOF'
feat: LedgerRepository.MergeLedgersAsync に tx 受入オーバーロード追加 (Issue #1458)

既存 MergeLedgersAsync(3 引数) を新オーバーロードに委譲する形に統一。
監査ログ INSERT を同一トランザクションで連結できるようにする。

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: LedgerRepository.ReplaceDetailsAsync — tx 受入オーバーロード

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Data/Repositories/ILedgerRepository.cs`
- Modify: `ICCardManager/src/ICCardManager/Data/Repositories/LedgerRepository.cs:963-976`

- [ ] **Step 4.1: 失敗するテストを書く**

`LedgerRepositoryTests.cs` 末尾に追加:

```csharp
    #region ReplaceDetailsAsync(..., SQLiteTransaction) テスト (Issue #1458)

    [Fact]
    public async Task ReplaceDetailsAsync_WithTransactionCommitted_ReplacesDetails()
    {
        // Arrange
        var ledger = CreateValidLedger(cardIdm: "3333333333333333");
        var ledgerId = await _repository.InsertAsync(ledger);

        var oldDetails = new[] { CreateValidDetail(ledgerId, sequence: 1, balance: 100) };
        await _repository.InsertDetailsAsync(ledgerId, oldDetails);

        var newDetails = new[]
        {
            CreateValidDetail(ledgerId, sequence: 1, balance: 200),
            CreateValidDetail(ledgerId, sequence: 2, balance: 300)
        };

        // Act
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            await _repository.ReplaceDetailsAsync(ledgerId, newDetails, scope.Transaction);
            scope.Commit();
        }

        // Assert
        var details = (await _repository.GetByIdAsync(ledgerId))!.Details;
        details.Should().HaveCount(2);
        details.Select(d => d.Balance).Should().Contain(new[] { 200, 300 });
    }

    #endregion
```

注: `CreateValidDetail` ヘルパは既存テストに合わせ追加。

- [ ] **Step 4.2: テストを実行して失敗を確認**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter "FullyQualifiedName~ReplaceDetailsAsync_WithTransaction"`
Expected: コンパイルエラー

- [ ] **Step 4.3: インターフェース更新**

`ILedgerRepository.cs` の既存 `ReplaceDetailsAsync` 宣言の直後に追加:

```csharp
        /// <summary>
        /// 履歴詳細を既存トランザクション内で全置換する (Issue #1458)。
        /// </summary>
        Task<bool> ReplaceDetailsAsync(int ledgerId, IEnumerable<LedgerDetail> details, System.Data.SQLite.SQLiteTransaction transaction);
```

- [ ] **Step 4.4: LedgerRepository に実装追加**

`LedgerRepository.cs` line 963-976 の既存 `ReplaceDetailsAsync` を以下に置き換え:

```csharp
        /// <inheritdoc/>
        public Task<bool> ReplaceDetailsAsync(int ledgerId, IEnumerable<LedgerDetail> details)
            => ReplaceDetailsAsync(ledgerId, details, transaction: null);

        /// <inheritdoc/>
        public async Task<bool> ReplaceDetailsAsync(int ledgerId, IEnumerable<LedgerDetail> details, SQLiteTransaction transaction)
        {
            ConnectionLease lease = null;
            try
            {
                SQLiteConnection connection;
                if (transaction != null)
                {
                    connection = (SQLiteConnection)transaction.Connection;
                }
                else
                {
                    lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
                    connection = lease.Connection;
                }

                // 既存の詳細をすべて削除
                using (var deleteCommand = connection.CreateCommand())
                {
                    if (transaction != null) deleteCommand.Transaction = transaction;
                    deleteCommand.CommandText = "DELETE FROM ledger_detail WHERE ledger_id = @ledgerId";
                    deleteCommand.Parameters.AddWithValue("@ledgerId", ledgerId);
                    await deleteCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                // 新しい詳細を登録（同一 tx 利用）
                return await InsertDetailsAsync(ledgerId, details, transaction).ConfigureAwait(false);
            }
            finally
            {
                lease?.Dispose();
            }
        }
```

- [ ] **Step 4.5: テスト実行**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter "FullyQualifiedName~LedgerRepositoryTests"`
Expected: 既存 + 新規 PASS

- [ ] **Step 4.6: Commit**

```bash
git add ICCardManager/src/ICCardManager/Data/Repositories/ILedgerRepository.cs \
        ICCardManager/src/ICCardManager/Data/Repositories/LedgerRepository.cs \
        ICCardManager/tests/ICCardManager.Tests/Repositories/LedgerRepositoryTests.cs
git commit -m "$(cat <<'EOF'
feat: LedgerRepository.ReplaceDetailsAsync に tx 受入オーバーロード追加 (Issue #1458)

LedgerSplitService が 1 トランザクションで複数 Ledger を更新できるよう
詳細置換 API を tx 対応に拡張。既存版は新版に transaction=null で委譲。

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: OperationLogger — 5 つの tx 受入オーバーロード

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Services/OperationLogger.cs`
- Create: `ICCardManager/tests/ICCardManager.Tests/Services/OperationLoggerTransactionTests.cs`

- [ ] **Step 5.1: 失敗するテストを書く**

Create `ICCardManager/tests/ICCardManager.Tests/Services/OperationLoggerTransactionTests.cs`:

```csharp
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Moq;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Threading.Tasks;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// OperationLogger の tx 受入オーバーロードのテスト (Issue #1458)
/// </summary>
public class OperationLoggerTransactionTests
{
    private readonly Mock<IOperationLogRepository> _repoMock;
    private readonly Mock<ICurrentOperatorContext> _ctxMock;
    private readonly OperationLogger _logger;

    public OperationLoggerTransactionTests()
    {
        _repoMock = new Mock<IOperationLogRepository>();
        _ctxMock = new Mock<ICurrentOperatorContext>();
        _ctxMock.SetupGet(c => c.HasSession).Returns(true);
        _ctxMock.SetupGet(c => c.CurrentIdm).Returns("1111111111111111");
        _ctxMock.SetupGet(c => c.CurrentName).Returns("テスト操作者");
        _logger = new OperationLogger(_repoMock.Object, _ctxMock.Object);
    }

    [Fact]
    public async Task LogLedgerInsertAsync_WithTransaction_PassesTransactionToRepository()
    {
        // Arrange
        SQLiteTransaction tx = null; // モック用ダミー（実際は null でも検証可能）
        var ledger = new Ledger { Id = 42 };

        // Act
        await _logger.LogLedgerInsertAsync(ledger, tx);

        // Assert
        _repoMock.Verify(r => r.InsertAsync(
            It.Is<OperationLog>(l =>
                l.TargetTable == OperationLogger.Tables.Ledger &&
                l.TargetId == "42" &&
                l.Action == OperationLogger.Actions.Insert),
            tx), Times.Once);
    }

    [Fact]
    public async Task LogLedgerUpdateAsync_WithTransaction_PassesTransactionToRepository()
    {
        SQLiteTransaction tx = null;
        var before = new Ledger { Id = 42, Summary = "前" };
        var after = new Ledger { Id = 42, Summary = "後" };

        await _logger.LogLedgerUpdateAsync(before, after, tx);

        _repoMock.Verify(r => r.InsertAsync(
            It.Is<OperationLog>(l =>
                l.TargetTable == OperationLogger.Tables.Ledger &&
                l.TargetId == "42" &&
                l.Action == OperationLogger.Actions.Update),
            tx), Times.Once);
    }

    [Fact]
    public async Task LogLedgerDeleteAsync_WithTransaction_PassesTransactionToRepository()
    {
        SQLiteTransaction tx = null;
        var ledger = new Ledger { Id = 42 };

        await _logger.LogLedgerDeleteAsync(ledger, tx);

        _repoMock.Verify(r => r.InsertAsync(
            It.Is<OperationLog>(l =>
                l.TargetTable == OperationLogger.Tables.Ledger &&
                l.TargetId == "42" &&
                l.Action == OperationLogger.Actions.Delete),
            tx), Times.Once);
    }

    [Fact]
    public async Task LogLedgerMergeAsync_WithTransaction_PassesTransactionToRepository()
    {
        SQLiteTransaction tx = null;
        var sources = new List<Ledger> { new() { Id = 1 }, new() { Id = 2 } };
        var merged = new Ledger { Id = 42 };

        await _logger.LogLedgerMergeAsync(sources, merged, tx);

        _repoMock.Verify(r => r.InsertAsync(
            It.Is<OperationLog>(l =>
                l.TargetTable == OperationLogger.Tables.Ledger &&
                l.TargetId == "42" &&
                l.Action == OperationLogger.Actions.Merge),
            tx), Times.Once);
    }

    [Fact]
    public async Task LogLedgerSplitAsync_WithTransaction_PassesTransactionToRepository()
    {
        SQLiteTransaction tx = null;
        var original = new Ledger { Id = 42 };
        var splits = new List<Ledger> { new() { Id = 42 }, new() { Id = 43 } };

        await _logger.LogLedgerSplitAsync(original, splits, tx);

        _repoMock.Verify(r => r.InsertAsync(
            It.Is<OperationLog>(l =>
                l.TargetTable == OperationLogger.Tables.Ledger &&
                l.TargetId == "42" &&
                l.Action == OperationLogger.Actions.Split),
            tx), Times.Once);
    }
}
```

- [ ] **Step 5.2: テスト実行で失敗を確認**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter "FullyQualifiedName~OperationLoggerTransactionTests"`
Expected: コンパイルエラー（5 つの tx 受入メソッド未定義）

- [ ] **Step 5.3: OperationLogger にオーバーロード追加**

`OperationLogger.cs` の `#region 新 API` の末尾（line 449 `#endregion` の直前）に追加:

```csharp
        #region tx 受入オーバーロード — Issue #1458

        /// <summary>
        /// 履歴挿入のログを既存トランザクションで記録する (Issue #1458)。
        /// </summary>
        public async Task LogLedgerInsertAsync(Ledger ledger, SQLiteTransaction transaction)
        {
            var (idm, name) = ResolveOperator();
            await _operationLogRepository.InsertAsync(new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = idm,
                OperatorName = name,
                TargetTable = Tables.Ledger,
                TargetId = ledger.Id.ToString(),
                Action = Actions.Insert,
                BeforeData = null,
                AfterData = SerializeToJson(ledger)
            }, transaction).ConfigureAwait(false);
        }

        /// <summary>
        /// 履歴更新のログを既存トランザクションで記録する (Issue #1458)。
        /// </summary>
        public async Task LogLedgerUpdateAsync(Ledger beforeLedger, Ledger afterLedger, SQLiteTransaction transaction)
        {
            var (idm, name) = ResolveOperator();
            await _operationLogRepository.InsertAsync(new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = idm,
                OperatorName = name,
                TargetTable = Tables.Ledger,
                TargetId = afterLedger.Id.ToString(),
                Action = Actions.Update,
                BeforeData = SerializeToJson(beforeLedger),
                AfterData = SerializeToJson(afterLedger)
            }, transaction).ConfigureAwait(false);
        }

        /// <summary>
        /// 履歴削除のログを既存トランザクションで記録する (Issue #1458)。
        /// </summary>
        public async Task LogLedgerDeleteAsync(Ledger ledger, SQLiteTransaction transaction)
        {
            var (idm, name) = ResolveOperator();
            await _operationLogRepository.InsertAsync(new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = idm,
                OperatorName = name,
                TargetTable = Tables.Ledger,
                TargetId = ledger.Id.ToString(),
                Action = Actions.Delete,
                BeforeData = SerializeToJson(ledger),
                AfterData = null
            }, transaction).ConfigureAwait(false);
        }

        /// <summary>
        /// 履歴統合のログを既存トランザクションで記録する (Issue #1458)。
        /// </summary>
        public async Task LogLedgerMergeAsync(IReadOnlyList<Ledger> sourceLedgers, Ledger mergedLedger, SQLiteTransaction transaction)
        {
            var (idm, name) = ResolveOperator();
            await _operationLogRepository.InsertAsync(new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = idm,
                OperatorName = name,
                TargetTable = Tables.Ledger,
                TargetId = mergedLedger.Id.ToString(),
                Action = Actions.Merge,
                BeforeData = SerializeToJson(sourceLedgers),
                AfterData = SerializeToJson(mergedLedger)
            }, transaction).ConfigureAwait(false);
        }

        /// <summary>
        /// 履歴分割のログを既存トランザクションで記録する (Issue #1458)。
        /// </summary>
        public async Task LogLedgerSplitAsync(Ledger originalLedger, IReadOnlyList<Ledger> splitLedgers, SQLiteTransaction transaction)
        {
            var (idm, name) = ResolveOperator();
            await _operationLogRepository.InsertAsync(new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = idm,
                OperatorName = name,
                TargetTable = Tables.Ledger,
                TargetId = originalLedger.Id.ToString(),
                Action = Actions.Split,
                BeforeData = SerializeToJson(originalLedger),
                AfterData = SerializeToJson(splitLedgers)
            }, transaction).ConfigureAwait(false);
        }

        #endregion
```

`OperationLogger.cs` 冒頭の using に `using System.Data.SQLite;` を追加（必要なら）。

- [ ] **Step 5.4: テスト実行**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter "FullyQualifiedName~OperationLoggerTransactionTests"`
Expected: 5 tests PASS

- [ ] **Step 5.5: 既存 OperationLoggerTests がパスすることを確認**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter "FullyQualifiedName~OperationLoggerTests"`
Expected: 既存テストもすべて PASS

- [ ] **Step 5.6: Commit**

```bash
git add ICCardManager/src/ICCardManager/Services/OperationLogger.cs \
        ICCardManager/tests/ICCardManager.Tests/Services/OperationLoggerTransactionTests.cs
git commit -m "$(cat <<'EOF'
feat: OperationLogger に LogLedger*Async の tx 受入オーバーロード追加 (Issue #1458)

LedgerInsert/Update/Delete/Merge/Split の 5 メソッドに SQLiteTransaction
受入版を追加。各 callsite が Ledger 操作と同一 tx で監査ログ INSERT できる
基盤となる。

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: 原子性統合テスト LedgerLogAtomicityTests

**Files:**
- Create: `ICCardManager/tests/ICCardManager.Tests/Integration/LedgerLogAtomicityTests.cs`

- [ ] **Step 6.1: テストファイル作成**

Create `ICCardManager/tests/ICCardManager.Tests/Integration/LedgerLogAtomicityTests.cs`:

```csharp
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Moq;
using System;
using System.Threading.Tasks;
using Xunit;

namespace ICCardManager.Tests.Integration;

/// <summary>
/// Ledger 操作と operation_log INSERT の原子性検証 (Issue #1458)
/// </summary>
public class LedgerLogAtomicityTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _ledgerRepo;
    private readonly OperationLogRepository _logRepo;
    private readonly OperationLogger _logger;

    public LedgerLogAtomicityTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();
        _ledgerRepo = new LedgerRepository(_dbContext);
        _logRepo = new OperationLogRepository(_dbContext);

        var ctxMock = new Mock<ICurrentOperatorContext>();
        ctxMock.SetupGet(c => c.HasSession).Returns(true);
        ctxMock.SetupGet(c => c.CurrentIdm).Returns("1111111111111111");
        ctxMock.SetupGet(c => c.CurrentName).Returns("テスト操作者");
        _logger = new OperationLogger(_logRepo, ctxMock.Object);
    }

    public void Dispose()
    {
        _dbContext?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task UpdateAndLog_InSameTransaction_BothCommittedTogether()
    {
        // Arrange
        var ledger = CreateValidLedger();
        var id = await _ledgerRepo.InsertAsync(ledger);
        ledger.Id = id;
        var before = CloneLedger(ledger);
        ledger.Summary = "変更後";

        // Act
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            await _ledgerRepo.UpdateAsync(ledger, scope.Transaction);
            await _logger.LogLedgerUpdateAsync(before, ledger, scope.Transaction);
            scope.Commit();
        }

        // Assert
        var actual = await _ledgerRepo.GetByIdAsync(id);
        actual!.Summary.Should().Be("変更後");
        var logs = await _logRepo.GetByDateRangeAsync(DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1));
        logs.Should().ContainSingle(l =>
            l.TargetTable == "ledger" &&
            l.TargetId == id.ToString() &&
            l.Action == "UPDATE");
    }

    [Fact]
    public async Task UpdateAndLog_TransactionRolledBack_NeitherPersisted()
    {
        // Arrange
        var ledger = CreateValidLedger();
        var id = await _ledgerRepo.InsertAsync(ledger);
        ledger.Id = id;
        var before = CloneLedger(ledger);
        ledger.Summary = "rollback されるはず";

        // Act
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            await _ledgerRepo.UpdateAsync(ledger, scope.Transaction);
            await _logger.LogLedgerUpdateAsync(before, ledger, scope.Transaction);
            // Commit せず → 自動 rollback
        }

        // Assert
        var actual = await _ledgerRepo.GetByIdAsync(id);
        actual!.Summary.Should().Be(before.Summary);
        var logs = await _logRepo.GetByDateRangeAsync(DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1));
        logs.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAndLog_InSameTransaction_BothCommittedTogether()
    {
        // Arrange
        var ledger = CreateValidLedger();
        var id = await _ledgerRepo.InsertAsync(ledger);
        ledger.Id = id;

        // Act
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            await _ledgerRepo.DeleteAsync(id, scope.Transaction);
            await _logger.LogLedgerDeleteAsync(ledger, scope.Transaction);
            scope.Commit();
        }

        // Assert
        (await _ledgerRepo.GetByIdAsync(id)).Should().BeNull();
        var logs = await _logRepo.GetByDateRangeAsync(DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1));
        logs.Should().ContainSingle(l =>
            l.TargetTable == "ledger" &&
            l.Action == "DELETE");
    }

    private static Ledger CreateValidLedger() => new()
    {
        CardIdm = "1111111111111111",
        Date = DateTime.Today,
        Summary = "テスト",
        Income = 0,
        Expense = 0,
        Balance = 1000,
        IsLentRecord = false
    };

    private static Ledger CloneLedger(Ledger src) => new()
    {
        Id = src.Id,
        CardIdm = src.CardIdm,
        Date = src.Date,
        Summary = src.Summary,
        Income = src.Income,
        Expense = src.Expense,
        Balance = src.Balance,
        IsLentRecord = src.IsLentRecord
    };
}
```

- [ ] **Step 6.2: テスト実行**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter "FullyQualifiedName~LedgerLogAtomicityTests"`
Expected: 3 tests PASS

- [ ] **Step 6.3: Commit**

```bash
git add ICCardManager/tests/ICCardManager.Tests/Integration/LedgerLogAtomicityTests.cs
git commit -m "$(cat <<'EOF'
test: Ledger 操作と監査ログの原子性統合テスト追加 (Issue #1458)

UPDATE/DELETE と LogLedger*Async を同一トランザクションで実行した際の
コミット/ロールバック原子性を in-memory SQLite で検証。

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: LedgerRowEditViewModel — Insert 経路 (line 610)

**Files:**
- Modify: `ICCardManager/src/ICCardManager/ViewModels/LedgerRowEditViewModel.cs`

- [ ] **Step 7.1: 該当箇所を読み、必要な context を確認**

LedgerRowEditViewModel.cs line 600-615 周辺を読む（実行コマンドではなく Read tool で）。

- [ ] **Step 7.2: 改修コード適用**

LedgerRowEditViewModel.cs に `IDbContext`（既存名は `DbContext` のはず）が DI されていることを確認。なければ追加。

該当箇所の Edit:

```csharp
// Before (line 610 周辺)
var newId = await _ledgerRepository.InsertAsync(newLedger);
if (newId > 0)
{
    newLedger.Id = newId;
    await _operationLogger.LogLedgerInsertAsync(newLedger);
    IsSaved = true;
}

// After
using var scope = await _dbContext.BeginTransactionAsync();
var newId = await _ledgerRepository.InsertAsync(newLedger, scope.Transaction);
if (newId > 0)
{
    newLedger.Id = newId;
    await _operationLogger.LogLedgerInsertAsync(newLedger, scope.Transaction);
    scope.Commit();
    IsSaved = true;
}
```

注: 既存の周辺コード（StatusMessage 設定、IsSaved 制御、エラー時パス）は維持する。`else` 分岐があれば commit せずに終わるため自動 rollback される。

- [ ] **Step 7.3: ビルド確認**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`
Expected: 警告ゼロ・エラーゼロ

- [ ] **Step 7.4: 関連テスト実行**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter "FullyQualifiedName~LedgerRowEditViewModelTests"`
Expected: 既存テスト PASS。`_dbContext.BeginTransactionAsync` の mock が必要なテストは個別対応する。

注: 既存テストでモックが不足する場合、`Mock<DbContext>` セットアップが必要になる可能性あり。テスト失敗が出た場合は次のステップで対応:
- DbContext の `BeginTransactionAsync` メソッドが virtual かを確認 (line 313 では `Task<ConnectionLease> LeaseConnectionAsync` は virtual。`BeginTransactionAsync` も virtual のはず。要確認)
- mock セットアップを追加

- [ ] **Step 7.5: Commit**

```bash
git add ICCardManager/src/ICCardManager/ViewModels/LedgerRowEditViewModel.cs
git commit -m "$(cat <<'EOF'
perf: LedgerRowEditViewModel.Insert 経路を同一 tx 化 (Issue #1458)

履歴追加保存時に Ledger.InsertAsync と LogLedgerInsertAsync を同一
SQLiteTransaction でコミット。SMB 共有モードで fsync 1 RTT 削減。

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: LedgerRowEditViewModel — Update 経路 (line 681)

**Files:**
- Modify: `ICCardManager/src/ICCardManager/ViewModels/LedgerRowEditViewModel.cs:660-690`

- [ ] **Step 8.1: 改修コード適用**

```csharp
// Before (line 669-687 周辺)
var result = await _ledgerRepository.UpdateAsync(ledger);
if (result)
{
    // Issue #983: 摘要編集時にバス停名をDetailに同期
    if (beforeLedger.Summary != ledger.Summary)
    {
        await SyncBusStopsFromSummaryAsync(ledger);
    }

    await _operationLogger.LogLedgerUpdateAsync(beforeLedger, ledger);
    IsSaved = true;
}
else
{
    StatusMessage = "保存に失敗しました";
}

// After
bool result;
using (var scope = await _dbContext.BeginTransactionAsync())
{
    result = await _ledgerRepository.UpdateAsync(ledger, scope.Transaction);
    if (result)
    {
        await _operationLogger.LogLedgerUpdateAsync(beforeLedger, ledger, scope.Transaction);
        scope.Commit();
    }
}

if (result)
{
    // Issue #983: バス停名同期は tx 外で実行（既存通り）
    if (beforeLedger.Summary != ledger.Summary)
    {
        await SyncBusStopsFromSummaryAsync(ledger);
    }
    IsSaved = true;
}
else
{
    StatusMessage = "保存に失敗しました";
}
```

理由: `SyncBusStopsFromSummaryAsync` は内部で別の Ledger 更新を行うため、外側 tx に含めると意味的に「主更新の一部」として扱われ、また同じ tx 内での detail 更新によるロック競合の懸念もある。設計判断として「Ledger UPDATE + 監査ログ INSERT」のみを 1 tx に含め、補助同期は元の通り別 tx で実行する。

- [ ] **Step 8.2: ビルド・テスト**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build
"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter "FullyQualifiedName~LedgerRowEditViewModelTests"
```
Expected: 警告ゼロ、テスト PASS

- [ ] **Step 8.3: Commit**

```bash
git add ICCardManager/src/ICCardManager/ViewModels/LedgerRowEditViewModel.cs
git commit -m "$(cat <<'EOF'
perf: LedgerRowEditViewModel.Update 経路を同一 tx 化 (Issue #1458)

履歴編集保存時に Ledger.UpdateAsync と LogLedgerUpdateAsync を同一
SQLiteTransaction でコミット。バス停名同期 (Issue #983) は副次処理として
tx 外に出し、txn 境界を最小化。

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: MainViewModel.DeleteLedgerRow (line 1644)

**Files:**
- Modify: `ICCardManager/src/ICCardManager/ViewModels/MainViewModel.cs:1641-1645`

- [ ] **Step 9.1: 改修コード適用**

```csharp
// Before (line 1641-1644)
var fullLedger = await _ledgerRepository.GetByIdAsync(ledger.Id);
if (fullLedger == null) return;
await _ledgerRepository.DeleteAsync(ledger.Id);
await _operationLogger.LogLedgerDeleteAsync(fullLedger);

// After
var fullLedger = await _ledgerRepository.GetByIdAsync(ledger.Id);
if (fullLedger == null) return;
using (var scope = await _dbContext.BeginTransactionAsync())
{
    await _ledgerRepository.DeleteAsync(ledger.Id, scope.Transaction);
    await _operationLogger.LogLedgerDeleteAsync(fullLedger, scope.Transaction);
    scope.Commit();
}
```

`_dbContext` が DI されていない場合、`MainViewModel` のコンストラクタに `DbContext dbContext` を追加し、`App.xaml.cs` の DI 登録順序が問題ないことを確認（DbContext は Singleton 登録済み）。

- [ ] **Step 9.2: ビルド・テスト**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build
"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter "FullyQualifiedName~MainViewModelTests"
```

- [ ] **Step 9.3: Commit**

```bash
git add ICCardManager/src/ICCardManager/ViewModels/MainViewModel.cs
git commit -m "$(cat <<'EOF'
perf: MainViewModel.DeleteLedgerRow を同一 tx 化 (Issue #1458)

履歴行削除と監査ログ INSERT を同一 SQLiteTransaction でコミット。

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 10: MainViewModel — EditLedgerWithAuthAsync 内 Delete (line 1698)

**Files:**
- Modify: `ICCardManager/src/ICCardManager/ViewModels/MainViewModel.cs:1690-1700`

- [ ] **Step 10.1: 該当箇所を読み、context を確認**

Read MainViewModel.cs line 1690-1710 to confirm structure.

- [ ] **Step 10.2: 改修コード適用**

該当箇所の DeleteAsync + LogLedgerDeleteAsync を Task 9 と同じパターンで同一 tx 化する:

```csharp
// Pattern:
using (var scope = await _dbContext.BeginTransactionAsync())
{
    await _ledgerRepository.DeleteAsync(fullLedger.Id, scope.Transaction);
    await _operationLogger.LogLedgerDeleteAsync(fullLedger, scope.Transaction);
    scope.Commit();
}
```

- [ ] **Step 10.3: ビルド・テスト・Commit**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build
"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter "FullyQualifiedName~MainViewModelTests"

git add ICCardManager/src/ICCardManager/ViewModels/MainViewModel.cs
git commit -m "$(cat <<'EOF'
perf: MainViewModel.EditLedgerWithAuth 内 Delete を同一 tx 化 (Issue #1458)

履歴編集フロー内の削除経路で Ledger 削除と監査ログ INSERT を同一
SQLiteTransaction でコミット。

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 11: LedgerDetailViewModel — Update (line 592)

**Files:**
- Modify: `ICCardManager/src/ICCardManager/ViewModels/LedgerDetailViewModel.cs:585-600`

- [ ] **Step 11.1: 該当箇所を読み、context を確認**

Read LedgerDetailViewModel.cs line 580-605 to see surrounding flow.

- [ ] **Step 11.2: 改修コード適用**

Task 8 と同じパターンを適用。`UpdateAsync` と `LogLedgerUpdateAsync` を同一 tx に。

```csharp
using (var scope = await _dbContext.BeginTransactionAsync())
{
    var result = await _ledgerRepository.UpdateAsync(_ledger, scope.Transaction);
    if (result)
    {
        await _operationLogger.LogLedgerUpdateAsync(beforeLedger, _ledger, scope.Transaction);
        scope.Commit();
    }
}
```

`_dbContext` がコンストラクタで DI されていなければ追加。

- [ ] **Step 11.3: ビルド・テスト・Commit**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build
"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter "FullyQualifiedName~LedgerDetailViewModelTests"

git add ICCardManager/src/ICCardManager/ViewModels/LedgerDetailViewModel.cs
git commit -m "$(cat <<'EOF'
perf: LedgerDetailViewModel.Save を同一 tx 化 (Issue #1458)

履歴詳細編集ダイアログでの保存時、Ledger.UpdateAsync と
LogLedgerUpdateAsync を同一 SQLiteTransaction でコミット。

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 12: LedgerMergeService — Merge を同一 tx 化

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Services/LedgerMergeService.cs:280-310`

- [ ] **Step 12.1: 該当箇所を読む**

Read LedgerMergeService.cs line 270-315 (MergeAsync の txn 直前から log 呼出後まで).

- [ ] **Step 12.2: 改修コード適用**

```csharp
// Before (line 290-306 周辺)
var success = await _ledgerRepository.MergeLedgersAsync(target.Id, sourceIds, target).ConfigureAwait(false);
if (!success)
{
    return new LedgerMergeResult { Success = false, ErrorMessage = "..." };
}
await _operationLogger.LogLedgerMergeAsync(beforeLedgers, target).ConfigureAwait(false);

// After
using var scope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false);
var success = await _ledgerRepository.MergeLedgersAsync(target.Id, sourceIds, target, scope.Transaction).ConfigureAwait(false);
if (!success)
{
    return new LedgerMergeResult { Success = false, ErrorMessage = "..." };
}
await _operationLogger.LogLedgerMergeAsync(beforeLedgers, target, scope.Transaction).ConfigureAwait(false);
scope.Commit();
```

`_dbContext` がコンストラクタで DI されていなければ `LedgerMergeService` に追加。

- [ ] **Step 12.3: ビルド・テスト**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build
"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter "FullyQualifiedName~LedgerMergeServiceTests"
```

既存テストの mock セットアップが破損する可能性: `_ledgerRepository.MergeLedgersAsync` のモックを `(int, IEnumerable<int>, Ledger, SQLiteTransaction)` 形に更新。`Mock<DbContext>.Setup(c => c.BeginTransactionAsync(...))` も必要になる場合あり。

修正例:
```csharp
// LedgerMergeServiceTests のセットアップに追加
_dbContextMock.Setup(c => c.BeginTransactionAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(/* TransactionScope mock */);
_ledgerRepoMock.Setup(r => r.MergeLedgersAsync(
        It.IsAny<int>(),
        It.IsAny<IEnumerable<int>>(),
        It.IsAny<Ledger>(),
        It.IsAny<SQLiteTransaction>()))
    .ReturnsAsync(true);
```

- [ ] **Step 12.4: Commit**

```bash
git add ICCardManager/src/ICCardManager/Services/LedgerMergeService.cs \
        ICCardManager/tests/ICCardManager.Tests/Services/LedgerMergeServiceTests.cs
git commit -m "$(cat <<'EOF'
perf: LedgerMergeService を同一 tx 化 (Issue #1458)

MergeLedgersAsync(tx 受入版) と LogLedgerMergeAsync(tx 受入版) を
同一 SQLiteTransaction でコミット。既存テストの mock セットアップを
新シグネチャに更新。

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 13: LedgerSplitService — Split 全体を 1 tx 化

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Services/LedgerSplitService.cs:86-160`

- [ ] **Step 13.1: 該当箇所を再読**

Read LedgerSplitService.cs line 50-170 全体（SplitAsync メソッド本体）。

- [ ] **Step 13.2: 改修コード適用**

`SplitAsync` メソッドの try ブロック内全体を 1 つの `BeginTransactionAsync` でラップ。`ReplaceDetailsAsync`, `UpdateAsync`, `InsertAsync`, `InsertDetailsAsync`, `LogLedgerSplitAsync` すべてに `scope.Transaction` を渡す。

```csharp
// 変更の中核 (line 86-148 周辺):
try
{
    using var scope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false);

    var createdIds = new List<int>();
    var allSplitLedgers = new List<Ledger>();

    // グループ1: 元のLedgerを更新
    var firstGroup = groups[0].ToList();
    ClearGroupIds(firstGroup);

    var (firstIncome, firstExpense, firstBalance) = CalculateGroupFinancials(firstGroup);
    var firstSummary = _summaryGenerator.Generate(firstGroup);

    originalLedger.Summary = !string.IsNullOrEmpty(firstSummary) ? firstSummary : originalLedger.Summary;
    originalLedger.Income = firstIncome;
    originalLedger.Expense = firstExpense;
    originalLedger.Balance = firstBalance;

    await _ledgerRepository.ReplaceDetailsAsync(originalLedger.Id, firstGroup.AsEnumerable().Reverse(), scope.Transaction).ConfigureAwait(false);
    await _ledgerRepository.UpdateAsync(originalLedger, scope.Transaction).ConfigureAwait(false);
    allSplitLedgers.Add(originalLedger);

    for (int i = 1; i < groups.Count; i++)
    {
        var groupDetails = groups[i].ToList();
        ClearGroupIds(groupDetails);

        var (income, expense, balance) = CalculateGroupFinancials(groupDetails);
        var summary = _summaryGenerator.Generate(groupDetails);

        var newLedger = new Ledger
        {
            CardIdm = originalLedger.CardIdm,
            LenderIdm = originalLedger.LenderIdm,
            StaffName = originalLedger.StaffName,
            ReturnerIdm = originalLedger.ReturnerIdm,
            LentAt = originalLedger.LentAt,
            ReturnedAt = originalLedger.ReturnedAt,
            IsLentRecord = false,
            Date = GetGroupDate(groupDetails, originalLedger.Date),
            Summary = !string.IsNullOrEmpty(summary) ? summary : "（分割）",
            Income = income,
            Expense = expense,
            Balance = balance,
            Note = null
        };

        var newId = await _ledgerRepository.InsertAsync(newLedger, scope.Transaction).ConfigureAwait(false);
        newLedger.Id = newId;
        await _ledgerRepository.InsertDetailsAsync(newId, groupDetails.AsEnumerable().Reverse(), scope.Transaction).ConfigureAwait(false);

        createdIds.Add(newId);
        allSplitLedgers.Add(newLedger);
    }

    // 操作ログを同 tx で記録
    await _operationLogger.LogLedgerSplitAsync(beforeLedger, allSplitLedgers, scope.Transaction).ConfigureAwait(false);

    scope.Commit();

    _logger.LogInformation(
        "Split ledger {LedgerId} into {Count} ledgers (new IDs: {NewIds})",
        ledgerId, allSplitLedgers.Count, string.Join(", ", createdIds));

    return new LedgerSplitResult
    {
        Success = true,
        CreatedLedgerIds = createdIds
    };
}
catch (Exception ex)
{
    // 既存の catch 節をそのまま残す（自動 rollback により部分書き込みは消える）
    // ...
}
```

`_dbContext` を DI で受け取るようにコンストラクタを修正。

- [ ] **Step 13.3: ビルド・テスト**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build
"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter "FullyQualifiedName~LedgerSplitServiceTests"
```

既存テストの mock セットアップを Task 12 同様に更新（`ReplaceDetailsAsync`, `UpdateAsync`, `InsertAsync`, `InsertDetailsAsync` すべて tx 受入版を期待）。

- [ ] **Step 13.4: Commit**

```bash
git add ICCardManager/src/ICCardManager/Services/LedgerSplitService.cs \
        ICCardManager/tests/ICCardManager.Tests/Services/LedgerSplitServiceTests.cs
git commit -m "$(cat <<'EOF'
perf: LedgerSplitService を同一 tx 化 (Issue #1458)

ReplaceDetailsAsync/UpdateAsync/InsertAsync/InsertDetailsAsync/
LogLedgerSplitAsync を 1 つの SQLiteTransaction で実行。
従来の複数 fsync を 1 回に集約し、部分書き込みも原子的に解消。

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 14: テスト設計書同期

**Files:**
- Modify: `ICCardManager/docs/design/07_テスト設計書.md`

- [ ] **Step 14.1: 件数計測**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --list-tests --configuration Release | grep -c "    "`
（注意: `--list-tests` はテストフレームワークによって出力フォーマットが異なる。プロジェクトで使われている実測コマンドに合わせる。`memory/feedback_test_count_snapshot_sync.md` を参照）

期待される件数増加: +約 12 件
- `OperationLogRepositoryTransactionTests`: +2
- `LedgerRepositoryTests` の追加メソッド: +5 (Delete×2, Merge×2, Replace×1)
- `OperationLoggerTransactionTests`: +5
- `LedgerLogAtomicityTests`: +3

合計 +15 件前後。実測値を採用する。

- [ ] **Step 14.2: §1.1a 件数表更新**

`docs/design/07_テスト設計書.md` の §1.1a 件数表を実測値で更新。該当行（OperationLogRepositoryTests, LedgerRepositoryTests, OperationLoggerTests/Services, Integration など）の件数を更新し、新規行（`OperationLogRepositoryTransactionTests`, `OperationLoggerTransactionTests`, `LedgerLogAtomicityTests`）を追加。

- [ ] **Step 14.3: §8.1 テスト一覧更新**

§8.1 の Repositories/Services/Integration 各セクションに新規テストクラス・メソッドを追記。

- [ ] **Step 14.4: Commit**

```bash
git add ICCardManager/docs/design/07_テスト設計書.md
git commit -m "$(cat <<'EOF'
docs: Issue #1458 テスト追加に伴う件数表・テスト一覧の同期 (Issue #1458)

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 15: CHANGELOG.md 更新

**Files:**
- Modify: `ICCardManager/CHANGELOG.md`

- [ ] **Step 15.1: `### Unreleased` セクションに追記**

```markdown
### Unreleased

- perf: Ledger 関連 7 箱所で Ledger 操作と監査ログ INSERT を同一トランザクション化 (Issue #1458)
    - 履歴の追加/編集/削除/統合/分割で fsync 1 RTT 削減（SMB 共有モードで体感速度改善）
    - 副次効果として Ledger と監査ログが原子的にコミットされるためデータ整合性向上
    - 影響範囲: LedgerRowEditViewModel, MainViewModel, LedgerDetailViewModel, LedgerMergeService, LedgerSplitService
```

`memory/feedback_release_changelog_promote.md` に従い、バージョン番号への繰り上げはリリース時に行う。

- [ ] **Step 15.2: Commit**

```bash
git add ICCardManager/CHANGELOG.md
git commit -m "$(cat <<'EOF'
docs: CHANGELOG に Issue #1458 を追記

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 16: 最終検証

- [ ] **Step 16.1: フルビルド・警告ゼロ確認**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build --configuration Release`
Expected: Build succeeded、Warning(s) 0、Error(s) 0

- [ ] **Step 16.2: 全テスト実行**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --configuration Release`
Expected: 全件 PASS

- [ ] **Step 16.3: §1.1a 件数の最終整合確認**

実測テスト件数と §1.1a 件数表の各セクション値を再確認。乖離があれば §1.1a を実測値で再更新してコミット。

- [ ] **Step 16.4: PR 作成**

```bash
git push -u origin feat/issue-1458-operation-log-cotransaction
gh pr create --title "perf: Issue #1458 OperationLogger を ledger 操作と同一トランザクション化" \
             --body "$(cat <<'EOF'
## Summary
- Ledger 関連 7 箱所で Ledger 操作と監査ログ INSERT を同一トランザクションに統合
- SMB 共有モードで fsync 2→1 回に削減（1 RTT 短縮）
- 副次効果として Ledger と監査ログの原子的コミットによりデータ整合性向上

## Scope
- LedgerRowEditViewModel (Insert/Update 2 箱所)
- MainViewModel (Delete 2 箱所)
- LedgerDetailViewModel (Update 1 箱所)
- LedgerMergeService (Merge 1 箱所)
- LedgerSplitService (Split 1 箱所)
- スコープ外: Card/Staff/Backup/Restore/Import/Export 系 (別 Issue 候補)

## Test plan
- [x] 単体テスト追加 (OperationLogRepositoryTransactionTests, OperationLoggerTransactionTests, LedgerLogAtomicityTests, LedgerRepositoryTests 既存への追加)
- [x] 既存 LedgerMergeServiceTests / LedgerSplitServiceTests のモックを新シグネチャに更新
- [x] dotnet build 警告ゼロ
- [x] dotnet test --configuration Release 全件 PASS
- [x] テスト設計書 §1.1a / §8.1 更新
- [x] CHANGELOG.md Unreleased 追記
- [ ] **手動テスト依頼**: ローカルモードで履歴の追加/編集/削除/統合/分割が正常動作すること
- [ ] **手動テスト依頼**: 共有モード (UNC パス) で同操作の体感速度が改善されたこと
- [ ] **手動テスト依頼**: ネットワーク切断中の保存操作でデータ/ログとも未書き込みであること

## 設計書
ICCardManager/docs/superpowers/specs/2026-05-21-issue-1458-operation-log-cotransaction-design.md

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

---

## Self-Review チェックリスト（プラン作成後の検証用）

- [ ] 設計書のすべての要件にタスクが対応している
- [ ] 各タスクに具体的なファイルパスと行番号がある
- [ ] 各タスクに失敗テスト → 実装 → パス → commit の TDD サイクルがある
- [ ] コード変更ステップには実コードが書かれている（プレースホルダなし）
- [ ] 型・メソッド名がタスク間で整合（`InsertAsync(log, tx)` → `MergeLedgersAsync(..., tx)` 等）
- [ ] `memory/feedback_test_count_snapshot_sync.md` を意識し、Release 構成で件数計測する
- [ ] `memory/feedback_zero_build_warnings.md` を意識し、警告ゼロを最終検証に含める
- [ ] `memory/feedback_release_changelog_promote.md` に従い、`### Unreleased` への追記のみ（バージョン繰り上げなし）
- [ ] `.claude/rules/git-workflow.md` 厳守（個別 `git add`、ブランチ作成済み）
