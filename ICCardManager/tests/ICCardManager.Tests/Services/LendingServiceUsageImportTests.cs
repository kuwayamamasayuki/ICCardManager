using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using ICCardManager.Tests.Infrastructure;
using ICCardManager.Tests.Infrastructure.Timing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2293: 残額の食い違い（貸出中でないカード）を、カードの利用履歴から取り込んで復旧する。
/// </summary>
/// <remarks>
/// <para>
/// 取り込む範囲（下限・既存明細との照合）は DB の行で決まるため、リポジトリはモックにせず実物を使う
/// （<see cref="LendingServiceReturnUsageLowerBoundTests"/> と同じ理由）。操作ログも実リポジトリへ書き、
/// 台帳と同じトランザクションで確定する（失敗すれば両方とも残らない）ことを DB の行で確かめる。
/// </para>
/// </remarks>
public sealed class LendingServiceUsageImportTests : IDisposable
{
    private const string TestCardIdm = "07FE112233445566";
    private const string TestStaffIdm = "FFFF000000000001";
    private const string TestStaffName = "テスト職員";
    private const string OperatorIdm = "FFFF000000000009";
    private const string OperatorName = "取込 担当";

    private static readonly DateTime Now = new(2026, 9, 20, 10, 0, 0);

    private readonly DbContext _dbContext;
    private readonly LedgerRepository _ledgerRepository;
    private readonly CardRepository _cardRepository;
    private readonly StaffRepository _staffRepository;
    private readonly SettingsRepository _settingsRepository;
    private readonly OperationLogRepository _operationLogRepository;
    private readonly RecordingLogger<LendingService> _logger = new();
    private readonly CardLockManager _lockManager;
    private readonly LendingService _service;

    public LendingServiceUsageImportTests()
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

        _ledgerRepository = new LedgerRepository(_dbContext);
        _cardRepository = new CardRepository(_dbContext, cacheServiceMock.Object, cacheOptions, NullLogger<CardRepository>.Instance);
        _staffRepository = new StaffRepository(_dbContext, cacheServiceMock.Object, cacheOptions, NullLogger<StaffRepository>.Instance);
        _settingsRepository = new SettingsRepository(_dbContext, cacheServiceMock.Object, cacheOptions);
        _operationLogRepository = new OperationLogRepository(_dbContext);
        _lockManager = new CardLockManager(NullLogger<CardLockManager>.Instance);
        _service = CreateService(_operationLogRepository);

        _staffRepository.InsertAsync(new Staff
        {
            StaffIdm = TestStaffIdm,
            Name = TestStaffName,
            Number = "001",
            IsDeleted = false,
        }).GetAwaiter().GetResult();

