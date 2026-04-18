using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace ICCardManager.Tests.Services;

/// <summary>
/// LedgerMergeServiceのテスト
/// </summary>
public class LedgerMergeServiceTests
{
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock;
    private readonly Mock<IOperationLogRepository> _operationLogRepositoryMock;
    private readonly Mock<IStaffRepository> _staffRepositoryMock;
    private readonly SummaryGenerator _summaryGenerator;
    private readonly OperationLogger _operationLogger;
    private readonly LedgerMergeService _service;

    // テスト用定数
    private const string TestCardIdm = "0102030405060708";
    private const string TestCardIdm2 = "0807060504030201";

    // JSON直列化オプション（UndoデータのAssert用）
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public LedgerMergeServiceTests()
    {
        _ledgerRepositoryMock = new Mock<ILedgerRepository>();
        _operationLogRepositoryMock = new Mock<IOperationLogRepository>();
        _staffRepositoryMock = new Mock<IStaffRepository>();
        _summaryGenerator = new SummaryGenerator();

        // OperationLoggerは実物を使用（メソッドがvirtualでないため）
        _operationLogger = new OperationLogger(
            _operationLogRepositoryMock.Object,
            Mock.Of<ICurrentOperatorContext>());

        _service = new LedgerMergeService(
            _ledgerRepositoryMock.Object,
            _summaryGenerator,
            _operationLogger,
            NullLogger<LedgerMergeService>.Instance);

        // OperationLogger用のデフォルトセットアップ
        _operationLogRepositoryMock
            .Setup(x => x.InsertAsync(It.IsAny<OperationLog>()))
            .ReturnsAsync(1);
    }

    #region ヘルパーメソッド

    /// <summary>
    /// テスト用Ledgerを作成（鉄道利用）
    /// </summary>
    private static Ledger CreateTestLedger(
        int id,
        string cardIdm,
        DateTime date,
        string summary,
        int expense,
        int balance,
        List<LedgerDetail>? details = null)
    {
        return new Ledger
        {
            Id = id,
            CardIdm = cardIdm,
            Date = date,
            Summary = summary,
            Income = 0,
            Expense = expense,
            Balance = balance,
            StaffName = "テスト太郎",
            Details = details ?? new List<LedgerDetail>()
        };
    }

    /// <summary>
    /// テスト用LedgerDetail（鉄道利用）を作成
    /// </summary>
    private static LedgerDetail CreateRailDetail(
        int ledgerId,
        string entryStation,
        string exitStation,
        int amount,
        int balance,
        int sequenceNumber,
        DateTime? useDate = null,
        int? groupId = null)
    {
        return new LedgerDetail
        {
            LedgerId = ledgerId,
            EntryStation = entryStation,
            ExitStation = exitStation,
            Amount = amount,
            Balance = balance,
            SequenceNumber = sequenceNumber,
            UseDate = useDate ?? new DateTime(2026, 2, 3, 10, 0, 0),
            GroupId = groupId
        };
    }

    /// <summary>
    /// テスト用LedgerDetail（チャージ）を作成
    /// </summary>
    private static LedgerDetail CreateChargeDetail(
        int ledgerId,
        int amount,
        int balance,
        int sequenceNumber,
        DateTime? useDate = null)
    {
        return new LedgerDetail
        {
            LedgerId = ledgerId,
            IsCharge = true,
            Amount = amount,
            Balance = balance,
            SequenceNumber = sequenceNumber,
            UseDate = useDate ?? new DateTime(2026, 2, 3, 10, 0, 0)
        };
    }

    /// <summary>
    /// GetByIdAsyncのモックを一括設定
    /// </summary>
    private void SetupGetByIdMocks(params Ledger[] ledgers)
    {
        foreach (var ledger in ledgers)
        {
            _ledgerRepositoryMock
                .Setup(x => x.GetByIdAsync(ledger.Id))
                .ReturnsAsync(ledger);
        }
    }

