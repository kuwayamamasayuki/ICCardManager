# Issue #1456: LedgerRepository.InsertDetailsAsync バッチ化 実装プラン

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `LedgerRepository.InsertDetailsAsync` を「単一トランザクション＋単一 SQLiteCommand 再利用」に置き換え、N+1 とトランザクション無しによる SMB 環境での書込み遅延を解消する。

**Architecture:** `tx=null` 経路では内部で `BeginTransactionAsync()` を開いて commit/rollback まで責任を持つ。`tx` 指定経路は呼び出し元の tx を共有して commit/rollback には介入しない。ループ内では `SQLiteCommand` を 1 個生成し、`Parameters` をあらかじめ宣言、毎回値だけ再代入する。`InsertDetailAsync`（単一明細書込み）は触らない。

**Tech Stack:** C# 10 / .NET Framework 4.8 / System.Data.SQLite / xUnit / FluentAssertions

**設計書:** `ICCardManager/docs/superpowers/specs/2026-05-20-issue-1456-ledger-insert-details-batch-design.md`

**ブランチ:** `feat/issue-1456-ledger-insert-details-batch`（既に作成済み）

---

## ファイル構成

- **Modify:** `ICCardManager/src/ICCardManager/Data/Repositories/LedgerRepository.cs`
  - 既存 `InsertDetailsAsync(int, IEnumerable<LedgerDetail>, SQLiteTransaction)` を再実装
  - `InsertDetailsAsync(int, IEnumerable<LedgerDetail>)` の薄いラッパーは維持
  - private ヘルパー `InsertDetailsCore` を追加（1 SQLiteCommand を再利用してループ内 ExecuteNonQuery）
- **Create:** `ICCardManager/tests/ICCardManager.Tests/Data/Repositories/LedgerRepositoryBatchInsertTests.cs`
  - 設計書のテストケース #1〜#5 を実装
- **Modify:** `ICCardManager/docs/design/07_テスト設計書.md`
  - LedgerRepository テストセクションに 5 件追記、件数表 §1.1a を更新
- **Modify:** `ICCardManager/CHANGELOG.md`
  - Unreleased セクションに改善項目追加

---

## Task 1: 失敗するテスト #1（happy path 100 件）

**Files:**
- Create: `ICCardManager/tests/ICCardManager.Tests/Data/Repositories/LedgerRepositoryBatchInsertTests.cs`

- [ ] **Step 1: 新規テストファイルを作成して、最初のテストだけ書く**

```csharp
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Tests.Data;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;

namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// Issue #1456: InsertDetailsAsync のバッチ化（単一 tx ＋単一 SQLiteCommand 再利用）後のリグレッション守備網。
/// </summary>
public class LedgerRepositoryBatchInsertTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _repository;
    private readonly CardRepository _cardRepository;
    private readonly StaffRepository _staffRepository;

    private const string TestCardIdm = "0102030405060708";
    private const string TestStaffIdm = "STAFF00000000001";
    private const string TestStaffName = "テスト職員";

    public LedgerRepositoryBatchInsertTests()
    {
        _dbContext = TestDbContextFactory.Create();

        var cacheServiceMock = new Mock<ICacheService>();
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<IEnumerable<IcCard>>>>(),
            It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<IcCard>>> factory, TimeSpan _) => factory());
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<IEnumerable<Staff>>>>(),
            It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<Staff>>> factory, TimeSpan _) => factory());

        _repository = new LedgerRepository(_dbContext);
        _cardRepository = new CardRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()));
        _staffRepository = new StaffRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()));

        SetupTestDataAsync().GetAwaiter().GetResult();
    }

    private async Task SetupTestDataAsync()
    {
        await _staffRepository.InsertAsync(new Staff
        {
            StaffIdm = TestStaffIdm,
            Name = TestStaffName,
            IsDeleted = false
        });

        await _cardRepository.InsertAsync(new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "H001"
        });
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private Ledger CreateLedger() => new()
    {
        CardIdm = TestCardIdm,
        LenderIdm = TestStaffIdm,
        Date = new DateTime(2026, 4, 1, 9, 0, 0),
        Summary = "鉄道（A駅〜Z駅）",
        Income = 0,
        Expense = 10000,
        Balance = 0,
        StaffName = TestStaffName,
        IsLentRecord = false
    };

    private static List<LedgerDetail> CreateDetails(int count, int startBalance = 10000)
    {
        var list = new List<LedgerDetail>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(new LedgerDetail
            {
                LedgerId = 0, // InsertDetailsAsync が ledgerId で上書きする
                UseDate = new DateTime(2026, 4, 1, 9, 0, 0).AddMinutes(i),
                EntryStation = $"駅{i:D3}",
                ExitStation = $"駅{i + 1:D3}",
                Amount = 100,
                Balance = startBalance - (i + 1) * 100,
                IsCharge = false,
                IsPointRedemption = false,
                IsBus = false
            });
        }
        return list;
    }

    [Fact]
    public async Task InsertDetailsAsync_LargeBatch_TxNull_AllPersisted()
    {
        // Issue #1456: tx=null で 100 件を一括挿入し、全件 DB に入ることを確認。
        var ledgerId = await _repository.InsertAsync(CreateLedger());
        var details = CreateDetails(100);

        var result = await _repository.InsertDetailsAsync(ledgerId, details);

        result.Should().BeTrue();
        var persisted = await _repository.GetByIdAsync(ledgerId);
        persisted.Should().NotBeNull();
        persisted!.Details.Should().HaveCount(100);
    }
}
```

