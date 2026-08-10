using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Security;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1735: 摘要が空欄の台帳行を保存しないガードの回帰テスト。
/// 駅名が片側しか解決できない鉄道利用が摘要から黙って消え、
/// Summary="" のまま ledger 行が INSERT/UPDATE される問題を固定する。
/// </summary>
/// <remarks>
/// 摘要の具体文字列をアサートするため <see cref="SummaryGeneratorCollection"/> に属させる
/// （SummaryGenerator の静的設定を変更するテストとの並列実行干渉を防ぐ運用ルール。
/// SummaryGeneratorCollection.cs 参照）。
/// </remarks>
[Collection(SummaryGeneratorCollection.Name)]
public class LendingServiceSummaryGuardTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly Mock<ICardRepository> _cardRepositoryMock;
    private readonly Mock<IStaffRepository> _staffRepositoryMock;
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock;
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock;
    private readonly CardLockManager _lockManager;
    private readonly LendingService _service;

    private const string TestCardIdm = "0102030405060708";
    private const string TestStaffIdm = "1112131415161718";
    private const string TestStaffName = "テスト太郎";

    public LendingServiceSummaryGuardTests()
    {
        SummaryGenerator.ResetToDefaults();

        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();

        _cardRepositoryMock = new Mock<ICardRepository>();
        _staffRepositoryMock = new Mock<IStaffRepository>();
        _ledgerRepositoryMock = new Mock<ILedgerRepository>();
        _settingsRepositoryMock = new Mock<ISettingsRepository>();
        _settingsRepositoryMock.Setup(s => s.GetAppSettings()).Returns(new AppSettings());
        _settingsRepositoryMock.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());
        _ledgerRepositoryMock.Setup(x => x.DeleteAllLentRecordsAsync(It.IsAny<string>()))
            .ReturnsAsync(1);

        _lockManager = new CardLockManager(NullLogger<CardLockManager>.Instance);

        _service = new LendingService(
            _dbContext,
            _cardRepositoryMock.Object,
            _staffRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            new SummaryGenerator(),
            _lockManager,
            Options.Create(new AppOptions()),
            NullLogger<LendingService>.Instance);
    }

    public void Dispose()
    {
        SummaryGenerator.ResetToDefaults();
        _lockManager.Dispose();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Issue #1735 の故障シナリオ本体: StationCode.csv 未収録の新駅で降車し
    /// 降車駅名が解決できなかった鉄道利用のみで返却した場合、
    /// 摘要が空欄ではなくプレースホルダ付きの区間で INSERT されることを検証。
    /// </summary>
    [Fact]
    public async Task ReturnAsync_ExitStationUnresolved_InsertsPlaceholderSummaryLedger()
    {
        // Arrange
        var card = CreateTestCard(isLent: true);
        var staff = CreateTestStaff();
        var lentRecord = CreateTestLentRecord();
        var usageDetails = new List<LedgerDetail>
        {
            new()
            {
                UseDate = DateTime.Now,
                EntryStation = "博多",
                ExitStation = null,  // 降車駅名が解決できなかった（IsBus=false のまま）
                Amount = 210,
                Balance = 1790,
                IsBus = false,
                IsCharge = false,
                IsPointRedemption = false
            }
        };

        SetupReturnMocks(card, staff, lentRecord);
        var insertedLedgers = CaptureInsertedLedgers();

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert
        result.Success.Should().BeTrue();
        var usageLedger = insertedLedgers.FirstOrDefault(l => l.Expense > 0);
        usageLedger.Should().NotBeNull("利用 Ledger が作成されるはず");
        usageLedger!.Summary.Should().Be("鉄道（博多～?）",
            "片側だけ駅名が解決できた区間は摘要から落とさずプレースホルダで補完する (Issue #1735)");
    }

    /// <summary>
    /// 乗車駅・降車駅の両方が欠落した鉄道利用（摘要の自動生成が空文字になる形状）でも、
    /// 摘要が空欄ではなく代替文言で INSERT されることを検証（新規作成経路のガード）。
    /// </summary>
    [Fact]
    public async Task ReturnAsync_StationlessRailwayUsage_InsertsFallbackSummaryInsteadOfEmpty()
    {
        // Arrange
        var card = CreateTestCard(isLent: true);
        var staff = CreateTestStaff();
        var lentRecord = CreateTestLentRecord();
        var usageDetails = new List<LedgerDetail>
        {
            new()
            {
                UseDate = DateTime.Now,
                EntryStation = null,
                ExitStation = null,
                Amount = 210,
                Balance = 1790,
                IsBus = false,  // バス判別されなかった駅名なし明細
                IsCharge = false,
                IsPointRedemption = false
            }
        };

        SetupReturnMocks(card, staff, lentRecord);
        var insertedLedgers = CaptureInsertedLedgers();

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert
        result.Success.Should().BeTrue();
        var usageLedger = insertedLedgers.FirstOrDefault(l => l.Expense > 0);
        usageLedger.Should().NotBeNull("利用 Ledger が作成されるはず");
        usageLedger!.Summary.Should().NotBeNullOrEmpty("摘要が空欄の台帳行を保存しない (Issue #1735)");
        usageLedger.Summary.Should().Be(SummaryGenerator.GetUnknownUsageSummary(),
            "摘要を生成できない利用には代替文言を充てる");
    }

    /// <summary>
    /// 同一日の既存利用レコードへ統合する経路で、再生成した摘要が空文字でも
    /// 既存の摘要が空文字で上書きされないことを検証（統合経路のガード）。
    /// </summary>
    /// <remarks>
    /// 「摘要は残っているが明細に駅名がない Ledger」は実 DB で成立する状態
    /// （履歴編集で駅名を消しても LedgerDetailViewModel は再生成摘要が空の場合
    /// 既存摘要を維持するため）。そこへ駅名なしの返却明細が同日統合されると、
    /// 修正前は全明細から再生成した空摘要が既存摘要を上書きしていた。
    /// </remarks>
    [Fact]
    public async Task ReturnAsync_MergeRegeneratesEmptySummary_KeepsExistingSummary()
    {
        // Arrange
        var card = CreateTestCard(isLent: true);
        var staff = CreateTestStaff();
        var lentRecord = CreateTestLentRecord();
        var today = DateTime.Today;
        const int existingLedgerId = 42;
        const string existingSummary = "鉄道（博多～天神）";

        var existingLedger = new Ledger
        {
            Id = existingLedgerId,
            CardIdm = TestCardIdm,
            Date = today,
            Summary = existingSummary,
            Income = 0,
            Expense = 210,
            Balance = 1790,
            LenderIdm = TestStaffIdm,
            StaffName = TestStaffName
        };
        // 統合後の全明細再読込の結果: いずれの明細にも駅名がなく、摘要の再生成は空文字になる
        var fullLedger = new Ledger
        {
            Id = existingLedgerId,
            CardIdm = TestCardIdm,
            Date = today,
            Summary = existingSummary,
            Income = 0,
            Expense = 210,
            Balance = 1790,
            LenderIdm = TestStaffIdm,
            StaffName = TestStaffName,
            Details = new List<LedgerDetail>
            {
                new() { UseDate = today, EntryStation = null, ExitStation = null, Amount = 210, Balance = 1790 },
                new() { UseDate = today, EntryStation = null, ExitStation = null, Amount = 210, Balance = 1580 }
            }
        };

        SetupReturnMocks(card, staff, lentRecord);
        _ledgerRepositoryMock.Setup(x => x.GetByDateRangeAsync(TestCardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<Ledger> { existingLedger });
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(existingLedgerId))
            .ReturnsAsync(fullLedger);

        Ledger updatedLedger = null;
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => { if (!l.IsLentRecord) updatedLedger = l; })
            .ReturnsAsync(true);

        var usageDetails = new List<LedgerDetail>
        {
            new()
            {
                UseDate = today,
                EntryStation = null,
                ExitStation = null,
                Amount = 210,
                Balance = 1580,
                IsBus = false,
                IsCharge = false,
                IsPointRedemption = false
            }
        };

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert
        result.Success.Should().BeTrue();
        updatedLedger.Should().NotBeNull("既存レコードへの統合で UpdateAsync が呼ばれるはず");
        updatedLedger!.Summary.Should().Be(existingSummary,
            "再生成した摘要が空文字の場合は既存の摘要を維持する (Issue #1735)");
    }

    /// <summary>
    /// 残高不足マージ経路（チャージと利用を1行に統合）でも、利用明細に駅名がない場合に
    /// 摘要が空欄ではなく代替文言で INSERT されることを検証。
    /// </summary>
    [Fact]
    public async Task ReturnAsync_InsufficientBalanceStationlessUsage_InsertsFallbackSummaryInsteadOfEmpty()
    {
        // Arrange: 残高不足→不足分チャージ→利用 のパターン（駅名は両側とも未解決）
        // 検出条件は LendingServiceTests.ReturnAsync_InsufficientBalancePattern_* と同じ金額形状
        var card = CreateTestCard(isLent: true);
        var staff = CreateTestStaff();
        var lentRecord = CreateTestLentRecord();
        var now = DateTime.Now;
        var usageDetails = new List<LedgerDetail>
        {
            new()
            {
                UseDate = now.AddMinutes(-1),
                IsCharge = true,
                Amount = 140,    // 不足分のチャージ
                Balance = 210    // チャージ後の残高
            },
            new()
            {
                UseDate = now,
                EntryStation = null,
                ExitStation = null,
                Amount = 210,    // 運賃
                Balance = 0,     // 利用後の残高
                IsCharge = false
            }
        };

        SetupReturnMocks(card, staff, lentRecord);
        var insertedLedgers = CaptureInsertedLedgers();

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert
        result.Success.Should().BeTrue();
        var mergedLedger = insertedLedgers.FirstOrDefault(l => !string.IsNullOrEmpty(l.Note) && l.Note.Contains("現金で支払"));
        mergedLedger.Should().NotBeNull("残高不足統合 Ledger が作成されるはず");
        mergedLedger!.Summary.Should().NotBeNullOrEmpty("摘要が空欄の台帳行を保存しない (Issue #1735)");
        mergedLedger.Summary.Should().Be(SummaryGenerator.GetUnknownUsageSummary(),
            "摘要を生成できない利用には代替文言を充てる");
    }

    #region ヘルパーメソッド

    private static IcCard CreateTestCard(bool isLent)
    {
        return new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "H001",
            IsLent = isLent,
            IsDeleted = false
        };
    }

    private static Staff CreateTestStaff()
    {
        return new Staff
        {
            StaffIdm = TestStaffIdm,
            Name = TestStaffName,
            IsDeleted = false
        };
    }

    private static Ledger CreateTestLentRecord()
    {
        return new Ledger
        {
            Id = 1,
            CardIdm = TestCardIdm,
            LenderIdm = TestStaffIdm,
            StaffName = TestStaffName,
            Date = DateTime.Today,
            IsLentRecord = true,
            LentAt = DateTime.Now.AddHours(-1),
            Summary = SummaryGenerator.GetLendingSummary()
        };
    }

    /// <summary>
    /// LendingServiceTests.SetupReturnMocks と同じ返却フローの標準モック設定
    /// </summary>
    private void SetupReturnMocks(IcCard card, Staff staff, Ledger lentRecord)
    {
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(TestCardIdm, false))
            .ReturnsAsync(card);
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(TestStaffIdm, false))
            .ReturnsAsync(staff);
        _ledgerRepositoryMock.Setup(x => x.GetLentRecordAsync(TestCardIdm))
            .ReturnsAsync(lentRecord);
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(1);
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(x => x.DeleteAllLentRecordsAsync(TestCardIdm))
            .ReturnsAsync(1);
        _ledgerRepositoryMock.Setup(x => x.InsertDetailAsync(It.IsAny<LedgerDetail>()))
            .ReturnsAsync(true);
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
    /// InsertAsync に渡された Ledger を捕捉するリストを返す
    /// </summary>
    private List<Ledger> CaptureInsertedLedgers()
    {
        var insertedLedgers = new List<Ledger>();
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .ReturnsAsync((Ledger l) => { insertedLedgers.Add(l); return insertedLedgers.Count; });
        return insertedLedgers;
    }

    #endregion
}