        _cardRepository.InsertAsync(new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "H-001",
        }).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _lockManager.Dispose();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    #region 下限（ResolveImportLowerBound）

    [Fact]
    public void ResolveImportLowerBound_導入行があれば導入行の日付であること()
    {
        var result = LendingService.ResolveImportLowerBound(
            introductionDate: new DateTime(2026, 9, 1, 15, 0, 0),
            latestLedgerDate: new DateTime(2026, 9, 12),
            latestLedgerDateWithoutDetails: null);

        result.Should().Be(new DateTime(2026, 9, 1), "返却時（#2237）と同じく、導入行より前は導入行の残高に含まれている");
    }

    [Fact]
    public void ResolveImportLowerBound_導入行が無ければ台帳の最新行の日付であること()
    {
        var result = LendingService.ResolveImportLowerBound(
            introductionDate: null,
            latestLedgerDate: new DateTime(2026, 9, 12, 18, 30, 0),
            latestLedgerDateWithoutDetails: null);

        result.Should().Be(new DateTime(2026, 9, 12),
            "最新行と同じ日の利用は既存明細との照合で除けるので、その日を含める");
    }

    [Fact]
    public void ResolveImportLowerBound_明細を持たない行があればその翌日へ押し上げ_上限を設けないこと()
    {
        // 返却なら貸出日で頭打ちになる未来の日付でも、取り込みでは翌日まで押し上げる（守るべき貸出が無い）
        var result = LendingService.ResolveImportLowerBound(
            introductionDate: new DateTime(2026, 9, 1),
            latestLedgerDate: new DateTime(2026, 9, 25),
            latestLedgerDateWithoutDetails: new DateTime(2026, 9, 25));

        result.Should().Be(new DateTime(2026, 9, 26));
    }

    [Fact]
    public void ResolveImportLowerBound_明細を持たない行が導入行より前なら導入行の日付のままであること()
    {
        var result = LendingService.ResolveImportLowerBound(
            introductionDate: new DateTime(2026, 9, 10),
            latestLedgerDate: new DateTime(2026, 9, 12),
            latestLedgerDateWithoutDetails: new DateTime(2026, 9, 5));

        result.Should().Be(new DateTime(2026, 9, 10));
    }

    [Fact]
    public void ResolveImportLowerBound_導入行も記録も無ければnullを返すこと()
    {
        var result = LendingService.ResolveImportLowerBound(null, null, null);

        result.Should().BeNull("どこまでが計上済みかの根拠が無いので取り込まない");
    }

    #endregion

    #region 計画（PlanUsageImportAsync）

    [Fact]
    public async Task PlanUsageImportAsync_導入行より前と記録済みの利用を除いた未記録の利用だけを返すこと()
    {
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        await InsertRecordedUsageAsync(new DateTime(2026, 9, 5), balanceAfter: 4790);

        var plan = await _service.PlanUsageImportAsync(TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 15), balanceAfter: 4370),  // 未記録
            Usage(new DateTime(2026, 9, 10), balanceAfter: 4580),  // 未記録
            Usage(new DateTime(2026, 9, 5), balanceAfter: 4790),   // 記録済み（#326 で除く）
            Usage(new DateTime(2026, 8, 30), balanceAfter: 5000),  // 導入行より前（導入行の残高に含まれる）
        });

        plan.BlockReason.Should().Be(UsageImportBlockReason.None);
        plan.LowerBound.Should().Be(new DateTime(2026, 9, 1));
        plan.DetailsToRecord.Select(d => d.UseDate).Should().Equal(
            new DateTime(2026, 9, 15), new DateTime(2026, 9, 10));
    }

    [Fact]
    public async Task PlanUsageImportAsync_利用日の無い履歴は台帳行にできないので除くこと()
    {
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);

        var plan = await _service.PlanUsageImportAsync(TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 10), balanceAfter: 4790),
            new() { UseDate = null, Amount = 210, Balance = 4580 },
        });

        plan.DetailsToRecord.Should().ContainSingle()
            .Which.UseDate.Should().Be(new DateTime(2026, 9, 10));
    }

    [Fact]
    public async Task PlanUsageImportAsync_貸出中のカードは取り込まないこと()
    {
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        (await _service.LendAsync(TestStaffIdm, TestCardIdm)).Success.Should().BeTrue();

        var plan = await _service.PlanUsageImportAsync(TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 10), balanceAfter: 4790),
        });

        plan.BlockReason.Should().Be(UsageImportBlockReason.CardIsLent, "貸出中のカードは返却が利用履歴を記録する");
        plan.DetailsToRecord.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanUsageImportAsync_削除されたカードは取り込まないこと()
    {
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        (await _cardRepository.DeleteAsync(TestCardIdm)).Should().Be(CardOperationResult.Success);

        var plan = await _service.PlanUsageImportAsync(TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 10), balanceAfter: 4790),
        });

        plan.BlockReason.Should().Be(UsageImportBlockReason.CardNotInOperation);
    }

    [Fact]
    public async Task PlanUsageImportAsync_導入行も記録も無いカードは取り込まないこと()
    {
        var plan = await _service.PlanUsageImportAsync(TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 10), balanceAfter: 4790),
        });

        plan.BlockReason.Should().Be(UsageImportBlockReason.NoBaseline);
    }

    [Fact]
    public async Task PlanUsageImportAsync_台帳へは書き込まないこと()
    {
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);

        await _service.PlanUsageImportAsync(TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 10), balanceAfter: 4790),
        });

        (await GetUsageLedgersAsync()).Should().BeEmpty();
    }

    #endregion

    #region 取り込み（ImportUnrecordedUsageAsync）

    [Fact]
    public async Task ImportUnrecordedUsageAsync_未記録の利用を利用者空欄で記録し_最新残額がカードの残額に追いつくこと()
    {
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        await InsertRecordedUsageAsync(new DateTime(2026, 9, 5), balanceAfter: 4790);

        var result = await _service.ImportUnrecordedUsageAsync(TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 15), balanceAfter: 4370),
            Usage(new DateTime(2026, 9, 10), balanceAfter: 4580),
            Usage(new DateTime(2026, 9, 5), balanceAfter: 4790),
        });

        result.Success.Should().BeTrue(_logger.FormatEntries());
        result.CreatedLedgers.Should().HaveCount(2);
        result.RecordedBalanceAfter.Should().Be(4370, "カードの残額（最新の利用後）と一致する");

        var imported = (await GetUsageLedgersAsync()).Where(l => l.Date >= new DateTime(2026, 9, 10)).ToList();
        imported.Select(l => (l.Date.Date, l.Expense, l.Balance)).Should().Equal(
            (new DateTime(2026, 9, 10), 210, 4580),
            (new DateTime(2026, 9, 15), 210, 4370));
        imported.Should().OnlyContain(l => l.StaffName == null && l.LenderIdm == null,
            "カードからは誰が使ったか分からないので、利用者は空欄で記録する（後から履歴の変更で直す）");
        imported.Should().OnlyContain(l => l.Summary == "鉄道（博多～天神）", "摘要は返却と同じ生成器で作る");

        var card = await _cardRepository.GetByIdmAsync(TestCardIdm);
        card!.IsLent.Should().BeFalse("取り込みは貸出状態を変えない");
    }

    [Fact]
    public async Task ImportUnrecordedUsageAsync_記録した行ごとに認証した職員を操作者として操作ログへ残すこと()
    {
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);

        var result = await _service.ImportUnrecordedUsageAsync(TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 15), balanceAfter: 4580),
            Usage(new DateTime(2026, 9, 10), balanceAfter: 4790),
        });

        result.Success.Should().BeTrue(_logger.FormatEntries());
        var logs = await GetLedgerLogsAsync();
        logs.Should().HaveCount(2);
        logs.Should().OnlyContain(l => l.Action == OperationLogger.Actions.Insert
            && l.OperatorIdm == OperatorIdm && l.OperatorName == OperatorName);
        logs.Select(l => l.TargetId).Should().BeEquivalentTo(
            result.CreatedLedgers.Select(l => l.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task ImportUnrecordedUsageAsync_同じ日の利用者空欄の行へ足したときは変更前を残してUPDATEで記録すること()
    {
        // 9/10 にカード登録時の取り込み（利用者空欄・明細あり）で記録済みの行がある。同じ日の未記録の利用は
        // 返却と同じ同日統合（#837 / #1147）でこの行へ足される
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        var existingId = await InsertRecordedUsageAsync(new DateTime(2026, 9, 10), balanceAfter: 4790, staffName: null);

        var result = await _service.ImportUnrecordedUsageAsync(TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 10), balanceAfter: 4580),
            Usage(new DateTime(2026, 9, 10), balanceAfter: 4790),
        });

        result.Success.Should().BeTrue(_logger.FormatEntries());
        result.CreatedLedgers.Should().ContainSingle().Which.Id.Should().Be(existingId);

        var merged = await _ledgerRepository.GetByIdAsync(existingId);
        merged!.Balance.Should().Be(4580);
        merged.Expense.Should().Be(420);

        var log = (await GetLedgerLogsAsync()).Should().ContainSingle().Subject;
        log.Action.Should().Be(OperationLogger.Actions.Update);
        log.TargetId.Should().Be(existingId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        log.BeforeData.Should().Contain("4790", "変更前は明細を足す前の行（残額 4,790 円）");
        log.AfterData.Should().Contain("4580");
    }

    [Fact]
    public async Task ImportUnrecordedUsageAsync_2回実行しても二重に記録しないこと()
    {
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        var history = new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 15), balanceAfter: 4580),
            Usage(new DateTime(2026, 9, 10), balanceAfter: 4790),
        };

        (await _service.ImportUnrecordedUsageAsync(TestCardIdm, history)).Success.Should().BeTrue();
        var second = await _service.ImportUnrecordedUsageAsync(TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 15), balanceAfter: 4580),
            Usage(new DateTime(2026, 9, 10), balanceAfter: 4790),
        });

        second.Success.Should().BeTrue("記録する利用が無いのは失敗ではない");
        second.CreatedLedgers.Should().BeEmpty();
        (await GetUsageLedgersAsync()).Should().HaveCount(2);
        (await GetLedgerLogsAsync()).Should().HaveCount(2, "2 回目は何も書かない");
    }

    [Fact]
    public async Task ImportUnrecordedUsageAsync_残高不足で現金を足した利用は返却と同じく1行にまとめること()
    {
        // 残額 200 円で運賃 210 円 → 10 円チャージして 210 円を払う（Issue #380 / #978）
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 200);

        var result = await _service.ImportUnrecordedUsageAsync(TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 10), balanceAfter: 0),
            new() { UseDate = new DateTime(2026, 9, 10), IsCharge = true, Amount = 10, Balance = 210 },
        });

        result.Success.Should().BeTrue(_logger.FormatEntries());
        var row = (await GetUsageLedgersAsync()).Should().ContainSingle().Subject;
        row.Expense.Should().Be(200, "払出は運賃からチャージ額を引いた額（カードの元残高から充当した額）");
        row.Balance.Should().Be(0);
        row.Note.Should().Contain("現金");
    }

    [Fact]
    public async Task ImportUnrecordedUsageAsync_貸出中のカードには何も書かずに理由を返すこと()
    {
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        (await _service.LendAsync(TestStaffIdm, TestCardIdm)).Success.Should().BeTrue();

        var result = await _service.ImportUnrecordedUsageAsync(TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 10), balanceAfter: 4790),
        });

        result.Success.Should().BeFalse();
        result.BlockReason.Should().Be(UsageImportBlockReason.CardIsLent);
        (await GetUsageLedgersAsync()).Should().BeEmpty();
        (await GetLedgerLogsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ImportUnrecordedUsageAsync_操作ログを書けなければ台帳の行も残さないこと()
    {
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        var failingLogRepository = new Mock<IOperationLogRepository>();
        failingLogRepository
            .Setup(r => r.InsertAsync(It.IsAny<OperationLog>(), It.IsAny<SQLiteTransaction>()))
            .ThrowsAsync(new InvalidOperationException("操作ログの書き込みに失敗（テスト）"));
        var service = CreateService(failingLogRepository.Object);

        var result = await service.ImportUnrecordedUsageAsync(TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 10), balanceAfter: 4790),
        });

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
        (await GetUsageLedgersAsync()).Should().BeEmpty("台帳と操作ログは 1 つのトランザクションで確定する");
        (await _ledgerRepository.GetExistingDetailKeysAsync(TestCardIdm, new DateTime(2026, 9, 1)))
            .Should().BeEmpty("明細も巻き戻る（次の取り込み・返却の照合を誤らせない）");
    }

    [Fact]
    public async Task ImportUnrecordedUsageAsync_記録する利用が無ければ何も書かずに成功を返すこと()
    {
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);

        var result = await _service.ImportUnrecordedUsageAsync(TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 8, 30), balanceAfter: 5000),  // 導入行より前
        });

        result.Success.Should().BeTrue();
        result.CreatedLedgers.Should().BeEmpty();
        result.RecordedBalanceAfter.Should().Be(5000, "残る差額の案内に使う（導入行の残額）");
        (await GetLedgerLogsAsync()).Should().BeEmpty();
    }

    #endregion

    #region ヘルパー

    private LendingService CreateService(IOperationLogRepository operationLogRepository)
    {
        var operatorContext = new Mock<ICurrentOperatorContext>();
        string? operatorIdmOut = OperatorIdm;
        string? operatorNameOut = OperatorName;
        operatorContext.Setup(c => c.TryGetOperator(out operatorIdmOut, out operatorNameOut)).Returns(true);

        return new LendingService(
            _dbContext,
            _cardRepository,
            _staffRepository,
            _ledgerRepository,
            _settingsRepository,
            new OperationLogger(operationLogRepository, operatorContext.Object),
            new SummaryGenerator(DepartmentType.MayorOffice),
            _lockManager,
            Options.Create(new AppOptions { CardLockTimeoutSeconds = 5, RetouchWindowSeconds = 30 }),
            _logger,
            new FixedSystemClock(Now));
    }

    private async Task InsertIntroductionAsync(DateTime date, int balance)
    {
        await _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = TestCardIdm,
            Date = date,
            Summary = "新規購入",
            Income = balance,
            Expense = 0,
            Balance = balance,
        });
    }

    private async Task<int> InsertRecordedUsageAsync(DateTime date, int balanceAfter, string? staffName = TestStaffName)
    {
        var id = await _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = TestCardIdm,
            Date = date,
            Summary = "鉄道（博多～天神）",
            Expense = 210,
            Balance = balanceAfter,
            LenderIdm = staffName == null ? null : TestStaffIdm,
            StaffName = staffName,
        });
        await _ledgerRepository.InsertDetailsAsync(id, new[] { Usage(date, balanceAfter) });
        return id;
    }

    /// <summary>導入行・貸出中レコードを除いた、利用の台帳行（日付昇順）</summary>
    private async Task<List<Ledger>> GetUsageLedgersAsync()
    {
        var ledgers = await _ledgerRepository.GetByDateRangeAsync(
            TestCardIdm, new DateTime(2026, 1, 1), new DateTime(2026, 12, 31));
        return ledgers
            .Where(l => !l.IsLentRecord && !l.IsInitialRecord)
            .OrderBy(l => l.Date)
            .ThenBy(l => l.Id)
            .ToList();
    }

    /// <summary>台帳（ledger）を対象にした操作ログ</summary>
    private async Task<List<OperationLog>> GetLedgerLogsAsync()
    {
        var logs = await _operationLogRepository.GetByDateRangeAsync(new DateTime(2000, 1, 1), new DateTime(2100, 1, 1));
        return logs.Where(l => l.TargetTable == OperationLogger.Tables.Ledger).ToList();
    }

    private static LedgerDetail Usage(DateTime useDate, int balanceAfter) => new()
    {
        UseDate = useDate,
        EntryStation = "博多",
        ExitStation = "天神",
        Amount = 210,
        Balance = balanceAfter,
    };

    #endregion
}