- [ ] **Step 2: テストをビルド＆実行して PASS を確認**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~LedgerRepositoryBatchInsertTests.InsertDetailsAsync_LargeBatch_TxNull_AllPersisted"`

Expected: PASS（既存実装でも happy path は通る）。これは N+1 が遅いだけで機能としては動くため、リファクタ後も挙動が変わっていないことの足場となる。

- [ ] **Step 3: コミット**

```bash
git add ICCardManager/tests/ICCardManager.Tests/Data/Repositories/LedgerRepositoryBatchInsertTests.cs
git commit -m "test: Issue #1456 InsertDetailsAsync 100件挿入のリグレッション守備網を追加

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

## Task 2: テスト #2（caller-tx Rollback で 100 件全消滅）

**Files:**
- Modify: `ICCardManager/tests/ICCardManager.Tests/Data/Repositories/LedgerRepositoryBatchInsertTests.cs`

- [ ] **Step 1: テスト #2 を追加**

`LargeBatch_TxNull_AllPersisted` の直後に追加:

```csharp
[Fact]
public async Task InsertDetailsAsync_LargeBatch_WithCallerTransaction_RollbackDiscardsAll()
{
    // Issue #1456: 呼び出し元 tx 経由で 100 件挿入し、呼び出し元が Rollback すると
    // 1 件も残らないことを確認。これにより以下を保証する:
    //   (a) InsertDetailsAsync が呼び出し元 tx に介入していない（自分で commit していない）
    //   (b) 100 件分のループが同一 tx 内で実行されている
    int ledgerId;
    using (var scope = await _dbContext.BeginTransactionAsync())
    {
        ledgerId = await _repository.InsertAsync(CreateLedger(), scope.Transaction);
        var details = CreateDetails(100);
        var result = await _repository.InsertDetailsAsync(ledgerId, details, scope.Transaction);
        result.Should().BeTrue();
        scope.Rollback();
    }

    var persisted = await _repository.GetByIdAsync(ledgerId);
    persisted.Should().BeNull("呼び出し元 tx の Rollback で ledger ヘッダと 100 件の detail が全て消えるべき");
}
```

- [ ] **Step 2: テスト実行して PASS を確認**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~LedgerRepositoryBatchInsertTests"`

Expected: 2 件 PASS。既存実装でも `tx` を渡しているので両方通るはず。

- [ ] **Step 3: コミット**

