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
            _staffRepositoryMock.Object);

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
        List<LedgerDetail> details = null)
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
            .ReturnsAsync((Ledger)null);

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

        string capturedUndoJson = null;
        int capturedTargetId = 0;
        string capturedDescription = null;

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
        var undoData = JsonSerializer.Deserialize<LedgerMergeUndoData>(capturedUndoJson, JsonOptions);
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

        string capturedUndoJson = null;
        _ledgerRepositoryMock
            .Setup(x => x.SaveMergeHistoryAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<int, string, string>((_, _, json) => capturedUndoJson = json)
            .Returns(Task.CompletedTask);

        // Act
        await _service.MergeAsync(new List<int> { 1, 2 });

        // Assert
        var undoData = JsonSerializer.Deserialize<LedgerMergeUndoData>(capturedUndoJson, JsonOptions);
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
}
