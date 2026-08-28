using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// LedgerRowEditViewModelの単体テスト（Issue #635）
/// </summary>
public class LedgerRowEditViewModelTests : IDisposable
{
    private readonly Mock<ILedgerRepository> _ledgerRepoMock;
    private readonly Mock<IStaffRepository> _staffRepoMock;
    private readonly Mock<IOperationLogRepository> _operationLogRepoMock;
    private readonly OperationLogger _operationLogger;
    private readonly DbContext _dbContext;
    private readonly LedgerRowEditViewModel _viewModel;

    /// <summary>
    /// 確認ダイアログ（Issue #1837 で <c>MessageBox.Show</c> 直呼びから <c>IDialogService</c> へ移行）。
    /// 既定では <c>ShowWarningConfirmation</c> が false（＝「いいえ」）を返すため、
    /// 確認を伴う操作を検証するテストは明示的に true を返すよう設定すること。
    /// </summary>
    private readonly Mock<IDialogService> _dialogServiceMock;

    private const string TestCardIdm = "0102030405060708";
    private const string TestOperatorIdm = "FFFF000000000001";

    private readonly Staff _staffA = new Staff { StaffIdm = "AAAA000000000001", Name = "田中太郎" };
    private readonly Staff _staffB = new Staff { StaffIdm = "BBBB000000000002", Name = "山田花子" };

    public LedgerRowEditViewModelTests()
    {
        _ledgerRepoMock = new Mock<ILedgerRepository>();
        _dialogServiceMock = new Mock<IDialogService>();
        _staffRepoMock = new Mock<IStaffRepository>();
        _operationLogRepoMock = new Mock<IOperationLogRepository>();
        _operationLogger = new OperationLogger(
            _operationLogRepoMock.Object,
            Mock.Of<ICurrentOperatorContext>());

        // Issue #1458: 実体の DbContext を使い、BeginTransactionAsync が本物の tx を返す状態でテスト
        _dbContext = TestDbContextFactory.Create();

        _staffRepoMock.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<Staff> { _staffA, _staffB });

