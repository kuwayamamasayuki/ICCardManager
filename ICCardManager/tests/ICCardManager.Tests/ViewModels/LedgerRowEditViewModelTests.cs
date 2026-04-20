using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// LedgerRowEditViewModelの単体テスト（Issue #635）
/// </summary>
public class LedgerRowEditViewModelTests
{
    private readonly Mock<ILedgerRepository> _ledgerRepoMock;
    private readonly Mock<IStaffRepository> _staffRepoMock;
    private readonly Mock<IOperationLogRepository> _operationLogRepoMock;
    private readonly OperationLogger _operationLogger;
    private readonly LedgerRowEditViewModel _viewModel;

    private const string TestCardIdm = "0102030405060708";
    private const string TestOperatorIdm = "FFFF000000000001";

    private readonly Staff _staffA = new Staff { StaffIdm = "AAAA000000000001", Name = "田中太郎" };
    private readonly Staff _staffB = new Staff { StaffIdm = "BBBB000000000002", Name = "山田花子" };

    public LedgerRowEditViewModelTests()
    {
        _ledgerRepoMock = new Mock<ILedgerRepository>();
        _staffRepoMock = new Mock<IStaffRepository>();
        _operationLogRepoMock = new Mock<IOperationLogRepository>();
        _operationLogger = new OperationLogger(
            _operationLogRepoMock.Object,
            Mock.Of<ICurrentOperatorContext>());

        _staffRepoMock.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<Staff> { _staffA, _staffB });

