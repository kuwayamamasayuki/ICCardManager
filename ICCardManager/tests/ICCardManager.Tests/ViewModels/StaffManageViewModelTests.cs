using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Moq;
using Xunit;
using IOperationLogRepository = ICCardManager.Data.Repositories.IOperationLogRepository;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// StaffManageViewModelの単体テスト
/// </summary>
public class StaffManageViewModelTests
{
    private readonly Mock<IStaffRepository> _staffRepositoryMock;
    private readonly Mock<ICardReader> _cardReaderMock;
    private readonly Mock<IValidationService> _validationServiceMock;
    private readonly Mock<OperationLogger> _operationLoggerMock;
    /// <summary>
    /// 操作ログの記録先。<see cref="OperationLogger"/> のログ記録メソッドは virtual ではないため
    /// <see cref="_operationLoggerMock"/> では検証できない（モックの実体が本物の実装を実行する）。
    /// 「ログが残ったか」は本物の実装が書き込むこのリポジトリで検証する（Issue #1760）。
    /// </summary>
    private readonly Mock<IOperationLogRepository> _operationLogRepositoryMock;
    private readonly Mock<IDialogService> _dialogServiceMock;
    private readonly Mock<IStaffAuthService> _staffAuthServiceMock;
    private readonly StaffManageViewModel _viewModel;

    public StaffManageViewModelTests()
    {
        _staffRepositoryMock = new Mock<IStaffRepository>();
        _cardReaderMock = new Mock<ICardReader>();
        _validationServiceMock = new Mock<IValidationService>();
        _dialogServiceMock = new Mock<IDialogService>();
        _staffAuthServiceMock = new Mock<IStaffAuthService>();

        // OperationLoggerのモック（コンストラクタ引数が必要なためMock.Ofで作成）
        _operationLogRepositoryMock = new Mock<IOperationLogRepository>();
        _operationLoggerMock = new Mock<OperationLogger>(_operationLogRepositoryMock.Object, Mock.Of<ICurrentOperatorContext>());

        // バリデーションはデフォルトで成功を返す
        _validationServiceMock.Setup(v => v.ValidateStaffIdm(It.IsAny<string>())).Returns(ValidationResult.Success());
        _validationServiceMock.Setup(v => v.ValidateStaffName(It.IsAny<string>())).Returns(ValidationResult.Success());

        // ダイアログはデフォルトでYes/Trueを返す（テストがブロックされないように）
        _dialogServiceMock.Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        _dialogServiceMock.Setup(d => d.ShowWarningConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        // 認証はデフォルトで成功を返す（Issue #429）
        _staffAuthServiceMock.Setup(s => s.RequestAuthenticationAsync(It.IsAny<string>()))
            .ReturnsAsync(new StaffAuthResult { Idm = "TEST_OPERATOR_IDM", StaffName = "テスト操作者" });

        _viewModel = new StaffManageViewModel(
            _staffRepositoryMock.Object,
            _cardReaderMock.Object,
            _validationServiceMock.Object,
            _operationLoggerMock.Object,
            _dialogServiceMock.Object,
            _staffAuthServiceMock.Object,
            new WeakReferenceMessenger());
    }

    #region 職員一覧読み込みテスト

    /// <summary>
    /// 職員一覧が正しく読み込まれること
    /// </summary>
    [Fact]
    public async Task LoadStaffAsync_ShouldLoadStaffOrderedByNumberAndName()
    {
        // Arrange
        var staffList = new List<Staff>
        {
            new() { StaffIdm = "01", Name = "田中太郎", Number = "002" },
            new() { StaffIdm = "02", Name = "鈴木花子", Number = "001" },
            new() { StaffIdm = "03", Name = "山田次郎", Number = "001" }
        };
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(staffList);

        // Act
        await _viewModel.LoadStaffAsync();

        // Assert
        _viewModel.StaffList.Should().HaveCount(3);
        // 番号→氏名順にソートされている
        _viewModel.StaffList[0].Number.Should().Be("001");
        _viewModel.StaffList[0].Name.Should().Be("山田次郎");
        _viewModel.StaffList[1].Number.Should().Be("001");
        _viewModel.StaffList[1].Name.Should().Be("鈴木花子");
        _viewModel.StaffList[2].Number.Should().Be("002");
    }

    /// <summary>
    /// 職員一覧が空の場合、空のコレクションになること
    /// </summary>
    [Fact]
    public async Task LoadStaffAsync_WithNoStaff_ShouldHaveEmptyCollection()
    {
        // Arrange
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());

        // Act
        await _viewModel.LoadStaffAsync();

        // Assert
        _viewModel.StaffList.Should().BeEmpty();
    }

    #endregion

    #region 新規登録モードテスト