        _viewModel = new LedgerRowEditViewModel(
            _ledgerRepoMock.Object,
            _staffRepoMock.Object,
            _operationLogger,
            _dbContext,
            _dialogServiceMock.Object);
    }

    public void Dispose()
    {
        _dbContext?.Dispose();
        GC.SuppressFinalize(this);
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

    #region Editモードの残高自動計算（Issue #1740）

    /// <summary>
    /// Issue #1740 の検証用: 前行残高 2,000 円のあとに続くチャージ行
    /// （受入 3,000 円・払出 0 円・残高 5,000 円）を Edit モードで開く。
    /// </summary>
    private LedgerDto SetupChargeRowForEdit()
    {
        var ledger = new Ledger
        {
            Id = 10, CardIdm = TestCardIdm,
            Date = new DateTime(2026, 1, 12),
            Summary = "役務費によりチャージ",
            Income = 3000, Expense = 0, Balance = 5000,
            LenderIdm = _staffA.StaffIdm,
            StaffName = _staffA.Name,
            Note = string.Empty
        };
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(10)).ReturnsAsync(ledger);

        return new LedgerDto
        {
            Id = 10, CardIdm = TestCardIdm,
            Date = ledger.Date, DateDisplay = "R8.1.12",
            Summary = ledger.Summary,
            Income = 3000, Expense = 0, Balance = 5000,
            StaffName = _staffA.Name
        };
    }

    /// <summary>
    /// Issue #1740: Edit モードで「自動計算」を ON にすると、前行残高を起点に再計算されること。
    /// 修正前は PreviousBalance が既定値 0 のままで 0+3000-0=3000 となり、
    /// DB の正しい残高 5,000 円を破壊していた。
    /// </summary>
    [Fact]
    public async Task EditMode_AutoBalanceOn_前行残高を起点に再計算されること()
    {
        // Arrange
        var dto = SetupChargeRowForEdit();
        await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm, previousBalance: 2000);

        // Act
        _viewModel.IsAutoBalance = true;

        // Assert: 2000 + 3000 - 0
        _viewModel.Balance.Should().Be(5000);
        _viewModel.PreviousBalance.Should().Be(2000);
    }

    /// <summary>
    /// Issue #1740: 自動計算 ON の状態で金額を修正しても、前行残高を起点に追随すること。
    /// </summary>
    [Fact]
    public async Task EditMode_AutoBalanceOn_金額修正時も前行残高を起点に追随すること()
    {
        // Arrange
        var dto = SetupChargeRowForEdit();
        await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm, previousBalance: 2000);
        _viewModel.IsAutoBalance = true;

        // Act: チャージ額を 3,000 → 5,000 に訂正
        _viewModel.Income = 5000;

        // Assert: 2000 + 5000 - 0
        _viewModel.Balance.Should().Be(7000);
    }

    /// <summary>
    /// Issue #1740: 前行が特定できない場合（ページ先頭行など）は自動計算を使えないこと。
    /// </summary>
    [Fact]
    public async Task EditMode_前行残高が不明なら自動計算を使えないこと()
    {
        // Arrange & Act
        var dto = SetupChargeRowForEdit();
        await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm, previousBalance: null);

        // Assert
        _viewModel.CanAutoBalance.Should().BeFalse();
    }

    /// <summary>
    /// Issue #1740: 前行が特定できない状態で自動計算フラグが立っても、残高を書き換えないこと。
    /// チェックボックスは無効化されるが、コード経路が残らないことを表明する（fail-safe）。
    /// </summary>
    [Fact]
    public async Task EditMode_前行残高が不明なら自動計算ONでも残高が変化しないこと()
    {
        // Arrange
        var dto = SetupChargeRowForEdit();
        await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm, previousBalance: null);

        // Act
        _viewModel.IsAutoBalance = true;

        // Assert: DB の値のまま
        _viewModel.Balance.Should().Be(5000);
    }

    /// <summary>
    /// Issue #1740: 引数を省略した呼び出しでは自動計算を使えないこと（fail-safe な既定値）。
    /// 呼び出し元の渡し忘れで残高が破壊されないことを表明する。
    /// </summary>
    [Fact]
    public async Task EditMode_前行残高を省略した場合はfail_safeに倒れること()
    {
        // Arrange
        var dto = SetupChargeRowForEdit();

        // Act
        await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm);
        _viewModel.IsAutoBalance = true;

        // Assert
        _viewModel.CanAutoBalance.Should().BeFalse();
        _viewModel.Balance.Should().Be(5000);
    }

    /// <summary>
    /// Issue #1740: 初期化直後は DB の残高がそのまま入り、自動計算は OFF で始まること。
    /// 修正前は初期化中にも誤った再計算が走り、直後の Balance 代入が偶然打ち消していた。
    /// </summary>
    [Fact]
    public async Task EditMode_初期化直後はDB値の残高と自動計算OFFで始まること()
    {
        // Arrange & Act
        var dto = SetupChargeRowForEdit();
        await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm, previousBalance: 2000);

        // Assert
        _viewModel.Balance.Should().Be(5000);
        _viewModel.IsAutoBalance.Should().BeFalse();
    }

    /// <summary>
    /// Issue #1740: Add モードの自動計算は従来どおり常に使えること（既存挙動の不変）。
    /// </summary>
    [Fact]
    public async Task AddMode_自動計算は常に使えること()
    {
        // Arrange & Act
        var allLedgers = CreateTestLedgers();
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);

        // Assert
        _viewModel.CanAutoBalance.Should().BeTrue();
        _viewModel.IsAutoBalance.Should().BeTrue();
    }

    /// <summary>
    /// Issue #1740: 自動計算が使えない場合、ToolTip がその理由と対処を説明すること。
    /// 無効化された操作部品の理由が利用者に伝わらない状態を防ぐ（error-messages.md）。
    /// </summary>
    [Fact]
    public async Task EditMode_自動計算が使えない場合はToolTipが理由と対処を示すこと()
    {
        // Arrange
        var dto = SetupChargeRowForEdit();
        var enabledTooltip = _viewModel.AutoBalanceToolTip;

        // Act
        await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm, previousBalance: null);

        // Assert
        _viewModel.AutoBalanceUnavailableReason.Should()
            .Be(AutoBalanceUnavailableReason.PreviousRowNotIdentified);
        _viewModel.AutoBalanceToolTip.Should().NotBe(enabledTooltip);
        _viewModel.AutoBalanceToolTip.Should().Contain("前の行");
        _viewModel.AutoBalanceToolTip.Should().EndWith("してください。");
    }

    /// <summary>
    /// Issue #1740: ToolTip の「どうすれば」は画面上に実在する操作でなければならない。
    /// 履歴一覧の表示期間は暦月固定で「広げる」操作が存在しないため、案内してはいけない。
    /// </summary>
    [Fact]
    public async Task EditMode_ToolTipが存在しない操作を案内しないこと()
    {
        // Arrange & Act
        var dto = SetupChargeRowForEdit();
        await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm, previousBalance: null);

        // Assert
        _viewModel.AutoBalanceToolTip.Should().NotContain("表示期間",
            "履歴一覧の期間は暦月固定で、広げる操作は画面に存在しない（Issue #1740）");
    }

    /// <summary>
    /// Issue #1740: Editモードで利用日を変更すると行の入る位置が変わるため、
    /// 初期化時に確定した直前行はもう直前ではなくなる。自動計算を無効化すること。
    /// </summary>
    [Fact]
    public async Task EditMode_利用日を変更したら自動計算が無効になること()
    {
        // Arrange
        var dto = SetupChargeRowForEdit();
        await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm, previousBalance: 2000);
        _viewModel.CanAutoBalance.Should().BeTrue();

        // Act: 利用日を後ろの日付へ訂正
        _viewModel.EditDate = new DateTime(2026, 1, 26);

        // Assert
        _viewModel.CanAutoBalance.Should().BeFalse();
        _viewModel.AutoBalanceUnavailableReason.Should()
            .Be(AutoBalanceUnavailableReason.EditDateChanged);
    }

    /// <summary>
    /// Issue #1740: 自動計算 ON のまま利用日を変更した場合、古い起点で残高を書き換えないこと。
    /// 自動計算は解除され、残高は ON にする前の値へ戻る。
    /// </summary>
    [Fact]
    public async Task EditMode_自動計算ONのまま利用日を変更しても古い起点で計算しないこと()
    {
        // Arrange
        var dto = SetupChargeRowForEdit();
        await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm, previousBalance: 2000);
        _viewModel.IsAutoBalance = true;
        _viewModel.Balance.Should().Be(5000);

        // Act: 利用日を後ろの日付へ訂正（この行はもう 2000 の次ではない）
        _viewModel.EditDate = new DateTime(2026, 1, 26);

        // Assert: 自動計算は解除され、ON にする前の DB 値へ戻る
        _viewModel.IsAutoBalance.Should().BeFalse();
        _viewModel.Balance.Should().Be(5000);
    }

    /// <summary>
    /// Issue #1740: 利用日を元に戻したら自動計算を再び使えること（一方通行にしない）。
    /// </summary>
    [Fact]
    public async Task EditMode_利用日を元に戻したら自動計算が再び使えること()
    {
        // Arrange
        var dto = SetupChargeRowForEdit();
        await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm, previousBalance: 2000);
        var originalDate = _viewModel.EditDate;
        _viewModel.EditDate = new DateTime(2026, 1, 26);
        _viewModel.CanAutoBalance.Should().BeFalse();

        // Act
        _viewModel.EditDate = originalDate;

        // Assert
        _viewModel.CanAutoBalance.Should().BeTrue();
        _viewModel.AutoBalanceUnavailableReason.Should().Be(AutoBalanceUnavailableReason.None);
    }

    /// <summary>
    /// Issue #1740: 自動計算を ON→OFF に戻したら、上書き前の残高が復元されること。
    /// 復元手段が無いと、試しにチェックを入れて外しただけで DB の元残高が失われる。
    /// </summary>
    [Fact]
    public async Task EditMode_自動計算をOFFに戻すと上書き前の残高が復元されること()
    {
        // Arrange: 前行残高が不整合で、自動計算値が DB 値と食い違うケース
        var dto = SetupChargeRowForEdit();
        await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm, previousBalance: 1000);

        // Act: ON にすると 1000+3000-0=4000 で上書きされる → OFF に戻す
        _viewModel.IsAutoBalance = true;
        _viewModel.Balance.Should().Be(4000);
        _viewModel.IsAutoBalance = false;

        // Assert: DB の元残高が戻る
        _viewModel.Balance.Should().Be(5000);
    }

    /// <summary>
    /// Issue #1740: 編集対象行が他PCに削除されていた場合、初期化を途中で打ち切り
    /// 無関係なバリデーションエラーを表示しないこと。
    /// </summary>
    [Fact]
    public async Task EditMode_対象行が存在しない場合に無関係なエラーを表示しないこと()
    {
        // Arrange: GetByIdAsync が null（他PCが削除済み）
        var dto = new LedgerDto
        {
            Id = 777, CardIdm = TestCardIdm,
            Date = new DateTime(2026, 1, 12), DateDisplay = "R8.1.12",
            Summary = "鉄道（天神～博多）", Income = 0, Expense = 210, Balance = 2090
        };
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(777)).ReturnsAsync((Ledger)null);

        // Act
        await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm, previousBalance: 2300);

        // Assert: 摘要未入力を理由にしたエラーは、行が存在しないという実際の原因と無関係
        _viewModel.ValidationMessage.Should().BeEmpty();
    }

    #endregion

    #region Addモードの自動計算の可否（Issue #1740）

    /// <summary>
    /// Issue #1740: 一覧の先頭がカードの履歴の先頭でない場合、先頭への挿入では
    /// 直前残高 0 を起点にできないため自動計算を無効化すること。
    /// 0 を起点にすると Edit モードと同型の残高破壊になる。
    /// </summary>
    [Fact]
    public async Task AddMode_一覧先頭がカード履歴の先頭でない場合_先頭挿入で自動計算が無効になること()
    {
        // Arrange: ページ2以降を想定（先頭より前に履歴がある）
        var allLedgers = CreateTestLedgers();
        await _viewModel.InitializeForAddAsync(
            TestCardIdm, allLedgers, TestOperatorIdm, historyStartsAtCardBeginning: false);

        // Act: 先頭まで挿入位置を上げる
        for (int i = 0; i < 3; i++)
        {
            _viewModel.MoveInsertPositionUpCommand.Execute(null);
        }

        // Assert
        _viewModel.InsertIndex.Should().Be(0);
        _viewModel.CanAutoBalance.Should().BeFalse();
        _viewModel.AutoBalanceUnavailableReason.Should()
            .Be(AutoBalanceUnavailableReason.PreviousRowNotIdentified);
    }

    /// <summary>
    /// Issue #1740: 先頭への挿入で自動計算が無効化されたあとは、残高が 0 起点で書き換わらないこと。
    /// </summary>
    [Fact]
    public async Task AddMode_先頭挿入で残高が0起点に書き換わらないこと()
    {
        // Arrange: 先頭の1つ下（InsertIndex=1）まで移動する。
        // ここまでは直前行（残高2300）が特定できるので 2300+3000=5300 が入る。
        var allLedgers = CreateTestLedgers();
        await _viewModel.InitializeForAddAsync(
            TestCardIdm, allLedgers, TestOperatorIdm, historyStartsAtCardBeginning: false);
        _viewModel.Income = 3000;
        _viewModel.MoveInsertPositionUpCommand.Execute(null);
        _viewModel.MoveInsertPositionUpCommand.Execute(null);
        _viewModel.InsertIndex.Should().Be(1);
        _viewModel.Balance.Should().Be(5300);

        // Act: さらに先頭へ（直前行が無くなる）
        _viewModel.MoveInsertPositionUpCommand.Execute(null);

        // Assert: 0 + 3000 - 0 = 3000 で上書きされず、直前の値のまま残る
        _viewModel.InsertIndex.Should().Be(0);
        _viewModel.Balance.Should().Be(5300);
        _viewModel.Balance.Should().NotBe(3000, "起点不明のまま 0 から計算してはいけない");
    }

    /// <summary>
    /// Issue #1740: 一覧の先頭がカードの履歴の先頭であれば、先頭への挿入で
    /// 直前残高 0 を起点にしてよい（そのカードの最初の行なので 0 が正しい）。
    /// </summary>
    [Fact]
    public async Task AddMode_一覧先頭がカード履歴の先頭なら先頭挿入でも自動計算が使えること()
    {
        // Arrange
        var allLedgers = CreateTestLedgers();
        await _viewModel.InitializeForAddAsync(
            TestCardIdm, allLedgers, TestOperatorIdm, historyStartsAtCardBeginning: true);
        _viewModel.Income = 3000;

        // Act
        for (int i = 0; i < 3; i++)
        {
            _viewModel.MoveInsertPositionUpCommand.Execute(null);
        }

        // Assert
        _viewModel.InsertIndex.Should().Be(0);
        _viewModel.CanAutoBalance.Should().BeTrue();
        _viewModel.PreviousBalance.Should().Be(0);
        _viewModel.Balance.Should().Be(3000);
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
        _viewModel.IsLentRecord.Should().BeFalse(
            "Issue #1574: 通常レコードの IsLentRecord フラグも初期化される");
        _viewModel.IsDeleteRequested.Should().BeFalse();
    }

    /// <summary>
    /// Issue #1574: 貸出中レコード（IsLentRecord = true）も Edit モードでは削除可能。
    /// 旧仕様（Issue #750）では <c>CanDelete=false</c> だったが、異常状態で残った
    /// 「（貸出中）」行の復旧手段がなくなる問題に対応し、削除を許可するように変更。
    /// 誤操作防止は <see cref="LedgerRowEditViewModel.RequestDelete"/> の警告メッセージで担保する。
    /// </summary>
    [Fact]
    public async Task EditMode_LentRecord_CanDelete_IsTrue_Issue1574()
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

        // Assert: 貸出中レコードも Edit モードでは削除可能（Issue #1574）
        _viewModel.CanDelete.Should().BeTrue(
            "Issue #1574: 異常状態で残った貸出中レコードを復旧するため削除可能とする");
        _viewModel.IsLentRecord.Should().BeTrue(
            "RequestDelete で貸出中専用の警告メッセージを出すためフラグが伝播する必要がある");
        _viewModel.IsDeleteRequested.Should().BeFalse(
            "削除要求はユーザーが明示的に行うまで false");
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

        _ledgerRepoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(100);

        // Act
        await _viewModel.SaveCommand.ExecuteAsync(null);

        // Assert
        _viewModel.IsSaved.Should().BeTrue();
        _ledgerRepoMock.Verify(r => r.InsertAsync(It.Is<Ledger>(l =>
            l.CardIdm == TestCardIdm &&
            l.Summary == "鉄道（博多～天神）" &&
            l.Expense == 210
        ), It.IsAny<SQLiteTransaction>()), Times.Once);
    }

    [Fact]
    public async Task SaveAdd_LogsOperation()
    {
        // Arrange
        var allLedgers = CreateTestLedgers();
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);

        _viewModel.Summary = "テスト摘要";
        _viewModel.Income = 500;

        _ledgerRepoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(100);

        _staffRepoMock.Setup(r => r.GetByIdmAsync(TestOperatorIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = TestOperatorIdm, Name = "操作者" });

        // Act
        await _viewModel.SaveCommand.ExecuteAsync(null);

        // Assert: 操作ログが記録される
        _operationLogRepoMock.Verify(r => r.InsertAsync(It.Is<OperationLog>(log =>
            log.Action == OperationLogger.Actions.Insert &&
            log.TargetTable == OperationLogger.Tables.Ledger
        ), It.IsAny<SQLiteTransaction>()), Times.Once);
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
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);

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
        ), It.IsAny<SQLiteTransaction>()), Times.Once);
    }

    #region 同行者数（Issue #1906）

    [Fact]
    public async Task InitializeForEdit_LoadsCompanionCountAndPreview()
    {
        var ledger = new Ledger
        {
            Id = 1, CardIdm = TestCardIdm, Date = new DateTime(2026, 1, 10),
            Summary = "鉄道（天神～博多）", Expense = 210, Balance = 2300,
            LenderIdm = _staffA.StaffIdm, StaffName = _staffA.Name, CompanionCount = 2
        };
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(ledger);
        var dto = new LedgerDto
        {
            Id = 1, CardIdm = TestCardIdm, Date = ledger.Date, DateDisplay = "R8.1.10",
            Summary = ledger.Summary, Expense = 210, Balance = 2300, StaffName = _staffA.Name, CompanionCount = 2
        };

        await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm);

        _viewModel.CompanionCount.Should().Be(2);
        _viewModel.DisplayStaffNamePreview.Should().Be("田中太郎 外2名");
    }

    [Fact]
    public async Task SaveEdit_PersistsCompanionCount_WithoutTouchingStaffName()
    {
        var ledger = new Ledger
        {
            Id = 1, CardIdm = TestCardIdm, Date = new DateTime(2026, 1, 10),
            Summary = "鉄道（天神～博多）", Expense = 210, Balance = 2300,
            LenderIdm = _staffA.StaffIdm, StaffName = _staffA.Name
        };
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(ledger);
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        _staffRepoMock.Setup(r => r.GetByIdmAsync(TestOperatorIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = TestOperatorIdm, Name = "操作者" });
        var dto = new LedgerDto
        {
            Id = 1, CardIdm = TestCardIdm, Date = ledger.Date, DateDisplay = "R8.1.10",
            Summary = ledger.Summary, Expense = 210, Balance = 2300, StaffName = _staffA.Name
        };
        await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm);

        _viewModel.CompanionCount = 1;
        await _viewModel.SaveCommand.ExecuteAsync(null);

        _viewModel.IsSaved.Should().BeTrue();
        _ledgerRepoMock.Verify(r => r.UpdateAsync(It.Is<Ledger>(l =>
            l.CompanionCount == 1 && l.StaffName == _staffA.Name
        ), It.IsAny<SQLiteTransaction>()), Times.Once, "staff_name には「外N名」を書き込まず companion_count だけを更新する");
    }

    [Fact]
    public async Task SaveAdd_PersistsCompanionCount()
    {
        var allLedgers = CreateTestLedgers();
        _ledgerRepoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(99);
        _staffRepoMock.Setup(r => r.GetByIdmAsync(TestOperatorIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = TestOperatorIdm, Name = "操作者" });
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);
        _viewModel.Summary = "鉄道（天神～博多）";
        _viewModel.Expense = 210;
        _viewModel.SelectedStaff = _staffA;
        _viewModel.CompanionCount = 3;

        await _viewModel.SaveCommand.ExecuteAsync(null);

        _ledgerRepoMock.Verify(r => r.InsertAsync(It.Is<Ledger>(l => l.CompanionCount == 3 && l.StaffName == _staffA.Name),
            It.IsAny<SQLiteTransaction>()), Times.Once);
    }

    [Fact]
    public async Task SaveEdit_CompanionCountUnchanged_DoesNotRecordFabricatedAuditDiff()
    {
        // Issue #1906 / #1726: 更新前スナップショットに CompanionCount を載せ忘れると
        // 摘要だけ直した保存で「同行者数 0 → 2」という実際には起きていない変更が監査ログに残る
        var ledger = new Ledger
        {
            Id = 1, CardIdm = TestCardIdm, Date = new DateTime(2026, 1, 10),
            Summary = "元の摘要", Expense = 210, Balance = 2300,
            LenderIdm = _staffA.StaffIdm, StaffName = _staffA.Name, CompanionCount = 2
        };
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(ledger);
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        _staffRepoMock.Setup(r => r.GetByIdmAsync(TestOperatorIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = TestOperatorIdm, Name = "操作者" });
        OperationLog? recorded = null;
        _operationLogRepoMock
            .Setup(r => r.InsertAsync(It.IsAny<OperationLog>(), It.IsAny<SQLiteTransaction>()))
            .Callback<OperationLog, SQLiteTransaction>((log, _) => recorded = log)
            .ReturnsAsync(1);
        var dto = new LedgerDto
        {
            Id = 1, CardIdm = TestCardIdm, Date = ledger.Date, DateDisplay = "R8.1.10",
            Summary = "元の摘要", Expense = 210, Balance = 2300, StaffName = _staffA.Name, CompanionCount = 2
        };
        await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm);

        _viewModel.Summary = "変更後の摘要";
        await _viewModel.SaveCommand.ExecuteAsync(null);

        recorded.Should().NotBeNull();
        recorded!.BeforeData.Should().Contain("\"CompanionCount\":2",
            "更新前データにも同行者数を載せる（載せないと 0 → 2 の虚偽の差分が 6 年保存の監査ログに残る）");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100)]
    public async Task Validate_CompanionCountOutOfRange_BlocksSaveWithThreeElementMessage(int value)
    {
        var allLedgers = CreateTestLedgers();
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);
        _viewModel.Summary = "鉄道（天神～博多）";
        _viewModel.Expense = 210;

        _viewModel.CompanionCount = value;

        _viewModel.CanSave.Should().BeFalse();
        _viewModel.ValidationMessage.Should().Contain(value.ToString())
            .And.Contain("0～99")
            .And.EndWith("入力してください。");
        _viewModel.FirstErrorField.Should().Be(nameof(LedgerRowEditViewModel.CompanionCount));
    }

    [Fact]
    public async Task Validate_CompanionCountInRange_AllowsSave()
    {
        var allLedgers = CreateTestLedgers();
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);
        _viewModel.Summary = "鉄道（天神～博多）";
        _viewModel.Expense = 210;

        _viewModel.CompanionCount = 99;

        _viewModel.CanSave.Should().BeTrue();
    }

    #endregion

    [Fact]
    public async Task SaveAdd_InsertFails_ShowsError()
    {
        // Arrange
        var allLedgers = CreateTestLedgers();
        await _viewModel.InitializeForAddAsync(TestCardIdm, allLedgers, TestOperatorIdm);

        _viewModel.Summary = "テスト";
        _viewModel.Income = 500;

        _ledgerRepoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()))
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
        _ledgerRepoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()))
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
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
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

    #region Issue #1837: 確認ダイアログの IDialogService 移行

    /*
     * 移行前は MessageBox.Show の直呼びだったため、これら 3 経路の単体テストは
     * 1 件も書けなかった（実モーダルが開いてテストランナーが止まる）。
     * IDialogService へ移した副次的な利得として、「確認で『いいえ』を選んだら
     * 破壊的な要求を立てない」というガードをここで初めて固定できる。
     */

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task RequestDelete_確認の結果に従って削除要求を立てること(bool confirmed, bool expected)
    {
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(new Ledger
        {
            Id = 1, CardIdm = TestCardIdm, Date = new DateTime(2026, 1, 10),
            Summary = "鉄道（天神～博多）", Income = 0, Expense = 210, Balance = 2300
        });
        await _viewModel.InitializeForEditAsync(
            new LedgerDto
            {
                Id = 1, CardIdm = TestCardIdm, DateDisplay = "R8.1.10",
                Summary = "鉄道（天神～博多）", Income = 0, Expense = 210, Balance = 2300,
                StaffName = _staffA.Name
            },
            TestOperatorIdm);
        _viewModel.CanDelete.Should().BeTrue("編集モードの初期化が成立していること（前提の表明）");
        _dialogServiceMock
            .Setup(d => d.ShowWarningConfirmation(It.IsAny<string>(), "履歴の削除"))
            .Returns(confirmed);

        _viewModel.RequestDeleteCommand.Execute(null);

        _viewModel.IsDeleteRequested.Should().Be(expected);
        _dialogServiceMock.Verify(
            d => d.ShowWarningConfirmation(It.IsAny<string>(), "履歴の削除"), Times.Once,
            "確認は IDialogService 経由で行うこと（MessageBox 直呼びはオーナー無しになる。Issue #1837）");
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void SkipToNext_未保存の変更がある場合は確認の結果に従うこと(bool confirmed, bool expected)
    {
        _viewModel.Mode = LedgerRowEditMode.Add;
        _viewModel.Summary = "入力途中";
        _dialogServiceMock
            .Setup(d => d.ShowWarningConfirmation(It.IsAny<string>(), "確認"))
            .Returns(confirmed);

        _viewModel.SkipToNextCommand.Execute(null);

        _viewModel.IsSkipToNextRequested.Should().Be(expected);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Back_未保存の変更がある場合は確認の結果に従うこと(bool confirmed, bool expected)
    {
        _viewModel.Mode = LedgerRowEditMode.Add;
        _viewModel.Summary = "入力途中";
        _dialogServiceMock
            .Setup(d => d.ShowWarningConfirmation(It.IsAny<string>(), "確認"))
            .Returns(confirmed);

        _viewModel.BackCommand.Execute(null);

        _viewModel.IsBackRequested.Should().Be(expected);
    }

    /// <summary>
    /// 対の表明: 未保存の変更が無いときは確認を出さずに進むこと。
    /// これが無いと「常に確認する」実装でも上のテストは緑になる。
    /// </summary>
    [Fact]
    public void SkipToNext_未保存の変更が無ければ確認を出さずに進むこと()
    {
        _viewModel.Mode = LedgerRowEditMode.Add;
        _viewModel.Summary = string.Empty;
        _viewModel.Income = 0;
        _viewModel.Expense = 0;

        _viewModel.SkipToNextCommand.Execute(null);

        _viewModel.IsSkipToNextRequested.Should().BeTrue();
        _dialogServiceMock.Verify(
            d => d.ShowWarningConfirmation(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    #endregion
}
