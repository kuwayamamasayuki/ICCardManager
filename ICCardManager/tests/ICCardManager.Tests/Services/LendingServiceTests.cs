using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Moq;
using Xunit;

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

        _service = new LendingService(
            _dbContext,
            _cardRepositoryMock.Object,
            _staffRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            _summaryGenerator);
    }

    public void Dispose()
    {
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
}