    /// <summary>
    /// 新規登録モードが正しく開始されること
    /// </summary>
    [Fact]
    public void StartNewStaff_ShouldSetEditingModeCorrectly()
    {
        // Arrange
        _viewModel.SelectedStaff = new StaffDto { StaffIdm = "existing", Name = "既存職員" };

        // Act
        _viewModel.StartNewStaff();

        // Assert
        _viewModel.IsEditing.Should().BeTrue();
        _viewModel.IsNewStaff.Should().BeTrue();
        _viewModel.IsWaitingForCard.Should().BeTrue();
        _viewModel.SelectedStaff.Should().BeNull();
        _viewModel.EditStaffIdm.Should().BeEmpty();
        _viewModel.EditName.Should().BeEmpty();
        _viewModel.EditNumber.Should().BeEmpty();
        _viewModel.EditNote.Should().BeEmpty();
        _viewModel.StatusMessage.Should().Contain("タッチ");
    }

    #endregion

    #region 編集モードテスト

    /// <summary>
    /// 編集モードが正しく開始されること
    /// </summary>
    [Fact]
    public void StartEdit_ShouldLoadSelectedStaffData()
    {
        // Arrange
        var staff = new StaffDto
        {
            StaffIdm = "FFFF000000000001",
            Name = "田中太郎",
            Number = "S-001",
            Note = "テスト職員"
        };
        _viewModel.SelectedStaff = staff;

        // Act
        _viewModel.StartEdit();

        // Assert
        _viewModel.IsEditing.Should().BeTrue();
        _viewModel.IsNewStaff.Should().BeFalse();
        _viewModel.IsWaitingForCard.Should().BeFalse();
        _viewModel.EditStaffIdm.Should().Be("FFFF000000000001");
        _viewModel.EditName.Should().Be("田中太郎");
        _viewModel.EditNumber.Should().Be("S-001");
        _viewModel.EditNote.Should().Be("テスト職員");
    }

    /// <summary>
    /// 職員未選択時に編集モードを開始しても何も起きないこと
    /// </summary>
    [Fact]
    public void StartEdit_WithNoSelectedStaff_ShouldDoNothing()
    {
        // Arrange
        _viewModel.SelectedStaff = null;
        _viewModel.IsEditing = false;

        // Act
        _viewModel.StartEdit();

        // Assert
        _viewModel.IsEditing.Should().BeFalse();
    }

    #endregion

    #region 保存テスト