```bash
git add ICCardManager/tests/ICCardManager.Tests/Data/Repositories/LedgerRepositoryBatchInsertTests.cs
git commit -m "test: Issue #1456 caller-tx Rollback で 100 件全消滅するテストを追加

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

## Task 3: テスト #3〜#5（空入力・LedgerId 上書き・例外後の lease 解放）

**Files:**
- Modify: `ICCardManager/tests/ICCardManager.Tests/Data/Repositories/LedgerRepositoryBatchInsertTests.cs`

- [ ] **Step 1: 残り 3 件のテストを追加**

```csharp
[Fact]
public async Task InsertDetailsAsync_EmptyCollection_ReturnsTrue_NoSideEffect()
{
    var ledgerId = await _repository.InsertAsync(CreateLedger());

    var result = await _repository.InsertDetailsAsync(ledgerId, Array.Empty<LedgerDetail>());

    result.Should().BeTrue("空コレクションでも成功扱い");
    var persisted = await _repository.GetByIdAsync(ledgerId);
    persisted!.Details.Should().BeEmpty();
}

[Fact]
public async Task InsertDetailsAsync_OverwritesLedgerId_OnEachRow()
{
    // detail.LedgerId に -1 を入れて呼び、引数の ledgerId で全行が書き換えられることを確認。
    var ledgerId = await _repository.InsertAsync(CreateLedger());
    var details = CreateDetails(5);
    foreach (var d in details) d.LedgerId = -1;

    var result = await _repository.InsertDetailsAsync(ledgerId, details);

    result.Should().BeTrue();
    var persisted = await _repository.GetByIdAsync(ledgerId);
    persisted!.Details.Should().HaveCount(5);
    persisted.Details.Should().OnlyContain(d => d.LedgerId == ledgerId);
}

[Fact]
public async Task InsertDetailsAsync_TxNull_OnSqliteException_DoesNotLeakSemaphore()
{
    // 不正な FK で例外が出た直後でも、次の BeginTransactionAsync がタイムアウトせず
    // 取れること（内部 tx の rollback と lease 解放が正しく行われている証明）。
    var invalidLedgerId = 999_999;
    var details = CreateDetails(3);

    var act = async () => await _repository.InsertDetailsAsync(invalidLedgerId, details);
    await act.Should().ThrowAsync<SQLiteException>();

    // セマフォが解放されていないと、ここで実質ハングする。テスト全体のタイムアウトで失敗する。
    using var scope = await _dbContext.BeginTransactionAsync();
    scope.Should().NotBeNull();
}
```

- [ ] **Step 2: テスト実行**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~LedgerRepositoryBatchInsertTests"`

Expected: 既存実装では **テスト #5（DoesNotLeakSemaphore）が FAIL する可能性がある**。理由: 旧 `InsertDetailsAsync` は `InsertDetailAsync` を tx=null で呼ぶ → 各呼び出しが独立した lease を即時解放するため例外時もセマフォは漏れない。つまり既存実装でも通ってしまう公算が高い。

ただし: 旧実装でも `tx=null` 経路は `LeaseConnectionAsync()` がセマフォを取らない設計（DbContext.cs:316）なので、例外でもセマフォ漏れは発生しない。新実装は `BeginTransactionAsync()` でセマフォを取るため、catch して rollback すれば lease 解放経由でセマフォは戻る。

このテストは新実装が正しく rollback してセマフォを解放することを保証する**新実装専用のテスト**として価値がある。旧実装でも PASS するが、新実装では特に重要。

Expected: 5 件 PASS。

- [ ] **Step 3: コミット**

```bash
git add ICCardManager/tests/ICCardManager.Tests/Data/Repositories/LedgerRepositoryBatchInsertTests.cs
git commit -m "test: Issue #1456 空入力・LedgerId上書き・例外時lease解放のテストを追加

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

## Task 4: 本体実装（InsertDetailsAsync をバッチ化）

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Data/Repositories/LedgerRepository.cs`（行 371〜386 を置き換え）

- [ ] **Step 1: `using System.Data;` を追加（DbType を使うため）**

`LedgerRepository.cs` の冒頭の using に追加:

```csharp
using System;
using System.Collections.Generic;
using System.Data;                  // ← 追加
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Models;
using System.Data.Common;
using System.Data.SQLite;
```

- [ ] **Step 2: `InsertDetailsAsync(int, IEnumerable<LedgerDetail>, SQLiteTransaction)` の本体を置き換え**

旧コード（行 374〜386）を削除:

```csharp
/// <inheritdoc/>
public async Task<bool> InsertDetailsAsync(int ledgerId, IEnumerable<LedgerDetail> details, SQLiteTransaction transaction)
{
    foreach (var detail in details)
    {
        detail.LedgerId = ledgerId;
        if (!await InsertDetailAsync(detail, transaction))
        {
            return false;
        }
    }
    return true;
}
```

新コードに置き換え（既存の `/// <inheritdoc/>` と Task<bool> InsertDetailsAsync ラッパー宣言は残す）:

```csharp
/// <inheritdoc/>
public async Task<bool> InsertDetailsAsync(int ledgerId, IEnumerable<LedgerDetail> details, SQLiteTransaction transaction)
{
    // Issue #1456: 単一 SQLiteCommand を再利用してループ内 ExecuteNonQuery する。
    // tx=null 経路では内部で BeginTransactionAsync して commit/rollback まで責任を持つ。
    // tx 指定経路は呼び出し元の tx を共有し、commit/rollback には介入しない。
    var list = details as IList<LedgerDetail> ?? details.ToList();
    if (list.Count == 0)
    {
        return true;
    }

    if (transaction != null)
    {
        return await InsertDetailsCore(ledgerId, list, transaction.Connection, transaction).ConfigureAwait(false);
    }

    using var scope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false);
    try
    {
        var ok = await InsertDetailsCore(ledgerId, list, scope.Lease.Connection, scope.Transaction).ConfigureAwait(false);
        if (ok)
        {
            scope.Commit();
        }
        else
        {
            scope.Rollback();
        }
        return ok;
    }
    catch
    {
        scope.Rollback();
        throw;
    }
}

/// <summary>
/// Issue #1456: ledger_detail への一括 INSERT 本体。
/// 1 つの SQLiteCommand を生成し、パラメータを宣言したうえでループ内では値だけを差し替える。
/// </summary>
private static async Task<bool> InsertDetailsCore(
    int ledgerId, IList<LedgerDetail> details, SQLiteConnection connection, SQLiteTransaction transaction)
{
    using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = @"INSERT INTO ledger_detail (ledger_id, use_date, entry_station, exit_station,
                               bus_stops, amount, balance, is_charge, is_point_redemption, is_bus, group_id)
VALUES (@ledgerId, @useDate, @entryStation, @exitStation,
       @busStops, @amount, @balance, @isCharge, @isPointRedemption, @isBus, @groupId)";

    var pLedgerId          = command.Parameters.Add("@ledgerId",          DbType.Int32);
    var pUseDate           = command.Parameters.Add("@useDate",           DbType.String);
    var pEntryStation      = command.Parameters.Add("@entryStation",      DbType.String);
    var pExitStation       = command.Parameters.Add("@exitStation",       DbType.String);
    var pBusStops          = command.Parameters.Add("@busStops",          DbType.String);
    var pAmount            = command.Parameters.Add("@amount",            DbType.Int32);
    var pBalance           = command.Parameters.Add("@balance",           DbType.Int32);
    var pIsCharge          = command.Parameters.Add("@isCharge",          DbType.Int32);
    var pIsPointRedemption = command.Parameters.Add("@isPointRedemption", DbType.Int32);
    var pIsBus             = command.Parameters.Add("@isBus",             DbType.Int32);
    var pGroupId           = command.Parameters.Add("@groupId",           DbType.Int32);

    foreach (var detail in details)
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
        {
            return false;
        }
    }
    return true;
}
```

- [ ] **Step 3: ビルドが通るか確認**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/src/ICCardManager/ICCardManager.csproj -nologo 2>&1 | tail -10`

Expected: `Build succeeded.` / 警告 0 件（既存 warning 0 維持）

- [ ] **Step 4: 新しいテストを全件実行**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~LedgerRepositoryBatchInsertTests"`

Expected: 5 件 PASS

- [ ] **Step 5: LedgerRepository 周辺の既存テストも全件実行（リグレッション無しを確認）**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~LedgerRepository"`

Expected: 既存テスト全件 PASS（`LedgerRepositoryTests`, `LedgerRepositoryTransactionTests`, `LedgerDetailOrderingIntegrationTests` 等）

- [ ] **Step 6: コミット**

