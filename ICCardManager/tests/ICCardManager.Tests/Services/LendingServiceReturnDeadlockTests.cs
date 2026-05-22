using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1575: 仮想タッチ／物理タッチ返却処理のデッドロック回帰検出。
///
/// 経緯:
/// Issue #1456 で <see cref="LedgerRepository.InsertDetailsAsync(int, IEnumerable{LedgerDetail})"/>
/// の 1 引数版が「内部で BeginTransactionAsync を開いて commit/rollback まで責任を持つ」設計に変更された。
/// 一方 <see cref="LendingService"/> の <c>PersistReturnAsync</c> は外側で <c>BeginTransactionAsync</c> を
/// 呼んで <see cref="System.Threading.SemaphoreSlim"/> を握っているのに、
/// <c>CreateUsageLedgersAsync</c> へ <c>transaction</c> を伝搬していなかった。
/// その結果、内部の InsertDetailsAsync が同じセマフォを再取得しようとして無限待機 → デッドロック。
///
/// 本テストは LendAsync → ReturnAsync を実 SQLite で実行し、タイムアウト時間内に完了することを検証する。
/// 修正前は ReturnAsync が <c>InsertDetailsAsync</c> 呼び出し時点で永久にハングする。
/// </summary>
public sealed class LendingServiceReturnDeadlockTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly LendingService _service;

    private const string TestCardIdm = "07FE112233445566";
    private const string TestStaffIdm = "FFFF000000000001";

    private readonly CardRepository _cardRepo;
    private readonly StaffRepository _staffRepo;

    public LendingServiceReturnDeadlockTests()
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
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<AppSettings>>>(),
            It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<AppSettings>> factory, TimeSpan _) => factory());

        var cacheOptions = Options.Create(new CacheOptions());

        var ledgerRepo = new LedgerRepository(_dbContext);
        _cardRepo = new CardRepository(_dbContext, cacheServiceMock.Object, cacheOptions);
        _staffRepo = new StaffRepository(_dbContext, cacheServiceMock.Object, cacheOptions);
        var settingsRepo = new SettingsRepository(_dbContext, cacheServiceMock.Object, cacheOptions);

        var summaryGenerator = new SummaryGenerator(DepartmentType.MayorOffice);
        var lockManager = new CardLockManager(NullLogger<CardLockManager>.Instance);

        _service = new LendingService(
            _dbContext,
            _cardRepo,
            _staffRepo,
            ledgerRepo,
            settingsRepo,
            summaryGenerator,
            lockManager,
            Options.Create(new AppOptions { CardLockTimeoutSeconds = 5, RetouchWindowSeconds = 30 }),
            NullLogger<LendingService>.Instance);

        SetupTestDataAsync().GetAwaiter().GetResult();
    }

    private async Task SetupTestDataAsync()
    {
        await _staffRepo.InsertAsync(new Staff
        {
            StaffIdm = TestStaffIdm,
            Name = "テスト職員",
            Number = "001",
            IsDeleted = false,
        });

        await _cardRepo.InsertAsync(new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "H-001",
        });
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// LendAsync → ReturnAsync を実 SQLite で実行し、デッドロックせずに完了することを検証する。
    /// 修正前はここで <see cref="LedgerRepository.InsertDetailsAsync(int, IEnumerable{LedgerDetail})"/>
    /// の 1 引数版が外側 tx のセマフォを再取得しようとして無限待機する。
    /// </summary>
    [Fact]
    public async Task ReturnAsync_利用履歴ありの返却が10秒以内に完了すること()
    {
        // Arrange: 残高 5,000 円のカードを貸し出す
        var lendResult = await _service.LendAsync(TestStaffIdm, TestCardIdm, 5000);
        lendResult.Success.Should().BeTrue($"貸出が失敗（{lendResult.ErrorMessage}）");

        // 利用履歴 2 件（バス利用想定: 駅情報なし、IsBus=true）
        var historyDetails = new List<LedgerDetail>
        {
            new()
            {
                UseDate = DateTime.Today,
                Amount = 200,
                Balance = 5000, // 新しい順（最新）
                IsBus = true,
                BusStops = "★",
            },
            new()
            {
                UseDate = DateTime.Today,
                Amount = 200,
                Balance = 5200,
                IsBus = true,
                BusStops = "★",
            },
        };

        // Act: 10 秒のタイムアウトで ReturnAsync を実行
        // デッドロック時はここで永久にハングする。タイムアウトで失敗扱い。
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var returnTask = _service.ReturnAsync(TestStaffIdm, TestCardIdm, historyDetails, skipDuplicateCheck: true);

        var completed = await Task.WhenAny(returnTask, Task.Delay(Timeout.Infinite, cts.Token));
        completed.Should().BeSameAs(returnTask,
            "ReturnAsync が 10 秒以内に戻らない（PersistReturnAsync → CreateUsageLedgersAsync → InsertDetailsAsync の" +
            "外側 tx 未伝搬によるセマフォ再取得デッドロックの回帰）");

        var returnResult = await returnTask;
        returnResult.Success.Should().BeTrue($"返却が失敗（{returnResult.ErrorMessage}）");
    }
}