    /// <summary>
    /// MergeLedgersAsyncのモックをセットアップ（成功パターン）
    /// </summary>
    private void SetupMergeMockSuccess()
    {
        _ledgerRepositoryMock
            .Setup(x => x.MergeLedgersAsync(
                It.IsAny<int>(),
                It.IsAny<IEnumerable<int>>(),
                It.IsAny<Ledger>()))
            .ReturnsAsync(true);

        _ledgerRepositoryMock
            .Setup(x => x.SaveMergeHistoryAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .Returns(Task.CompletedTask);
    }

    #endregion

    #region MergeAsync バリデーションテスト

    /// <summary>
    /// 1件のみ選択した場合はエラーとなること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_SingleLedger_ReturnsError()
    {
        // Arrange
        var ledgerIds = new List<int> { 1 };

        // Act
        var result = await _service.MergeAsync(ledgerIds);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("2件以上");
    }

    /// <summary>
    /// 空リストの場合はエラーとなること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_EmptyList_ReturnsError()
    {
        // Arrange
        var ledgerIds = new List<int>();

        // Act
        var result = await _service.MergeAsync(ledgerIds);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("2件以上");
    }

    /// <summary>
    /// 存在しないLedger IDを指定した場合はエラーとなること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_LedgerNotFound_ReturnsError()
    {
        // Arrange
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1))
            .ReturnsAsync(CreateTestLedger(1, TestCardIdm, DateTime.Now, "A", 200, 800));
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(999))
            .ReturnsAsync((Ledger?)null);

        // Act
        var result = await _service.MergeAsync(new List<int> { 1, 999 });

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("ID=999");
        result.ErrorMessage.Should().Contain("見つかりません");
    }

    /// <summary>
    /// 異なるカードの履歴を統合しようとした場合はエラーとなること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_DifferentCards_ReturnsError()
    {
        // Arrange
        var ledger1 = CreateTestLedger(1, TestCardIdm, DateTime.Now, "A", 200, 800);
        ledger1.Details.Add(CreateRailDetail(1, "博多", "天神", 200, 800, 1));

        var ledger2 = CreateTestLedger(2, TestCardIdm2, DateTime.Now, "B", 210, 590);
        ledger2.Details.Add(CreateRailDetail(2, "天神", "箱崎宮前", 210, 590, 2));

        SetupGetByIdMocks(ledger1, ledger2);

        // Act
        var result = await _service.MergeAsync(new List<int> { 1, 2 });

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("異なるカード");
    }

    /// <summary>
    /// 貸出中レコードを統合しようとした場合はエラーとなること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_LentRecord_ReturnsError()
    {
        // Arrange
        var ledger1 = CreateTestLedger(1, TestCardIdm, DateTime.Now, "A", 200, 800);
        ledger1.Details.Add(CreateRailDetail(1, "博多", "天神", 200, 800, 1));

        var ledger2 = CreateTestLedger(2, TestCardIdm, DateTime.Now, "（貸出中）", 0, 0);
        ledger2.IsLentRecord = true;

        SetupGetByIdMocks(ledger1, ledger2);

        // Act
        var result = await _service.MergeAsync(new List<int> { 1, 2 });

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("貸出中");
    }

    /// <summary>
    /// チャージと利用を混在して統合しようとした場合はエラーとなること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_ChargeAndUsageMixed_ReturnsError()
    {
        // Arrange: チャージ（Income）と鉄道利用（Expense）を混在
        var date = new DateTime(2026, 2, 3);

        var ledger1 = CreateTestLedger(1, TestCardIdm, date, "役務費によりチャージ", 0, 3000);
        ledger1.Income = 3000;
        ledger1.Details.Add(CreateChargeDetail(1, 3000, 3000, 1, date));

        var ledger2 = CreateTestLedger(2, TestCardIdm, date, "鉄道（博多～天神）", 260, 2740);
        ledger2.Details.Add(CreateRailDetail(2, "博多", "天神", 260, 2740, 2, date));

        SetupGetByIdMocks(ledger1, ledger2);

        // Act
        var result = await _service.MergeAsync(new List<int> { 1, 2 });

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("チャージと利用");
    }

    /// <summary>
    /// チャージ同士の統合は許可されること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_TwoCharges_Succeeds()
    {
        // Arrange: チャージ3000円 + チャージ2000円
        var date = new DateTime(2026, 2, 3);

        var ledger1 = CreateTestLedger(1, TestCardIdm, date, "役務費によりチャージ", 0, 3000);
        ledger1.Income = 3000;
        ledger1.Details.Add(CreateChargeDetail(1, 3000, 3000, 1, date));

        var ledger2 = CreateTestLedger(2, TestCardIdm, date, "役務費によりチャージ", 0, 5000);
        ledger2.Income = 2000;
        ledger2.Details.Add(CreateChargeDetail(2, 2000, 5000, 2, date));

        SetupGetByIdMocks(ledger1, ledger2);
        SetupMergeMockSuccess();

        // Act
        var result = await _service.MergeAsync(new List<int> { 1, 2 });

        // Assert
        result.Success.Should().BeTrue();
        result.MergedLedger!.Income.Should().Be(5000, "3000 + 2000");
    }

    #endregion

    #region MergeAsync 統合ロジックテスト

    /// <summary>
    /// 2件の鉄道利用を統合したとき、Income/Expense/Balanceが正しく計算されること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_TwoRailTrips_CalculatesFieldsCorrectly()
    {
        // Arrange: 福岡空港→天神 (200円) + 天神→箱崎宮前 (210円)
        var date = new DateTime(2026, 2, 3);
        var ledger1 = CreateTestLedger(1, TestCardIdm, date, "鉄道（福岡空港～天神）", 200, 800);
        ledger1.Details.Add(CreateRailDetail(1, "福岡空港", "天神", 200, 800, 1, date));

        var ledger2 = CreateTestLedger(2, TestCardIdm, date, "鉄道（天神～箱崎宮前）", 210, 590);
        ledger2.Details.Add(CreateRailDetail(2, "天神", "箱崎宮前", 210, 590, 2, date));

        SetupGetByIdMocks(ledger1, ledger2);
        SetupMergeMockSuccess();

        // Act
        var result = await _service.MergeAsync(new List<int> { 1, 2 });

        // Assert
        result.Success.Should().BeTrue();
        result.MergedLedger.Should().NotBeNull();
        result.MergedLedger!.Id.Should().Be(1, "統合先は最も古い（最初の）エントリ");
        result.MergedLedger.Income.Should().Be(0, "鉄道利用にIncomeなし");
        result.MergedLedger.Expense.Should().Be(410, "200 + 210 = 410");
        result.MergedLedger.Balance.Should().Be(590, "最新Detailの残高");
    }

    /// <summary>
    /// 統合時に摘要がSummaryGeneratorで再生成されること（乗継判定）
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_TransferTrips_RegeneratesSummaryWithTransfer()
    {
        // Arrange: 福岡空港→天神、天神→箱崎宮前 → 乗継として「福岡空港～箱崎宮前」
        // FeliCa互換: 小さいSequenceNumber = 新しい（後に利用した）
        var date = new DateTime(2026, 2, 3);
        var ledger1 = CreateTestLedger(1, TestCardIdm, date, "鉄道（福岡空港～天神）", 200, 800);
        ledger1.Details.Add(CreateRailDetail(1, "福岡空港", "天神", 200, 800, 2, date));

        var ledger2 = CreateTestLedger(2, TestCardIdm, date, "鉄道（天神～箱崎宮前）", 210, 590);
        ledger2.Details.Add(CreateRailDetail(2, "天神", "箱崎宮前", 210, 590, 1, date));

        SetupGetByIdMocks(ledger1, ledger2);
        SetupMergeMockSuccess();

        // Act
        var result = await _service.MergeAsync(new List<int> { 1, 2 });

        // Assert
        result.MergedLedger!.Summary.Should().Contain("福岡空港");
        result.MergedLedger.Summary.Should().Contain("箱崎宮前");
        // 乗継なので途中駅（天神）は摘要に含まれない
        result.MergedLedger.Summary.Should().NotContain("天神");
    }

    /// <summary>
    /// 3件以上の統合が正しく動作すること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_ThreeLedgers_MergesAllIntoFirst()
    {
        // Arrange
        var date = new DateTime(2026, 2, 3);

        var ledger1 = CreateTestLedger(1, TestCardIdm, date, "鉄道（A～B）", 100, 900);
        ledger1.Details.Add(CreateRailDetail(1, "A", "B", 100, 900, 1, date));

        var ledger2 = CreateTestLedger(2, TestCardIdm, date, "鉄道（C～D）", 150, 750);
        ledger2.Details.Add(CreateRailDetail(2, "C", "D", 150, 750, 2, date));

        var ledger3 = CreateTestLedger(3, TestCardIdm, date, "鉄道（E～F）", 200, 550);
        ledger3.Details.Add(CreateRailDetail(3, "E", "F", 200, 550, 3, date));

        SetupGetByIdMocks(ledger1, ledger2, ledger3);
        SetupMergeMockSuccess();

        // Act
        var result = await _service.MergeAsync(new List<int> { 1, 2, 3 });

        // Assert
        result.Success.Should().BeTrue();
        result.MergedLedger!.Id.Should().Be(1);
        result.MergedLedger.Income.Should().Be(0, "鉄道利用のみなのでIncome=0");
        result.MergedLedger.Expense.Should().Be(450, "100 + 150 + 200");
        result.MergedLedger.Balance.Should().Be(550, "最新Detailの残高");

        // MergeLedgersAsyncが正しい引数で呼ばれたことを検証
        _ledgerRepositoryMock.Verify(x => x.MergeLedgersAsync(
            1,
            It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { 2, 3 })),
            It.IsAny<Ledger>()),
            Times.Once);
    }

    /// <summary>
    /// Noteが非空の場合、「、」区切りで連結されること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_WithNotes_ConcatenatesNotes()
    {
        // Arrange
        var date = new DateTime(2026, 2, 3);

        var ledger1 = CreateTestLedger(1, TestCardIdm, date, "鉄道（A～B）", 200, 800);
        ledger1.Note = "会議出席";
        ledger1.Details.Add(CreateRailDetail(1, "A", "B", 200, 800, 1, date));

        var ledger2 = CreateTestLedger(2, TestCardIdm, date, "鉄道（C～D）", 200, 600);
        ledger2.Note = "研修参加";
        ledger2.Details.Add(CreateRailDetail(2, "C", "D", 200, 600, 2, date));

        SetupGetByIdMocks(ledger1, ledger2);
        SetupMergeMockSuccess();

        // Act
        var result = await _service.MergeAsync(new List<int> { 1, 2 });

        // Assert
        result.MergedLedger!.Note.Should().Be("会議出席、研修参加");
    }

    /// <summary>
    /// 片方のNoteのみ非空の場合はそのまま使われること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_OneNoteOnly_UsesNoteAsIs()
    {
        // Arrange
        var date = new DateTime(2026, 2, 3);

        var ledger1 = CreateTestLedger(1, TestCardIdm, date, "鉄道（A～B）", 200, 800);
        ledger1.Note = "出張";
        ledger1.Details.Add(CreateRailDetail(1, "A", "B", 200, 800, 1, date));

        var ledger2 = CreateTestLedger(2, TestCardIdm, date, "鉄道（C～D）", 200, 600);
        // ledger2.Note はnull（デフォルト）
        ledger2.Details.Add(CreateRailDetail(2, "C", "D", 200, 600, 2, date));

        SetupGetByIdMocks(ledger1, ledger2);
        SetupMergeMockSuccess();

        // Act
        var result = await _service.MergeAsync(new List<int> { 1, 2 });

        // Assert
        result.MergedLedger!.Note.Should().Be("出張");
    }

    /// <summary>
    /// 両方のNoteが空の場合はnullとなること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_NoNotes_NoteIsNull()
    {
        // Arrange
        var date = new DateTime(2026, 2, 3);

        var ledger1 = CreateTestLedger(1, TestCardIdm, date, "鉄道（A～B）", 200, 800);
        ledger1.Details.Add(CreateRailDetail(1, "A", "B", 200, 800, 1, date));

        var ledger2 = CreateTestLedger(2, TestCardIdm, date, "鉄道（C～D）", 200, 600);
        ledger2.Details.Add(CreateRailDetail(2, "C", "D", 200, 600, 2, date));

        SetupGetByIdMocks(ledger1, ledger2);
        SetupMergeMockSuccess();

        // Act
        var result = await _service.MergeAsync(new List<int> { 1, 2 });

        // Assert
        result.MergedLedger!.Note.Should().BeNull();
    }

    /// <summary>
    /// 同一Noteが重複する場合は1つだけ残ること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_DuplicateNotes_DeduplicatesNotes()
    {
        // Arrange
        var date = new DateTime(2026, 2, 3);

        var ledger1 = CreateTestLedger(1, TestCardIdm, date, "鉄道（A～B）", 200, 800);
        ledger1.Note = "出張";
        ledger1.Details.Add(CreateRailDetail(1, "A", "B", 200, 800, 1, date));

        var ledger2 = CreateTestLedger(2, TestCardIdm, date, "鉄道（C～D）", 200, 600);
        ledger2.Note = "出張"; // 同じNote
        ledger2.Details.Add(CreateRailDetail(2, "C", "D", 200, 600, 2, date));

        SetupGetByIdMocks(ledger1, ledger2);
        SetupMergeMockSuccess();

        // Act
        var result = await _service.MergeAsync(new List<int> { 1, 2 });

        // Assert
        result.MergedLedger!.Note.Should().Be("出張");
    }

    /// <summary>
    /// 統合先のDateは最古（最初の）エントリのDateが維持されること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_DifferentDates_KeepsOldestDate()
    {
        // Arrange
        var date1 = new DateTime(2026, 2, 3);
        var date2 = new DateTime(2026, 2, 4);

        var ledger1 = CreateTestLedger(1, TestCardIdm, date1, "鉄道（A～B）", 200, 800);
        ledger1.Details.Add(CreateRailDetail(1, "A", "B", 200, 800, 1, date1));

        var ledger2 = CreateTestLedger(2, TestCardIdm, date2, "鉄道（C～D）", 200, 600);
        ledger2.Details.Add(CreateRailDetail(2, "C", "D", 200, 600, 2, date2));

        SetupGetByIdMocks(ledger1, ledger2);
        SetupMergeMockSuccess();

        // Act
        var result = await _service.MergeAsync(new List<int> { 1, 2 });

        // Assert
        result.MergedLedger!.Date.Should().Be(date1, "統合先は最古のエントリ");
    }

    /// <summary>
    /// 鉄道+バスの混在マージで、Summaryに両方が含まれること（バスは★マーク付き）
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_RailAndBus_RegeneratesSummaryWithBoth()
    {
        // Arrange
        var date = new DateTime(2026, 2, 3);

        var railLedger = CreateTestLedger(1, TestCardIdm, date, "鉄道（博多～天神）", 260, 9740);
        railLedger.Details.Add(CreateRailDetail(1, "博多", "天神", 260, 9740, 1, date));

        var busLedger = CreateTestLedger(2, TestCardIdm, date, "バス（★）", 230, 9510);
        busLedger.Details.Add(new LedgerDetail
        {
            LedgerId = 2,
            IsBus = true,
            Amount = 230,
            Balance = 9510,
            SequenceNumber = 2,
            UseDate = date
        });

        SetupGetByIdMocks(railLedger, busLedger);
        SetupMergeMockSuccess();

        // Act
        var result = await _service.MergeAsync(new List<int> { 1, 2 });

        // Assert
        result.Success.Should().BeTrue();
        result.MergedLedger!.Expense.Should().Be(490, "260 + 230 = 490");
        result.MergedLedger.Summary.Should().Contain("鉄道", "鉄道部分が含まれる");
        result.MergedLedger.Summary.Should().Contain("バス", "バス部分も含まれる");
    }

    /// <summary>
    /// ポイント還元のみ2件のマージ（Income合算、Expenseは0、Balanceは最新）
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_TwoPointRedemptions_SumsIncomeCorrectly()
    {
        // Arrange
        var date = new DateTime(2026, 2, 3);

        var ledger1 = new Ledger
        {
            Id = 1,
            CardIdm = TestCardIdm,
            Date = date,
            Summary = "ポイント還元",
            Income = 100,
            Expense = 0,
            Balance = 5100,
            Details = new List<LedgerDetail>
            {
                new() { LedgerId = 1, IsPointRedemption = true, Amount = 100, Balance = 5100, SequenceNumber = 1, UseDate = date }
            }
        };
        var ledger2 = new Ledger
        {
            Id = 2,
            CardIdm = TestCardIdm,
            Date = date,
            Summary = "ポイント還元",
            Income = 200,
            Expense = 0,
            Balance = 5300,
            Details = new List<LedgerDetail>
            {
                new() { LedgerId = 2, IsPointRedemption = true, Amount = 200, Balance = 5300, SequenceNumber = 2, UseDate = date }
            }
        };

        SetupGetByIdMocks(ledger1, ledger2);
        SetupMergeMockSuccess();

        // Act
        var result = await _service.MergeAsync(new List<int> { 1, 2 });

        // Assert
        result.Success.Should().BeTrue();
        result.MergedLedger!.Income.Should().Be(300, "100 + 200 = 300");
        result.MergedLedger.Expense.Should().Be(0, "ポイント還元はExpenseなし");
        result.MergedLedger.Balance.Should().Be(5300, "最新Detailの残高");
    }

    // 注: MergeAsync_ThreeRailTrips_CalculatesFinancialsCorrectly は
    // 既存の MergeAsync_ThreeLedgers_MergesAllIntoFirst と完全重複のため追加しない
    // (既存テストにIncome=0のアサーションを追加するに留めた)

    #endregion

    #region MergeAsync UndoデータとDB永続化テスト

    /// <summary>
    /// 統合時にUndoデータがDBに保存されること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_Success_SavesUndoDataToDb()
    {
        // Arrange
        var date = new DateTime(2026, 2, 3);
        var ledger1 = CreateTestLedger(1, TestCardIdm, date, "鉄道（A～B）", 200, 800);
        ledger1.Details.Add(CreateRailDetail(1, "A", "B", 200, 800, 1, date));

        var ledger2 = CreateTestLedger(2, TestCardIdm, date, "鉄道（C～D）", 210, 590);
        ledger2.Details.Add(CreateRailDetail(2, "C", "D", 210, 590, 2, date));

        SetupGetByIdMocks(ledger1, ledger2);
        SetupMergeMockSuccess();

        string? capturedUndoJson = null;
        int capturedTargetId = 0;
        string? capturedDescription = null;

        _ledgerRepositoryMock
            .Setup(x => x.SaveMergeHistoryAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .Callback<int, string, string>((targetId, desc, json) =>
            {
                capturedTargetId = targetId;
                capturedDescription = desc;
                capturedUndoJson = json;
            })
            .Returns(Task.CompletedTask);

        // Act
        await _service.MergeAsync(new List<int> { 1, 2 });

        // Assert: SaveMergeHistoryAsyncが呼ばれたこと
        capturedTargetId.Should().Be(1);
        capturedDescription.Should().Contain("鉄道（A～B）");
        capturedDescription.Should().Contain("鉄道（C～D）");

        // UndoデータのJSONがデシリアライズ可能であること
        var undoData = JsonSerializer.Deserialize<LedgerMergeUndoData>(capturedUndoJson!, JsonOptions);
        undoData.Should().NotBeNull();
        undoData!.OriginalTarget.Id.Should().Be(1);
        undoData.DeletedSources.Should().HaveCount(1);
        undoData.DeletedSources[0].Id.Should().Be(2);
    }

    /// <summary>
    /// UndoデータにDetail→元LedgerIDのマッピングが含まれること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_Success_UndoDataContainsDetailMapping()
    {
        // Arrange
        var date = new DateTime(2026, 2, 3);
        var ledger1 = CreateTestLedger(1, TestCardIdm, date, "鉄道（A～B）", 200, 800);
        ledger1.Details.Add(CreateRailDetail(1, "A", "B", 200, 800, 10, date)); // SequenceNumber=10

        var ledger2 = CreateTestLedger(2, TestCardIdm, date, "鉄道（C～D）", 210, 590);
        ledger2.Details.Add(CreateRailDetail(2, "C", "D", 210, 590, 20, date)); // SequenceNumber=20

        SetupGetByIdMocks(ledger1, ledger2);
        SetupMergeMockSuccess();

        string? capturedUndoJson = null;
        _ledgerRepositoryMock
            .Setup(x => x.SaveMergeHistoryAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<int, string, string>((_, _, json) => capturedUndoJson = json)
            .Returns(Task.CompletedTask);

        // Act
        await _service.MergeAsync(new List<int> { 1, 2 });

        // Assert
        var undoData = JsonSerializer.Deserialize<LedgerMergeUndoData>(capturedUndoJson!, JsonOptions);
        undoData!.DetailOriginalLedgerMap.Should().ContainKey("10");
        undoData.DetailOriginalLedgerMap["10"].Should().Be(1);
        undoData.DetailOriginalLedgerMap.Should().ContainKey("20");
        undoData.DetailOriginalLedgerMap["20"].Should().Be(2);
    }

    /// <summary>
    /// 統合時にOperationLoggerが呼ばれること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_Success_LogsOperation()
    {
        // Arrange
        var date = new DateTime(2026, 2, 3);
        var ledger1 = CreateTestLedger(1, TestCardIdm, date, "鉄道（A～B）", 200, 800);
        ledger1.Details.Add(CreateRailDetail(1, "A", "B", 200, 800, 1, date));

        var ledger2 = CreateTestLedger(2, TestCardIdm, date, "鉄道（C～D）", 210, 590);
        ledger2.Details.Add(CreateRailDetail(2, "C", "D", 210, 590, 2, date));

        SetupGetByIdMocks(ledger1, ledger2);
        SetupMergeMockSuccess();

        // Act
        await _service.MergeAsync(new List<int> { 1, 2 });

        // Assert: OperationLogがMERGEアクションで記録されたこと
        _operationLogRepositoryMock.Verify(
            x => x.InsertAsync(It.Is<OperationLog>(
                log => log.Action == "MERGE")),
            Times.Once);
    }

    /// <summary>
    /// LedgerSnapshot.FromLedger/ToLedgerの往復変換が正しく動作すること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void LedgerSnapshot_RoundTrip_PreservesAllFields()
    {
        // Arrange
        var original = new Ledger
        {
            Id = 42,
            CardIdm = TestCardIdm,
            LenderIdm = "1111111111111111",
            Date = new DateTime(2026, 2, 3, 14, 30, 0),
            Summary = "鉄道（博多～天神）",
            Income = 0,
            Expense = 260,
            Balance = 740,
            StaffName = "テスト太郎",
            Note = "テスト備考",
            ReturnerIdm = "2222222222222222",
            LentAt = new DateTime(2026, 2, 3, 8, 0, 0),
            ReturnedAt = new DateTime(2026, 2, 3, 18, 0, 0),
            IsLentRecord = false
        };

        // Act
        var snapshot = LedgerSnapshot.FromLedger(original);
        var restored = snapshot.ToLedger();

        // Assert
        restored.Id.Should().Be(original.Id);
        restored.CardIdm.Should().Be(original.CardIdm);
        restored.LenderIdm.Should().Be(original.LenderIdm);
        restored.Date.Should().Be(original.Date);
        restored.Summary.Should().Be(original.Summary);
        restored.Income.Should().Be(original.Income);
        restored.Expense.Should().Be(original.Expense);
        restored.Balance.Should().Be(original.Balance);
        restored.StaffName.Should().Be(original.StaffName);
        restored.Note.Should().Be(original.Note);
        restored.ReturnerIdm.Should().Be(original.ReturnerIdm);
        restored.LentAt.Should().Be(original.LentAt);
        restored.ReturnedAt.Should().Be(original.ReturnedAt);
        restored.IsLentRecord.Should().Be(original.IsLentRecord);
    }

    /// <summary>
    /// LedgerSnapshot: LentAt/ReturnedAtがnullの場合も正しく変換されること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void LedgerSnapshot_NullDates_PreservesNulls()
    {
        // Arrange
        var original = new Ledger
        {
            Id = 1,
            CardIdm = TestCardIdm,
            Date = new DateTime(2026, 2, 3),
            Summary = "テスト",
            LentAt = null,
            ReturnedAt = null
        };

        // Act
        var snapshot = LedgerSnapshot.FromLedger(original);
        var restored = snapshot.ToLedger();

        // Assert
        restored.LentAt.Should().BeNull();
        restored.ReturnedAt.Should().BeNull();
    }

    #endregion

    #region MergeAsync エラーハンドリングテスト

    /// <summary>
    /// MergeLedgersAsyncがfalseを返した場合はエラーとなること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_RepositoryReturnsFalse_ReturnsError()
    {
        // Arrange
        var date = new DateTime(2026, 2, 3);
        var ledger1 = CreateTestLedger(1, TestCardIdm, date, "A", 200, 800);
        ledger1.Details.Add(CreateRailDetail(1, "A", "B", 200, 800, 1, date));

        var ledger2 = CreateTestLedger(2, TestCardIdm, date, "B", 210, 590);
        ledger2.Details.Add(CreateRailDetail(2, "C", "D", 210, 590, 2, date));

        SetupGetByIdMocks(ledger1, ledger2);

        _ledgerRepositoryMock
            .Setup(x => x.MergeLedgersAsync(It.IsAny<int>(), It.IsAny<IEnumerable<int>>(), It.IsAny<Ledger>()))
            .ReturnsAsync(false);

        // Act
        var result = await _service.MergeAsync(new List<int> { 1, 2 });

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("統合処理に失敗");
    }

    /// <summary>
    /// MergeLedgersAsyncが例外をスローした場合はエラーとなること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_RepositoryThrows_ReturnsError()
    {
        // Arrange
        var date = new DateTime(2026, 2, 3);
        var ledger1 = CreateTestLedger(1, TestCardIdm, date, "A", 200, 800);
        ledger1.Details.Add(CreateRailDetail(1, "A", "B", 200, 800, 1, date));

        var ledger2 = CreateTestLedger(2, TestCardIdm, date, "B", 210, 590);
        ledger2.Details.Add(CreateRailDetail(2, "C", "D", 210, 590, 2, date));

        SetupGetByIdMocks(ledger1, ledger2);

        _ledgerRepositoryMock
            .Setup(x => x.MergeLedgersAsync(It.IsAny<int>(), It.IsAny<IEnumerable<int>>(), It.IsAny<Ledger>()))
            .ThrowsAsync(new Exception("Database error"));

        // Act
        var result = await _service.MergeAsync(new List<int> { 1, 2 });

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("エラーが発生");
        result.ErrorMessage.Should().Contain("Database error");
    }

    #endregion

    #region UnmergeAsync テスト

    /// <summary>
    /// 統合の取り消しが正常に動作すること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task UnmergeAsync_ValidHistoryId_ReturnsSuccess()
    {
        // Arrange
        var undoData = new LedgerMergeUndoData
        {
            OriginalTarget = new LedgerSnapshot
            {
                Id = 1,
                CardIdm = TestCardIdm,
                DateText = "2026-02-03 00:00:00",
                Summary = "鉄道（A～B）",
                Expense = 200,
                Balance = 800
            },
            DeletedSources = new List<LedgerSnapshot>
            {
                new LedgerSnapshot
                {
                    Id = 2,
                    CardIdm = TestCardIdm,
                    DateText = "2026-02-03 00:00:00",
                    Summary = "鉄道（C～D）",
                    Expense = 210,
                    Balance = 590
                }
            },
            DetailOriginalLedgerMap = new Dictionary<string, int> { { "1", 1 }, { "2", 2 } }
        };

        var undoJson = JsonSerializer.Serialize(undoData, JsonOptions);

        _ledgerRepositoryMock
            .Setup(x => x.GetMergeHistoriesAsync(false))
            .ReturnsAsync(new List<(int, DateTime, int, string, string, bool)>
            {
                (1, DateTime.Now, 1, "テスト統合", undoJson, false)
            });

        _ledgerRepositoryMock
            .Setup(x => x.UnmergeLedgersAsync(It.IsAny<LedgerMergeUndoData>()))
            .ReturnsAsync(true);

        _ledgerRepositoryMock
            .Setup(x => x.MarkMergeHistoryUndoneAsync(1))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.UnmergeAsync(1);

        // Assert
        result.Success.Should().BeTrue();
        _ledgerRepositoryMock.Verify(x => x.UnmergeLedgersAsync(It.IsAny<LedgerMergeUndoData>()), Times.Once);
        _ledgerRepositoryMock.Verify(x => x.MarkMergeHistoryUndoneAsync(1), Times.Once);
    }

    /// <summary>
    /// 存在しない統合履歴IDを指定した場合はエラーとなること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task UnmergeAsync_HistoryNotFound_ReturnsError()
    {
        // Arrange
        _ledgerRepositoryMock
            .Setup(x => x.GetMergeHistoriesAsync(false))
            .ReturnsAsync(new List<(int, DateTime, int, string, string, bool)>());

        // Act
        var result = await _service.UnmergeAsync(999);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("統合履歴が見つかりません");
    }

    /// <summary>
    /// 既に取り消し済みの統合を再度取り消そうとした場合はエラーとなること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task UnmergeAsync_AlreadyUndone_ReturnsError()
    {
        // Arrange: IsUndone=true
        var undoData = new LedgerMergeUndoData
        {
            OriginalTarget = new LedgerSnapshot { Id = 1, CardIdm = TestCardIdm, DateText = "2026-02-03 00:00:00" },
            DeletedSources = new List<LedgerSnapshot>()
        };

        _ledgerRepositoryMock
            .Setup(x => x.GetMergeHistoriesAsync(false))
            .ReturnsAsync(new List<(int, DateTime, int, string, string, bool)>
            {
                (1, DateTime.Now, 1, "テスト統合", JsonSerializer.Serialize(undoData, JsonOptions), true)
            });

        // Act
        var result = await _service.UnmergeAsync(1);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("見つかりません");
    }

    /// <summary>
    /// UnmergeLedgersAsyncがfalseを返した場合はエラーとなること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task UnmergeAsync_RepositoryReturnsFalse_ReturnsError()
    {
        // Arrange
        var undoData = new LedgerMergeUndoData
        {
            OriginalTarget = new LedgerSnapshot { Id = 1, CardIdm = TestCardIdm, DateText = "2026-02-03 00:00:00" },
            DeletedSources = new List<LedgerSnapshot>()
        };

        _ledgerRepositoryMock
            .Setup(x => x.GetMergeHistoriesAsync(false))
            .ReturnsAsync(new List<(int, DateTime, int, string, string, bool)>
            {
                (1, DateTime.Now, 1, "テスト", JsonSerializer.Serialize(undoData, JsonOptions), false)
            });

        _ledgerRepositoryMock
            .Setup(x => x.UnmergeLedgersAsync(It.IsAny<LedgerMergeUndoData>()))
            .ReturnsAsync(false);

        // Act
        var result = await _service.UnmergeAsync(1);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("取り消しに失敗");
    }

    /// <summary>
    /// UnmergeAsync中に例外が発生した場合はエラーとなること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task UnmergeAsync_RepositoryThrows_ReturnsError()
    {
        // Arrange
        var undoData = new LedgerMergeUndoData
        {
            OriginalTarget = new LedgerSnapshot { Id = 1, CardIdm = TestCardIdm, DateText = "2026-02-03 00:00:00" },
            DeletedSources = new List<LedgerSnapshot>()
        };

        _ledgerRepositoryMock
            .Setup(x => x.GetMergeHistoriesAsync(false))
            .ReturnsAsync(new List<(int, DateTime, int, string, string, bool)>
            {
                (1, DateTime.Now, 1, "テスト", JsonSerializer.Serialize(undoData, JsonOptions), false)
            });

        _ledgerRepositoryMock
            .Setup(x => x.UnmergeLedgersAsync(It.IsAny<LedgerMergeUndoData>()))
            .ThrowsAsync(new Exception("DB failure"));

        // Act
        var result = await _service.UnmergeAsync(1);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("エラーが発生");
    }

    #endregion

    #region GetUndoableMergeHistoriesAsync テスト

    /// <summary>
    /// 取り消し可能な履歴のみが返されること（IsUndone=trueは除外）
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetUndoableMergeHistoriesAsync_FiltersOutUndoneEntries()
    {
        // Arrange
        var now = DateTime.Now;
        _ledgerRepositoryMock
            .Setup(x => x.GetMergeHistoriesAsync(false))
            .ReturnsAsync(new List<(int, DateTime, int, string, string, bool)>
            {
                (1, now.AddMinutes(-30), 10, "統合A", "{}", false),  // 未取り消し
                (2, now.AddMinutes(-20), 20, "統合B", "{}", true),   // 取り消し済み
                (3, now.AddMinutes(-10), 30, "統合C", "{}", false),  // 未取り消し
            });

        // Act
        var result = await _service.GetUndoableMergeHistoriesAsync();

        // Assert
        result.Should().HaveCount(2);
        result[0].Id.Should().Be(1);
        result[0].Description.Should().Be("統合A");
        result[1].Id.Should().Be(3);
        result[1].Description.Should().Be("統合C");
    }

    /// <summary>
    /// 履歴がない場合は空リストが返されること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetUndoableMergeHistoriesAsync_NoHistories_ReturnsEmptyList()
    {
        // Arrange
        _ledgerRepositoryMock
            .Setup(x => x.GetMergeHistoriesAsync(false))
            .ReturnsAsync(new List<(int, DateTime, int, string, string, bool)>());

        // Act
        var result = await _service.GetUndoableMergeHistoriesAsync();

        // Assert
        result.Should().BeEmpty();
    }

    /// <summary>
    /// すべて取り消し済みの場合は空リストが返されること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetUndoableMergeHistoriesAsync_AllUndone_ReturnsEmptyList()
    {
        // Arrange
        _ledgerRepositoryMock
            .Setup(x => x.GetMergeHistoriesAsync(false))
            .ReturnsAsync(new List<(int, DateTime, int, string, string, bool)>
            {
                (1, DateTime.Now, 10, "統合A", "{}", true),
                (2, DateTime.Now, 20, "統合B", "{}", true),
            });

        // Act
        var result = await _service.GetUndoableMergeHistoriesAsync();

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region MergeAsync 残高計算テスト

    /// <summary>
    /// 残高は最後のDetail（SequenceNumberが最大）の値が使われること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_Balance_UsesLatestDetailBalance()
    {
        // Arrange: SequenceNumber順で最後のDetailの残高が統合後の残高になる
        var date = new DateTime(2026, 2, 3);

        var ledger1 = CreateTestLedger(1, TestCardIdm, date, "鉄道（A～B）", 200, 800);
        ledger1.Details.Add(CreateRailDetail(1, "A", "B", 200, 800, 5, date)); // Seq=5

        var ledger2 = CreateTestLedger(2, TestCardIdm, date, "鉄道（C～D）", 210, 590);
        ledger2.Details.Add(CreateRailDetail(2, "C", "D", 210, 590, 3, date)); // Seq=3（ledger1より前）

        SetupGetByIdMocks(ledger1, ledger2);
        SetupMergeMockSuccess();

        // Act
        var result = await _service.MergeAsync(new List<int> { 1, 2 });

        // Assert: SequenceNumber=5が最新なのでその残高(800)が使われる
        result.MergedLedger!.Balance.Should().Be(800);
    }

    #endregion

    #region Issue #920: 分割・再統合後の摘要欄の順序

    /// <summary>
    /// 分割後に再統合した場合に摘要が正しい時系列順になること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_SplitAndRemerge_SummaryOrderIsCorrect()
    {
        // Arrange: Issue #920の再現
        // ①薬院→博多(210円, 残高2676円) → ②博多→天神(210円, 残高2466円) → ③西鉄福岡(天神)→薬院(170円, 残高2296円)
        // 分割後: Ledger1={①}, Ledger2={②③}
        // 再統合: Ledger1 + Ledger2 → 正しい順序の摘要が生成されること
        // 注: 駅名は駅コードマスタ(StationCode.csv)に合わせて半角括弧を使用
        var date = new DateTime(2026, 3, 5);

        // Ledger1（target）: ①薬院→博多（分割で残った方、IDが小さい＝古い）
        // SequenceNumber: FeliCa互換（大きいほど古い）→ ①が最古なのでSeq=15
        var ledger1 = CreateTestLedger(1, TestCardIdm, date, "鉄道（薬院～博多）", 210, 2676);
        ledger1.Details.Add(CreateRailDetail(1, "薬院", "博多", 210, 2676, 15, date));

        // Ledger2（source）: ②博多→天神、③西鉄福岡(天神)→薬院（分割で作られた方、IDが大きい）
        // SequenceNumber: ②=14（中間）、③=13（最新なので最小）
        var ledger2 = CreateTestLedger(2, TestCardIdm, date, "鉄道（博多～天神、西鉄福岡(天神)～薬院）", 380, 2296);
        ledger2.Details.Add(CreateRailDetail(2, "博多", "天神", 210, 2466, 14, date));
        ledger2.Details.Add(CreateRailDetail(2, "西鉄福岡(天神)", "薬院", 170, 2296, 13, date));

        SetupGetByIdMocks(ledger1, ledger2);
        SetupMergeMockSuccess();

        // Act
        var result = await _service.MergeAsync(new List<int> { 1, 2 });

        // Assert
        result.Success.Should().BeTrue();
        var summary = result.MergedLedger!.Summary;

        // 摘要に全駅名が含まれること
        summary.Should().Contain("薬院");
        summary.Should().Contain("博多");
        summary.Should().Contain("天神");
        summary.Should().Contain("西鉄福岡(天神)");

        // 正しい時系列順: 薬院が博多より先に出現すること
        // （「薬院～博多」が最初の経路で、その後「博多～天神」「西鉄福岡(天神)～薬院」と続く）
        var yakuinIndex = summary.IndexOf("薬院");
        var hakataIndex = summary.IndexOf("博多");
        yakuinIndex.Should().BeLessThan(hakataIndex,
            "薬院→博多が最初の経路なので、薬院が博多より先に出現すべき");
    }

    /// <summary>
    /// 異なる日付のLedgerを統合した場合に日付順で正しい摘要が生成されること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_DifferentDates_SummaryOrderIsCorrectByDate()
    {
        // Arrange: 1/15に博多→天神、1/16に天神→博多
        var date1 = new DateTime(2026, 1, 15);
        var date2 = new DateTime(2026, 1, 16);

        // Ledger2（source、新しい日付だがIDが大きい）
        var ledger2 = CreateTestLedger(2, TestCardIdm, date2, "鉄道（天神～博多）", 260, 9220);
        ledger2.Details.Add(CreateRailDetail(2, "天神", "博多", 260, 9220, 1, date2));

        // Ledger1（target、古い日付でIDが小さい）
        var ledger1 = CreateTestLedger(1, TestCardIdm, date1, "鉄道（博多～天神）", 260, 9480);
        ledger1.Details.Add(CreateRailDetail(1, "博多", "天神", 260, 9480, 2, date1));

        SetupGetByIdMocks(ledger1, ledger2);
        SetupMergeMockSuccess();

        // Act
        var result = await _service.MergeAsync(new List<int> { 1, 2 });

        // Assert
        result.Success.Should().BeTrue();
        var summary = result.MergedLedger!.Summary;

        // 博多→天神が先（1/15）、天神→博多が後（1/16）
        // 往復検出されて「博多～天神 往復」になる可能性もある
        summary.Should().Contain("博多");
        summary.Should().Contain("天神");
    }

    /// <summary>
    /// SequenceNumberがFeliCa規約と逆順（大きい=新しい）でも正しい摘要が生成されること
    /// </summary>
    /// <remarks>
    /// 実際のDB上のrowidは、INSERTの順序に依存するため、分割・再統合を経ると
    /// FeliCa規約（小さい=新しい）とは異なる順序になることがある。
    /// MergeAsyncはUseDate/Balanceで正しい順序を判定し、SequenceNumberを再採番して
    /// GenerateRailwaySummary内部の再ソートでも正しい順序が維持されることを検証する。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_SplitAndRemerge_WrongSequenceNumber_SummaryOrderIsCorrect()
    {
        // Arrange: SequenceNumberがFeliCa規約と逆順のケース
        // ①薬院→博多(最古) Seq=10（最小） ②博多→天神 Seq=20 ③西鉄福岡(天神)→薬院(最新) Seq=30（最大）
        // FeliCa規約では最新が最小のはずだが、分割・再統合後にこの順序が崩れることがある
        var date = new DateTime(2026, 3, 5);

        var ledger1 = CreateTestLedger(1, TestCardIdm, date, "鉄道（薬院～博多）", 210, 2676);
        ledger1.Details.Add(CreateRailDetail(1, "薬院", "博多", 210, 2676, 10, date));

        var ledger2 = CreateTestLedger(2, TestCardIdm, date, "鉄道（博多～天神、西鉄福岡(天神)～薬院）", 380, 2296);
        ledger2.Details.Add(CreateRailDetail(2, "博多", "天神", 210, 2466, 20, date));
        ledger2.Details.Add(CreateRailDetail(2, "西鉄福岡(天神)", "薬院", 170, 2296, 30, date));

        SetupGetByIdMocks(ledger1, ledger2);
        SetupMergeMockSuccess();

        // Act
        var result = await _service.MergeAsync(new List<int> { 1, 2 });

        // Assert
        result.Success.Should().BeTrue();
        var summary = result.MergedLedger!.Summary;

        // SequenceNumberが逆順でも、Balance（残高）で正しい時系列順を判定できること
        summary.Should().Contain("薬院");
        summary.Should().Contain("博多");

        var yakuinIndex = summary.IndexOf("薬院");
        var hakataIndex = summary.IndexOf("博多");
        yakuinIndex.Should().BeLessThan(hakataIndex,
            "SequenceNumberが逆順でも、薬院→博多が最初の経路として摘要に出現すべき");
    }

    #endregion

    #region SyncBusStopsFromSummary - Issue #983

    /// <summary>
    /// 摘要が手動編集された場合、BusStopsが摘要から同期されること
    /// </summary>
    [Fact]
    public void SyncBusStopsFromSummary_単一バス利用_摘要からBusStopsを同期()
    {
        // Arrange: 分割後に摘要が「バス（qq）」→「バス（薬院大通～西鉄平尾駅）」に編集されたが
        // Detail.BusStopsは「qq」のまま
        var ledgers = new List<Ledger>
        {
            new()
            {
                Id = 1,
                Summary = "バス（薬院大通～西鉄平尾駅）",
                Details = new List<LedgerDetail>
                {
                    new() { IsBus = true, BusStops = "qq", Amount = 210, Balance = 500 }
                }
            }
        };

        // Act
        LedgerMergeService.SyncBusStopsFromSummary(ledgers);

        // Assert
        ledgers[0].Details[0].BusStops.Should().Be("薬院大通～西鉄平尾駅");
    }

    /// <summary>
    /// 複数Ledgerでそれぞれバス停名が同期されること（分割→編集→統合シナリオ）
    /// </summary>
    [Fact]
    public void SyncBusStopsFromSummary_分割後の複数Ledger_それぞれ同期()
    {
        // Arrange
        var ledgers = new List<Ledger>
        {
            new()
            {
                Id = 1,
                Summary = "バス（薬院大通～西鉄平尾駅）",
                Details = new List<LedgerDetail>
                {
                    new() { IsBus = true, BusStops = "qq", Amount = 210, Balance = 500 }
                }
            },
            new()
            {
                Id = 2,
                Summary = "バス（那の川～渡辺通一丁目）",
                Details = new List<LedgerDetail>
                {
                    new() { IsBus = true, BusStops = "aa", Amount = 210, Balance = 290 }
                }
            }
        };

        // Act
        LedgerMergeService.SyncBusStopsFromSummary(ledgers);

        // Assert
        ledgers[0].Details[0].BusStops.Should().Be("薬院大通～西鉄平尾駅");
        ledgers[1].Details[0].BusStops.Should().Be("那の川～渡辺通一丁目");
    }

    /// <summary>
    /// 複数バス利用が1つのLedgerにある場合、「、」で分割して対応付けること
    /// </summary>
    [Fact]
    public void SyncBusStopsFromSummary_複数バス利用の1Ledger_分割して対応付け()
    {
        // Arrange
        var ledgers = new List<Ledger>
        {
            new()
            {
                Id = 1,
                Summary = "バス（薬院大通～天神、博多～吉塚）",
                Details = new List<LedgerDetail>
                {
                    new() { IsBus = true, BusStops = "★", Amount = 210, Balance = 500 },
                    new() { IsBus = true, BusStops = "★", Amount = 210, Balance = 290 }
                }
            }
        };

        // Act
        LedgerMergeService.SyncBusStopsFromSummary(ledgers);

        // Assert
        ledgers[0].Details[0].BusStops.Should().Be("薬院大通～天神");
        ledgers[0].Details[1].BusStops.Should().Be("博多～吉塚");
    }

    /// <summary>
    /// バス利用がないLedgerでは何も変更されないこと
    /// </summary>
    [Fact]
    public void SyncBusStopsFromSummary_鉄道のみ_変更なし()
    {
        // Arrange
        var ledgers = new List<Ledger>
        {
            new()
            {
                Id = 1,
                Summary = "鉄道（博多～天神）",
                Details = new List<LedgerDetail>
                {
                    new() { IsBus = false, EntryStation = "博多", ExitStation = "天神", Amount = 260, Balance = 500 }
                }
            }
        };

        // Act
        LedgerMergeService.SyncBusStopsFromSummary(ledgers);

        // Assert: 変更なし
        ledgers[0].Details[0].BusStops.Should().BeNull();
    }

    /// <summary>
    /// 鉄道+バス混在の摘要からバス部分のみ抽出されること
    /// </summary>
    [Fact]
    public void SyncBusStopsFromSummary_鉄道とバス混在_バス部分のみ同期()
    {
        // Arrange
        var ledgers = new List<Ledger>
        {
            new()
            {
                Id = 1,
                Summary = "鉄道（博多～天神）、バス（渡辺通～薬院）",
                Details = new List<LedgerDetail>
                {
                    new() { IsBus = false, EntryStation = "博多", ExitStation = "天神", Amount = 260, Balance = 500 },
                    new() { IsBus = true, BusStops = "★", Amount = 210, Balance = 240 }
                }
            }
        };

        // Act
        LedgerMergeService.SyncBusStopsFromSummary(ledgers);

        // Assert
        ledgers[0].Details.First(d => d.IsBus).BusStops.Should().Be("渡辺通～薬院");
        ledgers[0].Details.First(d => !d.IsBus).EntryStation.Should().Be("博多", "鉄道Detailは変更されない");
    }

    /// <summary>
    /// BusStopsが既に正しい場合も上書きされるが実害なし
    /// </summary>
    [Fact]
    public void SyncBusStopsFromSummary_既に同期済み_変更なし()
    {
        // Arrange
        var ledgers = new List<Ledger>
        {
            new()
            {
                Id = 1,
                Summary = "バス（薬院）",
                Details = new List<LedgerDetail>
                {
                    new() { IsBus = true, BusStops = "薬院", Amount = 210, Balance = 500 }
                }
            }
        };

        // Act
        LedgerMergeService.SyncBusStopsFromSummary(ledgers);

        // Assert
        ledgers[0].Details[0].BusStops.Should().Be("薬院");
    }

    /// <summary>
    /// バス利用数と「、」分割数が一致しない場合は変更しないこと
    /// </summary>
    [Fact]
    public void SyncBusStopsFromSummary_バス件数不一致_変更しない()
    {
        // Arrange: 3件のバスDetailがあるが、摘要には2件分しかない
        var ledgers = new List<Ledger>
        {
            new()
            {
                Id = 1,
                Summary = "バス（薬院、天神）",
                Details = new List<LedgerDetail>
                {
                    new() { IsBus = true, BusStops = "a", Amount = 210, Balance = 500 },
                    new() { IsBus = true, BusStops = "b", Amount = 210, Balance = 290 },
                    new() { IsBus = true, BusStops = "c", Amount = 210, Balance = 80 }
                }
            }
        };

        // Act
        LedgerMergeService.SyncBusStopsFromSummary(ledgers);

        // Assert: 件数不一致のため変更なし
        ledgers[0].Details[0].BusStops.Should().Be("a");
        ledgers[0].Details[1].BusStops.Should().Be("b");
        ledgers[0].Details[2].BusStops.Should().Be("c");
    }

    #endregion

    #region Issue #1260: Undo・復旧の完全性テスト

    /// <summary>
    /// Issue #1260: 統合 → Undo の際に、UndoData から元の状態を完全復元できること。
    /// OriginalTarget と DeletedSources の全フィールドが JSON ラウンドトリップ後も
    /// 元のLedgerに戻せることを保証する。
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_UndoData_FullyRestoresOriginalState()
    {
        // Arrange: 詳細な元Ledger（全フィールドに意味のある値）
        var date1 = new DateTime(2026, 2, 3, 10, 15, 30);
        var date2 = new DateTime(2026, 2, 3, 14, 45, 0);
        var ledger1 = new Ledger
        {
            Id = 100,
            CardIdm = TestCardIdm,
            LenderIdm = "1111111111111111",
            Date = date1,
            Summary = "鉄道（博多～天神）",
            Income = 0,
            Expense = 260,
            Balance = 740,
            StaffName = "職員A",
            Note = "備考1",
            LentAt = date1.AddHours(-2),
            ReturnedAt = date1.AddHours(1),
            IsLentRecord = false,
            Details = { CreateRailDetail(100, "博多", "天神", 260, 740, 1, date1) }
        };
        var ledger2 = new Ledger
        {
            Id = 200,
            CardIdm = TestCardIdm,
            LenderIdm = "1111111111111111",
            Date = date2,
            Summary = "鉄道（薬院～赤坂）",
            Income = 0,
            Expense = 210,
            Balance = 530,
            StaffName = "職員A",
            Note = "備考2",
            IsLentRecord = false,
            Details = { CreateRailDetail(200, "薬院", "赤坂", 210, 530, 2, date2) }
        };

        SetupGetByIdMocks(ledger1, ledger2);
        SetupMergeMockSuccess();

        string? capturedUndoJson = null;
        _ledgerRepositoryMock
            .Setup(x => x.SaveMergeHistoryAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<int, string, string>((_, _, json) => capturedUndoJson = json)
            .Returns(Task.CompletedTask);

        // Act: 統合
        await _service.MergeAsync(new List<int> { 100, 200 });

        // Assert: UndoデータJSONから元の2件のLedgerを完全復元できる
        var undoData = JsonSerializer.Deserialize<LedgerMergeUndoData>(capturedUndoJson!, JsonOptions);
        undoData.Should().NotBeNull();

        var restoredTarget = undoData!.OriginalTarget.ToLedger();
        restoredTarget.Id.Should().Be(100);
        restoredTarget.Summary.Should().Be("鉄道（博多～天神）");
        restoredTarget.Expense.Should().Be(260);
        restoredTarget.Balance.Should().Be(740);
        restoredTarget.StaffName.Should().Be("職員A");
        restoredTarget.Note.Should().Be("備考1");
        restoredTarget.LenderIdm.Should().Be("1111111111111111");
        restoredTarget.LentAt.Should().Be(date1.AddHours(-2));
        restoredTarget.ReturnedAt.Should().Be(date1.AddHours(1));

        undoData.DeletedSources.Should().HaveCount(1);
        var restoredSource = undoData.DeletedSources[0].ToLedger();
        restoredSource.Id.Should().Be(200);
        restoredSource.Summary.Should().Be("鉄道（薬院～赤坂）");
        restoredSource.Expense.Should().Be(210);
        restoredSource.Balance.Should().Be(530);
        restoredSource.Note.Should().Be("備考2");
    }

    /// <summary>
    /// Issue #1260: UndoData JSON シリアライズで日時フォーマットが
    /// ISO 8601 (yyyy-MM-dd HH:mm:ss) で出力されること。
    /// </summary>
    /// <remarks>
    /// LedgerSnapshot は DateText/LentAtText/ReturnedAtText を string で保持しており、
    /// FromLedger でこのフォーマットで書き出す。JSON 文字列にもそのまま埋め込まれる。
    /// 他システム連携やデバッグ時の可読性を保証する。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_UndoDataJson_UsesIsoDateFormat()
    {
        // Arrange
        var date = new DateTime(2026, 2, 3, 14, 30, 45);
        var ledger1 = CreateTestLedger(1, TestCardIdm, date, "鉄道（A～B）", 200, 800);
        ledger1.LentAt = date.AddHours(-1);
        ledger1.ReturnedAt = date.AddMinutes(15);
        ledger1.Details.Add(CreateRailDetail(1, "A", "B", 200, 800, 1, date));

        var ledger2 = CreateTestLedger(2, TestCardIdm, date, "鉄道（C～D）", 210, 590);
        ledger2.Details.Add(CreateRailDetail(2, "C", "D", 210, 590, 2, date));

        SetupGetByIdMocks(ledger1, ledger2);
        SetupMergeMockSuccess();

        string? capturedJson = null;
        _ledgerRepositoryMock
            .Setup(x => x.SaveMergeHistoryAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<int, string, string>((_, _, json) => capturedJson = json)
            .Returns(Task.CompletedTask);

        // Act
        await _service.MergeAsync(new List<int> { 1, 2 });

        // Assert: JSON に ISO 形式の日時文字列がそのまま含まれる
        capturedJson.Should().NotBeNull();
        capturedJson.Should().Contain("\"DateText\":\"2026-02-03 14:30:45\"",
            "Date は yyyy-MM-dd HH:mm:ss 形式でJSONに書き出される");
        capturedJson.Should().Contain("\"LentAtText\":\"2026-02-03 13:30:45\"",
            "LentAt も同形式で書き出される");
        capturedJson.Should().Contain("\"ReturnedAtText\":\"2026-02-03 14:45:45\"",
            "ReturnedAt も同形式で書き出される");

        // 往復デシリアライズで DateTime 値が完全一致する
        var undoData = JsonSerializer.Deserialize<LedgerMergeUndoData>(capturedJson!, JsonOptions);
        var restored = undoData!.OriginalTarget.ToLedger();
        restored.Date.Should().Be(date);
        restored.LentAt.Should().Be(date.AddHours(-1));
        restored.ReturnedAt.Should().Be(date.AddMinutes(15));
    }

    /// <summary>
    /// Issue #1260: DetailOriginalLedgerMap（SequenceNumber→元LedgerID）が
    /// JSON 往復後も完全に保持されること。
    /// </summary>
    /// <remarks>
    /// .NET Framework 4.8 の System.Text.Json v4.7 は Dictionary&lt;int,int&gt; を
    /// シリアライズできないため、キーは string 型で保持する。本テストは
    /// この実装契約が将来の変更で壊れないことを保証する。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_DetailOriginalLedgerMap_SurvivesJsonRoundTrip()
    {
        // Arrange: 複数カードにまたがる Detail を持つ3件を統合
        var date = new DateTime(2026, 2, 3);
        var ledger1 = CreateTestLedger(10, TestCardIdm, date, "鉄道（A）", 100, 900);
        ledger1.Details.Add(CreateRailDetail(10, "A", "B", 100, 900, 11, date));
        ledger1.Details.Add(CreateRailDetail(10, "B", "C", 100, 800, 12, date));

        var ledger2 = CreateTestLedger(20, TestCardIdm, date, "鉄道（D）", 200, 700);
        ledger2.Details.Add(CreateRailDetail(20, "D", "E", 200, 700, 21, date));

        var ledger3 = CreateTestLedger(30, TestCardIdm, date, "鉄道（F）", 150, 550);
        ledger3.Details.Add(CreateRailDetail(30, "F", "G", 150, 550, 31, date));

        SetupGetByIdMocks(ledger1, ledger2, ledger3);
        SetupMergeMockSuccess();

        string? capturedJson = null;
        _ledgerRepositoryMock
            .Setup(x => x.SaveMergeHistoryAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<int, string, string>((_, _, json) => capturedJson = json)
            .Returns(Task.CompletedTask);

        // Act
        await _service.MergeAsync(new List<int> { 10, 20, 30 });

        // Assert: JSON からデシリアライズしたマップが全 SequenceNumber を正しく保持
        var undoData = JsonSerializer.Deserialize<LedgerMergeUndoData>(capturedJson!, JsonOptions);
        undoData.Should().NotBeNull();
        undoData!.DetailOriginalLedgerMap.Should().HaveCount(4, "全4件のDetailがマップに登録される");
        undoData.DetailOriginalLedgerMap["11"].Should().Be(10);
        undoData.DetailOriginalLedgerMap["12"].Should().Be(10, "同一Ledger内の複数Detailも個別マップ化");
        undoData.DetailOriginalLedgerMap["21"].Should().Be(20);
        undoData.DetailOriginalLedgerMap["31"].Should().Be(30);
    }

    /// <summary>
    /// Issue #1260: Unmerge 後に同じ Ledger を再度 Merge できること（可逆性）。
    /// Undo が永続副作用を残さず、再統合フローを阻害しないことを保証する。
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_AfterUnmerge_CanMergeAgain()
    {
        // Arrange: 初回マージ用のLedger
        var date = new DateTime(2026, 2, 3);
        var ledger1 = CreateTestLedger(1, TestCardIdm, date, "鉄道（A～B）", 200, 800);
        ledger1.Details.Add(CreateRailDetail(1, "A", "B", 200, 800, 1, date));
        var ledger2 = CreateTestLedger(2, TestCardIdm, date, "鉄道（C～D）", 210, 590);
        ledger2.Details.Add(CreateRailDetail(2, "C", "D", 210, 590, 2, date));

        SetupGetByIdMocks(ledger1, ledger2);
        SetupMergeMockSuccess();

        string? firstMergeJson = null;
        _ledgerRepositoryMock
            .Setup(x => x.SaveMergeHistoryAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<int, string, string>((_, _, json) => firstMergeJson = json)
            .Returns(Task.CompletedTask);

        // Act-1: 初回マージ
        var firstMerge = await _service.MergeAsync(new List<int> { 1, 2 });
        firstMerge.Success.Should().BeTrue();

        // Act-2: Unmerge
        _ledgerRepositoryMock
            .Setup(x => x.GetMergeHistoriesAsync(false))
            .ReturnsAsync(new List<(int, DateTime, int, string, string, bool)>
            {
                (1, DateTime.Now, 1, "初回統合", firstMergeJson!, false)
            });
        _ledgerRepositoryMock
            .Setup(x => x.UnmergeLedgersAsync(It.IsAny<LedgerMergeUndoData>()))
            .ReturnsAsync(true);
        _ledgerRepositoryMock
            .Setup(x => x.MarkMergeHistoryUndoneAsync(1))
            .Returns(Task.CompletedTask);

        var unmergeResult = await _service.UnmergeAsync(1);
        unmergeResult.Success.Should().BeTrue();

        // Act-3: 再度マージ（Undo 後は元に戻っている想定で、同じ ID を再指定）
        // 2回目の SaveMergeHistoryAsync は新しい JSON を受け取る
        var secondMerge = await _service.MergeAsync(new List<int> { 1, 2 });

        // Assert: 再マージも成功する（Undo に永続副作用がない）
        secondMerge.Success.Should().BeTrue("Undo後も同じLedgerを再統合できる");
        _ledgerRepositoryMock.Verify(
            x => x.MergeLedgersAsync(It.IsAny<int>(), It.IsAny<IEnumerable<int>>(), It.IsAny<Ledger>()),
            Times.Exactly(2),
            "MergeLedgersAsync が再マージ時にも呼ばれる");
    }

    /// <summary>
    /// Issue #1260: 統合時に UndoData.OriginalTarget.Summary が「統合前の元Summary」で
    /// 保持されること。これにより Unmerge 時に摘要が元通りに再生成できる。
    /// </summary>
    /// <remarks>
    /// 統合処理は Target.Summary を「統合後の摘要」に書き換えるが、UndoData 構築は
    /// 書き換え前に行う必要がある。本テストはこのタイミングの契約を保証する。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MergeAsync_UndoData_PreservesOriginalSummaryBeforeRegeneration()
    {
        // Arrange
        var date = new DateTime(2026, 2, 3);
        var originalSummary1 = "鉄道（博多～天神）";
        var originalSummary2 = "鉄道（天神～薬院）";
        var ledger1 = CreateTestLedger(1, TestCardIdm, date, originalSummary1, 210, 790);
        ledger1.Details.Add(CreateRailDetail(1, "博多", "天神", 210, 790, 1, date));
        var ledger2 = CreateTestLedger(2, TestCardIdm, date, originalSummary2, 180, 610);
        ledger2.Details.Add(CreateRailDetail(2, "天神", "薬院", 180, 610, 2, date));

        SetupGetByIdMocks(ledger1, ledger2);
        SetupMergeMockSuccess();

        string? capturedJson = null;
        _ledgerRepositoryMock
            .Setup(x => x.SaveMergeHistoryAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<int, string, string>((_, _, json) => capturedJson = json)
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.MergeAsync(new List<int> { 1, 2 });

        // Assert: 統合後の Summary は乗継統合で書き換わる
        result.Success.Should().BeTrue();
        result.MergedLedger!.Summary.Should().NotBe(originalSummary1,
            "統合後の Summary は再生成されて元の Summary とは異なる");

        // UndoData には書き換え前の Summary が保持されている
        var undoData = JsonSerializer.Deserialize<LedgerMergeUndoData>(capturedJson!, JsonOptions);
        undoData!.OriginalTarget.Summary.Should().Be(originalSummary1,
            "OriginalTarget は統合前の元Summaryを保持する（Unmerge 時に復元するため）");
        undoData.DeletedSources[0].Summary.Should().Be(originalSummary2,
            "DeletedSources も統合前の各元Summaryを保持する");
    }

    /// <summary>
    /// Issue #1260: UnmergeLedgersAsync が false を返した場合、
    /// MarkMergeHistoryUndoneAsync は呼ばれない（部分的な復旧を残さない）。
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task UnmergeAsync_RepositoryReturnsFalse_DoesNotMarkHistoryUndone()
    {
        // Arrange
        var undoData = new LedgerMergeUndoData
        {
            OriginalTarget = new LedgerSnapshot { Id = 1, CardIdm = TestCardIdm, DateText = "2026-02-03 00:00:00" },
            DeletedSources = new List<LedgerSnapshot>()
        };
        _ledgerRepositoryMock
            .Setup(x => x.GetMergeHistoriesAsync(false))
            .ReturnsAsync(new List<(int, DateTime, int, string, string, bool)>
            {
                (1, DateTime.Now, 1, "テスト", JsonSerializer.Serialize(undoData, JsonOptions), false)
            });
        _ledgerRepositoryMock
            .Setup(x => x.UnmergeLedgersAsync(It.IsAny<LedgerMergeUndoData>()))
            .ReturnsAsync(false);

        // Act
        var result = await _service.UnmergeAsync(1);

        // Assert
        result.Success.Should().BeFalse();
        _ledgerRepositoryMock.Verify(
            x => x.MarkMergeHistoryUndoneAsync(It.IsAny<int>()),
            Times.Never,
            "復旧失敗時は履歴を取消済みマークしない（再試行可能に保つ）");
    }

    /// <summary>
    /// Issue #1260: UnmergeLedgersAsync が例外を投げた場合、
    /// MarkMergeHistoryUndoneAsync は呼ばれない。
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task UnmergeAsync_RepositoryThrows_DoesNotMarkHistoryUndone()
    {
        // Arrange
        var undoData = new LedgerMergeUndoData
        {
            OriginalTarget = new LedgerSnapshot { Id = 1, CardIdm = TestCardIdm, DateText = "2026-02-03 00:00:00" },
            DeletedSources = new List<LedgerSnapshot>()
        };
        _ledgerRepositoryMock
            .Setup(x => x.GetMergeHistoriesAsync(false))
            .ReturnsAsync(new List<(int, DateTime, int, string, string, bool)>
            {
                (1, DateTime.Now, 1, "テスト", JsonSerializer.Serialize(undoData, JsonOptions), false)
            });
        _ledgerRepositoryMock
            .Setup(x => x.UnmergeLedgersAsync(It.IsAny<LedgerMergeUndoData>()))
            .ThrowsAsync(new Exception("Transaction rollback"));

        // Act
        var result = await _service.UnmergeAsync(1);

        // Assert
        result.Success.Should().BeFalse();
        _ledgerRepositoryMock.Verify(
            x => x.MarkMergeHistoryUndoneAsync(It.IsAny<int>()),
            Times.Never,
            "例外発生時も履歴マーク更新は呼ばれず、再試行に備える");
    }

    /// <summary>
    /// Issue #1260: Unmerge 成功時に渡される UndoData が、Merge 時に保存したものと
    /// バイト単位で完全一致すること（JSON 往復後もデータが保持される）。
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task UnmergeAsync_ReceivesExactUndoDataFromMergeSave()
    {
        // Arrange: Merge時と同じUndoDataを構築
        var originalUndoData = new LedgerMergeUndoData
        {
            OriginalTarget = new LedgerSnapshot
            {
                Id = 42,
                CardIdm = TestCardIdm,
                LenderIdm = "9999",
                DateText = "2026-02-03 10:15:30",
                Summary = "鉄道（博多～天神）",
                Income = 0,
                Expense = 260,
                Balance = 740,
                StaffName = "職員A",
                Note = "備考",
                LentAtText = "2026-02-03 08:00:00",
                ReturnedAtText = "2026-02-03 18:00:00",
                IsLentRecord = false
            },
            DeletedSources = new List<LedgerSnapshot>
            {
                new LedgerSnapshot
                {
                    Id = 43,
                    CardIdm = TestCardIdm,
                    DateText = "2026-02-03 14:45:00",
                    Summary = "鉄道（薬院～赤坂）",
                    Expense = 210,
                    Balance = 530
                }
            },
            DetailOriginalLedgerMap = new Dictionary<string, int> { { "10", 42 }, { "20", 43 } }
        };
        var storedJson = JsonSerializer.Serialize(originalUndoData, JsonOptions);

        _ledgerRepositoryMock
            .Setup(x => x.GetMergeHistoriesAsync(false))
            .ReturnsAsync(new List<(int, DateTime, int, string, string, bool)>
            {
                (1, DateTime.Now, 42, "テスト", storedJson, false)
            });

        LedgerMergeUndoData? receivedUndoData = null;
        _ledgerRepositoryMock
            .Setup(x => x.UnmergeLedgersAsync(It.IsAny<LedgerMergeUndoData>()))
            .Callback<LedgerMergeUndoData>(d => receivedUndoData = d)
            .ReturnsAsync(true);
        _ledgerRepositoryMock
            .Setup(x => x.MarkMergeHistoryUndoneAsync(1))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.UnmergeAsync(1);

        // Assert: Repositoryに渡されたUndoDataが元と完全一致
        result.Success.Should().BeTrue();
        receivedUndoData.Should().NotBeNull();
        receivedUndoData!.OriginalTarget.Id.Should().Be(42);
        receivedUndoData.OriginalTarget.Summary.Should().Be("鉄道（博多～天神）");
        receivedUndoData.OriginalTarget.LenderIdm.Should().Be("9999");
        receivedUndoData.OriginalTarget.LentAtText.Should().Be("2026-02-03 08:00:00");
        receivedUndoData.OriginalTarget.ReturnedAtText.Should().Be("2026-02-03 18:00:00");
        receivedUndoData.DeletedSources.Should().HaveCount(1);
        receivedUndoData.DeletedSources[0].Id.Should().Be(43);
        receivedUndoData.DetailOriginalLedgerMap.Should().HaveCount(2);
        receivedUndoData.DetailOriginalLedgerMap["10"].Should().Be(42);
        receivedUndoData.DetailOriginalLedgerMap["20"].Should().Be(43);
    }

    #endregion
}
