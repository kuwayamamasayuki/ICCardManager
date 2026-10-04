using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Security;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure;
using ICCardManager.Tests.Infrastructure.Timing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2233: <see cref="LendingService"/> を Null 許容参照型へ移行したときに是正した、null の扱いの回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// 返却時の同日統合（Issue #837）は、既存の利用レコードへ明細を追加した直後に <see cref="ILedgerQueryService.GetByIdAsync"/> で
/// 全明細を読み直す。#2220 でこのメソッドは <c>Task&lt;Ledger?&gt;</c>（行が無ければ null）と注釈されたが、
/// 戻り値を null チェックせずに参照していた。現状の呼び出し経路では同じトランザクションで明細を INSERT した直後なので
/// 到達しないが、その前提はコードから読み取れず、崩れると <see cref="NullReferenceException"/> が返却の catch へ落ちて
/// 「何が起きたか」の分からない失敗になっていた。原因（統合先の利用レコードが読み直せない）と LedgerId を名指しする例外で止める。
/// </para>
/// </remarks>
public class LendingServiceSameDayMergeNullTests : IDisposable
{
    private const string TestCardIdm = "0102030405060708";
    private const string TestStaffIdm = "1112131415161718";
    private const string TestStaffName = "テスト太郎";
    private const int ExistingLedgerId = 42;

    /// <summary>本体が読む現在時刻（固定。testing.md「現在時刻に依存する本体は、時計を注入して固定日時で検証する」）</summary>
    private static readonly DateTime Now = new(2026, 6, 15, 10, 0, 0);

    private static readonly DateTime Today = Now.Date;

    private readonly DbContext _dbContext;
    private readonly Mock<ICardRepository> _cardRepositoryMock = new();
    private readonly Mock<IStaffRepository> _staffRepositoryMock = new();
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock = new();
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock = new();
    private readonly CardLockManager _lockManager;
    private readonly RecordingLogger<LendingService> _logger = new();
    private readonly LendingService _service;

    public LendingServiceSameDayMergeNullTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();

