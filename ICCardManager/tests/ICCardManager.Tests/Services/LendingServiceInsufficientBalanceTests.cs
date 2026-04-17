using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// LendingService.ReturnAsync の残高不足パターン（マージ処理）E2Eテスト (Issue #1256)。
///
/// LendingHistoryAnalyzer の純粋検出ロジックではなく、ReturnAsync を通して
/// Ledger の Expense / Balance / Note / StaffName が期待どおりマージされるかを検証する。
///
/// 検証観点:
/// - Issue #1256 例1: 残高76 → 140円チャージ → 210円利用 → 払出70・残額6
/// - Issue #1256 例2: 残高10 → 200円ぴったりチャージ → 210円利用 → 払出10・残額0
/// - Note フォーマット完全一致（OrganizationOptions.InsufficientBalanceNoteFormat 準拠）
/// - 連続する端数チャージ複数件 → 最後の1件のみマージ、先行チャージは別Ledger
/// </summary>
public class LendingServiceInsufficientBalanceTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly Mock<ICardRepository> _cardRepositoryMock;
    private readonly Mock<IStaffRepository> _staffRepositoryMock;
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock;
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock;
    private readonly SummaryGenerator _summaryGenerator;
    private readonly CardLockManager _lockManager;
    private readonly LendingService _service;

    private const string TestCardIdm = "0102030405060708";
    private const string TestStaffIdm = "1112131415161718";
    private const string TestStaffName = "テスト太郎";

    public LendingServiceInsufficientBalanceTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();

        _cardRepositoryMock = new Mock<ICardRepository>();
        _staffRepositoryMock = new Mock<IStaffRepository>();
        _ledgerRepositoryMock = new Mock<ILedgerRepository>();
        _settingsRepositoryMock = new Mock<ISettingsRepository>();
        _settingsRepositoryMock.Setup(s => s.GetAppSettings()).Returns(new AppSettings());

        _ledgerRepositoryMock.Setup(x => x.DeleteAllLentRecordsAsync(It.IsAny<string>()))
            .ReturnsAsync(1);

        _summaryGenerator = new SummaryGenerator();
        _lockManager = new CardLockManager(NullLogger<CardLockManager>.Instance);

        _service = new LendingService(
            _dbContext,
            _cardRepositoryMock.Object,
            _staffRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            _summaryGenerator,
            _lockManager,
            Options.Create(new AppOptions()),
            NullLogger<LendingService>.Instance);
    }

    public void Dispose()
    {
        _lockManager.Dispose();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private static IcCard CreateCard() => new()
    {
        CardIdm = TestCardIdm,
        CardType = "はやかけん",
        CardNumber = "H001",
        IsLent = true,
        IsDeleted = false
    };

    private static Staff CreateStaff() => new()
    {
        StaffIdm = TestStaffIdm,
        Name = TestStaffName,
        IsDeleted = false
    };

    private static Ledger CreateLentRecord() => new()
    {
        Id = 1,
        CardIdm = TestCardIdm,
        LenderIdm = TestStaffIdm,
        StaffName = TestStaffName,
        Date = DateTime.Today,
        IsLentRecord = true,
        LentAt = DateTime.Now.AddHours(-1),
        Summary = "（貸出中）"
    };

    private void SetupReturnMocks()
    {
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(TestCardIdm, false)).ReturnsAsync(CreateCard());
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(TestStaffIdm, false)).ReturnsAsync(CreateStaff());
        _ledgerRepositoryMock.Setup(x => x.GetLentRecordAsync(TestCardIdm)).ReturnsAsync(CreateLentRecord());
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>())).ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(x => x.DeleteAllLentRecordsAsync(TestCardIdm)).ReturnsAsync(1);
        _ledgerRepositoryMock.Setup(x => x.InsertDetailAsync(It.IsAny<LedgerDetail>())).ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(x => x.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(x => x.GetLatestBeforeDateAsync(TestCardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync(new Ledger { Balance = 10000 });
        _ledgerRepositoryMock.Setup(x => x.GetExistingDetailKeysAsync(TestCardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync(new HashSet<(DateTime?, int?, bool)>());
        _cardRepositoryMock.Setup(x => x.UpdateLentStatusAsync(TestCardIdm, false, null, null))
            .ReturnsAsync(true);
        _settingsRepositoryMock.Setup(x => x.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { WarningBalance = 1000 });
        _ledgerRepositoryMock.Setup(x => x.GetByDateRangeAsync(TestCardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<Ledger>());
    }

    /// <summary>
    /// Issue #1256 例1: 残高76円 → 140円チャージ → 210円利用 → 残額6円
    /// マージ結果が 払出70円・残額6円・Noteに不足額140円記載 であること。
    /// </summary>
    [Fact]
    public async Task ReturnAsync_Issue1256_76to140to210_MergesWithExpense70Balance6()
    {
        // Arrange
        SetupReturnMocks();
        var capturedLedgers = new List<Ledger>();
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => capturedLedgers.Add(l))
            .ReturnsAsync(1);

        var today = DateTime.Today;
        var usageDetails = new List<LedgerDetail>
        {
            // 残高76 → 140円チャージ → チャージ後216円
            new() { UseDate = today, IsCharge = true, Amount = 140, Balance = 216 },
            // 210円利用 → 残額6円
            new() { UseDate = today, EntryStation = "博多", ExitStation = "天神", Amount = 210, Balance = 6 }
        };

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert
        result.Success.Should().BeTrue();

        var nonLentLedgers = capturedLedgers.Where(l => !l.IsLentRecord).ToList();
        nonLentLedgers.Should().HaveCount(1, "残高不足パターンはチャージ+利用の2件が1件にマージされる");

        var merged = nonLentLedgers[0];
        merged.Income.Should().Be(0);
        merged.Expense.Should().Be(70, "払出 = 運賃210 - チャージ額140 = 70円");
        merged.Balance.Should().Be(6, "端数チャージのため残額6円が残る");
        merged.Note.Should().NotBeNullOrEmpty();
        merged.Note.Should().Contain("210", "支払額210円がNoteに含まれる");
        merged.Note.Should().Contain("140", "不足額140円（=チャージ額）がNoteに含まれる");
        merged.Note.Should().Contain("現金");
        merged.StaffName.Should().Be(TestStaffName);
    }

    /// <summary>
    /// Issue #1256 例2: 残高10円 → 200円ぴったりチャージ → 210円利用 → 残額0円
    /// マージ結果が 払出10円・残額0円・Noteに不足額200円記載 であること。
    /// </summary>
    [Fact]
    public async Task ReturnAsync_Issue1256_10to200to210_MergesWithExpense10Balance0()
    {
        // Arrange
        SetupReturnMocks();
        var capturedLedgers = new List<Ledger>();
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => capturedLedgers.Add(l))
            .ReturnsAsync(1);

        var today = DateTime.Today;
        var usageDetails = new List<LedgerDetail>
        {
            // 残高10 → 200円ぴったりチャージ → チャージ後210円
            new() { UseDate = today, IsCharge = true, Amount = 200, Balance = 210 },
            // 210円利用 → 残額0円
            new() { UseDate = today, EntryStation = "博多", ExitStation = "天神", Amount = 210, Balance = 0 }
        };

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert
        result.Success.Should().BeTrue();

        var nonLentLedgers = capturedLedgers.Where(l => !l.IsLentRecord).ToList();
        nonLentLedgers.Should().HaveCount(1);

        var merged = nonLentLedgers[0];
        merged.Income.Should().Be(0);
        merged.Expense.Should().Be(10, "払出 = 運賃210 - チャージ額200 = 10円");
        merged.Balance.Should().Be(0, "ぴったりチャージのため残額0円");
        merged.Note.Should().Contain("210");
        merged.Note.Should().Contain("200");
        merged.Note.Should().Contain("現金");
        merged.StaffName.Should().Be(TestStaffName);
    }

    /// <summary>
    /// Note が <see cref="OrganizationOptions.SummaryText.InsufficientBalanceNoteFormat"/>
    /// のデフォルトフォーマット "支払額{0}円のうち不足額{1}円は現金で支払（旅費支給）" と
    /// 完全一致することを検証（Issue #1256）。
    /// </summary>
    [Fact]
    public async Task ReturnAsync_InsufficientBalance_NoteFormatExactMatch()
    {
        // Arrange: 残高0 → 210円ぴったりチャージ → 210円利用 → 残額0
        SetupReturnMocks();
        var capturedLedgers = new List<Ledger>();
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => capturedLedgers.Add(l))
            .ReturnsAsync(1);

        var today = DateTime.Today;
        var usageDetails = new List<LedgerDetail>
        {
            new() { UseDate = today, IsCharge = true, Amount = 210, Balance = 210 },
            new() { UseDate = today, EntryStation = "博多", ExitStation = "天神", Amount = 210, Balance = 0 }
        };

        // Act
        await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert: Note 完全一致
        var merged = capturedLedgers.First(l => !l.IsLentRecord);
        merged.Note.Should().Be(
            "支払額210円のうち不足額210円は現金で支払（旅費支給）",
            "Note は OrganizationOptions のデフォルトフォーマットに完全一致する");
    }

    /// <summary>
    /// 連続する端数チャージが複数ある場合、最後の1件のみ利用とマージされ、
    /// 先行するチャージは別Ledger（チャージ単独行）として残ること（Issue #1256）。
    /// </summary>
    /// <remarks>
    /// シナリオ（元残高0円）:
    /// <list type="number">
    /// <item>1回目チャージ 100円 → 残高100</item>
    /// <item>2回目チャージ 150円 → 残高250</item>
    /// <item>240円利用 → 残額10</item>
    /// </list>
    /// 期待動作:
    /// - 2回目チャージ + 利用 = マージ1件（払出90、残額10）
    /// - 1回目チャージ = 独立したチャージ行1件
    /// </remarks>
    [Fact]
    public async Task ReturnAsync_MultipleConsecutiveCharges_OnlyLastMerged()
    {
        // Arrange
        SetupReturnMocks();
        var capturedLedgers = new List<Ledger>();
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => capturedLedgers.Add(l))
            .ReturnsAsync(1);

        var today = DateTime.Today;
        var usageDetails = new List<LedgerDetail>
        {
            new() { UseDate = today, IsCharge = true, Amount = 100, Balance = 100 },
            new() { UseDate = today, IsCharge = true, Amount = 150, Balance = 250 },
            new() { UseDate = today, EntryStation = "博多", ExitStation = "天神", Amount = 240, Balance = 10 }
        };

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert
        result.Success.Should().BeTrue();

        var nonLentLedgers = capturedLedgers.Where(l => !l.IsLentRecord).ToList();

        // マージLedger: Note が付与された残高不足パターン行
        var mergedLedger = nonLentLedgers.SingleOrDefault(l => !string.IsNullOrEmpty(l.Note));
        mergedLedger.Should().NotBeNull("2回目チャージと利用のマージLedgerが1件存在する");
        mergedLedger!.Expense.Should().Be(90, "払出 = 運賃240 - チャージ額150 = 90円");
        mergedLedger.Balance.Should().Be(10, "利用後残額10円");
        mergedLedger.Note.Should().Contain("150", "不足額は最後のチャージ額150円");

        // 1回目チャージは独立Ledgerとして残る（Note なし・Income=100）
        var standaloneCharges = nonLentLedgers
            .Where(l => string.IsNullOrEmpty(l.Note) && l.Income == 100)
            .ToList();
        standaloneCharges.Should().HaveCount(1,
            "1回目チャージは残高不足パターンに含まれず単独のチャージLedgerになる");
    }
}
