using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using ICCardManager.Common.Messages;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure.Timing;
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
    /// <summary>
    /// ViewModel が MainViewModel へ送るカード読み取り抑制メッセージ（Issue #852）を記録する。
    /// 抑制の取得／解放がダイアログの表示範囲と一致していることを表明するために使う（Issue #1807）。
    /// </summary>
    private readonly List<CardReadingSuppressedMessage> _suppressionMessages = new();
    /// <summary>
    /// Issue #1843: <c>OnCardRead</c> は fire-and-forget でディスパッチするため、
    /// 例外を観測するのは呼び出し元（<see cref="IDispatcherService"/>）の責務。
    /// 本番の <c>WpfDispatcherService</c> と同じく「記録して再スローしない」代役を使う。
    /// </summary>
    private readonly RecordingDispatcherService _dispatcher = new();
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

        var messenger = new WeakReferenceMessenger();
        messenger.Register<CardReadingSuppressedMessage>(this, (_, message) => _suppressionMessages.Add(message));

        _viewModel = new StaffManageViewModel(
            _staffRepositoryMock.Object,
            _cardReaderMock.Object,
            _validationServiceMock.Object,
            _operationLoggerMock.Object,
            _dialogServiceMock.Object,
            _staffAuthServiceMock.Object,
            messenger,
            _dispatcher);
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

        // Issue #1760: 削除前データを読めないと削除自体を行わないため、
        // 対象行が存在する（実 DB で成立する）状態を仕掛ける
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync("FFFF000000000001", false))
            .ReturnsAsync(new Staff { StaffIdm = "FFFF000000000001", Name = "田中太郎" });
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

        // Issue #1760: 削除前データを読めないと DeleteAsync まで到達しないため、
        // 対象行が存在する状態を仕掛けたうえで DeleteAsync に例外を注入する
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync("FFFF000000000001", false))
            .ReturnsAsync(new Staff { StaffIdm = "FFFF000000000001", Name = "田中太郎" });
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

    /// <summary>
    /// Issue #1760: 削除前データを読めなかったときは削除自体を行わないこと
    /// </summary>
    /// <remarks>
    /// カード側と同型。<c>DeleteAsync</c>（論理削除）の WHERE も <c>is_deleted = 0</c> のため、
    /// 読み取り後に他 PC が復元すると論理削除だけが確定して監査記録が残らない。
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_WhenTargetRowMissing_ShouldNotDeleteWithoutAuditLog()
    {
        // Arrange
        const string idm = "FFFF000000000001";
        _viewModel.SelectedStaff = new StaffDto
        {
            StaffIdm = idm,
            Name = "田中太郎",
            Number = "S-001"
        };

        // 読み取り時点では他 PC が論理削除済み
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync((Staff?)null);
        // その直後に他 PC が復元した → 論理削除は 1 行に一致して成功し得る
        _staffRepositoryMock.Setup(r => r.DeleteAsync(idm)).ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());

        // Act
        await _viewModel.DeleteAsync();

        // Assert
        _staffRepositoryMock.Verify(r => r.DeleteAsync(idm), Times.Never,
            "削除前データを読めていない状態で削除すると、変更が監査記録に残らない");
        _operationLogRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<OperationLog>()), Times.Never);

        // 一覧を再読込し、キャッシュも破棄していること（書き込みを通らないため #1759 の破棄が働かない）
        _staffRepositoryMock.Verify(r => r.InvalidateCache(), Times.Once);
        _staffRepositoryMock.Verify(r => r.GetAllAsync(), Times.Once);
        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.StatusMessage.Should().Contain("田中太郎");
        _viewModel.StatusMessage.Should().EndWith("やり直してください。");
    }

    /// <summary>
    /// Issue #1760: 削除が成功した経路では必ず操作ログが 1 行残ること（正常系の回帰固定）
    /// </summary>
    [Fact]
    public async Task DeleteAsync_WhenSucceeds_ShouldWriteAuditLog()
    {
        // Arrange
        const string idm = "FFFF000000000001";
        _viewModel.SelectedStaff = new StaffDto
        {
            StaffIdm = idm,
            Name = "田中太郎",
            Number = "S-001"
        };

        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false))
            .ReturnsAsync(new Staff { StaffIdm = idm, Name = "田中太郎", Number = "S-001" });
        _staffRepositoryMock.Setup(r => r.DeleteAsync(idm)).ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());

        // Act
        await _viewModel.DeleteAsync();

        // Assert
        _operationLogRepositoryMock.Verify(r => r.InsertAsync(It.Is<OperationLog>(log =>
            log.TargetTable == OperationLogger.Tables.Staff &&
            log.TargetId == idm &&
            log.Action == OperationLogger.Actions.Delete)), Times.Once);
    }

    /// <summary>
    /// Issue #1760: 復元の直後に他 PC が職員を削除しても、操作ログは残ること
    /// </summary>
    [Fact]
    public async Task SaveAsync_WhenRestoredStaffCannotBeReRead_ShouldStillWriteAuditLog()
    {
        // Arrange
        const string idm = "FFFF000000000001";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync(new Staff
        {
            StaffIdm = idm,
            Name = "田中太郎",
            Number = "S-001",
            IsDeleted = true
        });
        _staffRepositoryMock.Setup(r => r.RestoreAsync(idm)).ReturnsAsync(true);
        // 復元の直後に他 PC が削除した → 再読取は null
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync((Staff?)null);
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());

        OperationLog? recorded = null;
        _operationLogRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<OperationLog>()))
            .Callback<OperationLog>(log => recorded = log)
            .ReturnsAsync(1);

        _viewModel.StartNewStaff();
        _viewModel.EditStaffIdm = idm;
        _viewModel.EditName = "鈴木花子";

        // Act
        await _viewModel.SaveAsync();

        // Assert
        recorded.Should().NotBeNull("復元が確定した以上、監査記録を落としてはならない");
        recorded!.Action.Should().Be(OperationLogger.Actions.Restore);
        recorded.TargetId.Should().Be(idm);
        recorded.AfterData.Should().Contain("田中太郎", "復元前に読み取った値をそのまま記録すること");
    }

    /// <summary>
    /// Issue #1760: 書き込みを行わずに競合を案内する経路でも、一覧の再読込が
    /// キャッシュではなく DB を読むこと
    /// </summary>
    /// <remarks>
    /// Issue #1759 は影響行数 0 のときのキャッシュ破棄を <c>StaffRepository.UpdateAsync</c> の
    /// 内側に置いた。更新前データを読めなかった経路は <b>UpdateAsync を呼ばない</b>ため
    /// その契機が無く、<c>LoadStaffAsync()</c> が <c>GetAllAsync</c> のキャッシュ
    /// （既定 TTL 60 秒／共有モード 30 秒）から削除済みの職員を含む古い一覧を返す。
    /// 破棄と再読込の順序まで固定する（逆順だと古い一覧を読んでから破棄することになる）。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_ExistingStaff_WhenTargetRowMissing_ShouldInvalidateCacheBeforeReload()
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

        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync((Staff?)null);

        var callOrder = new List<string>();
        _staffRepositoryMock.Setup(r => r.InvalidateCache())
            .Callback(() => callOrder.Add("invalidate"));
        _staffRepositoryMock.Setup(r => r.GetAllAsync())
            .Callback(() => callOrder.Add("reload"))
            .ReturnsAsync(new List<Staff>());

        // Act
        await _viewModel.SaveAsync();

        // Assert
        callOrder.Should().Equal(new[] { "invalidate", "reload" },
            "書き込みを 1 回も行わない経路にはリポジトリ側のキャッシュ破棄（Issue #1759）が" +
            "働かないため、再読込より前に ViewModel から破棄すること");
    }

    #endregion

    #region Issue #1761: 一覧の選択が外れても編集を継続できること（SelectedStaff 非依存）

    /// <summary>
    /// Issue #1761: 編集中に一覧の選択が外れても、編集フォームと入力内容が保持されること
    /// </summary>
    /// <remarks>
    /// <c>SelectedItem="{Binding SelectedStaff}"</c> は TwoWay バインドのため、選択行の
    /// Ctrl+クリックや <c>StaffList.Clear()</c> による書き戻しで
    /// <see cref="StaffManageViewModel.SelectedStaff"/> だけが null に戻る。
    /// 編集対象は <c>EditStaffIdm</c>（主キー）が特定しており、編集は継続できる。
    /// カード側（<c>CardManageViewModelTests</c>）と同型のため同じ扱いにする。
    /// </remarks>
    [Fact]
    public void OnSelectedStaffChanged_WhenSelectionClearedDuringEdit_ShouldKeepEditFormAndInput()
    {
        // Arrange
        const string idm = "FFFF000000000001";
        _viewModel.SelectedStaff = new StaffDto
        {
            StaffIdm = idm,
            Name = "田中太郎",
            Number = "001",
            Note = "編集前のメモ"
        };
        _viewModel.StartEdit();
        _viewModel.EditNote = "入力途中のメモ";

        // Act - 選択行を Ctrl+クリックして選択解除した
        _viewModel.SelectedStaff = null;

        // Assert
        _viewModel.IsEditing.Should().BeTrue("選択解除でフォームを閉じると入力内容が予告なく消える");
        _viewModel.IsNewStaff.Should().BeFalse("既存職員の編集モードのままであること");
        _viewModel.EditStaffIdm.Should().Be(idm, "編集対象を特定するのは主キーであること");
        _viewModel.EditName.Should().Be("田中太郎");
        _viewModel.EditNumber.Should().Be("001");
        _viewModel.EditNote.Should().Be("入力途中のメモ");
    }

    /// <summary>
    /// Issue #1761: 選択が外れた状態で保存しても、<c>EditStaffIdm</c> の職員が更新され
    /// 監査ログも残ること
    /// </summary>
    [Fact]
    public async Task SaveAsync_WhenSelectionClearedDuringEdit_ShouldUpdateTargetIdentifiedByEditStaffIdm()
    {
        // Arrange
        const string idm = "FFFF000000000001";
        _viewModel.SelectedStaff = new StaffDto
        {
            StaffIdm = idm,
            Name = "田中太郎",
            Number = "001"
        };
        _viewModel.StartEdit();
        _viewModel.EditNote = "更新後のメモ";

        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync(new Staff
        {
            StaffIdm = idm,
            Name = "田中太郎",
            Number = "001",
            Note = "更新前のメモ"
        });
        _staffRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<Staff>())).ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());

        // 保存を押す直前に一覧の選択が外れた
        _viewModel.SelectedStaff = null;

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _staffRepositoryMock.Verify(r => r.UpdateAsync(It.Is<Staff>(s =>
            s.StaffIdm == idm && s.Note == "更新後のメモ")), Times.Once);

        _operationLogRepositoryMock.Verify(r => r.InsertAsync(It.Is<OperationLog>(log =>
            log.TargetTable == OperationLogger.Tables.Staff &&
            log.TargetId == idm &&
            log.Action == OperationLogger.Actions.Update &&
            log.AfterData!.Contains("更新後のメモ"))), Times.Once);

        _viewModel.StatusMessage.Should().Be("更新しました");
        _viewModel.IsStatusError.Should().BeFalse();
    }

    /// <summary>
    /// Issue #1761: 選択が外れた状態で競合しても、案内は<b>一覧に載っていた氏名</b>で
    /// 対象を名指しすること
    /// </summary>
    /// <remarks>
    /// Issue #1759 の実装は <c>SelectedStaff</c> を優先し null のときだけ未保存の入力値へ
    /// 退避する形だったため、選択が外れると<b>禁じたはずの「未保存の入力値による名指し」</b>へ落ちていた。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_WhenSelectionClearedAndUpdateConflicts_ShouldNameTargetByItsListedName()
    {
        // Arrange
        const string idm = "FFFF000000000001";
        _viewModel.SelectedStaff = new StaffDto
        {
            StaffIdm = idm,
            Name = "田中太郎",
            Number = "001"
        };
        _viewModel.StartEdit();
        _viewModel.EditName = "田中花子";  // 氏名を打ち直した（結婚等）

        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync((Staff?)null);
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());

        // 一覧の再読込などで選択が外れた
        _viewModel.SelectedStaff = null;

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.StatusMessage.Should().Contain("田中太郎",
            "選択が外れていても、編集開始時に一覧へ載っていた氏名で名指しすること");
        _viewModel.StatusMessage.Should().NotContain("田中花子",
            "未保存の入力値は一覧のどこにも存在せず、案内どおりの確認ができない");
        _viewModel.StatusMessage.Should().EndWith("やり直してください。");
        _viewModel.EditName.Should().Be("田中花子", "入力内容は消さないこと");
    }

    /// <summary>
    /// Issue #1761: 編集中に一覧で別の行を選び直したら、名指しに使う表記も切替後の行に追随すること
    /// </summary>
    [Fact]
    public async Task SaveAsync_WhenEditTargetSwitchedThenConflicts_ShouldNameSwitchedTarget()
    {
        // Arrange
        const string firstIdm = "FFFF000000000001";
        const string secondIdm = "FFFF000000000002";
        _viewModel.SelectedStaff = new StaffDto
        {
            StaffIdm = firstIdm,
            Name = "田中太郎",
            Number = "001"
        };
        _viewModel.StartEdit();

        // 編集中に一覧で別の職員を選び直した（フォームの中身も差し替わる）
        _viewModel.SelectedStaff = new StaffDto
        {
            StaffIdm = secondIdm,
            Name = "鈴木花子",
            Number = "002"
        };
        _viewModel.EditStaffIdm.Should().Be(secondIdm, "前提: 選択切替で編集対象が差し替わること");

        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(secondIdm, false)).ReturnsAsync((Staff?)null);
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());

        // 保存直前に選択が外れる（再読込による書き戻し）
        _viewModel.SelectedStaff = null;

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.StatusMessage.Should().Contain("鈴木花子", "切替後の職員で名指しすること");
        _viewModel.StatusMessage.Should().NotContain("田中太郎", "切替前の職員で名指ししないこと");
    }

    /// <summary>
    /// Issue #1761: 認証ダイアログの待機中に選択が外れても、削除は開始時点の対象に対して行われること
    /// </summary>
    /// <remarks>
    /// 職員側の <c>DeleteAsync</c> は Issue #1759 で識別情報をメソッド冒頭（最初の await より前）へ
    /// 確定させており、この形が正しいことをカード側と対で固定する（片方だけ直すと退行に気付けない）。
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_WhenSelectionClearedDuringAuthentication_ShouldStillDeleteInitialTarget()
    {
        // Arrange
        const string idm = "FFFF000000000001";
        _viewModel.SelectedStaff = new StaffDto
        {
            StaffIdm = idm,
            Name = "田中太郎",
            Number = "001"
        };

        // 認証（職員証タッチ待ち）の最中に一覧の選択が外れた
        _staffAuthServiceMock.Setup(s => s.RequestAuthenticationAsync(It.IsAny<string>()))
            .Callback(() => _viewModel.SelectedStaff = null)
            .ReturnsAsync(new StaffAuthResult { Idm = "TEST_OPERATOR_IDM", StaffName = "テスト操作者" });

        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false))
            .ReturnsAsync(new Staff { StaffIdm = idm, Name = "田中太郎", Number = "001" });
        _staffRepositoryMock.Setup(r => r.DeleteAsync(idm)).ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());

        // Act
        await _viewModel.DeleteAsync();

        // Assert
        _staffRepositoryMock.Verify(r => r.DeleteAsync(idm), Times.Once);
        _operationLogRepositoryMock.Verify(r => r.InsertAsync(It.Is<OperationLog>(log =>
            log.TargetTable == OperationLogger.Tables.Staff &&
            log.TargetId == idm &&
            log.Action == OperationLogger.Actions.Delete)), Times.Once);
        _viewModel.StatusMessage.Should().Be("削除しました");
    }

    #endregion

    #region Issue #1807: 抑制の取得／解放を職員登録ダイアログの表示範囲と一致させること

    /// <summary>
    /// 未登録カード経由（<see cref="StaffManageViewModel.StartNewStaffWithIdmAsync"/>）で
    /// 職員登録モードに入ったときも、MainViewModel のカード読み取りを抑制すること。
    /// この経路は #852 の抑制を一度も送っておらず、氏名入力中の別カードタッチが
    /// 背後の貸出・返却や 2 枚目のダイアログを引き起こしていた（Issue #1807 の 3）。
    /// </summary>
    [Fact]
    public async Task StartNewStaffWithIdmAsync_未登録職員証では抑制を取得したまま氏名入力を待つこと()
    {
        // Arrange
        var idm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync((Staff)null);

        // Act
        var shouldClose = await _viewModel.StartNewStaffWithIdmAsync(idm);

        // Assert
        shouldClose.Should().BeFalse("ダイアログは氏名入力のために開いたまま");
        _suppressionMessages.Should().Contain(m => m.Value && m.Source == CardReadingSource.StaffRegistration,
            "登録モードに入った時点で抑制を取得する");
        _suppressionMessages.Should().NotContain(m => !m.Value,
            "ダイアログが開いている間は抑制を解放しない（解放は CancelEdit / Cleanup のみ）");
    }

    /// <summary>
    /// 「新規登録」→ 職員証タッチで IDm を読み取った直後に抑制を解放しないこと。
    /// ダイアログはモーダルのまま氏名入力を待っているため、ここで解放すると
    /// 別カードのタッチが MainViewModel へ届き、背後で貸出・返却が進む（Issue #1807 の 3）。
    /// </summary>
    [Fact]
    public async Task HandleCardReadAsync_未登録職員証を読み取っても抑制を解放しないこと()
    {
        // Arrange
        var idm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync((Staff)null);
        _viewModel.StartNewStaff();
        _suppressionMessages.Should().ContainSingle(m => m.Value, "前提: 新規登録開始で抑制を取得している");

        // Act
        await _viewModel.HandleCardReadAsync(idm);

        // Assert
        _viewModel.EditStaffIdm.Should().Be(idm);
        _viewModel.IsEditing.Should().BeTrue("氏名入力のためフォームは開いたまま");
        _suppressionMessages.Should().NotContain(m => !m.Value,
            "IDm 読み取り後もダイアログは開いているので抑制を維持する");
    }

    /// <summary>
    /// 登録済み職員証を読み取った場合（フォームはそのまま残す）も抑制を解放しないこと。
    /// </summary>
    [Fact]
    public async Task HandleCardReadAsync_登録済み職員証を読み取っても抑制を解放しないこと()
    {
        // Arrange
        var idm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true))
            .ReturnsAsync(new Staff { StaffIdm = idm, Name = "田中太郎", Number = "001", IsDeleted = false });
        _viewModel.StartNewStaff();

        // Act
        await _viewModel.HandleCardReadAsync(idm);

        // Assert
        _viewModel.IsStatusError.Should().BeTrue("登録済みの案内は赤色で表示する（Issue #286）");
        _viewModel.IsEditing.Should().BeTrue("フォームはそのまま残す");
        _suppressionMessages.Should().NotContain(m => !m.Value);
    }

    /// <summary>
    /// 編集キャンセル（登録モードの終了）で抑制を解放すること。
    /// </summary>
    [Fact]
    public void CancelEdit_登録モード終了時に抑制を解放すること()
    {
        // Arrange
        _viewModel.StartNewStaff();

        // Act
        _viewModel.CancelEdit();

        // Assert - 取得（true）→ 解放（false）の順で送られていること
        var acquireIndex = _suppressionMessages.FindIndex(
            m => m.Value && m.Source == CardReadingSource.StaffRegistration);
        acquireIndex.Should().BeGreaterOrEqualTo(0, "新規登録開始で抑制を取得している");
        _suppressionMessages.Last().Value.Should().BeFalse();
        _suppressionMessages.Last().Source.Should().Be(CardReadingSource.StaffRegistration);
        (_suppressionMessages.Count - 1).Should().BeGreaterThan(acquireIndex, "解放は取得の後に送られる");
    }

    /// <summary>
    /// ダイアログ終了（Cleanup）で抑制を解放すること。
    /// 未登録カード経由で入口の抑制を取得しても、登録済み等でダイアログを閉じる経路は
    /// この解放で必ず回収される。
    /// </summary>
    [Fact]
    public async Task Cleanup_ダイアログ終了時に抑制を解放すること()
    {
        // Arrange - 登録済み職員証で StartNewStaffWithIdmAsync がダイアログを閉じる経路
        var idm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true))
            .ReturnsAsync(new Staff { StaffIdm = idm, Name = "田中太郎", Number = "001" });
        var shouldClose = await _viewModel.StartNewStaffWithIdmAsync(idm);
        shouldClose.Should().BeTrue("前提: 登録済みなのでダイアログを閉じる");

        // Act
        _viewModel.Cleanup();

        // Assert - 入口で取得（true）したものを Cleanup の解放（false）が回収していること
        var acquireIndex = _suppressionMessages.FindIndex(
            m => m.Value && m.Source == CardReadingSource.StaffRegistration);
        acquireIndex.Should().BeGreaterOrEqualTo(0, "登録済みで閉じる経路でも入口で抑制を取得している");
        _suppressionMessages.Last().Value.Should().BeFalse();
        _suppressionMessages.Last().Source.Should().Be(CardReadingSource.StaffRegistration);
        (_suppressionMessages.Count - 1).Should().BeGreaterThan(acquireIndex, "解放は取得の後に送られる");
    }

    #endregion

    #region Issue #1816: 職員証読み取りの fire-and-forget が例外を握りつぶさないこと

    /// <summary>
    /// 読み取り中に DB 例外が出たら、例外を呼び出し元へ抜かずステータスへ案内すること
    /// </summary>
    [Fact]
    public async Task HandleCardReadAsync_読み取り中の例外_ステータスへ案内し例外を伝播しないこと()
    {
        // Arrange
        var idm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true))
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        _viewModel.StartNewStaff();

        // Act
        Func<Task> act = () => _viewModel.HandleCardReadAsync(idm);

        // Assert
        await act.Should().NotThrowAsync("fire-and-forget の呼び出し元は例外を観測できないため");
        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.StatusMessage.Should().NotContain(
            "database is locked", "生の例外メッセージを職員へ出さないこと（Issue #1614）");
        _viewModel.StatusMessage.Should().EndWith("してください。");
        _viewModel.IsWaitingForCard.Should().BeTrue("タッチ待ちへ戻して再試行できること");
        _viewModel.EditStaffIdm.Should().BeEmpty("確認の済んでいない IDm をフォームに残さないこと");
    }

    /// <summary>
    /// 復元が確定した後の後処理で例外が出ても、読み取り失敗として案内しないこと
    /// </summary>
    /// <remarks>
    /// Issue #1816 のコードレビューで判明。<c>RestoreAsync</c> は既にコミット済みなので、
    /// 「もう一度職員証をタッチしてください」と案内すると、職員は復元済みの職員証を再タッチして
    /// 「既に登録されています」を見ることになる（#1727 / #1805）。
    /// </remarks>
    [Fact]
    public async Task HandleCardReadAsync_復元後の後処理で例外_復元は記録済みと案内し再タッチを促さないこと()
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
        // 復元は確定済み。その後の一覧再読込が共有モードのロックで失敗する
        _staffRepositoryMock.Setup(r => r.GetAllAsync())
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        _viewModel.StartNewStaff();

        // Act
        Func<Task> act = () => _viewModel.HandleCardReadAsync(idm);

        // Assert
        await act.Should().NotThrowAsync();
        _viewModel.StatusMessage.Should().Contain(
            "記録済み", "復元は確定しているため、失敗したかのように案内しないこと");
        _viewModel.StatusMessage.Should().NotContain(
            "もう一度職員証をタッチ", "再タッチを促すと「既に登録されています」に行き着く");
        _viewModel.StatusMessage.Should().NotContain(
            "database is locked", "生の例外メッセージを職員へ出さないこと（Issue #1614）");
        _viewModel.StatusMessage.Should().EndWith("してください。", "行動指示で終わること");
        _viewModel.IsWaitingForCard.Should().BeFalse("再タッチを待たないこと");
    }

    /// <summary>
    /// 対のテスト: 正常に読み取れた場合はタッチ待ちを解除しエラーにしないこと
    /// </summary>
    [Fact]
    public async Task HandleCardReadAsync_未登録職員証_タッチ待ちを解除しエラーにしないこと()
    {
        // Arrange
        var idm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync((Staff?)null);
        _viewModel.StartNewStaff();

        // Act
        await _viewModel.HandleCardReadAsync(idm);

        // Assert
        _viewModel.IsStatusError.Should().BeFalse();
        _viewModel.IsWaitingForCard.Should().BeFalse();
        _viewModel.EditStaffIdm.Should().Be(idm);
    }

    /// <summary>
    /// Issue #1816: タッチ待ちでない状態で本体が実行されても、状態を書き換えないこと
    /// </summary>
    [Fact]
    public async Task HandleCardReadAsync_タッチ待ちでなければ何もしないこと()
    {
        // Arrange
        var firstIdm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(It.IsAny<string>(), true)).ReturnsAsync((Staff?)null);
        _viewModel.StartNewStaff();
        await _viewModel.HandleCardReadAsync(firstIdm);
        _viewModel.IsWaitingForCard.Should().BeFalse("前提: 1 件目の読み取りでタッチ待ちが解除される");

        // Act
        await _viewModel.HandleCardReadAsync("0807060504030201");

        // Assert
        _viewModel.EditStaffIdm.Should().Be(firstIdm, "2 件目が 1 件目の読み取り結果を上書きしないこと");
        _staffRepositoryMock.Verify(r => r.GetByIdmAsync("0807060504030201", true), Times.Never);
    }

    #endregion

    #region Issue #1843: 読み取りのディスパッチ自体が例外を観測すること

    /// <summary>
    /// カード読み取りイベントが <see cref="IDispatcherService"/> 経由でディスパッチされ、
    /// 本体の catch 自体が失敗しても例外が観測される（無言で失われない）こと
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #1843: <c>OnCardRead</c> が生の <c>Application.Current.Dispatcher.InvokeAsync</c> を
    /// 使っていた頃は、戻り値の <c>DispatcherOperation&lt;Task&gt;</c> も内側の <c>Task</c> も
    /// 観測されず、例外は GC 契機の <c>TaskScheduler.UnobservedTaskException</c> まで遅れていた。
    /// </para>
    /// <para>
    /// Issue #1816 の「本体全体を try/catch で包む」は受け皿としては正しいが fail-safe ではない。
    /// <c>catch</c> ブロック自身（<c>CancelEdit()</c> の <c>_messenger.Send</c>、
    /// <c>StatusMessage</c> 代入の <c>PropertyChanged</c>）が投げれば再び無言になる
    /// （.claude/rules/development-conventions.md Issue #1745）。
    /// ここでは <c>PropertyChanged</c> の購読側を失敗させて catch ブロックを壊し、
    /// それでもディスパッチャが例外を観測することを表明する。
    /// </para>
    /// </remarks>
    [Fact]
    public void OnCardRead_本体のcatchが失敗しても_ディスパッチャが例外を観測すること()
    {
        // Arrange
        var idm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true))
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        _viewModel.StartNewStaff();

        // catch ブロック末尾の IsStatusError = true で例外が出る状況を作る
        // （バインディング側の失敗に相当。catch の中の後始末は、それ自体が失敗し得る＝#1745）
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StaffManageViewModel.IsStatusError) && _viewModel.IsStatusError)
            {
                throw new InvalidOperationException("binding failure");
            }
        };

        // Act
        _cardReaderMock.Raise(r => r.CardRead += null, new CardReadEventArgs { Idm = idm });

        // Assert
        _dispatcher.InvokeAsyncFuncCallCount.Should().Be(
            1, "OnCardRead は IDispatcherService 経由でディスパッチすること（生の Dispatcher を使わない）");
        _dispatcher.ObservedExceptions.Should().ContainSingle(
            "本体の catch が失敗しても、ディスパッチした側が例外を観測すること")
            .Which.Message.Should().Be("binding failure");
    }

    /// <summary>
    /// 正常な読み取りではディスパッチャが例外を観測しないこと（対のテスト）
    /// </summary>
    /// <remarks>
    /// 片側だけだと「常に例外が出る」実装でも緑になる
    /// （.claude/rules/error-messages.md Issue #1757）。
    /// </remarks>
    [Fact]
    public void OnCardRead_正常な読み取り_例外を観測せずIDmを反映すること()
    {
        // Arrange
        var idm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true))
            .ReturnsAsync((Staff)null);
        _viewModel.StartNewStaff();

        // Act
        _cardReaderMock.Raise(r => r.CardRead += null, new CardReadEventArgs { Idm = idm });

        // Assert
        _dispatcher.ObservedExceptions.Should().BeEmpty();
        _viewModel.EditStaffIdm.Should().Be(idm);
        _viewModel.IsWaitingForCard.Should().BeFalse();
    }

    #endregion
}