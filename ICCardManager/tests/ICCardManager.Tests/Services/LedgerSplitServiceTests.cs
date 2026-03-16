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
using System.Threading.Tasks;

namespace ICCardManager.Tests.Services;

/// <summary>
/// LedgerSplitServiceのテスト（Issue #634）
/// </summary>
public class LedgerSplitServiceTests
{
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock;
    private readonly Mock<IOperationLogRepository> _operationLogRepositoryMock;
    private readonly Mock<IStaffRepository> _staffRepositoryMock;
    private readonly SummaryGenerator _summaryGenerator;
    private readonly OperationLogger _operationLogger;
    private readonly LedgerSplitService _service;

    // テスト用定数
    private const string TestCardIdm = "0102030405060708";
    private const string TestStaffName = "テスト太郎";
    private const string TestLenderIdm = "AAAA000000000001";

    public LedgerSplitServiceTests()
    {
        _ledgerRepositoryMock = new Mock<ILedgerRepository>();
        _operationLogRepositoryMock = new Mock<IOperationLogRepository>();
        _staffRepositoryMock = new Mock<IStaffRepository>();
        _summaryGenerator = new SummaryGenerator();

        _operationLogger = new OperationLogger(
            _operationLogRepositoryMock.Object,
            _staffRepositoryMock.Object);

        _service = new LedgerSplitService(
            _ledgerRepositoryMock.Object,
            _summaryGenerator,
            _operationLogger,
            NullLogger<LedgerSplitService>.Instance);

        // OperationLogger用のデフォルトセットアップ
        _operationLogRepositoryMock
            .Setup(x => x.InsertAsync(It.IsAny<OperationLog>()))
            .ReturnsAsync(1);
    }

    #region ヘルパーメソッド

    /// <summary>
    /// テスト用Ledgerを作成
    /// </summary>
    private static Ledger CreateTestLedger(
        int id,
        DateTime date,
        string summary,
        int income,
        int expense,
        int balance)
    {
        return new Ledger
        {
            Id = id,
            CardIdm = TestCardIdm,
            LenderIdm = TestLenderIdm,
            StaffName = TestStaffName,
            Date = date,
            Summary = summary,
            Income = income,
            Expense = expense,
            Balance = balance,
            Note = "テスト備考"
        };
    }

    /// <summary>
    /// テスト用LedgerDetail（鉄道利用）を作成
    /// </summary>
    private static LedgerDetail CreateRailDetail(
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
        int amount,
        int balance,
        int sequenceNumber,
        DateTime? useDate = null,
        int? groupId = null)
    {
        return new LedgerDetail
        {
            IsCharge = true,
            Amount = amount,
            Balance = balance,
            SequenceNumber = sequenceNumber,
            UseDate = useDate ?? new DateTime(2026, 2, 3, 10, 0, 0),
            GroupId = groupId
        };
    }

    /// <summary>
    /// テスト用LedgerDetail（バス利用）を作成
    /// </summary>
    private static LedgerDetail CreateBusDetail(
        int amount,
        int balance,
        int sequenceNumber,
        DateTime? useDate = null,
        int? groupId = null)
    {
        return new LedgerDetail
        {
            IsBus = true,
            Amount = amount,
            Balance = balance,
            SequenceNumber = sequenceNumber,
            UseDate = useDate ?? new DateTime(2026, 2, 3, 10, 0, 0),
            GroupId = groupId
        };
    }