    /// <summary>
    /// 新規職員が正常に保存されること
    /// </summary>
    /// <remarks>
    /// 本テストはリポジトリ呼び出しで成功を検証する。完了メッセージが
    /// 残ることは Issue #1759 の *_ShouldKeepCompletionMessage が担保する
    /// （かつては CancelEdit() がメッセージを消していたため検証できなかった）。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_NewStaff_ShouldInsertStaff()
    {
        // Arrange
        _viewModel.StartNewStaff();
        _viewModel.EditStaffIdm = "FFFF000000000001";
        _viewModel.EditName = "田中太郎";
        _viewModel.EditNumber = "S-001";
        _viewModel.EditNote = "新規職員";

        _staffRepositoryMock.Setup(r => r.GetByIdmAsync("FFFF000000000001", true)).ReturnsAsync((Staff?)null);
        _staffRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Staff>())).ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());

        // Act
        await _viewModel.SaveAsync();

        // Assert - リポジトリが正しく呼ばれ、編集モードが終了していること
        _staffRepositoryMock.Verify(r => r.InsertAsync(It.Is<Staff>(s =>
            s.StaffIdm == "FFFF000000000001" &&
            s.Name == "田中太郎" &&
            s.Number == "S-001" &&
            s.Note == "新規職員"
        )), Times.Once);
        _viewModel.IsEditing.Should().BeFalse(); // CancelEdit()で編集モード終了
    }

    /// <summary>
    /// 重複する職員証は登録できないこと
    /// </summary>
    [Fact]
    public async Task SaveAsync_NewStaff_WithDuplicateIdm_ShouldShowError()
    {
        // Arrange
        _viewModel.StartNewStaff();
        _viewModel.EditStaffIdm = "FFFF000000000001";
        _viewModel.EditName = "田中太郎";

        var existingStaff = new Staff { StaffIdm = "FFFF000000000001", Name = "既存職員", Number = "E001" };
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync("FFFF000000000001", true)).ReturnsAsync(existingStaff);

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.StatusMessage.Should().Contain("既に登録");
        _viewModel.StatusMessage.Should().Contain("既存職員");  // 氏名が表示されること
        _staffRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Staff>()), Times.Never);
    }

    /// <summary>
    /// 職員証IDmが空の場合、保存できないこと
    /// </summary>
    [Fact]
    public async Task SaveAsync_WithEmptyIdm_ShouldShowError()
    {
        // Arrange
        _viewModel.StartNewStaff();
        _viewModel.EditStaffIdm = "";
        _viewModel.EditName = "田中太郎";

        // 空のIDmに対してエラーを返すようモックを設定
        _validationServiceMock.Setup(v => v.ValidateStaffIdm(string.Empty))
            .Returns(ValidationResult.Failure("IDmを入力してください"));

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.StatusMessage.Should().Contain("IDm");
        _staffRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Staff>()), Times.Never);
    }

    /// <summary>
    /// 氏名が空の場合、保存できないこと
    /// </summary>
    [Fact]
    public async Task SaveAsync_WithEmptyName_ShouldShowError()
    {
        // Arrange
        _viewModel.StartNewStaff();
        _viewModel.EditStaffIdm = "FFFF000000000001";
        _viewModel.EditName = "";

        // 空の氏名に対してエラーを返すようモックを設定
        _validationServiceMock.Setup(v => v.ValidateStaffName(string.Empty))
            .Returns(ValidationResult.Failure("氏名を入力してください"));

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.StatusMessage.Should().Contain("氏名");
        _staffRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Staff>()), Times.Never);
    }

    /// <summary>
    /// 職員番号が空でも登録できること（任意項目）
    /// </summary>
    [Fact]
    public async Task SaveAsync_WithEmptyNumber_ShouldSaveWithNullNumber()
    {
        // Arrange
        _viewModel.StartNewStaff();
        _viewModel.EditStaffIdm = "FFFF000000000001";
        _viewModel.EditName = "田中太郎";
        _viewModel.EditNumber = "";

        _staffRepositoryMock.Setup(r => r.GetByIdmAsync("FFFF000000000001", true)).ReturnsAsync((Staff?)null);
        _staffRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Staff>())).ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _staffRepositoryMock.Verify(r => r.InsertAsync(It.Is<Staff>(s => s.Number == null)), Times.Once);
    }

    /// <summary>
    /// 職員が正常に更新されること
    /// </summary>
    /// <remarks>
    /// 本テストはリポジトリ呼び出しで成功を検証する。完了メッセージが
    /// 残ることは Issue #1759 の *_ShouldKeepCompletionMessage が担保する
    /// （かつては CancelEdit() がメッセージを消していたため検証できなかった）。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_ExistingStaff_ShouldUpdateStaff()
    {
        // Arrange
        var existingStaff = new StaffDto
        {
            StaffIdm = "FFFF000000000001",
            Name = "田中太郎",
            Number = "S-001"
        };
        _viewModel.SelectedStaff = existingStaff;
        _viewModel.StartEdit();
        _viewModel.EditName = "田中花子"; // 名前を変更
        _viewModel.EditNote = "更新後のメモ";

        // Issue #1760: 更新前データを読めないと更新自体を行わないため、
        // 対象行が存在する（実 DB で成立する）状態を仕掛ける
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync("FFFF000000000001", false))
            .ReturnsAsync(new Staff { StaffIdm = "FFFF000000000001", Name = "田中太郎", Number = "S-001" });
        _staffRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<Staff>())).ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());

        // Act
        await _viewModel.SaveAsync();

        // Assert - リポジトリが正しく呼ばれ、編集モードが終了していること
        _staffRepositoryMock.Verify(r => r.UpdateAsync(It.Is<Staff>(s =>
            s.StaffIdm == "FFFF000000000001" &&
            s.Name == "田中花子" &&
            s.Note == "更新後のメモ"
        )), Times.Once);
        _viewModel.IsEditing.Should().BeFalse(); // CancelEdit()で編集モード終了
    }

    /// <summary>
    /// 保存に失敗した場合、エラーメッセージが表示されること
    /// </summary>
    [Fact]
    public async Task SaveAsync_WhenInsertFails_ShouldShowError()
    {
        // Arrange
        _viewModel.StartNewStaff();
        _viewModel.EditStaffIdm = "FFFF000000000001";
        _viewModel.EditName = "田中太郎";

        _staffRepositoryMock.Setup(r => r.GetByIdmAsync("FFFF000000000001", true)).ReturnsAsync((Staff?)null);
        _staffRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Staff>())).ReturnsAsync(false);

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.StatusMessage.Should().Contain("失敗");
    }

    /// <summary>
    /// 保存中に例外が発生した場合、生の <c>ex.Message</c> を漏らさず
    /// 3要素準拠（操作名を含み「～ください。」で終わる）の文言を表示すること（Issue #1614）。
    /// </summary>
    [Fact]
    public async Task SaveAsync_WhenExceptionThrown_ShouldShowUserFriendlyMessageWithoutRawDetail()
    {
        // Arrange
        const string rawTechnicalDetail = "SQLite Error 19: UNIQUE constraint failed";
        _viewModel.StartNewStaff();
        _viewModel.EditStaffIdm = "FFFF000000000001";
        _viewModel.EditName = "田中太郎";

        _staffRepositoryMock.Setup(r => r.GetByIdmAsync("FFFF000000000001", true)).ReturnsAsync((Staff?)null);
        _staffRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Staff>()))
            .ThrowsAsync(new Exception(rawTechnicalDetail));

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.StatusMessage.Should().NotContain(rawTechnicalDetail);   // 生の技術詳細が漏れない
        _viewModel.StatusMessage.Should().Contain("職員の保存");             // 「何が」= 操作名
        _viewModel.StatusMessage.Should().EndWith("ください。");             // 行動指示で終わる
    }

    #endregion

    #region 削除テスト

    /// <summary>
    /// 職員が正常に削除されること
    /// </summary>
    /// <remarks>
    /// 削除成功時のリポジトリ呼び出しと状態変更を検証します。
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_ShouldDeleteStaff()
    {
        // Arrange
        var staff = new StaffDto
        {
            StaffIdm = "FFFF000000000001",
            Name = "田中太郎"
        };
        _viewModel.SelectedStaff = staff;

        _staffRepositoryMock.Setup(r => r.DeleteAsync("FFFF000000000001")).ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());

        // Act
        await _viewModel.DeleteAsync();

        // Assert - リポジトリが正しく呼ばれたことを検証
        _staffRepositoryMock.Verify(r => r.DeleteAsync("FFFF000000000001"), Times.Once);
        // 削除後にLoadStaffAsyncが呼ばれて一覧が更新される
        _staffRepositoryMock.Verify(r => r.GetAllAsync(), Times.Once);
    }

    /// <summary>
    /// 職員未選択時に削除しても何も起きないこと
    /// </summary>
    [Fact]
    public async Task DeleteAsync_WithNoSelectedStaff_ShouldDoNothing()
    {
        // Arrange
        _viewModel.SelectedStaff = null;

        // Act
        await _viewModel.DeleteAsync();

        // Assert
        _staffRepositoryMock.Verify(r => r.DeleteAsync(It.IsAny<string>()), Times.Never);
    }

    // Issue #1759: 「削除に失敗した場合にエラーメッセージが表示されること」を検証していた
    // DeleteAsync_WhenDeleteFails_ShouldShowError は、同じ状況（DeleteAsync=false）を
    // より強く表明する DeleteAsync_WhenDeleteMatchesNoRow_ShouldReloadStaffAndShowActionableError
    // へ統合した（本ファイル末尾の Issue #1759 リージョン）。旧テストは
    // StatusMessage.Contain("失敗") で規約違反の旧文言をピン留めしていた。

    /// <summary>
    /// 削除中に例外が発生した場合、生の <c>ex.Message</c> を漏らさず
    /// 3要素準拠（操作名を含み「～ください。」で終わる）の文言を表示すること（Issue #1614）。
    /// </summary>
    [Fact]
    public async Task DeleteAsync_WhenExceptionThrown_ShouldShowUserFriendlyMessageWithoutRawDetail()
    {
        // Arrange
        const string rawTechnicalDetail = "SQLite Error 5: database is locked";
        var staff = new StaffDto { StaffIdm = "FFFF000000000001", Name = "田中太郎" };
        _viewModel.SelectedStaff = staff;

        _staffRepositoryMock.Setup(r => r.DeleteAsync("FFFF000000000001"))
            .ThrowsAsync(new Exception(rawTechnicalDetail));

        // Act
        await _viewModel.DeleteAsync();

        // Assert
        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.StatusMessage.Should().NotContain(rawTechnicalDetail);   // 生の技術詳細が漏れない
        _viewModel.StatusMessage.Should().Contain("職員の削除");             // 「何が」= 操作名
        _viewModel.StatusMessage.Should().EndWith("ください。");             // 行動指示で終わる
    }

    #endregion

    #region ハイライト表示テスト（Issue #707）

    /// <summary>
    /// 新規職員保存後、NewlyRegisteredIdmが保存IDmに設定されること
    /// </summary>
    [Fact]
    public async Task SaveAsync_NewStaff_ShouldSetNewlyRegisteredIdm()
    {
        // Arrange
        var idm = "FFFF000000000001";
        _viewModel.StartNewStaff();
        _viewModel.EditStaffIdm = idm;
        _viewModel.EditName = "田中太郎";

        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync((Staff?)null);
        _staffRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Staff>())).ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>
        {
            new() { StaffIdm = idm, Name = "田中太郎", Number = null }
        });

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.NewlyRegisteredIdm.Should().Be(idm);
    }

    /// <summary>
    /// 既存職員更新後、NewlyRegisteredIdmが更新したIDmに設定されること
    /// </summary>
    [Fact]
    public async Task SaveAsync_UpdateStaff_ShouldSetNewlyRegisteredIdm()
    {
        // Arrange
        var idm = "FFFF000000000001";
        var existingStaff = new StaffDto
        {
            StaffIdm = idm,
            Name = "田中太郎",
            Number = "S-001"
        };
        _viewModel.SelectedStaff = existingStaff;
        _viewModel.StartEdit();
        _viewModel.EditName = "田中花子";

        // Issue #1760: 更新前データを読めないと更新自体を行わないため、
        // 対象行が存在する（実 DB で成立する）状態を仕掛ける
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false))
            .ReturnsAsync(new Staff { StaffIdm = idm, Name = "田中太郎", Number = "S-001" });
        _staffRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<Staff>())).ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>
        {
            new() { StaffIdm = idm, Name = "田中花子", Number = "S-001" }
        });

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.NewlyRegisteredIdm.Should().Be(idm);
    }

    /// <summary>
    /// 同じIDmで連続操作してもNewlyRegisteredIdmが再設定されること
    /// </summary>
    [Fact]
    public async Task SaveAsync_SameIdmTwice_ShouldResetAndSetNewlyRegisteredIdm()
    {
        // Arrange
        var idm = "FFFF000000000001";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync((Staff?)null);
        _staffRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Staff>())).ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>
        {
            new() { StaffIdm = idm, Name = "田中太郎", Number = null }
        });

        // Act: 1回目
        _viewModel.StartNewStaff();
        _viewModel.EditStaffIdm = idm;
        _viewModel.EditName = "田中太郎";
        await _viewModel.SaveAsync();

        // PropertyChangedイベントの発火を確認するためトラッキング
        var propertyChangedCount = 0;
        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(StaffManageViewModel.NewlyRegisteredIdm)
                && _viewModel.NewlyRegisteredIdm != null)
                propertyChangedCount++;
        };

        // Act: 2回目（同じIDm）— 更新として
        var existingStaff = new StaffDto { StaffIdm = idm, Name = "田中太郎" };
        _viewModel.SelectedStaff = existingStaff;
        _viewModel.StartEdit();
        _viewModel.EditName = "田中花子";
        // Issue #1760: 更新前データを読めないと更新自体を行わないため、
        // 対象行が存在する（実 DB で成立する）状態を仕掛ける
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false))
            .ReturnsAsync(new Staff { StaffIdm = idm, Name = "田中太郎" });
        _staffRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<Staff>())).ReturnsAsync(true);
        await _viewModel.SaveAsync();

        // Assert: 2回目でもPropertyChangedが発火していること
        propertyChangedCount.Should().BeGreaterOrEqualTo(1);
        _viewModel.NewlyRegisteredIdm.Should().Be(idm);
    }

    #endregion

    #region キャンセルテスト

    /// <summary>
    /// 編集をキャンセルすると状態がリセットされること
    /// </summary>
    [Fact]
    public void CancelEdit_ShouldResetState()
    {
        // Arrange
        _viewModel.StartNewStaff();
        _viewModel.EditStaffIdm = "FFFF000000000001";
        _viewModel.EditName = "田中太郎";
        _viewModel.EditNumber = "S-001";
        _viewModel.StatusMessage = "何かのメッセージ";

        // Act
        _viewModel.CancelEdit();

        // Assert
        _viewModel.IsEditing.Should().BeFalse();
        _viewModel.IsNewStaff.Should().BeFalse();
        _viewModel.IsWaitingForCard.Should().BeFalse();
        _viewModel.EditStaffIdm.Should().BeEmpty();
        _viewModel.EditName.Should().BeEmpty();
        _viewModel.EditNumber.Should().BeEmpty();
        _viewModel.EditNote.Should().BeEmpty();
        _viewModel.StatusMessage.Should().BeEmpty();
    }

    #endregion

    #region フォーカス制御テスト（Issue #1429）

    /// <summary>
    /// 未登録職員証で StartNewStaffWithIdmAsync を呼ぶと RequestNameFocus が 1 回発火すること。
    /// </summary>
    /// <remarks>
    /// 職員証タッチ → IDm 取り込み → 新規登録モード遷移直後に View 側でフォーカスを当てるための通知。
    /// 発火対象は「未登録職員証」分岐のみ（既登録/削除済み分岐はダイアログを閉じるため不要）。
    /// </remarks>
    [Fact]
    public async Task StartNewStaffWithIdmAsync_WithUnregisteredCard_ShouldRaiseRequestNameFocus()
    {
        // Arrange
        var idm = "FFFF000000000001";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync((Staff?)null);

        var raisedCount = 0;
        _viewModel.RequestNameFocus += (_, _) => raisedCount++;

        // Act
        var shouldClose = await _viewModel.StartNewStaffWithIdmAsync(idm);

        // Assert
        shouldClose.Should().BeFalse();              // ダイアログは開いたまま（編集続行）
        _viewModel.IsWaitingForCard.Should().BeFalse();
        _viewModel.IsEditing.Should().BeTrue();
        raisedCount.Should().Be(1);                  // フォーカス要求が 1 回だけ発火
    }

    /// <summary>
    /// 登録済み職員証で StartNewStaffWithIdmAsync を呼んだ場合は RequestNameFocus を発火しないこと。
    /// </summary>
    /// <remarks>
    /// 既登録カードはダイアログを閉じる分岐に進むため、氏名入力欄へのフォーカス要求は不要。
    /// </remarks>
    [Fact]
    public async Task StartNewStaffWithIdmAsync_WithRegisteredCard_ShouldNotRaiseRequestNameFocus()
    {
        // Arrange
        var idm = "FFFF000000000001";
        var existingStaff = new Staff
        {
            StaffIdm = idm,
            Name = "田中太郎",
            Number = "S-001",
            IsDeleted = false
        };
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync(existingStaff);

        var raisedCount = 0;
        _viewModel.RequestNameFocus += (_, _) => raisedCount++;

        // Act
        var shouldClose = await _viewModel.StartNewStaffWithIdmAsync(idm);

        // Assert
        shouldClose.Should().BeTrue();               // 既登録なのでダイアログを閉じる
        raisedCount.Should().Be(0);                  // フォーカス要求は発火しない
    }

    #endregion

    #region Issue #1759: 影響行数0（競合）を検出したときの案内と一覧再読込

    /// <summary>
    /// Issue #1759: 編集保存で UpdateAsync が false を返したとき、
    /// 3要素の案内を出し、職員一覧を再読込すること
    /// </summary>
    /// <remarks>
    /// <c>StaffRepository.UpdateAsync</c> が false を返すのは
    /// <c>UPDATE ... WHERE staff_idm = @staffIdm AND is_deleted = 0</c> が 0 行に一致した場合だけ。
    /// カード側（<c>CardManageViewModel</c>）と同一の欠陥形状のため併せて是正する。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_ExistingStaff_WhenUpdateMatchesNoRow_ShouldReloadStaffAndShowActionableError()
    {
        // Arrange
        var idm = "0102030405060708";
        _viewModel.SelectedStaff = new StaffDto
        {
            StaffIdm = idm,
            Name = "田中太郎",
            Number = "001"
        };
        _viewModel.StartEdit();
        _viewModel.EditNote = "備考の誤字を直した";

        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync(new Staff
        {
            StaffIdm = idm,
            Name = "田中太郎",
            Number = "001"
        });
        // 他PCがこの職員を論理削除した → WHERE is_deleted = 0 に 0 行 → false
        _staffRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<Staff>())).ReturnsAsync(false);
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _staffRepositoryMock.Verify(r => r.GetAllAsync(), Times.Once);

        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.StatusMessage.Length.Should().BeGreaterOrEqualTo(20);
        _viewModel.StatusMessage.Should().Contain("田中太郎");           // 何が
        _viewModel.StatusMessage.Should().Contain("削除された可能性");    // なぜ
        _viewModel.StatusMessage.Should().EndWith("やり直してください。"); // どうすれば

        // 入力内容を失わせない
        _viewModel.IsEditing.Should().BeTrue();
        _viewModel.EditNote.Should().Be("備考の誤字を直した");
    }

    /// <summary>
    /// Issue #1759: 削除済み職員の復元で RestoreAsync が false を返したとき、
    /// 3要素の案内を出し、職員一覧を再読込すること
    /// </summary>
    [Fact]
    public async Task SaveAsync_NewStaff_WhenRestoreMatchesNoRow_ShouldReloadStaffAndShowActionableError()
    {
        // Arrange
        var idm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync(new Staff
        {
            StaffIdm = idm,
            Name = "田中太郎",
            Number = "001",
            IsDeleted = true
        });
        // 他PCが先に復元した → WHERE is_deleted = 1 に 0 行 → false
        _staffRepositoryMock.Setup(r => r.RestoreAsync(idm)).ReturnsAsync(false);
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());

        _viewModel.StartNewStaff();
        _viewModel.EditStaffIdm = idm;
        _viewModel.EditName = "鈴木花子";

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _staffRepositoryMock.Verify(r => r.GetAllAsync(), Times.Once);

        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.StatusMessage.Length.Should().BeGreaterOrEqualTo(20);
        _viewModel.StatusMessage.Should().Contain("田中太郎");               // 何が（削除時点の氏名）
        _viewModel.StatusMessage.Should().Contain("先に復元された可能性");    // なぜ
        _viewModel.StatusMessage.Should().EndWith("やり直してください。");    // どうすれば
        _viewModel.StatusMessage.Should().NotContain("削除された可能性");
    }

    /// <summary>
    /// Issue #1759: 削除で DeleteAsync が false を返したとき、
    /// 3要素の案内を出し、職員一覧を再読込すること
    /// </summary>
    /// <remarks>
    /// <c>StaffRepository.DeleteAsync</c> の WHERE も <c>is_deleted = 0</c> のため、
    /// false は「他 PC が先に削除した」ことを意味する。
    /// カード側の削除は <c>CardOperationResult</c> を返し Issue #1109 で既に是正済みだが、
    /// 職員側は bool のままで案内が「削除に失敗しました」の9文字だけだった。
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_WhenDeleteMatchesNoRow_ShouldReloadStaffAndShowActionableError()
    {
        // Arrange
        var idm = "0102030405060708";
        _viewModel.SelectedStaff = new StaffDto
        {
            StaffIdm = idm,
            Name = "田中太郎",
            Number = "001"
        };

        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync(new Staff
        {
            StaffIdm = idm,
            Name = "田中太郎",
            Number = "001"
        });
        _staffRepositoryMock.Setup(r => r.DeleteAsync(idm)).ReturnsAsync(false);
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());

        // Act
        await _viewModel.DeleteAsync();

        // Assert
        _staffRepositoryMock.Verify(r => r.GetAllAsync(), Times.Once);

        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.StatusMessage.Length.Should().BeGreaterOrEqualTo(20);
        _viewModel.StatusMessage.Should().Contain("田中太郎");                // 何が
        _viewModel.StatusMessage.Should().Contain("先に削除された可能性");     // なぜ
        _viewModel.StatusMessage.Should().EndWith("やり直してください。");     // どうすれば
    }

    /// <summary>
    /// Issue #1759: 一覧の再読込で選択が解除されても、削除の競合エラーが
    /// 例外にならず案内文言として表示されること
    /// </summary>
    /// <remarks>
    /// <para>
    /// 本番の <c>StaffManageDialog</c> は <c>SelectedItem="{Binding SelectedStaff}"</c>（TwoWay）で
    /// DataGrid に束縛されている。<c>LoadStaffAsync()</c> の <c>StaffList.Clear()</c> は
    /// Selector の選択解除を引き起こし、それが <b>SelectedStaff = null</b> として書き戻される。
    /// 再読込のあとで <c>SelectedStaff.Name</c> を読むと <c>NullReferenceException</c> になり、
    /// 3要素の案内の代わりに <c>ExceptionMessageFormatter</c> の汎用文言が出る。
    /// </para>
    /// <para>
    /// ViewModel 単体テストには View が無いためこの経路は素通りする。
    /// ここでは <c>CollectionChanged</c> を購読して DataGrid の書き戻しを再現し、
    /// 「識別情報を再読込より前に確定させる」実装を固定する。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_WhenReloadClearsSelection_ShouldStillShowActionableError()
    {
        // Arrange
        var idm = "0102030405060708";
        _viewModel.SelectedStaff = new StaffDto
        {
            StaffIdm = idm,
            Name = "田中太郎",
            Number = "001"
        };

        // DataGrid の SelectedItem バインドを再現する（Clear() で選択が解除される）
        _viewModel.StaffList.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                _viewModel.SelectedStaff = null;
            }
        };

        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync(new Staff
        {
            StaffIdm = idm,
            Name = "田中太郎",
            Number = "001"
        });
        _staffRepositoryMock.Setup(r => r.DeleteAsync(idm)).ReturnsAsync(false);
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());

        // Act
        await _viewModel.DeleteAsync();

        // Assert
        _viewModel.SelectedStaff.Should().BeNull("一覧の再読込で選択が解除された状況を再現している");

        // 例外の汎用文言（ExceptionMessageFormatter）ではなく競合の案内が出ること
        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.StatusMessage.Should().Contain("田中太郎");
        _viewModel.StatusMessage.Should().Contain("先に削除された可能性");
        _viewModel.StatusMessage.Should().NotContain("職員の削除");
    }

    /// <summary>
    /// Issue #1759: 編集中に氏名を書き換えていても、競合の案内は
    /// <b>一覧に載っている氏名</b>で対象を名指しすること
    /// </summary>
    /// <remarks>
    /// 「一覧で状態を確認してからやり直してください」と案内する以上、
    /// 一覧に存在しない編集後の氏名で名指しすると案内どおりの確認ができない。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_WhenUpdateMatchesNoRow_ShouldNameTargetByItsListedName()
    {
        // Arrange
        var idm = "0102030405060708";
        _viewModel.SelectedStaff = new StaffDto
        {
            StaffIdm = idm,
            Name = "田中太郎",
            Number = "001"
        };
        _viewModel.StartEdit();
        _viewModel.EditName = "田中花子";  // 改姓を入力した直後に他PCが削除した

        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync((Staff?)null);
        _staffRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<Staff>())).ReturnsAsync(false);
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.StatusMessage.Should().Contain("田中太郎", "一覧に載っている氏名で名指しすること");
        _viewModel.StatusMessage.Should().NotContain("田中花子", "未保存の入力値で名指ししないこと");
        _viewModel.EditName.Should().Be("田中花子", "入力内容は消さないこと");
    }

    #endregion

    #region Issue #1759: 成功メッセージが CancelEdit() で消されないこと

    /// <summary>
    /// Issue #1759: 登録成功時、完了メッセージが表示されたまま残ること
    /// </summary>
    /// <remarks>
    /// <c>CancelEdit()</c> は <c>StatusMessage</c> を空にするため、完了メッセージを
    /// その<b>前</b>に設定すると一度も表示されない。ステータス欄の所在（XAML）と
    /// この順序の両方が揃って初めてメッセージが利用者に届く。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_NewStaff_WhenInsertSucceeds_ShouldKeepCompletionMessage()
    {
        // Arrange
        _viewModel.StartNewStaff();
        _viewModel.EditStaffIdm = "FFFF000000000001";
        _viewModel.EditName = "田中太郎";

        _staffRepositoryMock.Setup(r => r.GetByIdmAsync("FFFF000000000001", true)).ReturnsAsync((Staff?)null);
        _staffRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Staff>())).ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.StatusMessage.Should().Be("登録しました");
        _viewModel.IsStatusError.Should().BeFalse();
        _viewModel.IsEditing.Should().BeFalse(); // CancelEdit() は従来どおり呼ばれる
    }

    /// <summary>
    /// Issue #1759: 更新成功時、完了メッセージが表示されたまま残ること
    /// </summary>
    [Fact]
    public async Task SaveAsync_ExistingStaff_WhenUpdateSucceeds_ShouldKeepCompletionMessage()
    {
        // Arrange
        var idm = "FFFF000000000001";
        _viewModel.SelectedStaff = new StaffDto { StaffIdm = idm, Name = "田中太郎", Number = "001" };
        _viewModel.StartEdit();
        _viewModel.EditNote = "更新後のメモ";

        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync(new Staff
        {
            StaffIdm = idm,
            Name = "田中太郎",
            Number = "001"
        });
        _staffRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<Staff>())).ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.StatusMessage.Should().Be("更新しました");
        _viewModel.IsStatusError.Should().BeFalse();
    }

    /// <summary>
    /// Issue #1759: 削除成功時、完了メッセージが表示されたまま残ること
    /// </summary>
    /// <remarks>
    /// 本 PR でステータス欄を編集フォームの外へ移した直接の動機がこの経路。
    /// 所在を直しても順序が直っていなければ、やはり一度も表示されない。
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_WhenSucceeds_ShouldKeepCompletionMessage()
    {
        // Arrange
        var idm = "FFFF000000000001";
        _viewModel.SelectedStaff = new StaffDto { StaffIdm = idm, Name = "田中太郎", Number = "001" };

        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync(new Staff
        {
            StaffIdm = idm,
            Name = "田中太郎",
            Number = "001"
        });
        _staffRepositoryMock.Setup(r => r.DeleteAsync(idm)).ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());

        // Act
        await _viewModel.DeleteAsync();

        // Assert
        _viewModel.StatusMessage.Should().Be("削除しました");
        _viewModel.IsStatusError.Should().BeFalse();
    }

    /// <summary>
    /// Issue #1759: 削除済み職員の復元成功時、完了メッセージが表示されたまま残ること
    /// </summary>
    [Fact]
    public async Task SaveAsync_NewStaff_WhenRestoreSucceeds_ShouldKeepCompletionMessage()
    {
        // Arrange
        var idm = "FFFF000000000001";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync(new Staff
        {
            StaffIdm = idm,
            Name = "田中太郎",
            Number = "001",
            IsDeleted = true
        });
        _staffRepositoryMock.Setup(r => r.RestoreAsync(idm)).ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync(new Staff
        {
            StaffIdm = idm,
            Name = "田中太郎",
            Number = "001"
        });
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());

        _viewModel.StartNewStaff();
        _viewModel.EditStaffIdm = idm;
        _viewModel.EditName = "鈴木花子";

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.StatusMessage.Should().Be("田中太郎（001） を復元しました");
        _viewModel.IsStatusError.Should().BeFalse();
    }

    #endregion

    #region Issue #1760: 監査ログを残せない書き込みを行わないこと

    /// <summary>
    /// Issue #1760: 更新前データを読めなかったときは更新自体を行わないこと
    /// </summary>
    /// <remarks>
    /// カード側（<c>CardManageViewModel</c>）と同型の欠陥。<c>GetByIdmAsync</c>
    /// （<c>is_deleted = 0</c>）が null を返した後に他 PC がその職員を復元すると、
    /// <c>UpdateAsync</c> が 1 行に一致して成功する一方で
    /// <c>if (beforeStaff != null)</c> により <c>operation_log</c> に 1 行も残らない。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_ExistingStaff_WhenTargetRowMissing_ShouldNotUpdateWithoutAuditLog()
    {
        // Arrange
        const string idm = "FFFF000000000001";
        _viewModel.SelectedStaff = new StaffDto
        {
            StaffIdm = idm,
            Name = "田中太郎",
            Number = "S-001"
        };
        _viewModel.StartEdit();
        _viewModel.EditName = "田中花子";

        // 読み取り時点では他 PC が論理削除済み
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync((Staff?)null);
        // その直後に他 PC が復元した → UPDATE は 1 行に一致して成功し得る
        _staffRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<Staff>())).ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _staffRepositoryMock.Verify(r => r.UpdateAsync(It.IsAny<Staff>()), Times.Never,
            "更新前データを読めていない状態で書き込むと、変更が監査記録に残らない");
        _operationLogRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<OperationLog>()), Times.Never);

        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.StatusMessage.Should().Contain("田中太郎");
        _viewModel.StatusMessage.Should().EndWith("やり直してください。");
        _staffRepositoryMock.Verify(r => r.GetAllAsync(), Times.Once);
        _viewModel.EditName.Should().Be("田中花子", "入力内容は消さないこと");
    }

    /// <summary>
    /// Issue #1760: 更新が成功した経路では必ず操作ログが 1 行残ること（正常系の回帰固定）
    /// </summary>
    [Fact]
    public async Task SaveAsync_ExistingStaff_WhenUpdateSucceeds_ShouldWriteAuditLog()
    {
        // Arrange
        const string idm = "FFFF000000000001";
        _viewModel.SelectedStaff = new StaffDto
        {
            StaffIdm = idm,
            Name = "田中太郎",
            Number = "S-001"
        };
        _viewModel.StartEdit();
        _viewModel.EditName = "田中花子";

        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync(new Staff
        {
            StaffIdm = idm,
            Name = "田中太郎",
            Number = "S-001"
        });
        _staffRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<Staff>())).ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _operationLogRepositoryMock.Verify(r => r.InsertAsync(It.Is<OperationLog>(log =>
            log.TargetTable == OperationLogger.Tables.Staff &&
            log.TargetId == idm &&
            log.Action == OperationLogger.Actions.Update &&
            log.BeforeData!.Contains("田中太郎") &&
            log.AfterData!.Contains("田中花子"))), Times.Once);
    }

    #endregion
}