        _settingsRepositoryMock.Setup(s => s.GetAppSettings()).Returns(new AppSettings());
        _settingsRepositoryMock.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings { WarningBalance = 1000 });

        _lockManager = new CardLockManager(Microsoft.Extensions.Logging.Abstractions.NullLogger<CardLockManager>.Instance);

        _service = new LendingService(
            _dbContext,
            _cardRepositoryMock.Object,
            _staffRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            new OperationLogger(Mock.Of<IOperationLogRepository>(), Mock.Of<ICurrentOperatorContext>()),
            new SummaryGenerator(),
            _lockManager,
            Options.Create(new AppOptions()),
            _logger,
            new FixedSystemClock(Now));
    }

    public void Dispose()
    {
        _lockManager.Dispose();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ReturnAsync_同日統合の対象を読み直せないとき_原因とLedgerIdを名指しして返却を失敗させること()
    {
        // Arrange: 同日の既存利用レコードがあり、明細を追加した後の読み直しで行が見つからない
        SetupSameDayMerge(reloaded: null);

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, CreateUsageDetails());

        // Assert: 返却は失敗として扱われ、統合先は書き換えない
        result.Success.Should().BeFalse("統合先を読み直せないまま返却を記録しない");
        _ledgerRepositoryMock.Verify(
            x => x.UpdateAsync(It.Is<Ledger>(l => l.Id == ExistingLedgerId)), Times.Never(),
            "読み直せなかった統合先を、再計算しないまま更新しない");

        // ログには原因と LedgerId が残る（NullReferenceException では何が null だったのか分からない）
        var error = _logger.Entries.Where(e => e.Level == LogLevel.Error).Should().ContainSingle(_logger.FormatEntries()).Subject;
        error.Exception.Should().BeOfType<InvalidOperationException>(_logger.FormatEntries())
            .Which.Message.Should().Contain($"LedgerId={ExistingLedgerId}");

        // 利用者向けの文言には、例外の原文（内部の ID）を出さない（Issue #1614）
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        result.ErrorMessage.Should().NotContain("LedgerId");
    }

    [Fact]
    public async Task ReturnAsync_同日統合の対象を読み直せたとき_統合先を更新して返却を記録すること()
    {
        // 対の表明: 上のテストと同じ準備で、読み直しが成功すれば統合の経路を最後まで通る
        // （準備が同日統合の経路に届いていない＝別の理由で失敗している、を検出する）
        var reloaded = new Ledger
        {
            Id = ExistingLedgerId,
            CardIdm = TestCardIdm,
            Date = Today,
            Summary = "鉄道（博多～天神）",
            Expense = 420,
            Balance = 1580,
            LenderIdm = TestStaffIdm,
            StaffName = TestStaffName,
            Details = new List<LedgerDetail>
            {
                new() { UseDate = Today, EntryStation = "博多", ExitStation = "天神", Amount = 210, Balance = 1790 },
                new() { UseDate = Today, EntryStation = "天神", ExitStation = "博多", Amount = 210, Balance = 1580 },
            },
        };
        SetupSameDayMerge(reloaded);

        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, CreateUsageDetails());

        result.Success.Should().BeTrue(_logger.FormatEntries());
        _ledgerRepositoryMock.Verify(
            x => x.UpdateAsync(It.Is<Ledger>(l => l.Id == ExistingLedgerId)), Times.Once());
        _logger.Entries.Should().NotContain(e => e.Level == LogLevel.Error, _logger.FormatEntries());
    }

    [Fact]
    public async Task ImportHistoryForRegistrationAsync_履歴にnullを渡すとArgumentNullExceptionになること()
    {
        // 呼び出し元は履歴が無いときも空リストを渡す（Issue #1763）。null は契約違反として入口で止める
        var act = () => _service.ImportHistoryForRegistrationAsync(TestCardIdm, null!, Today);

        (await act.Should().ThrowAsync<ArgumentNullException>())
            .Which.ParamName.Should().Be("historyDetails");
        _ledgerRepositoryMock.Verify(x => x.InsertAsync(It.IsAny<Ledger>()), Times.Never());
    }

    private static List<LedgerDetail> CreateUsageDetails()
        => new()
        {
            new()
            {
                UseDate = Today,
                EntryStation = "天神",
                ExitStation = "博多",
                Amount = 210,
                Balance = 1580,
            },
        };

    /// <summary>
    /// 返却の標準モックに加え、同日統合の対象になる既存の利用レコードと、明細追加後の読み直しの結果を設定する。
    /// </summary>
    private void SetupSameDayMerge(Ledger? reloaded)
    {
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(TestCardIdm, false))
            .ReturnsAsync(new IcCard { CardIdm = TestCardIdm, CardType = "はやかけん", CardNumber = "H001", IsLent = true });
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(TestStaffIdm, false))
            .ReturnsAsync(new Staff { StaffIdm = TestStaffIdm, Name = TestStaffName });
        _ledgerRepositoryMock.Setup(x => x.GetLentRecordAsync(TestCardIdm))
            .ReturnsAsync(new Ledger
            {
                Id = 1,
                CardIdm = TestCardIdm,
                LenderIdm = TestStaffIdm,
                StaffName = TestStaffName,
                Date = Today,
                IsLentRecord = true,
                LentAt = Now.AddHours(-1),
                Summary = SummaryGenerator.GetLendingSummary(),
            });
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(100);
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>())).ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(x => x.DeleteAllLentRecordsAsync(TestCardIdm)).ReturnsAsync(1);
        _ledgerRepositoryMock.Setup(x => x.InsertDetailAsync(It.IsAny<LedgerDetail>())).ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(x => x.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(x => x.GetLatestBeforeDateAsync(TestCardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync(new Ledger { Balance = 1790 });
        _ledgerRepositoryMock.Setup(x => x.GetExistingDetailKeysAsync(TestCardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync(new HashSet<(DateTime?, int?, bool)>());
        _cardRepositoryMock.Setup(x => x.UpdateLentStatusAsync(TestCardIdm, false, null, null)).ReturnsAsync(true);

        // 同日・同じ職員の利用レコード（統合の対象）
        _ledgerRepositoryMock.Setup(x => x.GetByDateRangeAsync(TestCardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<Ledger>
            {
                new()
                {
                    Id = ExistingLedgerId,
                    CardIdm = TestCardIdm,
                    Date = Today,
                    Summary = "鉄道（博多～天神）",
                    Expense = 210,
                    Balance = 1790,
                    LenderIdm = TestStaffIdm,
                    StaffName = TestStaffName,
                },
            });
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(ExistingLedgerId)).ReturnsAsync(reloaded);
    }
}