        _viewModel = new LedgerRowEditViewModel(
            _ledgerRepoMock.Object,
            _staffRepoMock.Object,
            _operationLogger);
    }

    /// <summary>
    /// テスト用の履歴行リストを作成
    /// </summary>
    private List<LedgerDto> CreateTestLedgers()
    {
        return new List<LedgerDto>
        {
            new LedgerDto
            {
                Id = 1, CardIdm = TestCardIdm,
                Date = new DateTime(2026, 1, 10), DateDisplay = "R8.1.10",
                Summary = "鉄道（天神～博多）", Income = 0, Expense = 210, Balance = 2300
            },
            new LedgerDto
            {
                Id = 2, CardIdm = TestCardIdm,
                Date = new DateTime(2026, 1, 10), DateDisplay = "R8.1.10",
                Summary = "鉄道（博多～天神）", Income = 0, Expense = 210, Balance = 2090
            },
            new LedgerDto
            {
                Id = 3, CardIdm = TestCardIdm,
                Date = new DateTime(2026, 1, 11), DateDisplay = "R8.1.11",
                Summary = "鉄道（天神～六本松）", Income = 0, Expense = 200, Balance = 1890
            }
        };
    }

    #region Addモード初期化

    [Fact]
    public async Task InitializeForAdd_SetsAddMode()
    {
        // Arrange
        var allLedgers = CreateTestLedgers();

        // Act
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);

        // Assert
        _viewModel.Mode.Should().Be(LedgerRowEditMode.Add);
        _viewModel.DialogTitle.Should().Be("履歴行の追加");
        _viewModel.IsAddMode.Should().BeTrue();
    }

    [Fact]
    public async Task InitializeForAdd_LoadsStaffList()
    {
        // Arrange
        var allLedgers = CreateTestLedgers();

        // Act
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);

        // Assert
        _viewModel.StaffList.Should().HaveCount(2);
    }

    [Fact]
    public async Task InitializeForAdd_SetsInsertIndexToEnd()
    {
        // Arrange
        var allLedgers = CreateTestLedgers();

        // Act
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);

        // Assert
        _viewModel.InsertIndex.Should().Be(3, "末尾に挿入");
    }

    #endregion

    #region Addモード: 残高自動計算

    [Fact]
    public async Task AddMode_AutoBalance_CalculatesFromPreviousRow()
    {
        // Arrange
        var allLedgers = CreateTestLedgers();
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);

        // Act: 受入3000円を設定
        _viewModel.Income = 3000;
        _viewModel.Expense = 0;

        // Assert: 前行の残高1890 + 3000 - 0 = 4890
        _viewModel.PreviousBalance.Should().Be(1890);
        _viewModel.Balance.Should().Be(4890);
    }

    [Fact]
    public async Task AddMode_AutoBalance_UpdatesWhenExpenseChanges()
    {
        // Arrange
        var allLedgers = CreateTestLedgers();
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);

        // Act: 払出200円を設定
        _viewModel.Income = 0;
        _viewModel.Expense = 200;

        // Assert: 1890 + 0 - 200 = 1690
        _viewModel.Balance.Should().Be(1690);
    }

    [Fact]
    public async Task AddMode_ManualBalance_DoesNotAutoCalculate()
    {
        // Arrange
        var allLedgers = CreateTestLedgers();
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);

        // Act: 自動計算をOFF → 手動入力
        _viewModel.IsAutoBalance = false;
        _viewModel.Balance = 9999;
        _viewModel.Income = 100;

        // Assert: 手動入力の値が維持される（自動計算されない）
        _viewModel.Balance.Should().Be(9999);
    }

    #endregion

    #region Addモード: 挿入位置の移動

    [Fact]
    public async Task AddMode_MoveUp_DecrementsInsertIndex()
    {
        // Arrange
        var allLedgers = CreateTestLedgers();
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);
        _viewModel.InsertIndex.Should().Be(3);

        // Act
        _viewModel.MoveInsertPositionUpCommand.Execute(null);

        // Assert
        _viewModel.InsertIndex.Should().Be(2);
    }

    [Fact]
    public async Task AddMode_MoveDown_AtEnd_DoesNotExceedCount()
    {
        // Arrange
        var allLedgers = CreateTestLedgers();
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);
        _viewModel.InsertIndex.Should().Be(3);

        // Act: 既に末尾なので下に移動しても変わらない
        _viewModel.MoveInsertPositionDownCommand.Execute(null);

        // Assert
        _viewModel.InsertIndex.Should().Be(3);
    }

    [Fact]
    public async Task AddMode_MoveUp_RecalculatesBalance()
    {
        // Arrange
        var allLedgers = CreateTestLedgers();
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);
        _viewModel.Expense = 100;

        // 初期状態: InsertIndex=3, PreviousBalance=1890, Balance=1790
        _viewModel.Balance.Should().Be(1790);

        // Act: 1つ上に移動 → InsertIndex=2, PreviousBalance=2090
        _viewModel.MoveInsertPositionUpCommand.Execute(null);

        // Assert
        _viewModel.InsertIndex.Should().Be(2);
        _viewModel.PreviousBalance.Should().Be(2090);
        _viewModel.Balance.Should().Be(1990, "2090 + 0 - 100 = 1990");
    }

    #endregion

    #region Editモード初期化

    [Fact]
    public async Task InitializeForEdit_SetsEditMode()
    {
        // Arrange
        var ledger = new Ledger
        {
            Id = 1, CardIdm = TestCardIdm,
            Date = new DateTime(2026, 1, 10),
            Summary = "鉄道（天神～博多）",
            Income = 0, Expense = 210, Balance = 2300,
            LenderIdm = _staffA.StaffIdm,
            StaffName = _staffA.Name,
            Note = "テスト備考"
        };
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(ledger);

        var dto = new LedgerDto
        {
            Id = 1, CardIdm = TestCardIdm,
            Date = new DateTime(2026, 1, 10), DateDisplay = "R8.1.10",
            Summary = "鉄道（天神～博多）",
            Income = 0, Expense = 210, Balance = 2300,
            StaffName = _staffA.Name, Note = "テスト備考"
        };

        // Act
        await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm);

        // Assert
        _viewModel.Mode.Should().Be(LedgerRowEditMode.Edit);
        _viewModel.DialogTitle.Should().Be("履歴行の修正");
        _viewModel.IsAddMode.Should().BeFalse();
        _viewModel.Summary.Should().Be("鉄道（天神～博多）");
        _viewModel.Income.Should().Be(0);
        _viewModel.Expense.Should().Be(210);
        _viewModel.Balance.Should().Be(2300);
        _viewModel.Note.Should().Be("テスト備考");
        _viewModel.SelectedStaff.Should().NotBeNull();
        _viewModel.SelectedStaff!.StaffIdm.Should().Be(_staffA.StaffIdm);
    }

    /// <summary>
    /// Issue #1303: 旧バグで作成された LenderIdm=null 行（StaffName のみ）を、
    /// 氏名で照合して利用者欄に正しく選択できることを確認
    /// </summary>
    [Fact]
    public async Task InitializeForEdit_LenderIdmNullButStaffNameMatches_SelectsByName()
    {
        // Arrange
        var ledger = new Ledger
        {
            Id = 1, CardIdm = TestCardIdm,
            Date = new DateTime(2026, 4, 17),
            Summary = "鉄道（薬院～博多 往復）",
            Income = 0, Expense = 420, Balance = 596,
            LenderIdm = null,             // バグで未設定
            StaffName = _staffA.Name,     // スナップショットには残っている
            Note = string.Empty
        };
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(ledger);
        var dto = new LedgerDto
        {
            Id = 1, CardIdm = TestCardIdm,
            Date = ledger.Date, DateDisplay = "R8.4.17",
            Summary = ledger.Summary,
            Income = 0, Expense = 420, Balance = 596,
            StaffName = _staffA.Name
        };

        // Act
        await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm);

        // Assert: 氏名フォールバックで職員 A が選択される
        _viewModel.SelectedStaff.Should().NotBeNull();
        _viewModel.SelectedStaff!.StaffIdm.Should().Be(_staffA.StaffIdm);
    }

    /// <summary>
    /// Issue #1303: チャージ等、利用者情報が無い行は SelectedStaff が null のままになることを確認
    /// </summary>
    [Fact]
    public async Task InitializeForEdit_LenderIdmNullAndStaffNameNull_LeavesSelectedStaffNull()
    {
        // Arrange
        var ledger = new Ledger
        {
            Id = 2, CardIdm = TestCardIdm,
            Date = new DateTime(2026, 4, 17),
            Summary = "役務費によりチャージ",
            Income = 1000, Expense = 0, Balance = 2000,
            LenderIdm = null,
            StaffName = null,
            Note = string.Empty
        };
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(2)).ReturnsAsync(ledger);
        var dto = new LedgerDto
        {
            Id = 2, CardIdm = TestCardIdm,
            Date = ledger.Date, DateDisplay = "R8.4.17",
            Summary = ledger.Summary,
            Income = 1000, Expense = 0, Balance = 2000
        };

        // Act
        await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm);

        // Assert
        _viewModel.SelectedStaff.Should().BeNull();
    }

    /// <summary>
    /// Issue #1303: 論理削除等で IDm が一致しない場合も、同名アクティブ職員にフォールバック選択することを確認
    /// （物品出納簿は氏名表示のみで区別不可のため許容）
    /// </summary>
    [Fact]
    public async Task InitializeForEdit_LenderIdmNotInListButStaffNameMatches_FallsBackByName()
    {
        // Arrange
        var ledger = new Ledger
        {
            Id = 3, CardIdm = TestCardIdm,
            Date = new DateTime(2026, 4, 17),
            Summary = "鉄道（薬院～博多）",
            Income = 0, Expense = 210, Balance = 800,
            LenderIdm = "DDDD000000000099",  // StaffList に存在しない IDm
            StaffName = _staffA.Name,         // 同名のアクティブ職員 A は存在
            Note = string.Empty
        };
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(3)).ReturnsAsync(ledger);
        var dto = new LedgerDto
        {
            Id = 3, CardIdm = TestCardIdm,
            Date = ledger.Date, DateDisplay = "R8.4.17",
            Summary = ledger.Summary,
            Income = 0, Expense = 210, Balance = 800,
            StaffName = _staffA.Name
        };

        // Act
        await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm);

        // Assert
        _viewModel.SelectedStaff.Should().NotBeNull();
        _viewModel.SelectedStaff!.StaffIdm.Should().Be(_staffA.StaffIdm);
    }

    /// <summary>
    /// Issue #1303: 該当氏名の職員がリストに存在しない場合は SelectedStaff が null のままになることを確認
    /// </summary>
    [Fact]
    public async Task InitializeForEdit_LenderIdmNullAndStaffNameNotInList_LeavesSelectedStaffNull()
    {
        // Arrange
        var ledger = new Ledger
        {
            Id = 4, CardIdm = TestCardIdm,
            Date = new DateTime(2026, 4, 17),
            Summary = "鉄道（博多～天神）",
            Income = 0, Expense = 210, Balance = 800,
            LenderIdm = null,
            StaffName = "存在しない人物",
            Note = string.Empty
        };
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(4)).ReturnsAsync(ledger);
        var dto = new LedgerDto
        {
            Id = 4, CardIdm = TestCardIdm,
            Date = ledger.Date, DateDisplay = "R8.4.17",
            Summary = ledger.Summary,
            Income = 0, Expense = 210, Balance = 800,
            StaffName = "存在しない人物"
        };

        // Act
        await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm);

        // Assert
        _viewModel.SelectedStaff.Should().BeNull();
    }

    #endregion

    #region バリデーション

    [Fact]
    public async Task Validation_EmptySummary_CannotSave()
    {
        // Arrange
        var allLedgers = CreateTestLedgers();
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);

        // Act: 摘要を空にする
        _viewModel.Summary = "";

        // Assert
        _viewModel.CanSave.Should().BeFalse();
        _viewModel.ValidationMessage.Should().Contain("摘要");
    }

    [Fact]
    public async Task Validation_NegativeBalance_CannotSave()
    {
        // Arrange
        var allLedgers = new List<LedgerDto>
        {
            new LedgerDto { Id = 1, Date = new DateTime(2026, 1, 1), Balance = 100 }
        };
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);

        // Act: PreviousBalance=100, Expense=200 → Balance=-100
        _viewModel.Summary = "テスト";
        _viewModel.Income = 0;
        _viewModel.Expense = 200;

        // Assert
        _viewModel.CanSave.Should().BeFalse();
        _viewModel.ValidationMessage.Should().Contain("マイナス");
    }

    [Fact]
    public async Task Validation_NegativeIncome_CannotSave()
    {
        // Arrange
        var allLedgers = CreateTestLedgers();
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);

        // Act
        _viewModel.Summary = "テスト";
        _viewModel.Income = -100;

        // Assert
        _viewModel.CanSave.Should().BeFalse();
        _viewModel.ValidationMessage.Should().Contain("受入");
    }

    [Fact]
    public async Task Validation_BothZeroAmount_ShowsWarning()
    {
        // Arrange
        var allLedgers = CreateTestLedgers();
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);

        // Act: 受入・払出ともに0
        _viewModel.Summary = "テスト";
        _viewModel.Income = 0;
        _viewModel.Expense = 0;

        // Assert: 警告は出るがCanSaveはtrue
        _viewModel.CanSave.Should().BeTrue();
        _viewModel.WarningMessage.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Validation_CarryoverWithZeroAmount_NoWarning()
    {
        // Arrange
        var allLedgers = CreateTestLedgers();
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);

        // Act: 繰越は受入・払出0でもOK
        _viewModel.Summary = "3月から繰越";
        _viewModel.Income = 0;
        _viewModel.Expense = 0;

        // Assert
        _viewModel.CanSave.Should().BeTrue();
        _viewModel.WarningMessage.Should().BeEmpty();
    }

    #endregion

    #region Issue #1279: FirstErrorField によるフォーカス情報

    [Fact]
    public async Task Validation_摘要空_FirstErrorFieldにSummaryが設定されること()
    {
        var allLedgers = CreateTestLedgers();
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);

        _viewModel.Summary = "";

        _viewModel.CanSave.Should().BeFalse();
        _viewModel.FirstErrorField.Should().Be(nameof(LedgerRowEditViewModel.Summary),
            "摘要空エラー時は Dialog 側が Summary フィールドへフォーカス移動できるよう FirstErrorField を設定すべき");
    }

    [Fact]
    public async Task Validation_残高マイナス_FirstErrorFieldにBalanceが設定されること()
    {
        var allLedgers = new List<LedgerDto>
        {
            new LedgerDto { Id = 1, Date = new DateTime(2026, 1, 1), Balance = 100 }
        };
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);

        _viewModel.Summary = "テスト";
        _viewModel.Income = 0;
        _viewModel.Expense = 200;

        _viewModel.CanSave.Should().BeFalse();
        _viewModel.FirstErrorField.Should().Be(nameof(LedgerRowEditViewModel.Balance));
    }

    [Fact]
    public async Task Validation_受入金額負_FirstErrorFieldにIncomeが設定されること()
    {
        var allLedgers = CreateTestLedgers();
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);

        _viewModel.Summary = "テスト";
        _viewModel.Income = -100;

        _viewModel.CanSave.Should().BeFalse();
        _viewModel.FirstErrorField.Should().Be(nameof(LedgerRowEditViewModel.Income));
    }

    [Fact]
    public async Task Validation_払出金額負_FirstErrorFieldにExpenseが設定されること()
    {
        var allLedgers = CreateTestLedgers();
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);

        _viewModel.Summary = "テスト";
        _viewModel.Income = 1000;  // 残高をプラスに保つ
        _viewModel.Expense = -50;

        _viewModel.CanSave.Should().BeFalse();
        _viewModel.FirstErrorField.Should().Be(nameof(LedgerRowEditViewModel.Expense));
    }

    [Fact]
    public async Task Validation_エラー解消後_FirstErrorFieldがnullに戻ること()
    {
        var allLedgers = CreateTestLedgers();
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);

        _viewModel.Summary = "";
        _viewModel.FirstErrorField.Should().Be(nameof(LedgerRowEditViewModel.Summary));

        _viewModel.Summary = "鉄道（天神～博多）";
        _viewModel.Income = 1000;
        _viewModel.Expense = 0;

        _viewModel.CanSave.Should().BeTrue();
        _viewModel.FirstErrorField.Should().BeNull(
            "全ての検証が通過した場合、FirstErrorField は null に戻るべき");
    }

    #endregion

    #region 削除機能（Issue #750）

    [Fact]
    public async Task AddMode_CanDelete_IsFalse()
    {
        // Arrange
        var allLedgers = CreateTestLedgers();

        // Act
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);

        // Assert: 追加モードでは削除できない
        _viewModel.CanDelete.Should().BeFalse();
        _viewModel.IsDeleteRequested.Should().BeFalse();
    }

    [Fact]
    public async Task EditMode_NormalRecord_CanDelete_IsTrue()
    {
        // Arrange: 通常の履歴（IsLentRecord = false）
        var ledger = new Ledger
        {
            Id = 1, CardIdm = TestCardIdm,
            Date = new DateTime(2026, 1, 10),
            Summary = "鉄道（天神～博多）",
            Income = 0, Expense = 210, Balance = 2300,
            LenderIdm = _staffA.StaffIdm,
            StaffName = _staffA.Name,
            IsLentRecord = false
        };
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(ledger);

        var dto = new LedgerDto
        {
            Id = 1, CardIdm = TestCardIdm,
            Date = new DateTime(2026, 1, 10), DateDisplay = "R8.1.10",
            Summary = "鉄道（天神～博多）",
            Income = 0, Expense = 210, Balance = 2300,
            StaffName = _staffA.Name,
            IsLentRecord = false
        };

        // Act
        await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm);

        // Assert: 通常レコードは削除可能
        _viewModel.CanDelete.Should().BeTrue();
        _viewModel.IsDeleteRequested.Should().BeFalse();
    }

    [Fact]
    public async Task EditMode_LentRecord_CanDelete_IsFalse()
    {
        // Arrange: 貸出中レコード（IsLentRecord = true）
        var ledger = new Ledger
        {
            Id = 2, CardIdm = TestCardIdm,
            Date = new DateTime(2026, 1, 15),
            Summary = "（貸出中）",
            Income = 0, Expense = 0, Balance = 2300,
            LenderIdm = _staffA.StaffIdm,
            StaffName = _staffA.Name,
            IsLentRecord = true
        };
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(2)).ReturnsAsync(ledger);

        var dto = new LedgerDto
        {
            Id = 2, CardIdm = TestCardIdm,
            Date = new DateTime(2026, 1, 15), DateDisplay = "R8.1.15",
            Summary = "（貸出中）",
            Income = 0, Expense = 0, Balance = 2300,
            StaffName = _staffA.Name,
            IsLentRecord = true
        };

        // Act
        await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm);

        // Assert: 貸出中レコードは削除不可
        _viewModel.CanDelete.Should().BeFalse();
        _viewModel.IsDeleteRequested.Should().BeFalse();
    }

    #endregion

    #region 保存処理

    [Fact]
    public async Task SaveAdd_CallsInsertAsync()
    {
        // Arrange
        var allLedgers = CreateTestLedgers();
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);

        _viewModel.Summary = "鉄道（博多～天神）";
        _viewModel.Income = 0;
        _viewModel.Expense = 210;

        _ledgerRepoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(100);

        // Act
        await _viewModel.SaveCommand.ExecuteAsync(null);

        // Assert
        _viewModel.IsSaved.Should().BeTrue();
        _ledgerRepoMock.Verify(r => r.InsertAsync(It.Is<Ledger>(l =>
            l.CardIdm == TestCardIdm &&
            l.Summary == "鉄道（博多～天神）" &&
            l.Expense == 210
        )), Times.Once);
    }

    [Fact]
    public async Task SaveAdd_LogsOperation()
    {
        // Arrange
        var allLedgers = CreateTestLedgers();
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);

        _viewModel.Summary = "テスト摘要";
        _viewModel.Income = 500;

        _ledgerRepoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(100);

        _staffRepoMock.Setup(r => r.GetByIdmAsync(TestOperatorIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = TestOperatorIdm, Name = "操作者" });

        // Act
        await _viewModel.SaveCommand.ExecuteAsync(null);

        // Assert: 操作ログが記録される
        _operationLogRepoMock.Verify(r => r.InsertAsync(It.Is<OperationLog>(log =>
            log.Action == OperationLogger.Actions.Insert &&
            log.TargetTable == OperationLogger.Tables.Ledger
        )), Times.Once);
    }

    [Fact]
    public async Task SaveEdit_CallsUpdateAsync()
    {
        // Arrange
        var ledger = new Ledger
        {
            Id = 1, CardIdm = TestCardIdm,
            Date = new DateTime(2026, 1, 10),
            Summary = "元の摘要",
            Income = 0, Expense = 210, Balance = 2300,
            LenderIdm = _staffA.StaffIdm,
            StaffName = _staffA.Name
        };
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(ledger);
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>())).ReturnsAsync(true);

        _staffRepoMock.Setup(r => r.GetByIdmAsync(TestOperatorIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = TestOperatorIdm, Name = "操作者" });

        var dto = new LedgerDto
        {
            Id = 1, CardIdm = TestCardIdm,
            Date = new DateTime(2026, 1, 10), DateDisplay = "R8.1.10",
            Summary = "元の摘要", Income = 0, Expense = 210, Balance = 2300,
            StaffName = _staffA.Name
        };
        await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm);

        // Act: 摘要を変更して保存
        _viewModel.Summary = "変更後の摘要";
        await _viewModel.SaveCommand.ExecuteAsync(null);

        // Assert
        _viewModel.IsSaved.Should().BeTrue();
        _ledgerRepoMock.Verify(r => r.UpdateAsync(It.Is<Ledger>(l =>
            l.Summary == "変更後の摘要"
        )), Times.Once);
    }

    [Fact]
    public async Task SaveAdd_InsertFails_ShowsError()
    {
        // Arrange
        var allLedgers = CreateTestLedgers();
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);

        _viewModel.Summary = "テスト";
        _viewModel.Income = 500;

        _ledgerRepoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(0); // 失敗

        // Act
        await _viewModel.SaveCommand.ExecuteAsync(null);

        // Assert
        _viewModel.IsSaved.Should().BeFalse();
        _viewModel.StatusMessage.Should().Contain("失敗");
    }

    #endregion

    #region Issue #1134: パンくず・保存して次へ

    [Fact]
    public void BreadcrumbText_SetBreadcrumbで設定値が保持されること()
    {
        // Act
        _viewModel.SetBreadcrumb("nimoca N-002 > 履歴詳細 > 行修正");

        // Assert
        _viewModel.BreadcrumbText.Should().Be("nimoca N-002 > 履歴詳細 > 行修正");
    }

    [Fact]
    public void ShowSaveAndNextButton_設定値が保持されること()
    {
        // Arrange
        _viewModel.ShowSaveAndNextButton.Should().BeFalse("初期値はfalse");

        // Act
        _viewModel.ShowSaveAndNextButton = true;

        // Assert
        _viewModel.ShowSaveAndNextButton.Should().BeTrue();
    }

    [Fact]
    public async Task SaveAndEditNext_Addモード保存後にIsSaveAndEditNextRequestedがtrueになること()
    {
        // Arrange
        _ledgerRepoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(100);
        _operationLogRepoMock.Setup(r => r.InsertAsync(It.IsAny<OperationLog>()))
            .ReturnsAsync(1);

        await _viewModel.InitializeForAddAsync(TestCardIdm, CreateTestLedgers(), TestOperatorIdm);
        _viewModel.Summary = "テスト摘要";
        _viewModel.Expense = 210;

        // Act
        await _viewModel.SaveAndEditNextCommand.ExecuteAsync(null);

        // Assert
        _viewModel.IsSaveAndEditNextRequested.Should().BeTrue("保存して次へが要求された");
        _viewModel.IsSaved.Should().BeFalse("IsSavedはfalseにリセットされる");
    }

    [Fact]
    public async Task SaveAndEditNext_Editモード保存後にIsSaveAndEditNextRequestedがtrueになること()
    {
        // Arrange
        var existingLedger = new Ledger
        {
            Id = 1, CardIdm = TestCardIdm,
            Date = new DateTime(2026, 1, 10),
            Summary = "鉄道（天神～博多）",
            Income = 0, Expense = 210, Balance = 2300,
            Details = new List<LedgerDetail>()
        };
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(existingLedger);
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>())).ReturnsAsync(true);
        _operationLogRepoMock.Setup(r => r.InsertAsync(It.IsAny<OperationLog>()))
            .ReturnsAsync(1);

        var dto = new LedgerDto
        {
            Id = 1, CardIdm = TestCardIdm,
            Date = new DateTime(2026, 1, 10),
            Summary = "鉄道（天神～博多）",
            Income = 0, Expense = 210, Balance = 2300
        };
        await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm);

        // Act
        await _viewModel.SaveAndEditNextCommand.ExecuteAsync(null);

        // Assert
        _viewModel.IsSaveAndEditNextRequested.Should().BeTrue("保存して次へが要求された");
        _viewModel.IsSaved.Should().BeFalse("IsSavedはfalseにリセットされる");
    }

    [Fact]
    public async Task SaveAndEditNext_CanSaveがfalseの場合何もしないこと()
    {
        // Arrange: 摘要を空にしてCanSave=falseにする
        await _viewModel.InitializeForAddAsync(TestCardIdm, CreateTestLedgers(), TestOperatorIdm);
        _viewModel.Summary = string.Empty; // バリデーションエラー
        _viewModel.CanSave.Should().BeFalse();

        // Act
        await _viewModel.SaveAndEditNextCommand.ExecuteAsync(null);

        // Assert
        _viewModel.IsSaveAndEditNextRequested.Should().BeFalse("保存できない場合は要求されない");
    }

    [Fact]
    public void Back_IsBackRequestedがtrueになること()
    {
        // Arrange
        _viewModel.IsBackRequested.Should().BeFalse("初期値はfalse");

        // Act
        _viewModel.BackCommand.Execute(null);

        // Assert
        _viewModel.IsBackRequested.Should().BeTrue("戻るが要求された");
    }

    [Fact]
    public async Task HasUnsavedChanges_Editモード初期化直後はfalseであること()
    {
        // Arrange
        var existingLedger = new Ledger
        {
            Id = 1, CardIdm = TestCardIdm,
            Date = new DateTime(2026, 1, 10),
            Summary = "鉄道（天神～博多）",
            Income = 0, Expense = 210, Balance = 2300,
            Details = new List<LedgerDetail>()
        };
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(existingLedger);

        var dto = new LedgerDto
        {
            Id = 1, CardIdm = TestCardIdm,
            Date = new DateTime(2026, 1, 10),
            Summary = "鉄道（天神～博多）",
            Income = 0, Expense = 210, Balance = 2300
        };
        await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm);

        // ShowSaveAndNextButton を有効にして「次へ」ボタンを使えるようにする
        _viewModel.ShowSaveAndNextButton = true;

        // Act: 変更なしで「戻る」を押す（確認ダイアログなしで戻れるはず）
        _viewModel.BackCommand.Execute(null);

        // Assert: 確認なしで戻れた
        _viewModel.IsBackRequested.Should().BeTrue("未変更時は確認なしで戻れる");
    }

    #endregion
}