    /// <summary>
    /// 標準的なリポジトリモックをセットアップ
    /// </summary>
    private void SetupDefaultMocks(Ledger originalLedger, int nextInsertId = 100)
    {
        _ledgerRepositoryMock
            .Setup(x => x.GetByIdAsync(originalLedger.Id))
            .ReturnsAsync(originalLedger);

        _ledgerRepositoryMock
            .Setup(x => x.ReplaceDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);

        _ledgerRepositoryMock
            .Setup(x => x.UpdateAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(true);

        _ledgerRepositoryMock
            .Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(nextInsertId);

        _ledgerRepositoryMock
            .Setup(x => x.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);
    }

    #endregion

    #region バリデーションテスト

    [Fact]
    public async Task SplitAsync_SingleGroup_ReturnsError()
    {
        // Arrange: 1グループしかない場合はエラー
        var details = new List<LedgerDetail>
        {
            CreateRailDetail("博多", "天神", 260, 740, 1, groupId: 1),
            CreateRailDetail("天神", "博多", 260, 480, 2, groupId: 1)
        };

        // Act
        var result = await _service.SplitAsync(1, details);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("2つ以上のグループ");
    }

    [Fact]
    public async Task SplitAsync_NoGroupIds_ReturnsError()
    {
        // Arrange: GroupIdが設定されていない場合もエラー
        var details = new List<LedgerDetail>
        {
            CreateRailDetail("博多", "天神", 260, 740, 1, groupId: null),
            CreateRailDetail("天神", "博多", 260, 480, 2, groupId: null)
        };

        // Act
        var result = await _service.SplitAsync(1, details);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("2つ以上のグループ");
    }

    [Fact]
    public async Task SplitAsync_LedgerNotFound_ReturnsError()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            CreateRailDetail("博多", "天神", 260, 740, 1, groupId: 1),
            CreateRailDetail("天神", "赤坂", 200, 540, 2, groupId: 2)
        };

        _ledgerRepositoryMock
            .Setup(x => x.GetByIdAsync(999))
            .ReturnsAsync((Ledger)null!);

