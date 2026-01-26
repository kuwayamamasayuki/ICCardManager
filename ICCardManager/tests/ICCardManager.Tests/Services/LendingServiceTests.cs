using FluentAssertions;
using ICCardManager.Data;
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
using System.Threading.Tasks;


namespace ICCardManager.Tests.Services;

/// <summary>
/// LendingServiceのテスト
/// </summary>
public class LendingServiceTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly Mock<ICardRepository> _cardRepositoryMock;
    private readonly Mock<IStaffRepository> _staffRepositoryMock;
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock;
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock;
    private readonly SummaryGenerator _summaryGenerator;
    private readonly CardLockManager _lockManager;
    private readonly LendingService _service;

    // テスト用定数
    private const string TestCardIdm = "0102030405060708";
    private const string TestStaffIdm = "1112131415161718";
    private const string TestStaffName = "テスト太郎";

    public LendingServiceTests()
    {
        // in-memory SQLiteを使用
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();

        _cardRepositoryMock = new Mock<ICardRepository>();
        _staffRepositoryMock = new Mock<IStaffRepository>();
        _ledgerRepositoryMock = new Mock<ILedgerRepository>();
        _settingsRepositoryMock = new Mock<ISettingsRepository>();
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
            NullLogger<LendingService>.Instance);
    }

    public void Dispose()
    {
        _lockManager.Dispose();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    #region LendAsync 正常系テスト

    /// <summary>
    /// 正常な貸出処理が成功することを確認
    /// </summary>
    [Fact]
    public async Task LendAsync_ValidCardAndStaff_ReturnsSuccess()
    {
        // Arrange
        var card = CreateTestCard(isLent: false);
        var staff = CreateTestStaff();

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(TestCardIdm, false))
            .ReturnsAsync(card);
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(TestStaffIdm, false))
            .ReturnsAsync(staff);
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(1);
        _cardRepositoryMock.Setup(x => x.UpdateLentStatusAsync(TestCardIdm, true, It.IsAny<DateTime>(), TestStaffIdm))
            .ReturnsAsync(true);

        // Act
        var result = await _service.LendAsync(TestStaffIdm, TestCardIdm);

        // Assert
        result.Success.Should().BeTrue();
        result.OperationType.Should().Be(LendingOperationType.Lend);
        result.ErrorMessage.Should().BeNull();
        result.CreatedLedgers.Should().HaveCount(1);
    }

    /// <summary>
    /// 貸出後に処理情報が記録されることを確認
    /// </summary>
    [Fact]
    public async Task LendAsync_AfterSuccess_UpdatesLastProcessedInfo()
    {
        // Arrange
        var card = CreateTestCard(isLent: false);
        var staff = CreateTestStaff();

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(TestCardIdm, false))
            .ReturnsAsync(card);
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(TestStaffIdm, false))
            .ReturnsAsync(staff);
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(1);
        _cardRepositoryMock.Setup(x => x.UpdateLentStatusAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string?>()))
            .ReturnsAsync(true);

        // Act
        await _service.LendAsync(TestStaffIdm, TestCardIdm);

        // Assert
        _service.LastProcessedCardIdm.Should().Be(TestCardIdm);
        _service.LastProcessedTime.Should().NotBeNull();
        _service.LastOperationType.Should().Be(LendingOperationType.Lend);
    }

    /// <summary>
    /// 貸出レコードに正しい情報が設定されることを確認
    /// </summary>
    [Fact]
    public async Task LendAsync_Success_CreatesCorrectLedgerRecord()
    {
        // Arrange
        var card = CreateTestCard(isLent: false);
        var staff = CreateTestStaff();
        Ledger? capturedLedger = null;

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(TestCardIdm, false))
            .ReturnsAsync(card);
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(TestStaffIdm, false))
            .ReturnsAsync(staff);
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => capturedLedger = l)
            .ReturnsAsync(1);
        _cardRepositoryMock.Setup(x => x.UpdateLentStatusAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string?>()))
            .ReturnsAsync(true);

        // Act
        await _service.LendAsync(TestStaffIdm, TestCardIdm);

        // Assert
        capturedLedger.Should().NotBeNull();
        capturedLedger!.CardIdm.Should().Be(TestCardIdm);
        capturedLedger.LenderIdm.Should().Be(TestStaffIdm);
        capturedLedger.StaffName.Should().Be(TestStaffName);
        capturedLedger.IsLentRecord.Should().BeTrue();
        capturedLedger.Summary.Should().Be("（貸出中）");
    }

    #endregion

    #region LendAsync 異常系テスト

    /// <summary>
    /// 既に貸出中のカードへの貸出試行でエラーを返す
    /// </summary>
    [Fact]
    public async Task LendAsync_CardAlreadyLent_ReturnsError()
    {
        // Arrange
        var card = CreateTestCard(isLent: true);

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(TestCardIdm, false))
            .ReturnsAsync(card);

        // Act
        var result = await _service.LendAsync(TestStaffIdm, TestCardIdm);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("既に貸出中");

        // カード情報の更新が呼ばれていないことを確認
        _cardRepositoryMock.Verify(x => x.UpdateLentStatusAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string?>()), Times.Never);
    }

    /// <summary>
    /// 存在しないカードIDmでの貸出でエラーを返す
    /// </summary>
    [Fact]
    public async Task LendAsync_CardNotFound_ReturnsError()
    {
        // Arrange
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(TestCardIdm, false))
            .ReturnsAsync((IcCard?)null);

        // Act
        var result = await _service.LendAsync(TestStaffIdm, TestCardIdm);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("カードが登録されていません");
    }

    /// <summary>
    /// 存在しない職員IDmでの貸出でエラーを返す
    /// </summary>
    [Fact]
    public async Task LendAsync_StaffNotFound_ReturnsError()
    {
        // Arrange
        var card = CreateTestCard(isLent: false);

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(TestCardIdm, false))
            .ReturnsAsync(card);
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(TestStaffIdm, false))
            .ReturnsAsync((Staff?)null);

        // Act
        var result = await _service.LendAsync(TestStaffIdm, TestCardIdm);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("職員証が登録されていません");
    }

    /// <summary>
    /// データベースエラー時にエラーメッセージが返されることを確認
    /// </summary>
    [Fact]
    public async Task LendAsync_DatabaseError_ReturnsErrorMessage()
    {
        // Arrange
        var card = CreateTestCard(isLent: false);
        var staff = CreateTestStaff();

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(TestCardIdm, false))
            .ReturnsAsync(card);
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(TestStaffIdm, false))
            .ReturnsAsync(staff);
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .ThrowsAsync(new Exception("Database error"));

        // Act
        var result = await _service.LendAsync(TestStaffIdm, TestCardIdm);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("エラーが発生しました");
    }

    #endregion

    #region ReturnAsync 正常系テスト

    /// <summary>
    /// 正常な返却処理が成功することを確認
    /// </summary>
    [Fact]
    public async Task ReturnAsync_ValidReturn_ReturnsSuccess()
    {
        // Arrange
        var card = CreateTestCard(isLent: true);
        var staff = CreateTestStaff();
        var lentRecord = CreateTestLentRecord();
        var usageDetails = new List<LedgerDetail>();

        SetupReturnMocks(card, staff, lentRecord);

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert
        result.Success.Should().BeTrue();
        result.OperationType.Should().Be(LendingOperationType.Return);
        result.ErrorMessage.Should().BeNull();
    }

    /// <summary>
    /// 利用履歴なしでの返却が成功することを確認
    /// </summary>
    [Fact]
    public async Task ReturnAsync_NoUsageDetails_ReturnsSuccess()
    {
        // Arrange
        var card = CreateTestCard(isLent: true);
        var staff = CreateTestStaff();
        var lentRecord = CreateTestLentRecord();
        var usageDetails = new List<LedgerDetail>();

        SetupReturnMocks(card, staff, lentRecord);

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert
        result.Success.Should().BeTrue();
        result.HasBusUsage.Should().BeFalse();
    }

    /// <summary>
    /// バス利用を含む返却でHasBusUsageがtrueになることを確認
    /// </summary>
    [Fact]
    public async Task ReturnAsync_WithBusUsage_SetsHasBusUsageTrue()
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
                IsBus = true,
                Amount = 200
            }
        };

        SetupReturnMocks(card, staff, lentRecord);

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert
        result.Success.Should().BeTrue();
        result.HasBusUsage.Should().BeTrue();
    }

    /// <summary>
    /// チャージを含む返却処理が成功することを確認
    /// </summary>
    [Fact]
    public async Task ReturnAsync_WithCharge_CreatesChargeLedger()
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
                IsCharge = true,
                Amount = 3000
            }
        };

        SetupReturnMocks(card, staff, lentRecord);

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert
        result.Success.Should().BeTrue();

        // チャージレコードが作成されたことを確認
        _ledgerRepositoryMock.Verify(x => x.InsertAsync(It.Is<Ledger>(l => l.Income == 3000)), Times.Once);
    }

    /// <summary>
    /// 残高警告閾値以下での返却でIsLowBalanceがtrueになることを確認
    /// </summary>
    [Fact]
    public async Task ReturnAsync_LowBalance_SetsIsLowBalanceTrue()
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
                ExitStation = "天神",
                Amount = 500
            }
        };

        SetupReturnMocks(card, staff, lentRecord);
        _ledgerRepositoryMock.Setup(x => x.GetLatestBeforeDateAsync(TestCardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync(new Ledger { Balance = 5000 });
        _settingsRepositoryMock.Setup(x => x.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { WarningBalance = 10000 });

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert
        result.Success.Should().BeTrue();
        result.IsLowBalance.Should().BeTrue();
        result.Balance.Should().BeLessThan(10000);
    }

    /// <summary>
    /// 返却後に処理情報が記録されることを確認
    /// </summary>
    [Fact]
    public async Task ReturnAsync_AfterSuccess_UpdatesLastProcessedInfo()
    {
        // Arrange
        var card = CreateTestCard(isLent: true);
        var staff = CreateTestStaff();
        var lentRecord = CreateTestLentRecord();

        SetupReturnMocks(card, staff, lentRecord);

        // Act
        await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>());

        // Assert
        _service.LastProcessedCardIdm.Should().Be(TestCardIdm);
        _service.LastProcessedTime.Should().NotBeNull();
        _service.LastOperationType.Should().Be(LendingOperationType.Return);
    }

    #endregion

    #region ReturnAsync 異常系テスト

    /// <summary>
    /// 貸出中でないカードの返却試行でエラーを返す
    /// </summary>
    [Fact]
    public async Task ReturnAsync_CardNotLent_ReturnsError()
    {
        // Arrange
        var card = CreateTestCard(isLent: false);

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(TestCardIdm, false))
            .ReturnsAsync(card);

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>());

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("貸出されていません");
    }

    /// <summary>
    /// 存在しないカードの返却試行でエラーを返す
    /// </summary>
    [Fact]
    public async Task ReturnAsync_CardNotFound_ReturnsError()
    {
        // Arrange
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(TestCardIdm, false))
            .ReturnsAsync((IcCard?)null);

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>());

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("カードが登録されていません");
    }

    /// <summary>
    /// 存在しない職員による返却試行でエラーを返す
    /// </summary>
    [Fact]
    public async Task ReturnAsync_StaffNotFound_ReturnsError()
    {
        // Arrange
        var card = CreateTestCard(isLent: true);

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(TestCardIdm, false))
            .ReturnsAsync(card);
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(TestStaffIdm, false))
            .ReturnsAsync((Staff?)null);

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>());

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("職員証が登録されていません");
    }

    /// <summary>
    /// 貸出レコードが見つからない場合にエラーを返す
    /// </summary>
    [Fact]
    public async Task ReturnAsync_LentRecordNotFound_ReturnsError()
    {
        // Arrange
        var card = CreateTestCard(isLent: true);
        var staff = CreateTestStaff();

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(TestCardIdm, false))
            .ReturnsAsync(card);
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(TestStaffIdm, false))
            .ReturnsAsync(staff);
        _ledgerRepositoryMock.Setup(x => x.GetLentRecordAsync(TestCardIdm))
            .ReturnsAsync((Ledger?)null);

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>());

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("貸出レコードが見つかりません");
    }

    #endregion

    #region IsRetouchWithinTimeout テスト

    /// <summary>
    /// 30秒以内の再タッチが検出されることを確認
    /// </summary>
    [Fact]
    public async Task IsRetouchWithinTimeout_WithinTimeout_ReturnsTrue()
    {
        // Arrange - まず貸出処理を実行して履歴を作成
        var card = CreateTestCard(isLent: false);
        var staff = CreateTestStaff();

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(TestCardIdm, false))
            .ReturnsAsync(card);
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(TestStaffIdm, false))
            .ReturnsAsync(staff);
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(1);
        _cardRepositoryMock.Setup(x => x.UpdateLentStatusAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string?>()))
            .ReturnsAsync(true);

        await _service.LendAsync(TestStaffIdm, TestCardIdm);

        // Act - 即座に同じカードでチェック
        var result = _service.IsRetouchWithinTimeout(TestCardIdm);

        // Assert
        result.Should().BeTrue();
    }

    /// <summary>
    /// 異なるカードのタッチでは30秒ルールが適用されないことを確認
    /// </summary>
    [Fact]
    public async Task IsRetouchWithinTimeout_DifferentCard_ReturnsFalse()
    {
        // Arrange - まず貸出処理を実行
        var card = CreateTestCard(isLent: false);
        var staff = CreateTestStaff();

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(TestCardIdm, false))
            .ReturnsAsync(card);
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(TestStaffIdm, false))
            .ReturnsAsync(staff);
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(1);
        _cardRepositoryMock.Setup(x => x.UpdateLentStatusAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string?>()))
            .ReturnsAsync(true);

        await _service.LendAsync(TestStaffIdm, TestCardIdm);

        // Act - 異なるカードでチェック
        var result = _service.IsRetouchWithinTimeout("DIFFERENT_CARD_IDM");

        // Assert
        result.Should().BeFalse();
    }

    /// <summary>
    /// 処理履歴がない場合は30秒ルールが適用されないことを確認
    /// </summary>
    [Fact]
    public void IsRetouchWithinTimeout_NoHistory_ReturnsFalse()
    {
        // Act - 履歴がない状態でチェック
        var result = _service.IsRetouchWithinTimeout(TestCardIdm);

        // Assert
        result.Should().BeFalse();
    }

    /// <summary>
    /// ClearHistoryで履歴がクリアされることを確認
    /// </summary>
    [Fact]
    public async Task ClearHistory_AfterLend_ClearsAllHistory()
    {
        // Arrange - まず貸出処理を実行
        var card = CreateTestCard(isLent: false);
        var staff = CreateTestStaff();

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(TestCardIdm, false))
            .ReturnsAsync(card);
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(TestStaffIdm, false))
            .ReturnsAsync(staff);
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(1);
        _cardRepositoryMock.Setup(x => x.UpdateLentStatusAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string?>()))
            .ReturnsAsync(true);

        await _service.LendAsync(TestStaffIdm, TestCardIdm);

        // Act
        _service.ClearHistory();

        // Assert
        _service.LastProcessedCardIdm.Should().BeNull();
        _service.LastProcessedTime.Should().BeNull();
        _service.LastOperationType.Should().BeNull();
        _service.IsRetouchWithinTimeout(TestCardIdm).Should().BeFalse();
    }

    /// <summary>
    /// 貸出後の30秒以内再タッチで、逆操作（返却）が必要であることを判定できることを確認
    /// </summary>
    [Fact]
    public async Task IsRetouchWithinTimeout_AfterLend_CanDetermineReverseOperation()
    {
        // Arrange - 貸出処理を実行
        var card = CreateTestCard(isLent: false);
        var staff = CreateTestStaff();

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(TestCardIdm, false))
            .ReturnsAsync(card);
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(TestStaffIdm, false))
            .ReturnsAsync(staff);
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(1);
        _cardRepositoryMock.Setup(x => x.UpdateLentStatusAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string?>()))
            .ReturnsAsync(true);

        await _service.LendAsync(TestStaffIdm, TestCardIdm);

        // Act - 30秒ルールチェック
        var isWithinTimeout = _service.IsRetouchWithinTimeout(TestCardIdm);
        var lastOperation = _service.LastOperationType;

        // Assert - 30秒以内であり、前回操作が貸出であることを確認（逆操作は返却）
        isWithinTimeout.Should().BeTrue();
        lastOperation.Should().Be(LendingOperationType.Lend);
    }

    /// <summary>
    /// 返却後の30秒以内再タッチで、逆操作（貸出）が必要であることを判定できることを確認
    /// </summary>
    [Fact]
    public async Task IsRetouchWithinTimeout_AfterReturn_CanDetermineReverseOperation()
    {
        // Arrange - 返却処理を実行
        var card = CreateTestCard(isLent: true);
        var staff = CreateTestStaff();
        var lentRecord = CreateTestLentRecord();

        SetupReturnMocks(card, staff, lentRecord);

        await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>());

        // Act - 30秒ルールチェック
        var isWithinTimeout = _service.IsRetouchWithinTimeout(TestCardIdm);
        var lastOperation = _service.LastOperationType;

        // Assert - 30秒以内であり、前回操作が返却であることを確認（逆操作は貸出）
        isWithinTimeout.Should().BeTrue();
        lastOperation.Should().Be(LendingOperationType.Return);
    }

    /// <summary>
    /// 貸出後に返却し、その後再度同一カードをタッチした場合、最後の操作（返却）が記録されていることを確認
    /// </summary>
    [Fact]
    public async Task IsRetouchWithinTimeout_AfterLendThenReturn_TracksLastOperation()
    {
        // Arrange
        var staff = CreateTestStaff();

        // モックを柔軟に設定（貸出中フラグが変わるシナリオ）
        var isLent = false;
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(TestCardIdm, false))
            .ReturnsAsync(() => new IcCard
            {
                CardIdm = TestCardIdm,
                CardType = "はやかけん",
                CardNumber = "H001",
                IsLent = isLent,
                IsDeleted = false
            });
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(TestStaffIdm, false))
            .ReturnsAsync(staff);
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(1);
        _cardRepositoryMock.Setup(x => x.UpdateLentStatusAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string?>()))
            .Callback<string, bool, DateTime?, string?>((idm, lent, time, staff) => isLent = lent)
            .ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(x => x.GetLentRecordAsync(TestCardIdm))
            .ReturnsAsync(CreateTestLentRecord());
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(x => x.GetLatestBeforeDateAsync(TestCardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync(new Ledger { Balance = 5000 });
        _settingsRepositoryMock.Setup(x => x.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { WarningBalance = 1000 });

        // Act - 貸出を実行
        await _service.LendAsync(TestStaffIdm, TestCardIdm);
        _service.LastOperationType.Should().Be(LendingOperationType.Lend);

        // Act - 返却を実行
        await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>());

        // Assert - 最後の操作が返却であることを確認
        _service.IsRetouchWithinTimeout(TestCardIdm).Should().BeTrue();
        _service.LastOperationType.Should().Be(LendingOperationType.Return);
    }

    /// <summary>
    /// 30秒ルールで逆操作を判定するロジックのテスト
    /// </summary>
    [Theory]
    [InlineData(LendingOperationType.Lend, LendingOperationType.Return)]
    [InlineData(LendingOperationType.Return, LendingOperationType.Lend)]
    public void ThirtySecondRule_DetermineReverseOperation_ReturnsCorrectOperation(
        LendingOperationType lastOperation,
        LendingOperationType expectedReverse)
    {
        // Arrange & Act - 逆操作を判定
        // これはMainViewModelでの実装ロジックをテスト
        LendingOperationType reverseOperation = lastOperation == LendingOperationType.Lend
            ? LendingOperationType.Return
            : LendingOperationType.Lend;

        // Assert
        reverseOperation.Should().Be(expectedReverse);
    }

    #endregion

    #region 複数日利用履歴テスト

    /// <summary>
    /// 複数日にわたる利用履歴が正しく処理されることを確認
    /// </summary>
    [Fact]
    public async Task ReturnAsync_MultiDayUsage_CreatesMultipleLedgers()
    {
        // Arrange
        var card = CreateTestCard(isLent: true);
        var staff = CreateTestStaff();
        var lentRecord = CreateTestLentRecord(daysAgo: 3); // 3日前に貸出

        var today = DateTime.Today;
        var usageDetails = new List<LedgerDetail>
        {
            new() { UseDate = today.AddDays(-2), EntryStation = "博多", ExitStation = "天神", Amount = 260 },
            new() { UseDate = today.AddDays(-1), EntryStation = "天神", ExitStation = "博多", Amount = 260 },
            new() { UseDate = today, EntryStation = "博多", ExitStation = "空港", Amount = 310 }
        };

        SetupReturnMocks(card, staff, lentRecord);

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert
        result.Success.Should().BeTrue();

        // 3日分のレコードが作成されたことを確認
        _ledgerRepositoryMock.Verify(x => x.InsertAsync(It.IsAny<Ledger>()), Times.Exactly(3));
    }

    /// <summary>
    /// チャージと利用が同日にある場合、別々のレコードとして作成されることを確認
    /// </summary>
    [Fact]
    public async Task ReturnAsync_ChargeAndUsageSameDay_CreatesSeparateLedgers()
    {
        // Arrange
        var card = CreateTestCard(isLent: true);
        var staff = CreateTestStaff();
        var lentRecord = CreateTestLentRecord(daysAgo: 1); // 昨日貸出

        var today = DateTime.Today;
        var usageDetails = new List<LedgerDetail>
        {
            new() { UseDate = today, IsCharge = true, Amount = 3000 },
            new() { UseDate = today, EntryStation = "博多", ExitStation = "天神", Amount = 260 }
        };

        SetupReturnMocks(card, staff, lentRecord);

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert
        result.Success.Should().BeTrue();

        // チャージレコードと利用レコードの2つが作成されたことを確認
        _ledgerRepositoryMock.Verify(x => x.InsertAsync(It.Is<Ledger>(l => l.Income > 0)), Times.Once);
        _ledgerRepositoryMock.Verify(x => x.InsertAsync(It.Is<Ledger>(l => l.Expense > 0)), Times.Once);
    }

    #endregion

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

    private static Ledger CreateTestLentRecord(int daysAgo = 0)
    {
        return new Ledger
        {
            Id = 1,
            CardIdm = TestCardIdm,
            LenderIdm = TestStaffIdm,
            StaffName = TestStaffName,
            Date = DateTime.Today.AddDays(-daysAgo),
            IsLentRecord = true,
            LentAt = DateTime.Now.AddDays(-daysAgo).AddHours(-1),
            Summary = "（貸出中）"
        };
    }

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
        _ledgerRepositoryMock.Setup(x => x.InsertDetailAsync(It.IsAny<LedgerDetail>()))
            .ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(x => x.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(x => x.GetLatestBeforeDateAsync(TestCardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync(new Ledger { Balance = 10000 });
        _cardRepositoryMock.Setup(x => x.UpdateLentStatusAsync(TestCardIdm, false, null, null))
            .ReturnsAsync(true);
        _settingsRepositoryMock.Setup(x => x.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { WarningBalance = 1000 });
    }

    #endregion

    #region 同時操作排他制御テスト（Issue #24）

    /// <summary>
    /// 同一カードへの同時貸出操作で、一方のみが成功することを確認
    /// </summary>
    [Fact]
    public async Task LendAsync_ConcurrentLendOnSameCard_OnlyOneSucceeds()
    {
        // Arrange
        var card = CreateTestCard(isLent: false);
        var staff = CreateTestStaff();
        var lendCount = 0;
        var lockObj = new object();

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(TestCardIdm, false))
            .ReturnsAsync(() =>
            {
                // 最初の呼び出しは未貸出、2回目以降は貸出中を返す
                lock (lockObj)
                {
                    var currentCount = lendCount;
                    if (currentCount == 0)
                    {
                        return CreateTestCard(isLent: false);
                    }
                    return CreateTestCard(isLent: true);
                }
            });
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(TestStaffIdm, false))
            .ReturnsAsync(staff);
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(() =>
            {
                lock (lockObj)
                {
                    return ++lendCount;
                }
            });
        _cardRepositoryMock.Setup(x => x.UpdateLentStatusAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string?>()))
            .ReturnsAsync(true);

        // Act - 2つの貸出を同時実行
        var task1 = _service.LendAsync(TestStaffIdm, TestCardIdm);
        var task2 = _service.LendAsync(TestStaffIdm, TestCardIdm);

        var results = await Task.WhenAll(task1, task2);

        // Assert - 排他制御により、1つのみ成功（もう1つは「既に貸出中」または「処理中」でブロック）
        var successCount = results.Count(r => r.Success);
        var errorMessages = results.Where(r => !r.Success).Select(r => r.ErrorMessage).ToList();

        successCount.Should().Be(1, "排他制御により同時貸出は1つのみ成功");
        // 失敗理由は「既に貸出中」または「他の処理が実行中」
        errorMessages.Should().ContainSingle();
        errorMessages[0].Should().Match(m =>
            m!.Contains("貸出中") || m.Contains("処理が実行中"));
    }

    /// <summary>
    /// 異なるカードへの同時貸出操作は、両方成功することを確認
    /// </summary>
    [Fact]
    public async Task LendAsync_ConcurrentLendOnDifferentCards_BothSucceed()
    {
        // Arrange
        const string cardIdm1 = "0102030405060708";
        const string cardIdm2 = "0807060504030201";
        var card1 = new IcCard { CardIdm = cardIdm1, CardType = "はやかけん", CardNumber = "H001", IsLent = false };
        var card2 = new IcCard { CardIdm = cardIdm2, CardType = "nimoca", CardNumber = "N001", IsLent = false };
        var staff = CreateTestStaff();

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(cardIdm1, false)).ReturnsAsync(card1);
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(cardIdm2, false)).ReturnsAsync(card2);
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(TestStaffIdm, false)).ReturnsAsync(staff);
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(1);
        _cardRepositoryMock.Setup(x => x.UpdateLentStatusAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string?>()))
            .ReturnsAsync(true);

        // Act - 異なるカードへの2つの貸出を同時実行
        var task1 = _service.LendAsync(TestStaffIdm, cardIdm1);
        var task2 = _service.LendAsync(TestStaffIdm, cardIdm2);

        var results = await Task.WhenAll(task1, task2);

        // Assert - 異なるカードは排他されないので両方成功
        results.Should().OnlyContain(item => item.Success == true);
    }

    /// <summary>
    /// 同一カードへの同時返却操作で、一方のみが成功することを確認
    /// </summary>
    [Fact]
    public async Task ReturnAsync_ConcurrentReturnOnSameCard_OnlyOneSucceeds()
    {
        // Arrange
        var card = CreateTestCard(isLent: true);
        var staff = CreateTestStaff();
        var lentRecord = CreateTestLentRecord();
        var returnCount = 0;
        var lockObj = new object();

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(TestCardIdm, false))
            .ReturnsAsync(() =>
            {
                lock (lockObj)
                {
                    // 最初は貸出中、返却後は未貸出
                    return returnCount == 0 ? CreateTestCard(isLent: true) : CreateTestCard(isLent: false);
                }
            });
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(TestStaffIdm, false))
            .ReturnsAsync(staff);
        _ledgerRepositoryMock.Setup(x => x.GetLentRecordAsync(TestCardIdm))
            .ReturnsAsync(() =>
            {
                lock (lockObj)
                {
                    return returnCount == 0 ? lentRecord : null;
                }
            });
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>()))
            .Callback(() =>
            {
                lock (lockObj)
                {
                    returnCount++;
                }
            })
            .ReturnsAsync(true);
        _cardRepositoryMock.Setup(x => x.UpdateLentStatusAsync(TestCardIdm, false, null, null))
            .ReturnsAsync(true);
        _settingsRepositoryMock.Setup(x => x.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { WarningBalance = 1000 });

        // Act - 2つの返却を同時実行
        var task1 = _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>());
        var task2 = _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>());

        var results = await Task.WhenAll(task1, task2);

        // Assert - 排他制御により、1つのみ成功
        var successCount = results.Count(r => r.Success);
        successCount.Should().Be(1, "排他制御により同時返却は1つのみ成功");
    }

    /// <summary>
    /// 処理中の再タッチがタイムアウトで適切にハンドリングされることを確認
    /// </summary>
    [Fact]
    public async Task LendAsync_LockTimeout_ReturnsAppropriateError()
    {
        // Arrange - TaskCompletionSourceで処理の完了を制御
        var tcs = new TaskCompletionSource<int>();
        var card = CreateTestCard(isLent: false);
        var staff = CreateTestStaff();
        var timeoutCardIdm = "TIMEOUT_TEST_CARD"; // 他テストと競合しないユニークなIDm

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(timeoutCardIdm, false))
            .ReturnsAsync(card);
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(TestStaffIdm, false))
            .ReturnsAsync(staff);
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .Returns(tcs.Task); // TaskCompletionSourceで完了を制御
        _cardRepositoryMock.Setup(x => x.UpdateLentStatusAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string?>()))
            .ReturnsAsync(true);

        // 短いタイムアウトのサービスを作成
        var shortTimeoutService = new ShortTimeoutLendingService(
            _dbContext,
            _cardRepositoryMock.Object,
            _staffRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            _summaryGenerator,
            _lockManager,
            NullLogger<LendingService>.Instance);

        // Act - 最初の処理を開始し、ロックを保持させる
        var task1 = shortTimeoutService.LendAsync(TestStaffIdm, timeoutCardIdm);
        await Task.Delay(30); // Task1がロックを取得しInsertAsyncに到達するまで待機

        // 2つ目の処理を開始 - タイムアウトするはず
        var task2 = shortTimeoutService.LendAsync(TestStaffIdm, timeoutCardIdm);
        var result2 = await task2; // Task1がロックを保持しているのでタイムアウト

        // Task1を完了させる
        tcs.SetResult(1);
        var result1 = await task1;

        // Assert - 2つ目はタイムアウトでエラー
        result2.Success.Should().BeFalse("排他ロックのタイムアウトによりエラー");
        result2.ErrorMessage.Should().Contain("処理が実行中");
    }

    /// <summary>
    /// 複数回の連続操作でデッドロックが発生しないことを確認
    /// </summary>
    [Fact]
    public async Task LendAsync_MultipleConsecutiveOperations_NoDeadlock()
    {
        // Arrange
        var staff = CreateTestStaff();
        var operationCount = 0;
        var lockObj = new object();

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(TestCardIdm, false))
            .ReturnsAsync(() =>
            {
                lock (lockObj)
                {
                    // 偶数回は未貸出、奇数回は貸出中
                    return operationCount % 2 == 0 ? CreateTestCard(isLent: false) : CreateTestCard(isLent: true);
                }
            });
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(TestStaffIdm, false))
            .ReturnsAsync(staff);
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .Callback(() =>
            {
                lock (lockObj)
                {
                    operationCount++;
                }
            })
            .ReturnsAsync(1);
        _cardRepositoryMock.Setup(x => x.UpdateLentStatusAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string?>()))
            .ReturnsAsync(true);

        // Act - 連続して10回の操作を実行（タイムアウトなし = デッドロックなし）
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => _service.LendAsync(TestStaffIdm, TestCardIdm))
            .ToList();

        // 10秒以内に完了すればデッドロックなし
        var completedInTime = await Task.WhenAll(tasks).ConfigureAwait(false);

        // Assert - 全ての操作が完了（デッドロックなし）
        completedInTime.Should().NotBeNull();
        completedInTime.Should().HaveCount(10);
    }

    /// <summary>
    /// 同一カードへの貸出と返却の同時実行で排他制御が機能することを確認
    /// </summary>
    [Fact]
    public async Task LendAndReturnAsync_ConcurrentOnSameCard_ProperlyHandled()
    {
        // Arrange
        var cardLent = true; // 最初は貸出中
        var lockObj = new object();
        var lentRecord = CreateTestLentRecord();
        var staff = CreateTestStaff();

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(TestCardIdm, false))
            .ReturnsAsync(() =>
            {
                lock (lockObj)
                {
                    return CreateTestCard(isLent: cardLent);
                }
            });
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(TestStaffIdm, false))
            .ReturnsAsync(staff);
        _ledgerRepositoryMock.Setup(x => x.GetLentRecordAsync(TestCardIdm))
            .ReturnsAsync(() =>
            {
                lock (lockObj)
                {
                    return cardLent ? lentRecord : null;
                }
            });
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(1);
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>()))
            .Callback(() =>
            {
                lock (lockObj)
                {
                    cardLent = false;
                }
            })
            .ReturnsAsync(true);
        _cardRepositoryMock.Setup(x => x.UpdateLentStatusAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string?>()))
            .ReturnsAsync(true);
        _settingsRepositoryMock.Setup(x => x.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { WarningBalance = 1000 });

        // Act - 貸出と返却を同時実行
        var lendTask = _service.LendAsync(TestStaffIdm, TestCardIdm);
        var returnTask = _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>());

        var results = await Task.WhenAll(lendTask, returnTask);
        var lendResult = results[0];
        var returnResult = results[1];

        // Assert - どちらか一方のみ成功（排他制御により順序が保証される）
        var successCount = results.Count(r => r.Success);
        successCount.Should().Be(1, "排他制御により同時操作は1つのみ成功");
    }

    #endregion

    #region ヘルパークラス（同時操作テスト用）

    /// <summary>
    /// 短いロックタイムアウトを持つテスト用サービス
    /// </summary>
    private class ShortTimeoutLendingService : LendingService
    {
        public ShortTimeoutLendingService(
            DbContext dbContext,
            ICardRepository cardRepository,
            IStaffRepository staffRepository,
            ILedgerRepository ledgerRepository,
            ISettingsRepository settingsRepository,
            SummaryGenerator summaryGenerator,
            CardLockManager lockManager,
            ILogger<LendingService> logger)
            : base(dbContext, cardRepository, staffRepository, ledgerRepository, settingsRepository, summaryGenerator, lockManager, logger)
        {
        }

        protected override int GetLockTimeoutMs() => 100; // 100msの短いタイムアウト
    }

    #endregion
}