```bash
git add ICCardManager/src/ICCardManager/Data/Repositories/LedgerRepository.cs
git commit -m "fix: Issue #1456 InsertDetailsAsync をバッチ化（単一tx＋SQLiteCommand再利用）

旧実装は foreach で InsertDetailAsync を 1 件ずつ呼んでおり、tx=null 経路では
毎回 LeaseConnectionAsync を取り直し独立した autocommit で INSERT していた。
共有モード（SMB）では 1〜10ms 往復 × 行数 が直線的に効き、ローカルでも
journal_mode=DELETE の各 INSERT で rollback journal の fsync が発生していた。

新実装は:
- tx=null では内部で BeginTransactionAsync して commit/rollback まで責任を持つ
- tx 指定では呼び出し元の tx を共有
- SQLiteCommand を 1 個生成し、Parameters は宣言のみ。ループ内で .Value を再代入

呼び出し元（LedgerSplitService / NewLedgerFromSegmentsBuilder /
ReplaceDetailsAsync 経由のインポート / LendingService 経由の貸出返却）は
シグネチャ変更なしで自動的に高速化される。

挙動の変化: tx=null 経路で途中失敗した場合、これまでは N-1 件 commit 済みの
不整合状態が残り得たが、新実装は ALL OR NOTHING（内部 tx の rollback による）。

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

## Task 5: 設計書とテスト件数の同期

**Files:**
- Modify: `ICCardManager/docs/design/07_テスト設計書.md`

- [ ] **Step 1: テスト件数の実測を取る**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --list-tests 2>&1 | grep -c "    "`

期待: 既存件数 + 5。実測した数値を控える。

- [ ] **Step 2: テスト設計書を更新**

`07_テスト設計書.md` 内の以下を更新:
1. **「LedgerRepository のテスト」セクション**（場所は `grep -n "LedgerRepository" ICCardManager/docs/design/07_テスト設計書.md` で特定する）に新規 5 件を箇条書きで追記:
   - `InsertDetailsAsync_LargeBatch_TxNull_AllPersisted` — 100 件 tx=null で全件挿入
   - `InsertDetailsAsync_LargeBatch_WithCallerTransaction_RollbackDiscardsAll` — caller-tx Rollback で 100 件全消滅
   - `InsertDetailsAsync_EmptyCollection_ReturnsTrue_NoSideEffect` — 空入力で副作用なし
   - `InsertDetailsAsync_OverwritesLedgerId_OnEachRow` — 各行 LedgerId 上書き
   - `InsertDetailsAsync_TxNull_OnSqliteException_DoesNotLeakSemaphore` — 例外後のセマフォ解放
2. **§1.1a 件数表** の総件数を Step 1 で実測した数値で更新。

- [ ] **Step 3: 件数表の同期が CI を通るか軽くチェック**

CI ワークフローは `.github/workflows/test-count-sync-check.yml` で実装されているため、ローカルでは件数の数値が `dotnet test --list-tests` の出力と一致していれば良い。コミット前に Step 1 の数値と §1.1a の数値が一致していることを目視確認する。

- [ ] **Step 4: コミット**

```bash
git add ICCardManager/docs/design/07_テスト設計書.md
git commit -m "docs: Issue #1456 InsertDetailsAsync バッチ化のテスト 5 件をテスト設計書に追記

§1.1a 件数表も実測値（dotnet test --list-tests）に同期。

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

## Task 6: CHANGELOG 更新

**Files:**
- Modify: `ICCardManager/CHANGELOG.md`

- [ ] **Step 1: CHANGELOG の Unreleased セクションを確認**

Run: `head -50 ICCardManager/CHANGELOG.md`

`## [Unreleased]` セクション、または最新バージョンの直前に `## [Unreleased]` がなければ新設する（既存パターンに合わせる）。

- [ ] **Step 2: Unreleased セクションに改善項目を追加**

Unreleased セクションの「### 改善」（無ければ新設）の下に追加:

```markdown
- `LedgerRepository.InsertDetailsAsync` を単一トランザクション＋SQLiteCommand 再利用に変更し、複数明細を一括書込みする際の I/O を大幅に削減（Issue #1456）。共有モード（SMB）で返却処理・分割・CSV インポートが体感で高速化されます。
```

