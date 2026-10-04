using System;
using System.Collections.Generic;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2237: 返却時に記録する利用履歴の下限（導入行の日付）を、実 DB を通して検証する。
/// </summary>
/// <remarks>
/// <para>
/// 修正前は貸出日の 7 日前より古い履歴を一律に捨てており、カードに残る直近 20 件の範囲内にある
/// 未記録の利用（ピッすいを通さずに使われた利用）が記録漏れのまま確定していた。
/// 下限は導入行（「新規購入」「○月から繰越」「前年度より繰越」）の日付で、それより前の利用は導入行の残高に
/// 含まれているので取り込まない。記録済みの利用を二重に記録しないのは既存明細との照合（Issue #326）の役目。
/// </para>
/// <para>
/// 照合と導入行の判定はどちらも DB の行で決まるため、リポジトリはモックにせず実物を使う
/// （モックの既定値では「導入行が無い」「既存明細が無い」が常に成り立ち、下限も照合も検証できない）。
/// </para>
/// </remarks>
public sealed class LendingServiceReturnUsageLowerBoundTests : IDisposable
{
    private const string TestCardIdm = "07FE112233445566";
    private const string TestStaffIdm = "FFFF000000000001";
    private const string TestStaffName = "テスト職員";

    /// <summary>貸出・返却の時刻（固定）。貸出日の 7 日前は 9/13</summary>
    private static readonly DateTime Now = new(2026, 9, 20, 10, 0, 0);

    private readonly DbContext _dbContext;
    private readonly LedgerRepository _ledgerRepository;
    private readonly RecordingLogger<LendingService> _logger = new();
    private readonly CardLockManager _lockManager;
    private readonly LendingService _service;

    public LendingServiceReturnUsageLowerBoundTests()
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
        var cardRepo = new CardRepository(_dbContext, cacheServiceMock.Object, cacheOptions, NullLogger<CardRepository>.Instance);
        var staffRepo = new StaffRepository(_dbContext, cacheServiceMock.Object, cacheOptions, NullLogger<StaffRepository>.Instance);
        var settingsRepo = new SettingsRepository(_dbContext, cacheServiceMock.Object, cacheOptions);
        _lockManager = new CardLockManager(NullLogger<CardLockManager>.Instance);

        _service = new LendingService(
            _dbContext,
            cardRepo,
            staffRepo,
            _ledgerRepository,
            settingsRepo,
            new OperationLogger(Mock.Of<IOperationLogRepository>(), Mock.Of<ICurrentOperatorContext>()),
            new SummaryGenerator(DepartmentType.MayorOffice),
            _lockManager,
            Options.Create(new AppOptions { CardLockTimeoutSeconds = 5, RetouchWindowSeconds = 30 }),
            _logger,
            new FixedSystemClock(Now));

        staffRepo.InsertAsync(new Staff
        {
            StaffIdm = TestStaffIdm,
            Name = TestStaffName,
            Number = "001",
            IsDeleted = false,
        }).GetAwaiter().GetResult();