        // Act
        var result = await _service.SplitAsync(999, details);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("見つかりません");
    }

    #endregion

    #region 2グループ分割テスト

    [Fact]
    public async Task SplitAsync_TwoGroups_UpdatesOriginalAndCreatesNewLedger()
    {
        // Arrange: 博多→天神（グループ1）、天神→赤坂（グループ2）
        var originalLedger = CreateTestLedger(
            id: 1,
            date: new DateTime(2026, 2, 3),
            summary: "鉄道（博多～赤坂）",
            income: 0,
            expense: 460,
            balance: 540);

        SetupDefaultMocks(originalLedger, nextInsertId: 100);

        var details = new List<LedgerDetail>
        {
            CreateRailDetail("博多", "天神", 260, 740, 1,
                useDate: new DateTime(2026, 2, 3, 10, 0, 0), groupId: 1),
            CreateRailDetail("天神", "赤坂", 200, 540, 2,
                useDate: new DateTime(2026, 2, 3, 14, 0, 0), groupId: 2)
        };

        // Act
        var result = await _service.SplitAsync(1, details);

        // Assert
        result.Success.Should().BeTrue();
        result.CreatedLedgerIds.Should().HaveCount(1, "新しいLedgerが1件作成される");
        result.CreatedLedgerIds[0].Should().Be(100);

        // 元のLedgerが更新されたことを検証
        _ledgerRepositoryMock.Verify(
            x => x.ReplaceDetailsAsync(1, It.IsAny<IEnumerable<LedgerDetail>>()),
            Times.Once);
        _ledgerRepositoryMock.Verify(
            x => x.UpdateAsync(It.Is<Ledger>(l => l.Id == 1)),
            Times.Once);

        // 新しいLedgerが挿入されたことを検証
        _ledgerRepositoryMock.Verify(
            x => x.InsertAsync(It.IsAny<Ledger>()),
            Times.Once);
        _ledgerRepositoryMock.Verify(
            x => x.InsertDetailsAsync(100, It.IsAny<IEnumerable<LedgerDetail>>()),
            Times.Once);
    }

    #endregion

    #region 3グループ分割テスト

    [Fact]
    public async Task SplitAsync_ThreeGroups_CreatesTwoNewLedgers()
    {
        // Arrange: 3区間を3グループに分割
        var originalLedger = CreateTestLedger(
            id: 1,
            date: new DateTime(2026, 2, 3),
            summary: "鉄道（博多～薬院）",
            income: 0,
            expense: 660,
            balance: 340);

        var insertCallCount = 0;
        _ledgerRepositoryMock
            .Setup(x => x.GetByIdAsync(1))
            .ReturnsAsync(originalLedger);
        _ledgerRepositoryMock
            .Setup(x => x.ReplaceDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);
        _ledgerRepositoryMock
            .Setup(x => x.UpdateAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(true);
        _ledgerRepositoryMock
            .Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(() => 100 + (++insertCallCount));
        _ledgerRepositoryMock
            .Setup(x => x.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);

        var details = new List<LedgerDetail>
        {
            CreateRailDetail("博多", "天神", 260, 740, 1,
                useDate: new DateTime(2026, 2, 3, 10, 0, 0), groupId: 1),
            CreateRailDetail("天神", "赤坂", 200, 540, 2,
                useDate: new DateTime(2026, 2, 3, 12, 0, 0), groupId: 2),
            CreateRailDetail("赤坂", "薬院", 200, 340, 3,
                useDate: new DateTime(2026, 2, 3, 14, 0, 0), groupId: 3)
        };

        // Act
        var result = await _service.SplitAsync(1, details);

        // Assert
        result.Success.Should().BeTrue();
        result.CreatedLedgerIds.Should().HaveCount(2, "新しいLedgerが2件作成される");

        // InsertAsyncが2回呼ばれたことを検証
        _ledgerRepositoryMock.Verify(
            x => x.InsertAsync(It.IsAny<Ledger>()),
            Times.Exactly(2));
    }

    #endregion

    #region CalculateGroupFinancials テスト

    [Fact]
    public void CalculateGroupFinancials_ExpenseOnly_CorrectValues()
    {
        // Arrange: 鉄道利用のみ（Expense）
        // FeliCa順: 小さいSequenceNumberほど新しい
        var details = new List<LedgerDetail>
        {
            CreateRailDetail("博多", "天神", 260, 740, 2,
                useDate: new DateTime(2026, 2, 3, 10, 0, 0)),
            CreateRailDetail("天神", "赤坂", 200, 540, 1,
                useDate: new DateTime(2026, 2, 3, 14, 0, 0))
        };

        // Act
        var (income, expense, balance) = LedgerSplitService.CalculateGroupFinancials(details);

        // Assert
        income.Should().Be(0, "鉄道利用のみなのでIncome=0");
        expense.Should().Be(460, "260+200=460");
        balance.Should().Be(540, "時系列で最後のdetailの残高");
    }

    [Fact]
    public void CalculateGroupFinancials_ChargeOnly_CorrectValues()
    {
        // Arrange: チャージのみ（Income）
        var details = new List<LedgerDetail>
        {
            CreateChargeDetail(3000, 3500, 1)
        };

        // Act
        var (income, expense, balance) = LedgerSplitService.CalculateGroupFinancials(details);

        // Assert
        income.Should().Be(3000, "チャージ額がIncome");
        expense.Should().Be(0, "チャージのみなのでExpense=0");
        balance.Should().Be(3500, "チャージ後の残高");
    }

    [Fact]
    public void CalculateGroupFinancials_Balance_IsLastDetailBalance()
    {
        // Arrange: 複数の利用明細
        var details = new List<LedgerDetail>
        {
            CreateRailDetail("博多", "天神", 260, 740, 1,
                useDate: new DateTime(2026, 2, 3, 10, 0, 0)),
            CreateRailDetail("天神", "赤坂", 200, 540, 2,
                useDate: new DateTime(2026, 2, 3, 12, 0, 0)),
            CreateRailDetail("赤坂", "薬院", 200, 340, 3,
                useDate: new DateTime(2026, 2, 3, 14, 0, 0))
        };

        // Act
        var (_, _, balance) = LedgerSplitService.CalculateGroupFinancials(details);

        // Assert: SequenceNumber順で最後のdetailの残高
        balance.Should().Be(340);
    }

    [Fact]
    public void CalculateGroupFinancials_PointRedemption_NotCountedAsExpense()
    {
        // Arrange: ポイント還元はExpenseに含まない
        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                IsPointRedemption = true,
                Amount = 100,
                Balance = 600,
                SequenceNumber = 1,
                UseDate = new DateTime(2026, 2, 3, 10, 0, 0)
            }
        };

        // Act
        var (income, expense, balance) = LedgerSplitService.CalculateGroupFinancials(details);

        // Assert
        income.Should().Be(0, "ポイント還元はIncomeではない");
        expense.Should().Be(0, "ポイント還元はExpenseに含まない");
        balance.Should().Be(600);
    }

    /// <summary>
    /// Issue #1004: ポイント還元と利用が混在するグループで、
    /// 残高チェーン順の最終残高（ポイント還元後）が正しく取得されること
    /// </summary>
    [Fact]
    public void CalculateGroupFinancials_UsageAndPointRedemption_ReturnsPointRedemptionBalance()
    {
        // Arrange - 利用(1876→1456)→ポイント還元(1456→1696)
        // 時系列で最後のdetailはポイント還元（残高1696）
        var sameDate = new DateTime(2026, 3, 10, 10, 0, 0);
        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                IsCharge = false,
                IsPointRedemption = true,
                Amount = 240,
                Balance = 1696,
                UseDate = sameDate
            },
            new LedgerDetail
            {
                IsCharge = false,
                IsPointRedemption = false,
                Amount = 420,
                Balance = 1456,
                EntryStation = "薬院",
                ExitStation = "博多",
                UseDate = sameDate
            }
        };

        // Act
        var (income, expense, balance) = LedgerSplitService.CalculateGroupFinancials(details);

        // Assert
        income.Should().Be(0, "ポイント還元はIncomeに含まない");
        expense.Should().Be(420, "利用の420円のみ");
        balance.Should().Be(1696, "時系列で最後のポイント還元後の残高が最終残高");
    }

    #endregion

    #region メタデータコピーテスト

    [Fact]
    public async Task SplitAsync_CopiesMetadataToNewLedger()
    {
        // Arrange: メタデータ（CardIdm, StaffName, LenderIdm等）が新しいLedgerにコピーされること
        var originalLedger = CreateTestLedger(
            id: 1,
            date: new DateTime(2026, 2, 3),
            summary: "鉄道（博多～赤坂）",
            income: 0,
            expense: 460,
            balance: 540);
        originalLedger.ReturnerIdm = "BBBB000000000002";
        originalLedger.LentAt = new DateTime(2026, 2, 3, 9, 0, 0);
        originalLedger.ReturnedAt = new DateTime(2026, 2, 3, 18, 0, 0);

        SetupDefaultMocks(originalLedger, nextInsertId: 100);

        Ledger? insertedLedger = null;
        _ledgerRepositoryMock
            .Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => insertedLedger = l)
            .ReturnsAsync(100);

        var details = new List<LedgerDetail>
        {
            CreateRailDetail("博多", "天神", 260, 740, 1,
                useDate: new DateTime(2026, 2, 3, 10, 0, 0), groupId: 1),
            CreateRailDetail("天神", "赤坂", 200, 540, 2,
                useDate: new DateTime(2026, 2, 3, 14, 0, 0), groupId: 2)
        };

        // Act
        await _service.SplitAsync(1, details);

        // Assert
        insertedLedger.Should().NotBeNull();
        insertedLedger!.CardIdm.Should().Be(TestCardIdm);
        insertedLedger.LenderIdm.Should().Be(TestLenderIdm);
        insertedLedger.StaffName.Should().Be(TestStaffName);
        insertedLedger.ReturnerIdm.Should().Be("BBBB000000000002");
        insertedLedger.LentAt.Should().Be(new DateTime(2026, 2, 3, 9, 0, 0));
        insertedLedger.ReturnedAt.Should().Be(new DateTime(2026, 2, 3, 18, 0, 0));
    }

    #endregion

    #region GroupIdクリアテスト

    [Fact]
    public async Task SplitAsync_ClearsGroupIdsInDetails()
    {
        // Arrange: 分割後のdetailのGroupIdがnullにクリアされること
        var originalLedger = CreateTestLedger(
            id: 1,
            date: new DateTime(2026, 2, 3),
            summary: "鉄道（博多～赤坂）",
            income: 0,
            expense: 460,
            balance: 540);

        SetupDefaultMocks(originalLedger, nextInsertId: 100);

        List<LedgerDetail>? replacedDetails = null;
        _ledgerRepositoryMock
            .Setup(x => x.ReplaceDetailsAsync(1, It.IsAny<IEnumerable<LedgerDetail>>()))
            .Callback<int, IEnumerable<LedgerDetail>>((id, d) => replacedDetails = d.ToList())
            .ReturnsAsync(true);

        List<LedgerDetail>? insertedDetails = null;
        _ledgerRepositoryMock
            .Setup(x => x.InsertDetailsAsync(100, It.IsAny<IEnumerable<LedgerDetail>>()))
            .Callback<int, IEnumerable<LedgerDetail>>((id, d) => insertedDetails = d.ToList())
            .ReturnsAsync(true);

        var details = new List<LedgerDetail>
        {
            CreateRailDetail("博多", "天神", 260, 740, 1,
                useDate: new DateTime(2026, 2, 3, 10, 0, 0), groupId: 1),
            CreateRailDetail("天神", "赤坂", 200, 540, 2,
                useDate: new DateTime(2026, 2, 3, 14, 0, 0), groupId: 2)
        };

        // Act
        await _service.SplitAsync(1, details);

        // Assert: 元のLedgerに紐づく詳細のGroupIdがクリアされている
        replacedDetails.Should().NotBeNull();
        replacedDetails!.Should().AllSatisfy(d =>
            d.GroupId.Should().BeNull("分割後はGroupIdがクリアされる"));

        // 新しいLedgerに紐づく詳細のGroupIdもクリアされている
        insertedDetails.Should().NotBeNull();
        insertedDetails!.Should().AllSatisfy(d =>
            d.GroupId.Should().BeNull("分割後はGroupIdがクリアされる"));
    }

    #endregion

    #region Summaryテスト

    [Fact]
    public async Task SplitAsync_GeneratesCorrectSummaryForEachGroup()
    {
        // Arrange: 博多→天神（グループ1）、天神→赤坂（グループ2）
        var originalLedger = CreateTestLedger(
            id: 1,
            date: new DateTime(2026, 2, 3),
            summary: "鉄道（博多～赤坂）",
            income: 0,
            expense: 460,
            balance: 540);

        SetupDefaultMocks(originalLedger, nextInsertId: 100);

        Ledger? insertedLedger = null;
        _ledgerRepositoryMock
            .Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => insertedLedger = l)
            .ReturnsAsync(100);

        var details = new List<LedgerDetail>
        {
            CreateRailDetail("博多", "天神", 260, 740, 1,
                useDate: new DateTime(2026, 2, 3, 10, 0, 0), groupId: 1),
            CreateRailDetail("天神", "赤坂", 200, 540, 2,
                useDate: new DateTime(2026, 2, 3, 14, 0, 0), groupId: 2)
        };

        // Act
        await _service.SplitAsync(1, details);

        // Assert: 元のLedger（グループ1）のSummaryが更新されている
        originalLedger.Summary.Should().Contain("博多").And.Contain("天神");

        // 新しいLedger（グループ2）のSummaryが正しく生成されている
        insertedLedger.Should().NotBeNull();
        insertedLedger!.Summary.Should().Contain("天神").And.Contain("赤坂");
    }

    #endregion

    #region 操作ログテスト

    [Fact]
    public async Task SplitAsync_LogsOperation()
    {
        // Arrange
        var originalLedger = CreateTestLedger(
            id: 1,
            date: new DateTime(2026, 2, 3),
            summary: "鉄道（博多～赤坂）",
            income: 0,
            expense: 460,
            balance: 540);

        SetupDefaultMocks(originalLedger, nextInsertId: 100);

        _staffRepositoryMock
            .Setup(x => x.GetByIdmAsync(TestLenderIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = TestLenderIdm, Name = TestStaffName });

        var details = new List<LedgerDetail>
        {
            CreateRailDetail("博多", "天神", 260, 740, 1,
                useDate: new DateTime(2026, 2, 3, 10, 0, 0), groupId: 1),
            CreateRailDetail("天神", "赤坂", 200, 540, 2,
                useDate: new DateTime(2026, 2, 3, 14, 0, 0), groupId: 2)
        };

        // Act
        await _service.SplitAsync(1, details, operatorIdm: TestLenderIdm);

        // Assert: 操作ログが記録される
        _operationLogRepositoryMock.Verify(
            x => x.InsertAsync(It.Is<OperationLog>(log =>
                log.Action == "SPLIT" &&
                log.TargetTable == "ledger" &&
                log.TargetId == "1")),
            Times.Once);
    }

    #endregion

    #region Expense/Income計算テスト

    [Fact]
    public async Task SplitAsync_CalculatesCorrectExpenseForEachGroup()
    {
        // Arrange: グループ1=260円、グループ2=200円
        var originalLedger = CreateTestLedger(
            id: 1,
            date: new DateTime(2026, 2, 3),
            summary: "鉄道（博多～赤坂）",
            income: 0,
            expense: 460,
            balance: 540);

        SetupDefaultMocks(originalLedger, nextInsertId: 100);

        Ledger? insertedLedger = null;
        _ledgerRepositoryMock
            .Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => insertedLedger = l)
            .ReturnsAsync(100);

        var details = new List<LedgerDetail>
        {
            CreateRailDetail("博多", "天神", 260, 740, 1,
                useDate: new DateTime(2026, 2, 3, 10, 0, 0), groupId: 1),
            CreateRailDetail("天神", "赤坂", 200, 540, 2,
                useDate: new DateTime(2026, 2, 3, 14, 0, 0), groupId: 2)
        };

        // Act
        await _service.SplitAsync(1, details);

        // Assert: 元のLedger（グループ1）のExpense
        originalLedger.Expense.Should().Be(260, "グループ1は博多→天神の260円");
        originalLedger.Income.Should().Be(0);
        originalLedger.Balance.Should().Be(740, "グループ1の最後の残高");

        // 新しいLedger（グループ2）のExpense
        insertedLedger.Should().NotBeNull();
        insertedLedger!.Expense.Should().Be(200, "グループ2は天神→赤坂の200円");
        insertedLedger.Income.Should().Be(0);
        insertedLedger.Balance.Should().Be(540, "グループ2の最後の残高");
    }

    #endregion

    #region Issue #880: FeliCa順序での残高計算テスト

    [Fact]
    public void CalculateGroupFinancials_FeliCaOrder_ReturnsChronologicallyLastBalance()
    {
        // Arrange: FeliCaカードリーダーは新しい順に履歴を返すため、
        // 小さいSequenceNumber（=rowid）ほど新しい（後に利用した）エントリ
        // SequenceNumber=1が最新（14:00）、SequenceNumber=3が最古（10:00）
        var details = new List<LedgerDetail>
        {
            CreateRailDetail("博多", "天神", 260, 740, 3,
                useDate: new DateTime(2026, 2, 3, 10, 0, 0)),
            CreateRailDetail("天神", "赤坂", 200, 540, 2,
                useDate: new DateTime(2026, 2, 3, 12, 0, 0)),
            CreateRailDetail("赤坂", "薬院", 200, 340, 1,
                useDate: new DateTime(2026, 2, 3, 14, 0, 0))
        };

        // Act
        var (_, _, balance) = LedgerSplitService.CalculateGroupFinancials(details);

        // Assert: 時系列順で最後（14:00の赤坂→薬院）の残高340を取得すべき
        balance.Should().Be(340, "時系列で最後のエントリの残高を返すべき");
    }

    [Fact]
    public void CalculateGroupFinancials_SameDateFeliCaOrder_ReturnsCorrectBalance()
    {
        // Arrange: 同一日付でSequenceNumberのみが異なるケース
        // FeliCa順: SequenceNumber=1が最新、SequenceNumber=3が最古
        var sameDate = new DateTime(2026, 2, 3, 10, 0, 0);
        var details = new List<LedgerDetail>
        {
            CreateRailDetail("博多", "天神", 260, 740, 3, useDate: sameDate),
            CreateRailDetail("天神", "赤坂", 200, 540, 2, useDate: sameDate),
            CreateRailDetail("赤坂", "薬院", 200, 340, 1, useDate: sameDate)
        };

        // Act
        var (_, _, balance) = LedgerSplitService.CalculateGroupFinancials(details);

        // Assert: rowid DESC順で最後（SequenceNumber=1、最新）の残高340を取得すべき
        balance.Should().Be(340,
            "同一日の場合、SequenceNumber（rowid）のDESC順で最後の残高を返すべき");
    }

    [Fact]
    public async Task SplitAsync_FeliCaOrder_CorrectBalanceForBothGroups()
    {
        // Arrange: FeliCa順のSequenceNumberで分割した場合の残高の整合性検証
        // 初期残高1000円からの3区間利用をFeliCa順で記録
        // SequenceNumber=3: 博多→天神 260円 (10:00, 最古) 残高740
        // SequenceNumber=2: 天神→赤坂 200円 (12:00)        残高540
        // SequenceNumber=1: 赤坂→薬院 200円 (14:00, 最新)  残高340
        // グループ1（博多→天神）とグループ2（天神→赤坂、赤坂→薬院）に分割
        var originalLedger = CreateTestLedger(
            id: 1,
            date: new DateTime(2026, 2, 3),
            summary: "鉄道（博多～薬院）",
            income: 0,
            expense: 660,
            balance: 340);

        SetupDefaultMocks(originalLedger, nextInsertId: 100);

        Ledger? insertedLedger = null;
        _ledgerRepositoryMock
            .Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => insertedLedger = l)
            .ReturnsAsync(100);

        var details = new List<LedgerDetail>
        {
            CreateRailDetail("博多", "天神", 260, 740, 3,
                useDate: new DateTime(2026, 2, 3, 10, 0, 0), groupId: 1),
            CreateRailDetail("天神", "赤坂", 200, 540, 2,
                useDate: new DateTime(2026, 2, 3, 12, 0, 0), groupId: 2),
            CreateRailDetail("赤坂", "薬院", 200, 340, 1,
                useDate: new DateTime(2026, 2, 3, 14, 0, 0), groupId: 2)
        };

        // Act
        await _service.SplitAsync(1, details);

        // Assert: グループ1の残高 = 博多→天神後の740円
        originalLedger.Balance.Should().Be(740,
            "グループ1は博多→天神の1件のみなので残高740");

        // Assert: グループ2の残高 = 赤坂→薬院後の340円（時系列で最後のエントリ）
        insertedLedger.Should().NotBeNull();
        insertedLedger!.Balance.Should().Be(340,
            "グループ2は天神→赤坂→薬院の2件で、時系列最後の残高340");
    }

    [Fact]
    public void CalculateGroupFinancials_ChargeAndUsageSameDate_ChargeBeforeUsage()
    {
        // Arrange: 同一日にチャージと利用がある場合、
        // DBの表示順ではチャージが先（is_charge DESC）なので、
        // 利用の残高が時系列で最後
        var sameDate = new DateTime(2026, 2, 3, 10, 0, 0);
        var details = new List<LedgerDetail>
        {
            CreateChargeDetail(3000, 3500, 2, useDate: sameDate),
            CreateRailDetail("博多", "天神", 260, 3240, 1, useDate: sameDate)
        };

        // Act
        var (income, expense, balance) = LedgerSplitService.CalculateGroupFinancials(details);

        // Assert: チャージ→利用の順で、最後の残高は利用後の3240
        income.Should().Be(3000);
        expense.Should().Be(260);
        balance.Should().Be(3240,
            "時系列ではチャージ→利用の順なので、利用後の残高が最終残高");
    }

    [Fact]
    public async Task SplitAsync_InsertsDetailsInReverseOrder_ForFeliCaRowidCompatibility()
    {
        // Arrange: 3件の明細を2グループに分割し、挿入順序が逆順であることを検証
        // UI（GetDetailsAsync）からは時系列順（古い順）で渡される:
        //   博多→天神(10:00) → 天神→赤坂(12:00) → 赤坂→薬院(14:00)
        // 挿入時は逆順（新しい順）にして、FeliCa互換のrowid順序を維持すべき
        var originalLedger = CreateTestLedger(
            id: 1,
            date: new DateTime(2026, 2, 3),
            summary: "鉄道（博多～薬院）",
            income: 0,
            expense: 660,
            balance: 340);

        _ledgerRepositoryMock
            .Setup(x => x.GetByIdAsync(1))
            .ReturnsAsync(originalLedger);
        _ledgerRepositoryMock
            .Setup(x => x.UpdateAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(true);

        var insertCallCount = 0;
        _ledgerRepositoryMock
            .Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(() => 100 + (++insertCallCount));

        // ReplaceDetailsAsync（グループ1）のキャプチャ
        List<LedgerDetail>? replacedDetails = null;
        _ledgerRepositoryMock
            .Setup(x => x.ReplaceDetailsAsync(1, It.IsAny<IEnumerable<LedgerDetail>>()))
            .Callback<int, IEnumerable<LedgerDetail>>((id, d) => replacedDetails = d.ToList())
            .ReturnsAsync(true);

        // InsertDetailsAsync（グループ2）のキャプチャ
        List<LedgerDetail>? insertedDetails = null;
        _ledgerRepositoryMock
            .Setup(x => x.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .Callback<int, IEnumerable<LedgerDetail>>((id, d) => insertedDetails = d.ToList())
            .ReturnsAsync(true);

        // UIからは時系列順（古い→新しい）で渡される
        var details = new List<LedgerDetail>
        {
            CreateRailDetail("博多", "天神", 260, 740, 3,
                useDate: new DateTime(2026, 2, 3, 10, 0, 0), groupId: 1),
            CreateRailDetail("天神", "赤坂", 200, 540, 2,
                useDate: new DateTime(2026, 2, 3, 12, 0, 0), groupId: 2),
            CreateRailDetail("赤坂", "薬院", 200, 340, 1,
                useDate: new DateTime(2026, 2, 3, 14, 0, 0), groupId: 2)
        };

        // Act
        await _service.SplitAsync(1, details);

        // Assert: グループ1（ReplaceDetailsAsync）は1件のみなので順序は関係ない
        replacedDetails.Should().NotBeNull();
        replacedDetails.Should().HaveCount(1);

        // Assert: グループ2（InsertDetailsAsync）は新しい順で挿入されるべき
        // （赤坂→薬院(14:00)が先、天神→赤坂(12:00)が後）
        insertedDetails.Should().NotBeNull();
        insertedDetails.Should().HaveCount(2);
        insertedDetails![0].ExitStation.Should().Be("薬院",
            "新しい明細（14:00の赤坂→薬院）が先に挿入されるべき");
        insertedDetails[1].ExitStation.Should().Be("赤坂",
            "古い明細（12:00の天神→赤坂）が後に挿入されるべき");
    }

    [Fact]
    public async Task SplitAsync_ReplaceDetailsAsync_InsertsInReverseOrder()
    {
        // Arrange: グループ1に複数の明細がある場合の挿入順序を検証
        var originalLedger = CreateTestLedger(
            id: 1,
            date: new DateTime(2026, 2, 3),
            summary: "鉄道（博多～薬院）",
            income: 0,
            expense: 660,
            balance: 340);

        _ledgerRepositoryMock
            .Setup(x => x.GetByIdAsync(1))
            .ReturnsAsync(originalLedger);
        _ledgerRepositoryMock
            .Setup(x => x.UpdateAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(true);
        _ledgerRepositoryMock
            .Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(100);
        _ledgerRepositoryMock
            .Setup(x => x.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);

        // ReplaceDetailsAsync（グループ1）のキャプチャ
        List<LedgerDetail>? replacedDetails = null;
        _ledgerRepositoryMock
            .Setup(x => x.ReplaceDetailsAsync(1, It.IsAny<IEnumerable<LedgerDetail>>()))
            .Callback<int, IEnumerable<LedgerDetail>>((id, d) => replacedDetails = d.ToList())
            .ReturnsAsync(true);

        // UIからは時系列順（古い→新しい）: 博多→天神(10:00)、天神→赤坂(12:00)
        var details = new List<LedgerDetail>
        {
            CreateRailDetail("博多", "天神", 260, 740, 3,
                useDate: new DateTime(2026, 2, 3, 10, 0, 0), groupId: 1),
            CreateRailDetail("天神", "赤坂", 200, 540, 2,
                useDate: new DateTime(2026, 2, 3, 12, 0, 0), groupId: 1),
            CreateRailDetail("赤坂", "薬院", 200, 340, 1,
                useDate: new DateTime(2026, 2, 3, 14, 0, 0), groupId: 2)
        };

        // Act
        await _service.SplitAsync(1, details);

        // Assert: グループ1（ReplaceDetailsAsync）も新しい順で挿入されるべき
        // （天神→赤坂(12:00)が先、博多→天神(10:00)が後）
        replacedDetails.Should().NotBeNull();
        replacedDetails.Should().HaveCount(2);
        replacedDetails![0].ExitStation.Should().Be("赤坂",
            "新しい明細（12:00の天神→赤坂）が先に挿入されるべき");
        replacedDetails[1].ExitStation.Should().Be("天神",
            "古い明細（10:00の博多→天神）が後に挿入されるべき");
    }

    #endregion
}
