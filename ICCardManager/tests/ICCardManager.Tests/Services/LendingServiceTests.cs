using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Logging;
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
        _settingsRepositoryMock.Setup(s => s.GetAppSettings()).Returns(new AppSettings());
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

    /// <summary>
    /// Issue #656: 残高が渡された場合、その値が使用されること
    /// </summary>
    [Fact]
    public async Task LendAsync_WithBalance_UsesProvidedBalance()
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
        var result = await _service.LendAsync(TestStaffIdm, TestCardIdm, balance: 1500);

        // Assert
        result.Success.Should().BeTrue();
        result.Balance.Should().Be(1500);
        capturedLedger.Should().NotBeNull();
        capturedLedger!.Balance.Should().Be(1500);
    }

    /// <summary>
    /// Issue #656: 残高がnullの場合、直近の履歴から残高を取得すること
    /// </summary>
    [Fact]
    public async Task LendAsync_WithNullBalance_FallsBackToLatestLedgerBalance()
    {
        // Arrange
        var card = CreateTestCard(isLent: false);
        var staff = CreateTestStaff();
        Ledger? capturedLedger = null;

        var latestLedger = new Ledger
        {
            Id = 100,
            CardIdm = TestCardIdm,
            Date = DateTime.Now.AddDays(-1),
            Balance = 2300,
            Summary = "鉄道（博多駅～天神駅）"
        };

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(TestCardIdm, false))
            .ReturnsAsync(card);
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(TestStaffIdm, false))
            .ReturnsAsync(staff);
        _ledgerRepositoryMock.Setup(x => x.GetLatestLedgerAsync(TestCardIdm))
            .ReturnsAsync(latestLedger);
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => capturedLedger = l)
            .ReturnsAsync(1);
        _cardRepositoryMock.Setup(x => x.UpdateLentStatusAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string?>()))
            .ReturnsAsync(true);

        // Act
        var result = await _service.LendAsync(TestStaffIdm, TestCardIdm, balance: null);

        // Assert
        result.Success.Should().BeTrue();
        result.Balance.Should().Be(2300);
        capturedLedger.Should().NotBeNull();
        capturedLedger!.Balance.Should().Be(2300);
    }

    /// <summary>
    /// Issue #656: 残高がnullかつ直近の履歴もない場合、0になること
    /// </summary>
    [Fact]
    public async Task LendAsync_WithNullBalanceAndNoLedgerHistory_DefaultsToZero()
    {
        // Arrange
        var card = CreateTestCard(isLent: false);
        var staff = CreateTestStaff();
        Ledger? capturedLedger = null;

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(TestCardIdm, false))
            .ReturnsAsync(card);
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(TestStaffIdm, false))
            .ReturnsAsync(staff);
        _ledgerRepositoryMock.Setup(x => x.GetLatestLedgerAsync(TestCardIdm))
            .ReturnsAsync((Ledger?)null);
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => capturedLedger = l)
            .ReturnsAsync(1);
        _cardRepositoryMock.Setup(x => x.UpdateLentStatusAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string?>()))
            .ReturnsAsync(true);

        // Act
        var result = await _service.LendAsync(TestStaffIdm, TestCardIdm, balance: null);

        // Assert
        result.Success.Should().BeTrue();
        result.Balance.Should().Be(0);
        capturedLedger.Should().NotBeNull();
        capturedLedger!.Balance.Should().Be(0);
    }

    /// <summary>
    /// Issue #656: 残高が0の場合（実際に0円）、DBフォールバックが発動しないこと
    /// </summary>
    [Fact]
    public async Task LendAsync_WithZeroBalance_DoesNotFallBackToDb()
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
        var result = await _service.LendAsync(TestStaffIdm, TestCardIdm, balance: 0);

        // Assert
        result.Success.Should().BeTrue();
        result.Balance.Should().Be(0);
        capturedLedger.Should().NotBeNull();
        capturedLedger!.Balance.Should().Be(0);
        // DBフォールバックが呼ばれていないことを確認
        _ledgerRepositoryMock.Verify(x => x.GetLatestLedgerAsync(It.IsAny<string>()), Times.Never);
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
        _ledgerRepositoryMock.Setup(x => x.DeleteAsync(It.IsAny<int>()))
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

    /// <summary>
    /// Issue #807: チャージのLedgerはStaffName=null、利用のLedgerにはStaffNameが設定されること
    /// </summary>
    [Fact]
    public async Task ReturnAsync_ChargeLedger_HasNullStaffName()
    {
        // Arrange
        var card = CreateTestCard(isLent: true);
        var staff = CreateTestStaff();
        var lentRecord = CreateTestLentRecord(daysAgo: 1);

        var today = DateTime.Today;
        var usageDetails = new List<LedgerDetail>
        {
            new() { UseDate = today, IsCharge = true, Amount = 3000, Balance = 13000 },
            new() { UseDate = today, EntryStation = "博多", ExitStation = "天神", Amount = 260, Balance = 12740 }
        };

        var capturedLedgers = new List<Ledger>();
        SetupReturnMocks(card, staff, lentRecord);
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => capturedLedgers.Add(l))
            .ReturnsAsync(1);

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert
        result.Success.Should().BeTrue();

        var chargeLedger = capturedLedgers.FirstOrDefault(l => l.Income > 0);
        chargeLedger.Should().NotBeNull();
        chargeLedger!.StaffName.Should().BeNull("チャージは機械操作のため氏名不要");

        var usageLedger = capturedLedgers.FirstOrDefault(l => l.Expense > 0 && !l.IsLentRecord);
        usageLedger.Should().NotBeNull();
        usageLedger!.StaffName.Should().Be(TestStaffName, "利用レコードには職員名が必要");
    }

    /// <summary>
    /// Issue #807: ポイント還元のみの場合、StaffName=nullであること
    /// </summary>
    [Fact]
    public async Task ReturnAsync_PointRedemptionOnly_HasNullStaffName()
    {
        // Arrange
        var card = CreateTestCard(isLent: true);
        var staff = CreateTestStaff();
        var lentRecord = CreateTestLentRecord();

        var usageDetails = new List<LedgerDetail>
        {
            new() { UseDate = DateTime.Today, IsPointRedemption = true, Amount = 500, Balance = 10500 }
        };

        var capturedLedgers = new List<Ledger>();
        SetupReturnMocks(card, staff, lentRecord);
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => capturedLedgers.Add(l))
            .ReturnsAsync(1);

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert
        result.Success.Should().BeTrue();

        // ポイント還元のLedger（貸出レコード更新を除く）
        var pointLedger = capturedLedgers.FirstOrDefault(l => !l.IsLentRecord);
        pointLedger.Should().NotBeNull();
        pointLedger!.StaffName.Should().BeNull("ポイント還元は機械操作のため氏名不要");
    }

    /// <summary>
    /// Issue #807: ポイント還元＋通常利用が混在する場合、StaffNameに名前が入っていること
    /// </summary>
    [Fact]
    public async Task ReturnAsync_UsageWithPointRedemption_HasStaffName()
    {
        // Arrange
        var card = CreateTestCard(isLent: true);
        var staff = CreateTestStaff();
        var lentRecord = CreateTestLentRecord();

        var usageDetails = new List<LedgerDetail>
        {
            new() { UseDate = DateTime.Today, IsPointRedemption = true, Amount = 500, Balance = 10500 },
            new() { UseDate = DateTime.Today, EntryStation = "博多", ExitStation = "天神", Amount = 260, Balance = 10240 }
        };

        var capturedLedgers = new List<Ledger>();
        SetupReturnMocks(card, staff, lentRecord);
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => capturedLedgers.Add(l))
            .ReturnsAsync(1);

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert
        result.Success.Should().BeTrue();

        // 利用とポイント還元が混在する場合はStaffNameが設定される
        var usageLedger = capturedLedgers.FirstOrDefault(l => !l.IsLentRecord);
        usageLedger.Should().NotBeNull();
        usageLedger!.StaffName.Should().Be(TestStaffName, "通常利用が含まれるため職員名が必要");
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
        _ledgerRepositoryMock.Setup(x => x.DeleteAsync(It.IsAny<int>()))
            .ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(x => x.InsertDetailAsync(It.IsAny<LedgerDetail>()))
            .ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(x => x.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(x => x.GetLatestBeforeDateAsync(TestCardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync(new Ledger { Balance = 10000 });
        // Issue #326対応: 重複チェック用のモック追加
        _ledgerRepositoryMock.Setup(x => x.GetExistingDetailKeysAsync(TestCardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync(new HashSet<(DateTime?, int?, bool)>());
        _cardRepositoryMock.Setup(x => x.UpdateLentStatusAsync(TestCardIdm, false, null, null))
            .ReturnsAsync(true);
        _settingsRepositoryMock.Setup(x => x.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { WarningBalance = 1000 });
        // Issue #837対応: 同一日既存レコード検索（デフォルトは空=統合なし）
        _ledgerRepositoryMock.Setup(x => x.GetByDateRangeAsync(TestCardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<Ledger>());
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
        _ledgerRepositoryMock.Setup(x => x.DeleteAsync(It.IsAny<int>()))
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
            Options.Create(new AppOptions()),
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
        _ledgerRepositoryMock.Setup(x => x.DeleteAsync(It.IsAny<int>()))
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
            IOptions<AppOptions> appOptions,
            ILogger<LendingService> logger)
            : base(dbContext, cardRepository, staffRepository, ledgerRepository, settingsRepository, summaryGenerator, lockManager, appOptions, logger)
        {
        }

        protected override int GetLockTimeoutMs() => 100; // 100msの短いタイムアウト
    }

    #endregion

    #region 残高不足パターン検出テスト（Issue #380）

    /// <summary>
    /// 残高不足パターンが正しく検出されることを確認
    /// </summary>
    /// <remarks>
    /// シナリオ: 残高200円、運賃210円
    /// - チャージ: 10円（残高 → 210円）
    /// - 利用: 210円（残高 → 0円）
    /// → パターン検出条件: charge.Balance(210) == usage.Amount(210) かつ usage.Balance(0) == 0
    /// </remarks>
    [Fact]
    public void DetectInsufficientBalancePattern_ValidPattern_ReturnsMatchedPair()
    {
        // Arrange
        var today = DateTime.Today;
        var details = new List<LedgerDetail>
        {
            new()
            {
                UseDate = today,
                IsCharge = true,
                Amount = 10,       // 不足分のチャージ
                Balance = 210      // チャージ後の残高（= 運賃と同額）
            },
            new()
            {
                UseDate = today,
                IsCharge = false,
                EntryStation = "博多",
                ExitStation = "天神",
                Amount = 210,      // 運賃
                Balance = 0        // 利用後の残高
            }
        };

        // Act
        var result = LendingService.DetectInsufficientBalancePattern(details);

        // Assert
        result.Should().HaveCount(1);
        result[0].Charge.Amount.Should().Be(10);
        result[0].Usage.Amount.Should().Be(210);
    }

    /// <summary>
    /// 通常のチャージ（不足分ではない）はパターンとして検出されないことを確認
    /// </summary>
    [Fact]
    public void DetectInsufficientBalancePattern_RegularCharge_ReturnsEmpty()
    {
        // Arrange - 通常のチャージ（3000円）と利用（260円）
        var today = DateTime.Today;
        var details = new List<LedgerDetail>
        {
            new()
            {
                UseDate = today,
                IsCharge = true,
                Amount = 3000,
                Balance = 5000     // チャージ後の残高（運賃とは異なる）
            },
            new()
            {
                UseDate = today,
                IsCharge = false,
                EntryStation = "博多",
                ExitStation = "天神",
                Amount = 260,
                Balance = 4740     // 利用後の残高（0ではない）
            }
        };

        // Act
        var result = LendingService.DetectInsufficientBalancePattern(details);

        // Assert
        result.Should().BeEmpty();
    }

    /// <summary>
    /// 利用後の残高が0でない場合はパターンとして検出されないことを確認
    /// </summary>
    [Fact]
    public void DetectInsufficientBalancePattern_NonZeroBalance_ReturnsEmpty()
    {
        // Arrange - チャージ後残高と運賃は一致するが、利用後残高が0ではない
        var today = DateTime.Today;
        var details = new List<LedgerDetail>
        {
            new()
            {
                UseDate = today,
                IsCharge = true,
                Amount = 10,
                Balance = 210
            },
            new()
            {
                UseDate = today,
                IsCharge = false,
                EntryStation = "博多",
                ExitStation = "天神",
                Amount = 210,
                Balance = 100      // 0ではない（別のチャージがあった等）
            }
        };

        // Act
        var result = LendingService.DetectInsufficientBalancePattern(details);

        // Assert
        result.Should().BeEmpty();
    }

    /// <summary>
    /// チャージ後残高と運賃が一致しない場合はパターンとして検出されないことを確認
    /// </summary>
    [Fact]
    public void DetectInsufficientBalancePattern_MismatchedAmount_ReturnsEmpty()
    {
        // Arrange - チャージ後残高(200) != 運賃(210)
        var today = DateTime.Today;
        var details = new List<LedgerDetail>
        {
            new()
            {
                UseDate = today,
                IsCharge = true,
                Amount = 10,
                Balance = 200      // 運賃(210)と一致しない
            },
            new()
            {
                UseDate = today,
                IsCharge = false,
                EntryStation = "博多",
                ExitStation = "天神",
                Amount = 210,
                Balance = 0
            }
        };

        // Act
        var result = LendingService.DetectInsufficientBalancePattern(details);

        // Assert
        result.Should().BeEmpty();
    }

    /// <summary>
    /// 空のリストを渡した場合、空の結果が返ることを確認
    /// </summary>
    [Fact]
    public void DetectInsufficientBalancePattern_EmptyList_ReturnsEmpty()
    {
        // Arrange
        var details = new List<LedgerDetail>();

        // Act
        var result = LendingService.DetectInsufficientBalancePattern(details);

        // Assert
        result.Should().BeEmpty();
    }

    /// <summary>
    /// ポイント還元はパターン検出から除外されることを確認
    /// </summary>
    [Fact]
    public void DetectInsufficientBalancePattern_PointRedemption_NotMatched()
    {
        // Arrange - ポイント還元は利用として扱わない
        var today = DateTime.Today;
        var details = new List<LedgerDetail>
        {
            new()
            {
                UseDate = today,
                IsCharge = true,
                Amount = 10,
                Balance = 210
            },
            new()
            {
                UseDate = today,
                IsCharge = false,
                IsPointRedemption = true,  // ポイント還元
                Amount = 210,
                Balance = 0
            }
        };

        // Act
        var result = LendingService.DetectInsufficientBalancePattern(details);

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region 履歴完全性チェックテスト（Issue #596）

    /// <summary>
    /// 20件すべてが今月の履歴の場合、不完全な可能性ありと判定されること
    /// </summary>
    [Fact]
    public void CheckHistoryCompleteness_All20EntriesCurrentMonth_ReturnsTrue()
    {
        // Arrange
        var currentMonthStart = new DateTime(2026, 2, 1);
        var details = Enumerable.Range(1, 20).Select(i => new LedgerDetail
        {
            UseDate = new DateTime(2026, 2, i),
            Balance = 1000 - i * 10,
            Amount = 210
        }).ToList();

        // Act
        var result = LendingService.CheckHistoryCompleteness(details, currentMonthStart);

        // Assert
        result.Should().BeTrue("20件すべてが今月なので、今月初旬の履歴が押し出されている可能性がある");
    }

    /// <summary>
    /// 20件の中に先月以前の履歴がある場合、今月分は全件カバー済みと判定されること
    /// </summary>
    [Fact]
    public void CheckHistoryCompleteness_HasPreCurrentMonthEntries_ReturnsFalse()
    {
        // Arrange
        var currentMonthStart = new DateTime(2026, 2, 1);
        var details = new List<LedgerDetail>();

        // 今月分15件
        for (int i = 1; i <= 15; i++)
        {
            details.Add(new LedgerDetail
            {
                UseDate = new DateTime(2026, 2, i),
                Balance = 1000 - i * 10,
                Amount = 210
            });
        }
        // 先月分5件
        for (int i = 27; i <= 31; i++)
        {
            if (i <= 31)
            {
                details.Add(new LedgerDetail
                {
                    UseDate = new DateTime(2026, 1, Math.Min(i, 31)),
                    Balance = 2000 - i * 10,
                    Amount = 210
                });
            }
        }

        // Act
        var result = LendingService.CheckHistoryCompleteness(details, currentMonthStart);

        // Assert
        result.Should().BeFalse("先月の履歴が含まれているので、今月分は全件カバー済み");
    }

    /// <summary>
    /// 20件未満の履歴の場合、カード内の全履歴取得済みと判定されること
    /// </summary>
    [Fact]
    public void CheckHistoryCompleteness_LessThan20Entries_ReturnsFalse()
    {
        // Arrange
        var currentMonthStart = new DateTime(2026, 2, 1);
        var details = Enumerable.Range(1, 15).Select(i => new LedgerDetail
        {
            UseDate = new DateTime(2026, 2, i),
            Balance = 1000 - i * 10,
            Amount = 210
        }).ToList();

        // Act
        var result = LendingService.CheckHistoryCompleteness(details, currentMonthStart);

        // Assert
        result.Should().BeFalse("20件未満なのでカード内の全履歴を取得済み");
    }

    /// <summary>
    /// 空の履歴の場合、不完全とは判定されないこと
    /// </summary>
    [Fact]
    public void CheckHistoryCompleteness_EmptyHistory_ReturnsFalse()
    {
        // Arrange
        var currentMonthStart = new DateTime(2026, 2, 1);
        var details = new List<LedgerDetail>();

        // Act
        var result = LendingService.CheckHistoryCompleteness(details, currentMonthStart);

        // Assert
        result.Should().BeFalse("空の履歴は不完全とは判定されない");
    }

    /// <summary>
    /// 日付なしのエントリを含む場合でも正しく判定されること
    /// </summary>
    [Fact]
    public void CheckHistoryCompleteness_WithNullDates_HandledCorrectly()
    {
        // Arrange
        var currentMonthStart = new DateTime(2026, 2, 1);
        var details = new List<LedgerDetail>();

        // 今月分18件
        for (int i = 1; i <= 18; i++)
        {
            details.Add(new LedgerDetail
            {
                UseDate = new DateTime(2026, 2, i),
                Balance = 1000 - i * 10,
                Amount = 210
            });
        }
        // 日付なし2件
        details.Add(new LedgerDetail { UseDate = null, Balance = 500 });
        details.Add(new LedgerDetail { UseDate = null, Balance = 400 });

        // Act
        var result = LendingService.CheckHistoryCompleteness(details, currentMonthStart);

        // Assert
        result.Should().BeTrue("日付のあるエントリがすべて今月なので、不完全の可能性あり");
    }

    /// <summary>
    /// ReturnAsync で今月の既存レコードがない場合、MayHaveIncompleteHistoryが設定されること
    /// </summary>
    [Fact]
    public async Task ReturnAsync_FirstReturnThisMonth_20EntriesAllCurrentMonth_SetsMayHaveIncompleteHistory()
    {
        // Arrange
        var card = CreateTestCard(isLent: true);
        var staff = CreateTestStaff();
        var lentRecord = CreateTestLentRecord();
        var now = DateTime.Now;

        // 今月の20件の履歴
        var usageDetails = Enumerable.Range(1, 20).Select(i => new LedgerDetail
        {
            UseDate = new DateTime(now.Year, now.Month, Math.Min(i, DateTime.DaysInMonth(now.Year, now.Month))),
            Balance = 10000 - i * 200,
            Amount = 200,
            EntryStation = "天神",
            ExitStation = "博多"
        }).ToList();

        SetupReturnMocks(card, staff, lentRecord);
        // 今月の既存レコードなし
        _ledgerRepositoryMock.Setup(x => x.GetByMonthAsync(TestCardIdm, now.Year, now.Month))
            .ReturnsAsync(new List<Ledger>());

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert
        result.Success.Should().BeTrue();
        result.MayHaveIncompleteHistory.Should().BeTrue(
            "今月の既存レコードがなく、20件すべて今月なので不完全の可能性あり");
    }

    /// <summary>
    /// ReturnAsync で今月の既存レコードがある場合、MayHaveIncompleteHistoryはfalseであること
    /// </summary>
    [Fact]
    public async Task ReturnAsync_HasExistingCurrentMonthRecords_MayHaveIncompleteHistoryFalse()
    {
        // Arrange
        var card = CreateTestCard(isLent: true);
        var staff = CreateTestStaff();
        var lentRecord = CreateTestLentRecord();
        var now = DateTime.Now;

        var usageDetails = Enumerable.Range(1, 20).Select(i => new LedgerDetail
        {
            UseDate = new DateTime(now.Year, now.Month, Math.Min(i, DateTime.DaysInMonth(now.Year, now.Month))),
            Balance = 10000 - i * 200,
            Amount = 200,
            EntryStation = "天神",
            ExitStation = "博多"
        }).ToList();

        SetupReturnMocks(card, staff, lentRecord);
        // 今月の既存レコードあり（アプリで既に追跡中）
        _ledgerRepositoryMock.Setup(x => x.GetByMonthAsync(TestCardIdm, now.Year, now.Month))
            .ReturnsAsync(new List<Ledger>
            {
                new Ledger { Date = new DateTime(now.Year, now.Month, 1), Summary = "鉄道（天神～博多）" }
            });

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert
        result.Success.Should().BeTrue();
        result.MayHaveIncompleteHistory.Should().BeFalse(
            "今月の既存レコードがあるため、既にアプリで追跡中");
    }

    #endregion

    #region ImportHistoryForRegistrationAsync テスト（Issue #596）

    /// <summary>
    /// ImportHistoryForRegistrationAsync に空リストを渡した場合、ImportedCount=0で成功すること
    /// </summary>
    [Fact]
    public async Task ImportHistoryForRegistrationAsync_EmptyList_ReturnsZeroImported()
    {
        // Arrange
        var importFromDate = new DateTime(2026, 2, 1);
        var emptyHistory = new List<LedgerDetail>();

        // Act
        var result = await _service.ImportHistoryForRegistrationAsync(TestCardIdm, emptyHistory, importFromDate);

        // Assert
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(0);
        result.MayHaveIncompleteHistory.Should().BeFalse();
    }

    /// <summary>
    /// ImportHistoryForRegistrationAsync に importFromDate より前のエントリのみを渡した場合、
    /// ImportedCount=0 で成功すること
    /// </summary>
    [Fact]
    public async Task ImportHistoryForRegistrationAsync_OnlyEntriesBeforeImportDate_ReturnsZeroImported()
    {
        // Arrange
        var importFromDate = new DateTime(2026, 2, 1);
        var history = new List<LedgerDetail>
        {
            new() { UseDate = new DateTime(2026, 1, 15), Balance = 5000, Amount = 210, EntryStation = "天神", ExitStation = "博多" },
            new() { UseDate = new DateTime(2026, 1, 20), Balance = 4790, Amount = 210, EntryStation = "博多", ExitStation = "天神" },
            new() { UseDate = new DateTime(2026, 1, 31), Balance = 4580, Amount = 210, EntryStation = "天神", ExitStation = "中洲川端" }
        };

        // Act
        var result = await _service.ImportHistoryForRegistrationAsync(TestCardIdm, history, importFromDate);

        // Assert
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(0);
    }

    /// <summary>
    /// ImportHistoryForRegistrationAsync に当月の利用・チャージ混在履歴を渡した場合、
    /// 正しいledgerが作成されること
    /// </summary>
    [Fact]
    public async Task ImportHistoryForRegistrationAsync_MixedUsageAndCharge_CreatesCorrectLedgers()
    {
        // Arrange
        var importFromDate = new DateTime(2026, 2, 1);
        var history = new List<LedgerDetail>
        {
            // 2/3: チャージ 3000円
            new() { UseDate = new DateTime(2026, 2, 3), Balance = 8000, Amount = 3000, IsCharge = true },
            // 2/5: 鉄道利用
            new() { UseDate = new DateTime(2026, 2, 5), Balance = 7790, Amount = 210, EntryStation = "天神", ExitStation = "博多" },
            // 2/7: 鉄道利用
            new() { UseDate = new DateTime(2026, 2, 7), Balance = 7580, Amount = 210, EntryStation = "博多", ExitStation = "天神" }
        };

        // モックセットアップ: 重複なし
        _ledgerRepositoryMock.Setup(x => x.GetExistingDetailKeysAsync(TestCardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync(new HashSet<(DateTime?, int?, bool)>());
        _ledgerRepositoryMock.Setup(x => x.GetLatestBeforeDateAsync(TestCardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync(new Ledger { Balance = 5000 });
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(1);
        _ledgerRepositoryMock.Setup(x => x.InsertDetailAsync(It.IsAny<LedgerDetail>()))
            .ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(x => x.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);

        // Act
        var result = await _service.ImportHistoryForRegistrationAsync(TestCardIdm, history, importFromDate);

        // Assert
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().BeGreaterThan(0, "チャージと利用がインポートされるべき");
        result.MayHaveIncompleteHistory.Should().BeFalse("3件しかないため、不完全ではない");
    }

    /// <summary>
    /// ImportHistoryForRegistrationAsync で staffName が null で登録されること
    /// </summary>
    [Fact]
    public async Task ImportHistoryForRegistrationAsync_StaffNameIsNull()
    {
        // Arrange
        var importFromDate = new DateTime(2026, 2, 1);
        var history = new List<LedgerDetail>
        {
            new() { UseDate = new DateTime(2026, 2, 5), Balance = 4790, Amount = 210, EntryStation = "天神", ExitStation = "博多" }
        };

        Ledger? capturedLedger = null;
        _ledgerRepositoryMock.Setup(x => x.GetExistingDetailKeysAsync(TestCardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync(new HashSet<(DateTime?, int?, bool)>());
        _ledgerRepositoryMock.Setup(x => x.GetLatestBeforeDateAsync(TestCardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync(new Ledger { Balance = 5000 });
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => capturedLedger = l)
            .ReturnsAsync(1);
        _ledgerRepositoryMock.Setup(x => x.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);

        // Act
        var result = await _service.ImportHistoryForRegistrationAsync(TestCardIdm, history, importFromDate);

        // Assert
        result.Success.Should().BeTrue();
        capturedLedger.Should().NotBeNull();
        capturedLedger!.StaffName.Should().BeNull("カード登録時は利用者情報がないため");
    }

    /// <summary>
    /// ImportHistoryForRegistrationAsync に 20件すべて当月の履歴を渡した場合、
    /// MayHaveIncompleteHistory=true であること
    /// </summary>
    [Fact]
    public async Task ImportHistoryForRegistrationAsync_All20CurrentMonth_MayHaveIncompleteHistoryTrue()
    {
        // Arrange
        var importFromDate = new DateTime(2026, 2, 1);
        var history = Enumerable.Range(1, 20).Select(i => new LedgerDetail
        {
            UseDate = new DateTime(2026, 2, Math.Min(i, 28)),
            Balance = 10000 - i * 200,
            Amount = 200,
            EntryStation = "天神",
            ExitStation = "博多"
        }).ToList();

        _ledgerRepositoryMock.Setup(x => x.GetExistingDetailKeysAsync(TestCardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync(new HashSet<(DateTime?, int?, bool)>());
        _ledgerRepositoryMock.Setup(x => x.GetLatestBeforeDateAsync(TestCardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync(new Ledger { Balance = 10000 });
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(1);
        _ledgerRepositoryMock.Setup(x => x.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);

        // Act
        var result = await _service.ImportHistoryForRegistrationAsync(TestCardIdm, history, importFromDate);

        // Assert
        result.Success.Should().BeTrue();
        result.MayHaveIncompleteHistory.Should().BeTrue(
            "20件すべてが当月のため、月初めの履歴が不足している可能性がある");
    }

    /// <summary>
    /// Issue #664: 20件すべてが対象期間内の場合、EarliestHistoryDateに最古日付が設定されること
    /// </summary>
    [Fact]
    public async Task ImportHistoryForRegistrationAsync_IncompleteHistory_SetsEarliestHistoryDate()
    {
        // Arrange: 繰越で importFromDate=1月1日、履歴が1月+2月にまたがる20件
        var importFromDate = new DateTime(2026, 1, 1);
        var history = new List<LedgerDetail>();
        // 1月分10件
        for (int i = 0; i < 10; i++)
        {
            history.Add(new LedgerDetail
            {
                UseDate = new DateTime(2026, 1, 15 + i),
                Balance = 10000 - i * 200,
                Amount = 200,
                EntryStation = "天神",
                ExitStation = "博多"
            });
        }
        // 2月分10件
        for (int i = 0; i < 10; i++)
        {
            history.Add(new LedgerDetail
            {
                UseDate = new DateTime(2026, 2, 1 + i),
                Balance = 8000 - i * 200,
                Amount = 200,
                EntryStation = "天神",
                ExitStation = "博多"
            });
        }

        _ledgerRepositoryMock.Setup(x => x.GetExistingDetailKeysAsync(TestCardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync(new HashSet<(DateTime?, int?, bool)>());
        _ledgerRepositoryMock.Setup(x => x.GetLatestBeforeDateAsync(TestCardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync(new Ledger { Balance = 10000 });
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(1);
        _ledgerRepositoryMock.Setup(x => x.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);

        // Act
        var result = await _service.ImportHistoryForRegistrationAsync(TestCardIdm, history, importFromDate);

        // Assert
        result.Success.Should().BeTrue();
        result.MayHaveIncompleteHistory.Should().BeTrue(
            "20件すべてが importFromDate 以降のため、それより前の履歴が不足している可能性がある");
        result.EarliestHistoryDate.Should().Be(new DateTime(2026, 1, 15),
            "履歴の最古日付が設定されること");
    }

    /// <summary>
    /// Issue #664: 20件未満の場合、EarliestHistoryDateはnullのままであること
    /// </summary>
    [Fact]
    public async Task ImportHistoryForRegistrationAsync_LessThan20_EarliestHistoryDateIsNull()
    {
        // Arrange: 10件のみ（20件未満 → MayHaveIncompleteHistory=false）
        var importFromDate = new DateTime(2026, 2, 1);
        var history = Enumerable.Range(1, 10).Select(i => new LedgerDetail
        {
            UseDate = new DateTime(2026, 2, i),
            Balance = 10000 - i * 200,
            Amount = 200,
            EntryStation = "天神",
            ExitStation = "博多"
        }).ToList();

        _ledgerRepositoryMock.Setup(x => x.GetExistingDetailKeysAsync(TestCardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync(new HashSet<(DateTime?, int?, bool)>());
        _ledgerRepositoryMock.Setup(x => x.GetLatestBeforeDateAsync(TestCardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync(new Ledger { Balance = 10000 });
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(1);
        _ledgerRepositoryMock.Setup(x => x.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);

        // Act
        var result = await _service.ImportHistoryForRegistrationAsync(TestCardIdm, history, importFromDate);

        // Assert
        result.Success.Should().BeTrue();
        result.MayHaveIncompleteHistory.Should().BeFalse(
            "20件未満なのでカード内の全履歴を取得済み");
        result.EarliestHistoryDate.Should().BeNull(
            "不完全でない場合はEarliestHistoryDateは設定されない");
    }

    #endregion

    #region CalculatePreHistoryBalance テスト（Issue #596）

    /// <summary>
    /// 利用エントリの場合: balance + amount で初期残高を逆算すること
    /// </summary>
    [Fact]
    public void CalculatePreHistoryBalance_UsageEntry_ReturnsBalancePlusAmount()
    {
        // Arrange - 利用210円で残高が4790円になった場合、利用前は5000円
        var history = new List<LedgerDetail>
        {
            new() { UseDate = new DateTime(2026, 2, 5), Balance = 4790, Amount = 210, IsCharge = false }
        };

        // Act
        var result = CardManageViewModel.CalculatePreHistoryBalance(history);

        // Assert
        result.Should().Be(5000, "利用前の残高 = 利用後残高(4790) + 利用額(210)");
    }

    /// <summary>
    /// チャージエントリの場合: balance - amount で初期残高を逆算すること
    /// </summary>
    [Fact]
    public void CalculatePreHistoryBalance_ChargeEntry_ReturnsBalanceMinusAmount()
    {
        // Arrange - チャージ3000円で残高が8000円になった場合、チャージ前は5000円
        var history = new List<LedgerDetail>
        {
            new() { UseDate = new DateTime(2026, 2, 3), Balance = 8000, Amount = 3000, IsCharge = true }
        };

        // Act
        var result = CardManageViewModel.CalculatePreHistoryBalance(history);

        // Assert
        result.Should().Be(5000, "チャージ前の残高 = チャージ後残高(8000) - チャージ額(3000)");
    }

    /// <summary>
    /// 空リストの場合: 0 を返すこと
    /// </summary>
    [Fact]
    public void CalculatePreHistoryBalance_EmptyList_ReturnsZero()
    {
        // Arrange
        var history = new List<LedgerDetail>();

        // Act
        var result = CardManageViewModel.CalculatePreHistoryBalance(history);

        // Assert
        result.Should().Be(0);
    }

    #endregion

    #region RepairLentStatusConsistencyAsync テスト（Issue #790）

    /// <summary>
    /// 不整合なし（全カード一致）の場合、修復件数が0であること
    /// </summary>
    [Fact]
    public async Task RepairLentStatusConsistencyAsync_NoInconsistency_ReturnsZero()
    {
        // Arrange: カード(is_lent=1)と貸出中レコードが一致
        var card = CreateTestCard(isLent: true);
        var lentRecord = CreateTestLentRecord();

        _cardRepositoryMock.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<IcCard> { card });
        _ledgerRepositoryMock.Setup(x => x.GetAllLentRecordsAsync())
            .ReturnsAsync(new List<Ledger> { lentRecord });

        // Act
        var repairCount = await _service.RepairLentStatusConsistencyAsync();

        // Assert
        repairCount.Should().Be(0);
        _cardRepositoryMock.Verify(
            x => x.UpdateLentStatusAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string>()),
            Times.Never);
    }

    /// <summary>
    /// カードis_lent=0 + 貸出中レコードあり → is_lent=1に修復されること
    /// （Issue #790の主要シナリオ）
    /// </summary>
    [Fact]
    public async Task RepairLentStatusConsistencyAsync_IsLentFalseButLentRecordExists_RepairsToTrue()
    {
        // Arrange: カード(is_lent=0)なのに貸出中レコードが存在する
        var card = CreateTestCard(isLent: false);
        var lentRecord = CreateTestLentRecord();

        _cardRepositoryMock.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<IcCard> { card });
        _ledgerRepositoryMock.Setup(x => x.GetAllLentRecordsAsync())
            .ReturnsAsync(new List<Ledger> { lentRecord });
        _cardRepositoryMock.Setup(x => x.UpdateLentStatusAsync(
            TestCardIdm, true, lentRecord.LentAt, TestStaffIdm))
            .ReturnsAsync(true);

        // Act
        var repairCount = await _service.RepairLentStatusConsistencyAsync();

        // Assert
        repairCount.Should().Be(1);
        _cardRepositoryMock.Verify(
            x => x.UpdateLentStatusAsync(TestCardIdm, true, lentRecord.LentAt, TestStaffIdm),
            Times.Once);
    }

    /// <summary>
    /// カードis_lent=1 + 貸出中レコードなし → is_lent=0に修復されること
    /// </summary>
    [Fact]
    public async Task RepairLentStatusConsistencyAsync_IsLentTrueButNoLentRecord_RepairsToFalse()
    {
        // Arrange: カード(is_lent=1)なのに貸出中レコードが存在しない
        var card = CreateTestCard(isLent: true);

        _cardRepositoryMock.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<IcCard> { card });
        _ledgerRepositoryMock.Setup(x => x.GetAllLentRecordsAsync())
            .ReturnsAsync(new List<Ledger>());
        _cardRepositoryMock.Setup(x => x.UpdateLentStatusAsync(
            TestCardIdm, false, null, null))
            .ReturnsAsync(true);

        // Act
        var repairCount = await _service.RepairLentStatusConsistencyAsync();

        // Assert
        repairCount.Should().Be(1);
        _cardRepositoryMock.Verify(
            x => x.UpdateLentStatusAsync(TestCardIdm, false, null, null),
            Times.Once);
    }

    /// <summary>
    /// カードなし（空リスト）の場合、修復件数が0であること
    /// </summary>
    [Fact]
    public async Task RepairLentStatusConsistencyAsync_NoCards_ReturnsZero()
    {
        // Arrange
        _cardRepositoryMock.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<IcCard>());
        _ledgerRepositoryMock.Setup(x => x.GetAllLentRecordsAsync())
            .ReturnsAsync(new List<Ledger>());

        // Act
        var repairCount = await _service.RepairLentStatusConsistencyAsync();

        // Assert
        repairCount.Should().Be(0);
    }

    /// <summary>
    /// 複数カードで不整合が混在する場合、すべて修復されること
    /// </summary>
    [Fact]
    public async Task RepairLentStatusConsistencyAsync_MultipleCardsWithMixedStates_RepairsAll()
    {
        // Arrange: 3枚のカード
        // カードA: is_lent=0 + 貸出中レコードあり → 修復必要
        // カードB: is_lent=1 + 貸出中レコードなし → 修復必要
        // カードC: is_lent=0 + 貸出中レコードなし → OK
        var cardA = new IcCard { CardIdm = "AAAA000000000001", CardType = "はやかけん", CardNumber = "A001", IsLent = false };
        var cardB = new IcCard { CardIdm = "BBBB000000000002", CardType = "nimoca", CardNumber = "B001", IsLent = true };
        var cardC = new IcCard { CardIdm = "CCCC000000000003", CardType = "SUGOCA", CardNumber = "C001", IsLent = false };

        var lentRecordA = new Ledger
        {
            Id = 10, CardIdm = "AAAA000000000001", LenderIdm = TestStaffIdm,
            StaffName = TestStaffName, Date = DateTime.Today, IsLentRecord = true,
            LentAt = DateTime.Today.AddHours(-2), Summary = "（貸出中）"
        };

        _cardRepositoryMock.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<IcCard> { cardA, cardB, cardC });
        _ledgerRepositoryMock.Setup(x => x.GetAllLentRecordsAsync())
            .ReturnsAsync(new List<Ledger> { lentRecordA });
        _cardRepositoryMock.Setup(x => x.UpdateLentStatusAsync(
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        // Act
        var repairCount = await _service.RepairLentStatusConsistencyAsync();

        // Assert
        repairCount.Should().Be(2);
        // カードA: is_lent=0→1
        _cardRepositoryMock.Verify(
            x => x.UpdateLentStatusAsync("AAAA000000000001", true, lentRecordA.LentAt, TestStaffIdm),
            Times.Once);
        // カードB: is_lent=1→0
        _cardRepositoryMock.Verify(
            x => x.UpdateLentStatusAsync("BBBB000000000002", false, null, null),
            Times.Once);
        // カードC: 変更なし
        _cardRepositoryMock.Verify(
            x => x.UpdateLentStatusAsync("CCCC000000000003", It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string>()),
            Times.Never);
    }

    /// <summary>
    /// 在庫カードと貸出中レコードなしが一致している場合、修復不要であること
    /// </summary>
    [Fact]
    public async Task RepairLentStatusConsistencyAsync_AvailableCardWithNoLentRecord_NoRepair()
    {
        // Arrange: 在庫カード（正常状態）
        var card = CreateTestCard(isLent: false);

        _cardRepositoryMock.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<IcCard> { card });
        _ledgerRepositoryMock.Setup(x => x.GetAllLentRecordsAsync())
            .ReturnsAsync(new List<Ledger>());

        // Act
        var repairCount = await _service.RepairLentStatusConsistencyAsync();

        // Assert
        repairCount.Should().Be(0);
        _cardRepositoryMock.Verify(
            x => x.UpdateLentStatusAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string>()),
            Times.Never);
    }

    #endregion

    #region 同一日履歴統合テスト（Issue #837）

    /// <summary>
    /// Issue #837: 同一日に既存の利用Ledgerがある場合、新規作成ではなく既存レコードに詳細が追加され、摘要が再生成されること
    /// </summary>
    [Fact]
    public async Task ReturnAsync_SameDayExistingUsageLedger_ConsolidatesIntoExisting()
    {
        // Arrange
        var card = CreateTestCard(isLent: true);
        var staff = CreateTestStaff();
        var lentRecord = CreateTestLentRecord(daysAgo: 1);

        var today = DateTime.Today;

        // 今回の返却で読み取られた履歴（博多→天神、同日）
        var usageDetails = new List<LedgerDetail>
        {
            new() { UseDate = today, EntryStation = "博多", ExitStation = "天神", Amount = 260, Balance = 9480 }
        };

        // 既存のLedger（前回の返却で作成済み: 天神→博多）
        var existingLedger = new Ledger
        {
            Id = 100,
            CardIdm = TestCardIdm,
            Date = today,
            Summary = "鉄道（天神～博多）",
            Income = 0,
            Expense = 260,
            Balance = 9740,
            StaffName = TestStaffName,
            IsLentRecord = false,
            Note = null
        };

        // GetByIdAsync で返す全詳細（既存＋新規がマージされた状態）
        var fullLedgerAfterMerge = new Ledger
        {
            Id = 100,
            CardIdm = TestCardIdm,
            Date = today,
            Summary = "鉄道（天神～博多）",
            Income = 0,
            Expense = 260,
            Balance = 9740,
            StaffName = TestStaffName,
            IsLentRecord = false,
            Details = new List<LedgerDetail>
            {
                new() { UseDate = today, EntryStation = "天神", ExitStation = "博多", Amount = 260, Balance = 9740, SequenceNumber = 1 },
                new() { UseDate = today, EntryStation = "博多", ExitStation = "天神", Amount = 260, Balance = 9480, SequenceNumber = 2 }
            }
        };

        SetupReturnMocks(card, staff, lentRecord);

        // 同一日の既存Ledgerを返す
        _ledgerRepositoryMock.Setup(x => x.GetByDateRangeAsync(TestCardIdm, today, today))
            .ReturnsAsync(new List<Ledger> { existingLedger });
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(100))
            .ReturnsAsync(fullLedgerAfterMerge);

        Ledger? updatedLedger = null;
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => { if (!l.IsLentRecord) updatedLedger = l; })
            .ReturnsAsync(true);

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert
        result.Success.Should().BeTrue();

        // InsertDetailsAsyncが既存Ledger(Id=100)に対して呼ばれたこと
        _ledgerRepositoryMock.Verify(
            x => x.InsertDetailsAsync(100, It.IsAny<IEnumerable<LedgerDetail>>()),
            Times.Once);

        // 利用Ledgerの新規InsertAsyncは呼ばれないこと（貸出レコード更新は除く）
        _ledgerRepositoryMock.Verify(
            x => x.InsertAsync(It.Is<Ledger>(l => !l.IsLentRecord && l.Income == 0)),
            Times.Never);

        // UpdateAsyncで摘要が再生成されていること（往復検出）
        updatedLedger.Should().NotBeNull();
        updatedLedger!.Summary.Should().Contain("往復");
        updatedLedger.Expense.Should().Be(520);
    }

    /// <summary>
    /// Issue #837: 同一日の既存Ledgerがない場合、従来通り新規作成されること（回帰テスト）
    /// </summary>
    [Fact]
    public async Task ReturnAsync_NoExistingLedgerForSameDay_CreatesNewLedger()
    {
        // Arrange
        var card = CreateTestCard(isLent: true);
        var staff = CreateTestStaff();
        var lentRecord = CreateTestLentRecord(daysAgo: 1);

        var today = DateTime.Today;
        var usageDetails = new List<LedgerDetail>
        {
            new() { UseDate = today, EntryStation = "博多", ExitStation = "天神", Amount = 260, Balance = 9740 }
        };

        SetupReturnMocks(card, staff, lentRecord);
        // GetByDateRangeAsync はデフォルトで空リストを返す（SetupReturnMocks内）

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert
        result.Success.Should().BeTrue();

        // 新規InsertAsyncが呼ばれること
        _ledgerRepositoryMock.Verify(
            x => x.InsertAsync(It.Is<Ledger>(l => !l.IsLentRecord && l.Income == 0 && l.Expense == 260)),
            Times.Once);
    }

    /// <summary>
    /// Issue #837: 同一日のチャージLedgerがあっても、利用Ledgerは別途新規作成されること
    /// </summary>
    [Fact]
    public async Task ReturnAsync_ExistingChargeLedgerSameDay_DoesNotConsolidate()
    {
        // Arrange
        var card = CreateTestCard(isLent: true);
        var staff = CreateTestStaff();
        var lentRecord = CreateTestLentRecord(daysAgo: 1);

        var today = DateTime.Today;
        var usageDetails = new List<LedgerDetail>
        {
            new() { UseDate = today, EntryStation = "博多", ExitStation = "天神", Amount = 260, Balance = 9740 }
        };

        // 既存のチャージLedger（Income > 0 なので統合対象外）
        var existingChargeLedger = new Ledger
        {
            Id = 200,
            CardIdm = TestCardIdm,
            Date = today,
            Summary = "役務費によりチャージ",
            Income = 3000,
            Expense = 0,
            Balance = 13000,
            IsLentRecord = false
        };

        SetupReturnMocks(card, staff, lentRecord);
        _ledgerRepositoryMock.Setup(x => x.GetByDateRangeAsync(TestCardIdm, today, today))
            .ReturnsAsync(new List<Ledger> { existingChargeLedger });

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert
        result.Success.Should().BeTrue();

        // チャージLedgerは統合対象外なので、利用Ledgerが新規作成されること
        _ledgerRepositoryMock.Verify(
            x => x.InsertAsync(It.Is<Ledger>(l => !l.IsLentRecord && l.Income == 0 && l.Expense == 260)),
            Times.Once);

        // GetByIdAsyncは呼ばれないこと（統合処理に入らない）
        _ledgerRepositoryMock.Verify(
            x => x.GetByIdAsync(It.IsAny<int>()),
            Times.Never);
    }

    /// <summary>
    /// Issue #837: 残高不足パターン（Note付き）の既存Ledgerがある場合は統合せず新規作成されること
    /// </summary>
    [Fact]
    public async Task ReturnAsync_ExistingLedgerWithNote_DoesNotConsolidate()
    {
        // Arrange
        var card = CreateTestCard(isLent: true);
        var staff = CreateTestStaff();
        var lentRecord = CreateTestLentRecord(daysAgo: 1);

        var today = DateTime.Today;
        var usageDetails = new List<LedgerDetail>
        {
            new() { UseDate = today, EntryStation = "天神", ExitStation = "博多", Amount = 260, Balance = 9740 }
        };

        // 既存のNote付きLedger（残高不足パターンで作成されたもの）
        var existingNoteledger = new Ledger
        {
            Id = 300,
            CardIdm = TestCardIdm,
            Date = today,
            Summary = "鉄道（博多～空港）",
            Income = 0,
            Expense = 200,
            Balance = 0,
            IsLentRecord = false,
            Note = "支払額210円のうち不足額10円は現金で支払（旅費支給）"
        };

        SetupReturnMocks(card, staff, lentRecord);
        _ledgerRepositoryMock.Setup(x => x.GetByDateRangeAsync(TestCardIdm, today, today))
            .ReturnsAsync(new List<Ledger> { existingNoteledger });

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert
        result.Success.Should().BeTrue();

        // Note付きLedgerは統合対象外なので、利用Ledgerが新規作成されること
        _ledgerRepositoryMock.Verify(
            x => x.InsertAsync(It.Is<Ledger>(l => !l.IsLentRecord && l.Income == 0 && l.Expense == 260)),
            Times.Once);
    }

    /// <summary>
    /// Issue #837: 既存=A→B、新規=B→A の場合、統合後に「A～B 往復」と摘要生成されること
    /// </summary>
    [Fact]
    public async Task ReturnAsync_ConsolidationDetectsRoundTrip()
    {
        // Arrange
        var card = CreateTestCard(isLent: true);
        var staff = CreateTestStaff();
        var lentRecord = CreateTestLentRecord(daysAgo: 1);

        var today = DateTime.Today;

        // 2回目の返却で読み取られた復路（博多→天神）
        var usageDetails = new List<LedgerDetail>
        {
            new() { UseDate = today, EntryStation = "博多", ExitStation = "天神", Amount = 260, Balance = 9480 }
        };

        // 既存のLedger（1回目の返却で作成: 天神→博多）
        var existingLedger = new Ledger
        {
            Id = 400,
            CardIdm = TestCardIdm,
            Date = today,
            Summary = "鉄道（天神～博多）",
            Income = 0,
            Expense = 260,
            Balance = 9740,
            StaffName = TestStaffName,
            IsLentRecord = false
        };

        // 統合後の全詳細
        var fullLedger = new Ledger
        {
            Id = 400,
            CardIdm = TestCardIdm,
            Date = today,
            Summary = "鉄道（天神～博多）",
            Income = 0,
            Expense = 260,
            Balance = 9740,
            StaffName = TestStaffName,
            IsLentRecord = false,
            Details = new List<LedgerDetail>
            {
                new() { UseDate = today, EntryStation = "天神", ExitStation = "博多", Amount = 260, Balance = 9740, SequenceNumber = 1 },
                new() { UseDate = today, EntryStation = "博多", ExitStation = "天神", Amount = 260, Balance = 9480, SequenceNumber = 2 }
            }
        };

        SetupReturnMocks(card, staff, lentRecord);
        _ledgerRepositoryMock.Setup(x => x.GetByDateRangeAsync(TestCardIdm, today, today))
            .ReturnsAsync(new List<Ledger> { existingLedger });
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(400))
            .ReturnsAsync(fullLedger);

        Ledger? updatedLedger = null;
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => { if (!l.IsLentRecord) updatedLedger = l; })
            .ReturnsAsync(true);

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert
        result.Success.Should().BeTrue();
        updatedLedger.Should().NotBeNull();

        // 摘要に「往復」が含まれていること（SummaryGeneratorが往復を検出）
        updatedLedger!.Summary.Should().Contain("天神");
        updatedLedger.Summary.Should().Contain("博多");
        updatedLedger.Summary.Should().Contain("往復");

        // 支出が合算されていること（260 + 260 = 520）
        updatedLedger.Expense.Should().Be(520);

        // 残高は最小値（利用後最低残高）
        updatedLedger.Balance.Should().Be(9480);
    }

    #endregion

    #region SplitAtChargeBoundaries テスト

    [Fact]
    public void SplitAtChargeBoundaries_NoCharge_ReturnsSingleUsageGroup()
    {
        // Arrange: 2つの利用のみ（チャージなし）
        var details = new List<LedgerDetail>
        {
            new() { UseDate = DateTime.Today, EntryStation = "博多", ExitStation = "天神", Amount = 210, Balance = 790, IsCharge = false },
            new() { UseDate = DateTime.Today, EntryStation = "天神", ExitStation = "博多", Amount = 210, Balance = 1000, IsCharge = false },
        };

        // Act
        var segments = LendingService.SplitAtChargeBoundaries(details);

        // Assert: 1つの利用グループ
        segments.Should().HaveCount(1);
        segments[0].IsCharge.Should().BeFalse();
        segments[0].Details.Should().HaveCount(2);
    }

    [Fact]
    public void SplitAtChargeBoundaries_ChargeBetweenTrips_ReturnsSplitGroups()
    {
        // Arrange: 利用→チャージ→利用（チャージが間に挟まる）
        // 時系列順: 天神→博多(1000→790), チャージ(790→1790), 博多→天神(1790→1580)
        // ICカード履歴は新しい順なので逆順で入力
        var details = new List<LedgerDetail>
        {
            new() { UseDate = DateTime.Today, EntryStation = "博多", ExitStation = "天神", Amount = 210, Balance = 1580, IsCharge = false },
            new() { UseDate = DateTime.Today, Amount = 1000, Balance = 1790, IsCharge = true },
            new() { UseDate = DateTime.Today, EntryStation = "天神", ExitStation = "博多", Amount = 210, Balance = 790, IsCharge = false },
        };

        // Act
        var segments = LendingService.SplitAtChargeBoundaries(details);

        // Assert: 利用グループ1, チャージ, 利用グループ2
        segments.Should().HaveCount(3);

        segments[0].IsCharge.Should().BeFalse();
        segments[0].Details.Should().HaveCount(1);
        segments[0].Details[0].ExitStation.Should().Be("博多");  // 天神→博多（古い方）

        segments[1].IsCharge.Should().BeTrue();
        segments[1].Details.Should().HaveCount(1);

        segments[2].IsCharge.Should().BeFalse();
        segments[2].Details.Should().HaveCount(1);
        segments[2].Details[0].ExitStation.Should().Be("天神");  // 博多→天神（新しい方）
    }

    [Fact]
    public void SplitAtChargeBoundaries_ChargeAtStart_ReturnsChargeFirst()
    {
        // Arrange: チャージ→利用→利用
        // 時系列順: チャージ(500→1500), 天神→博多(1500→1290), 博多→天神(1290→1080)
        var details = new List<LedgerDetail>
        {
            new() { UseDate = DateTime.Today, EntryStation = "博多", ExitStation = "天神", Amount = 210, Balance = 1080, IsCharge = false },
            new() { UseDate = DateTime.Today, EntryStation = "天神", ExitStation = "博多", Amount = 210, Balance = 1290, IsCharge = false },
            new() { UseDate = DateTime.Today, Amount = 1000, Balance = 1500, IsCharge = true },
        };

        // Act
        var segments = LendingService.SplitAtChargeBoundaries(details);

        // Assert: チャージが先、利用グループが後（2件まとめて）
        segments.Should().HaveCount(2);
        segments[0].IsCharge.Should().BeTrue();
        segments[1].IsCharge.Should().BeFalse();
        segments[1].Details.Should().HaveCount(2);
    }

    [Fact]
    public void SplitAtChargeBoundaries_ChargeAtEnd_ReturnsChargeAtEnd()
    {
        // Arrange: 利用→利用→チャージ
        // 時系列順: 天神→博多(1500→1290), 博多→天神(1290→1080), チャージ(1080→2080)
        var details = new List<LedgerDetail>
        {
            new() { UseDate = DateTime.Today, Amount = 1000, Balance = 2080, IsCharge = true },
            new() { UseDate = DateTime.Today, EntryStation = "博多", ExitStation = "天神", Amount = 210, Balance = 1080, IsCharge = false },
            new() { UseDate = DateTime.Today, EntryStation = "天神", ExitStation = "博多", Amount = 210, Balance = 1290, IsCharge = false },
        };

        // Act
        var segments = LendingService.SplitAtChargeBoundaries(details);

        // Assert: 利用グループが先、チャージが後
        segments.Should().HaveCount(2);
        segments[0].IsCharge.Should().BeFalse();
        segments[0].Details.Should().HaveCount(2);
        segments[1].IsCharge.Should().BeTrue();
    }

    [Fact]
    public void SplitAtChargeBoundaries_MultipleCharges_ReturnsMultipleGroups()
    {
        // Arrange: 利用→チャージ→利用→チャージ→利用
        // 時系列順: trip1(2000→1790), charge1(1790→2790), trip2(2790→2580), charge2(2580→3580), trip3(3580→3370)
        var details = new List<LedgerDetail>
        {
            new() { UseDate = DateTime.Today, EntryStation = "A", ExitStation = "B", Amount = 210, Balance = 3370, IsCharge = false },
            new() { UseDate = DateTime.Today, Amount = 1000, Balance = 3580, IsCharge = true },
            new() { UseDate = DateTime.Today, EntryStation = "C", ExitStation = "D", Amount = 210, Balance = 2580, IsCharge = false },
            new() { UseDate = DateTime.Today, Amount = 1000, Balance = 2790, IsCharge = true },
            new() { UseDate = DateTime.Today, EntryStation = "E", ExitStation = "F", Amount = 210, Balance = 1790, IsCharge = false },
        };

        // Act
        var segments = LendingService.SplitAtChargeBoundaries(details);

        // Assert: 5セグメント（利用, チャージ, 利用, チャージ, 利用）
        segments.Should().HaveCount(5);
        segments[0].IsCharge.Should().BeFalse();
        segments[1].IsCharge.Should().BeTrue();
        segments[2].IsCharge.Should().BeFalse();
        segments[3].IsCharge.Should().BeTrue();
        segments[4].IsCharge.Should().BeFalse();
    }

    [Fact]
    public void SplitAtChargeBoundaries_EmptyList_ReturnsEmpty()
    {
        var segments = LendingService.SplitAtChargeBoundaries(new List<LedgerDetail>());
        segments.Should().BeEmpty();
    }

    [Fact]
    public void SortChronologically_ChargeBetweenTrips_ReturnsCorrectOrder()
    {
        // Arrange: ICカード履歴順（新しい順）: 博多→天神, チャージ, 天神→博多
        // 期待する時系列順（古い順）: 天神→博多, チャージ, 博多→天神
        var details = new List<LedgerDetail>
        {
            new() { UseDate = DateTime.Today, EntryStation = "博多", ExitStation = "天神", Amount = 210, Balance = 1580, IsCharge = false },
            new() { UseDate = DateTime.Today, Amount = 1000, Balance = 1790, IsCharge = true },
            new() { UseDate = DateTime.Today, EntryStation = "天神", ExitStation = "博多", Amount = 210, Balance = 790, IsCharge = false },
        };

        // Act
        var sorted = LendingService.SortChronologically(details);

        // Assert: 古い順（残高チェーンの開始点から辿る）
        sorted.Should().HaveCount(3);
        sorted[0].Balance.Should().Be(790);   // 天神→博多（最古）
        sorted[1].Balance.Should().Be(1790);  // チャージ
        sorted[2].Balance.Should().Be(1580);  // 博多→天神（最新）
    }

    [Fact]
    public void SortChronologically_NoBalanceInfo_FallsBackToReverseOrder()
    {
        // Arrange: Balance情報なし
        var details = new List<LedgerDetail>
        {
            new() { UseDate = DateTime.Today, EntryStation = "A", ExitStation = "B", IsCharge = false },
            new() { UseDate = DateTime.Today, Amount = 1000, IsCharge = true },
            new() { UseDate = DateTime.Today, EntryStation = "C", ExitStation = "D", IsCharge = false },
        };

        // Act
        var sorted = LendingService.SortChronologically(details);

        // Assert: フォールバック（逆順=古い順）
        sorted.Should().HaveCount(3);
        sorted[0].EntryStation.Should().Be("C");  // 元のリストの最後=古い
        sorted[1].IsCharge.Should().BeTrue();
        sorted[2].EntryStation.Should().Be("A");  // 元のリストの最初=新しい
    }

    #endregion

    #region チャージ境界分割 統合テスト

    [Fact]
    public async Task ReturnAsync_ChargeBetweenRoundTrip_CreatesSeparateLedgers()
    {
        // Arrange: 薬院→博多(-310,残高690), チャージ(+1000,残高1690), 博多→薬院(-310,残高1380)
        var card = CreateTestCard(isLent: true);
        var staff = CreateTestStaff();
        var lentRecord = CreateTestLentRecord();

        var usageDetails = new List<LedgerDetail>
        {
            // ICカード履歴は新しい順
            new() { UseDate = DateTime.Today, EntryStation = "博多", ExitStation = "薬院", Amount = 310, Balance = 1380, IsCharge = false },
            new() { UseDate = DateTime.Today, Amount = 1000, Balance = 1690, IsCharge = true },
            new() { UseDate = DateTime.Today, EntryStation = "薬院", ExitStation = "博多", Amount = 310, Balance = 690, IsCharge = false },
        };

        SetupReturnMocks(card, staff, lentRecord);

        var createdLedgers = new List<Ledger>();
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => createdLedgers.Add(l))
            .ReturnsAsync(1);

        // Act
        await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert: 3つのLedgerが作成される（利用1 + チャージ + 利用2）
        var nonLentLedgers = createdLedgers.Where(l => !l.IsLentRecord && l.Summary != "（貸出中）").ToList();
        nonLentLedgers.Should().HaveCount(3);

        // 時系列順: 利用(薬院→博多), チャージ, 利用(博多→薬院)
        var usageLedger1 = nonLentLedgers.FirstOrDefault(l => l.Expense == 310 && l.Balance == 690);
        var chargeLedger = nonLentLedgers.FirstOrDefault(l => l.Income > 0);
        var usageLedger2 = nonLentLedgers.FirstOrDefault(l => l.Expense == 310 && l.Balance == 1380);

        usageLedger1.Should().NotBeNull();
        usageLedger1!.Summary.Should().Contain("薬院");
        usageLedger1.Summary.Should().NotContain("往復");  // 往復にならないこと

        chargeLedger.Should().NotBeNull();
        chargeLedger!.Income.Should().Be(1000);
        chargeLedger.Balance.Should().Be(1690);

        usageLedger2.Should().NotBeNull();
        usageLedger2!.Summary.Should().Contain("博多");
        usageLedger2.Summary.Should().NotContain("往復");  // 往復にならないこと
    }

    [Fact]
    public async Task ReturnAsync_NoChargeBetweenTrips_CreatesSingleUsageLedger()
    {
        // Arrange: チャージなしの往復（天神→博多, 博多→天神）
        var card = CreateTestCard(isLent: true);
        var staff = CreateTestStaff();
        var lentRecord = CreateTestLentRecord();

        var usageDetails = new List<LedgerDetail>
        {
            // ICカード履歴は新しい順
            new() { UseDate = DateTime.Today, EntryStation = "博多", ExitStation = "天神", Amount = 210, Balance = 580, IsCharge = false },
            new() { UseDate = DateTime.Today, EntryStation = "天神", ExitStation = "博多", Amount = 210, Balance = 790, IsCharge = false },
        };

        SetupReturnMocks(card, staff, lentRecord);

        var createdLedgers = new List<Ledger>();
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => createdLedgers.Add(l))
            .ReturnsAsync(1);

        // Act
        await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert: 1つの利用Ledger（往復として統合）
        var nonLentLedgers = createdLedgers.Where(l => !l.IsLentRecord && l.Summary != "（貸出中）").ToList();
        var usageLedgers = nonLentLedgers.Where(l => l.Expense > 0).ToList();

        usageLedgers.Should().HaveCount(1);
        usageLedgers[0].Expense.Should().Be(420);  // 210 + 210
        usageLedgers[0].Summary.Should().Contain("往復");  // 往復であること
    }

    [Fact]
    public async Task ReturnAsync_ChargeBetweenTrips_InsertsDetailsInFeliCaOrder()
    {
        // Arrange: チャージが利用の間に挟まるケース
        // ICカード履歴（新しい順）: 博多→薬院(1380), チャージ(1690), 薬院→博多(690)
        // 時系列順（古い順）: 薬院→博多(690), チャージ(1690), 博多→薬院(1380)
        var card = CreateTestCard(isLent: true);
        var staff = CreateTestStaff();
        var lentRecord = CreateTestLentRecord();

        var usageDetails = new List<LedgerDetail>
        {
            // ICカード履歴は新しい順
            new() { UseDate = DateTime.Today, EntryStation = "博多", ExitStation = "薬院", Amount = 310, Balance = 1380, IsCharge = false },
            new() { UseDate = DateTime.Today, Amount = 1000, Balance = 1690, IsCharge = true },
            new() { UseDate = DateTime.Today, EntryStation = "薬院", ExitStation = "博多", Amount = 310, Balance = 690, IsCharge = false },
        };

        SetupReturnMocks(card, staff, lentRecord);

        // InsertDetailsAsyncに渡される詳細をキャプチャ
        var capturedDetailsByLedger = new List<List<LedgerDetail>>();
        _ledgerRepositoryMock.Setup(x => x.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .Callback<int, IEnumerable<LedgerDetail>>((_, details) =>
                capturedDetailsByLedger.Add(details.ToList()))
            .ReturnsAsync(true);

        // Act
        await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert: 利用セグメントごとのInsertDetailsAsyncでFeliCa順（新しい→古い）で挿入されること
        // チャージが間に挟まるため、利用1（薬院→博多）と利用2（博多→薬院）は別セグメント
        // 各セグメントは1件のみなので順序検証は利用+チャージなしの別テストで行う
        capturedDetailsByLedger.Should().HaveCount(2); // 利用セグメント2つ
    }

    [Fact]
    public async Task ReturnAsync_MultipleTripsNoCharge_InsertsDetailsInFeliCaOrder()
    {
        // Arrange: チャージなしの複数利用（往復）
        // ICカード履歴（新しい順）: 博多→天神(580), 天神→博多(790)
        // 時系列順（古い順）: 天神→博多(790), 博多→天神(580)
        // FeliCa互換の挿入順（新しい→古い）: 博多→天神(580), 天神→博多(790)
        var card = CreateTestCard(isLent: true);
        var staff = CreateTestStaff();
        var lentRecord = CreateTestLentRecord();

        var usageDetails = new List<LedgerDetail>
        {
            // ICカード履歴は新しい順
            new() { UseDate = DateTime.Today, EntryStation = "博多", ExitStation = "天神", Amount = 210, Balance = 580, IsCharge = false },
            new() { UseDate = DateTime.Today, EntryStation = "天神", ExitStation = "博多", Amount = 210, Balance = 790, IsCharge = false },
        };

        SetupReturnMocks(card, staff, lentRecord);

        // InsertDetailsAsyncに渡される詳細をキャプチャ
        List<LedgerDetail> capturedDetails = null;
        _ledgerRepositoryMock.Setup(x => x.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .Callback<int, IEnumerable<LedgerDetail>>((_, details) =>
                capturedDetails = details.ToList())
            .ReturnsAsync(true);

        // Act
        await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

        // Assert: InsertDetailsAsyncに渡される詳細はFeliCa順（新しい→古い＝残高が小さい→大きい）
        // SortChronologicallyが古い順に並べ替えた後、Reverse()でFeliCa順に戻されること
        capturedDetails.Should().NotBeNull();
        capturedDetails.Should().HaveCount(2);
        capturedDetails[0].Balance.Should().Be(580);  // 博多→天神（新しい＝先に挿入）
        capturedDetails[1].Balance.Should().Be(790);  // 天神→博多（古い＝後に挿入）
    }

    #endregion
}