        cardRepo.InsertAsync(new IcCard
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

    [Fact]
    public async Task ReturnAsync_導入行があるとき_貸出日の8日以上前の未記録の利用を記録すること()
    {
        // Arrange: 9/1 に導入。9/5 にピッすいを通さずに使われ、9/20 に貸出 → 返却
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        await LendAsync();

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 20), balanceAfter: 4580),
            Usage(new DateTime(2026, 9, 5), balanceAfter: 4790),   // 貸出日の 15 日前（修正前は捨てられた）
        });

        // Assert
        result.Success.Should().BeTrue(_logger.FormatEntries());
        var usageLedgers = await GetUsageLedgersAsync();
        usageLedgers.Select(l => l.Date.Date).Should().Equal(
            new[] { new DateTime(2026, 9, 5), new DateTime(2026, 9, 20) },
            "導入日以降でカードに残っている未記録の利用は、貸出日の前後を問わず記録する");

        var old = usageLedgers.Single(l => l.Date.Date == new DateTime(2026, 9, 5));
        old.Expense.Should().Be(210);
        old.Balance.Should().Be(4790, "残高のチェーンが 9/5 の分だけずれない");
        old.StaffName.Should().Be(TestStaffName, "貸出日より前の利用も、今回の返却者の利用として記録する（従来の 7 日以内の分と同じ）");
    }

    [Fact]
    public async Task ReturnAsync_記録済みの利用は_範囲を広げても二重に記録しないこと()
    {
        // Arrange: 9/5 の利用は前回の返却で記録済み（既存明細のキー = 利用日 + 残高 + チャージ区分）
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        await InsertRecordedUsageAsync(new DateTime(2026, 9, 5), balanceAfter: 4790);
        await LendAsync();

        // Act: カードには 9/5 の利用が残ったまま
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 20), balanceAfter: 4580),
            Usage(new DateTime(2026, 9, 5), balanceAfter: 4790),
        });

        // Assert
        result.Success.Should().BeTrue(_logger.FormatEntries());
        var usageLedgers = await GetUsageLedgersAsync();
        usageLedgers.Count(l => l.Date.Date == new DateTime(2026, 9, 5)).Should().Be(1,
            "記録済みの利用は既存明細との照合（Issue #326）で除外する");
        usageLedgers.Should().ContainSingle(l => l.Date.Date == new DateTime(2026, 9, 20),
            "未記録の利用は記録する（照合が全件を落としていないことの対の表明）");
    }

    [Fact]
    public async Task ReturnAsync_導入行より前の利用は_貸出日の7日以内でも取り込まないこと()
    {
        // Arrange: 9/18 に導入（それより前の利用は導入行の残高 4790 円に含まれている）
        await InsertIntroductionAsync(new DateTime(2026, 9, 18), balance: 4790);
        await LendAsync();

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 20), balanceAfter: 4370),
            Usage(new DateTime(2026, 9, 18), balanceAfter: 4580),  // 導入日当日（境界。登録後の利用）
            Usage(new DateTime(2026, 9, 15), balanceAfter: 4790),  // 導入前・貸出日の 5 日前（修正前は取り込んでいた）
            Usage(new DateTime(2026, 9, 10), balanceAfter: 5000),  // 導入前・貸出日の 10 日前
        });

        // Assert
        result.Success.Should().BeTrue(_logger.FormatEntries());
        var usageLedgers = await GetUsageLedgersAsync();
        usageLedgers.Select(l => l.Date.Date).Should().Equal(
            new[] { new DateTime(2026, 9, 18), new DateTime(2026, 9, 20) },
            "導入行より前の利用は導入行の残高と二重に計上になるので取り込まない。導入日当日は取り込む");
    }

    [Fact]
    public async Task ReturnAsync_導入行が無いとき_従来どおり貸出日の7日前より古い利用は取り込まないこと()
    {
        // Arrange: 導入行の無いカード（導入前のデータ・登録時に残額を読めなかった等）。
        // どこまでが計上済みか決める根拠が無いので、挙動を変えない側（貸出日の 7 日前）へ倒す
        await LendAsync();

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 20), balanceAfter: 4370),
            Usage(new DateTime(2026, 9, 13), balanceAfter: 4580),  // 貸出日の 7 日前（境界・含む）
            Usage(new DateTime(2026, 9, 12), balanceAfter: 4790),  // 貸出日の 8 日前（含まない）
        });

        // Assert
        result.Success.Should().BeTrue(_logger.FormatEntries());
        var usageLedgers = await GetUsageLedgersAsync();
        usageLedgers.Select(l => l.Date.Date).Should().Equal(
            new[] { new DateTime(2026, 9, 13), new DateTime(2026, 9, 20) });
    }

    [Fact]
    public async Task ReturnAsync_貸出日より前の利用を記録したとき_件数と最古の利用日をInformationで残すこと()
    {
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        await LendAsync();

        await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 20), balanceAfter: 4370),
            Usage(new DateTime(2026, 9, 8), balanceAfter: 4580),
            Usage(new DateTime(2026, 9, 5), balanceAfter: 4790),
        });

        var entry = _logger.Entries
            .Where(e => e.Level == LogLevel.Information && e.Message.Contains("貸出日より前の利用"))
            .Should().ContainSingle(_logger.FormatEntries()).Subject;
        entry.Message.Should().Contain("貸出日=2026-09-20")
            .And.Contain("該当する台帳行=2件", "9/5 と 9/8 の 2 日分")
            .And.Contain("最も古い利用日=2026-09-05")
            .And.NotContain(TestCardIdm, "カード IDm はログへ生のまま残さない（IdmMasker.Mask）");
    }

    [Fact]
    public async Task ReturnAsync_貸出後の利用だけを記録したとき_貸出日より前の利用のログを出さないこと()
    {
        // 対の表明: 上のテストだけだと「常に出す」実装でも緑になる
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        await LendAsync();

        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 20), balanceAfter: 4790),
        });

        result.CreatedLedgers.Should().NotBeEmpty("台帳行が作られた返却で、ログを出さないことを確かめる");
        _logger.Entries.Should().NotContain(e => e.Message.Contains("貸出日より前の利用"),
            "通常の返却では出さない（正常運用でのログ肥大化を防ぐ）");
    }

    private async Task LendAsync()
    {
        var lendResult = await _service.LendAsync(TestStaffIdm, TestCardIdm);
        lendResult.Success.Should().BeTrue($"貸出が失敗（{lendResult.ErrorMessage}）");
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

    private async Task InsertRecordedUsageAsync(DateTime date, int balanceAfter)
    {
        var id = await _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = TestCardIdm,
            Date = date,
            Summary = "鉄道（博多～天神）",
            Expense = 210,
            Balance = balanceAfter,
            LenderIdm = TestStaffIdm,
            StaffName = TestStaffName,
        });
        await _ledgerRepository.InsertDetailsAsync(id, new[] { Usage(date, balanceAfter) });
    }

    /// <summary>導入行・貸出中レコードを除いた、利用の台帳行（日付昇順）</summary>
    private async Task<List<Ledger>> GetUsageLedgersAsync()
    {
        var ledgers = await _ledgerRepository.GetByDateRangeAsync(
            TestCardIdm, new DateTime(2026, 1, 1), new DateTime(2026, 12, 31));
        return ledgers
            .Where(l => !l.IsLentRecord && !l.IsInitialRecord)
            .OrderBy(l => l.Date)
            .ToList();
    }

    private static LedgerDetail Usage(DateTime useDate, int balanceAfter) => new()
    {
        UseDate = useDate,
        EntryStation = "博多",
        ExitStation = "天神",
        Amount = 210,
        Balance = balanceAfter,
    };
}
