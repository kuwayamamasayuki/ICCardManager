using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Models;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// StaffManageViewModelの単体テスト
/// </summary>
public class StaffManageViewModelTests
{
    private readonly Mock<IStaffRepository> _staffRepositoryMock;
    private readonly Mock<ICardReader> _cardReaderMock;
    private readonly StaffManageViewModel _viewModel;

    public StaffManageViewModelTests()
    {
        _staffRepositoryMock = new Mock<IStaffRepository>();
        _cardReaderMock = new Mock<ICardReader>();
        _viewModel = new StaffManageViewModel(
            _staffRepositoryMock.Object,
            _cardReaderMock.Object);
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
        _viewModel.SelectedStaff = new Staff { StaffIdm = "existing", Name = "既存職員" };

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
        var staff = new Staff
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
    /// 成功後にCancelEdit()が呼ばれStatusMessageがクリアされるため、
    /// リポジトリ呼び出しとIsEditing状態で成功を検証します。
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

        var existingStaff = new Staff { StaffIdm = "FFFF000000000001", Name = "既存職員" };
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync("FFFF000000000001", true)).ReturnsAsync(existingStaff);

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.StatusMessage.Should().Contain("既に登録");
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
    /// 成功後にCancelEdit()が呼ばれStatusMessageがクリアされるため、
    /// リポジトリ呼び出しとIsEditing状態で成功を検証します。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_ExistingStaff_ShouldUpdateStaff()
    {
        // Arrange
        var existingStaff = new Staff
        {
            StaffIdm = "FFFF000000000001",
            Name = "田中太郎",
            Number = "S-001"
        };
        _viewModel.SelectedStaff = existingStaff;
        _viewModel.StartEdit();
        _viewModel.EditName = "田中花子"; // 名前を変更
        _viewModel.EditNote = "更新後のメモ";

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
        var staff = new Staff
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

    /// <summary>
    /// 削除に失敗した場合、エラーメッセージが表示されること
    /// </summary>
    [Fact]
    public async Task DeleteAsync_WhenDeleteFails_ShouldShowError()
    {
        // Arrange
        var staff = new Staff { StaffIdm = "FFFF000000000001", Name = "田中太郎" };
        _viewModel.SelectedStaff = staff;

        _staffRepositoryMock.Setup(r => r.DeleteAsync("FFFF000000000001")).ReturnsAsync(false);

        // Act
        await _viewModel.DeleteAsync();

        // Assert
        _viewModel.StatusMessage.Should().Contain("失敗");
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
}