- [ ] **Step 3: コミット**

```bash
git add ICCardManager/CHANGELOG.md
git commit -m "docs: Issue #1456 CHANGELOG に InsertDetailsAsync バッチ化を追記

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

## Task 7: 全テスト実行・PR 作成

- [ ] **Step 1: 全テスト実行**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj -nologo 2>&1 | tail -20`

Expected: `Failed: 0` 等、全件 PASS

- [ ] **Step 2: リモートにブランチを push**

```bash
git push -u origin feat/issue-1456-ledger-insert-details-batch
```

- [ ] **Step 3: PR 作成**

```bash
gh pr create --base main --title "fix: LedgerRepository.InsertDetailsAsync をバッチ化 (Issue #1456)" --body "$(cat <<'EOF'
## Summary

- `LedgerRepository.InsertDetailsAsync` を単一トランザクション＋単一 `SQLiteCommand` 再利用に置き換え、N+1 とトランザクション無しによる SMB 環境での書込み遅延を解消。
- 呼び出し元（`LedgerSplitService` / `NewLedgerFromSegmentsBuilder` / `ReplaceDetailsAsync` 経由の CSV インポート / `LendingService` 経由の貸出返却）はシグネチャ変更なしで自動的に高速化。
- 既存挙動の変化: `tx=null` 経路で途中失敗時、これまでは N-1 件 commit 済みの不整合が起こり得たが、新実装は ALL OR NOTHING。

## 設計書

`ICCardManager/docs/superpowers/specs/2026-05-20-issue-1456-ledger-insert-details-batch-design.md`

## Test plan

- [x] 新規テスト 5 件追加（`LedgerRepositoryBatchInsertTests.cs`）
  - `InsertDetailsAsync_LargeBatch_TxNull_AllPersisted` — 100 件 tx=null で全件挿入
  - `InsertDetailsAsync_LargeBatch_WithCallerTransaction_RollbackDiscardsAll` — caller-tx Rollback で 100 件全消滅
  - `InsertDetailsAsync_EmptyCollection_ReturnsTrue_NoSideEffect`
  - `InsertDetailsAsync_OverwritesLedgerId_OnEachRow`
  - `InsertDetailsAsync_TxNull_OnSqliteException_DoesNotLeakSemaphore` — 例外後のセマフォ解放
- [x] 既存 `LedgerRepository` 周辺テスト（`LedgerRepositoryTests`, `LedgerRepositoryTransactionTests`, `LedgerDetailOrderingIntegrationTests`, `DbContextCleanupTests`, `CsvImportServiceTests`, `DebugDataServiceTests`）全件 PASS
- [x] `docs/design/07_テスト設計書.md` の件数表 §1.1a 同期
- [x] `CHANGELOG.md` Unreleased セクション更新

## リリース時の作業

ユーザー指示により、CHANGELOG の Unreleased → 該当バージョンへの繰り上げはリリース時に行う。

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Step 4: PR URL を控えて完了報告**

PR URL を控え、ユーザーへ完了を報告する。

---

## Self-Review チェック結果

**1. Spec coverage:**
- 設計書 §「設計方針」の実装方針 1〜4 → Task 4 で全てカバー
- 設計書 §「失敗モードと回復」 → Task 3 のテスト #5（DoesNotLeakSemaphore）でリソース解放を、Task 2 のテスト #2（caller-tx Rollback）で ALL OR NOTHING を間接的に確認
- 設計書 §「テスト方針」5 件 → Task 1〜3 で全てカバー
- 設計書 §「CHANGELOG への記載予定」 → Task 6 でカバー
- 設計書 §「ロールバック計画」 → 単一ファイル変更のため、Task 4 の commit を revert するだけで原状回復可能

**2. Placeholder scan:** TBD/TODO/「適宜」/「同様に」等の placeholder なし。

**3. Type consistency:** `InsertDetailsCore` のシグネチャは全タスクで一貫。パラメータ名（`@ledgerId`, `@useDate`, …）は既存 `InsertDetailAsync`（行 344〜347, 349〜359）と完全一致。
