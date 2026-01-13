using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.CardReader;
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
/// CardManageViewModelの単体テスト
/// </summary>
public class CardManageViewModelTests
{
    private readonly Mock<ICardRepository> _cardRepositoryMock;
    private readonly Mock<ICardReader> _cardReaderMock;
    private readonly Mock<IValidationService> _validationServiceMock;
    private readonly CardTypeDetector _cardTypeDetector;
    private readonly CardManageViewModel _viewModel;

    public CardManageViewModelTests()
    {
        _cardRepositoryMock = new Mock<ICardRepository>();
        _cardReaderMock = new Mock<ICardReader>();
        _validationServiceMock = new Mock<IValidationService>();
        _cardTypeDetector = new CardTypeDetector();

        // バリデーションはデフォルトで成功を返す
        _validationServiceMock.Setup(v => v.ValidateCardIdm(It.IsAny<string>())).Returns(ValidationResult.Success());
        _validationServiceMock.Setup(v => v.ValidateCardNumber(It.IsAny<string>())).Returns(ValidationResult.Success());
        _validationServiceMock.Setup(v => v.ValidateCardType(It.IsAny<string>())).Returns(ValidationResult.Success());

        _viewModel = new CardManageViewModel(
            _cardRepositoryMock.Object,
            _cardReaderMock.Object,
            _cardTypeDetector,
            _validationServiceMock.Object);
    }

    #region カード一覧読み込みテスト

    /// <summary>
    /// カード一覧が正しく読み込まれること
    /// </summary>
    [Fact]
    public async Task LoadCardsAsync_ShouldLoadCardsOrderedByTypeAndNumber()
    {
        // Arrange
        var cards = new List<IcCard>
        {
            new() { CardIdm = "01", CardType = "nimoca", CardNumber = "002" },
            new() { CardIdm = "02", CardType = "はやかけん", CardNumber = "001" },
            new() { CardIdm = "03", CardType = "はやかけん", CardNumber = "002" },
            new() { CardIdm = "04", CardType = "nimoca", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(cards);

        // Act
        await _viewModel.LoadCardsAsync();

        // Assert
        _viewModel.Cards.Should().HaveCount(4);
        // カード種別→番号順にソートされている
        _viewModel.Cards[0].CardType.Should().Be("nimoca");
        _viewModel.Cards[0].CardNumber.Should().Be("001");
        _viewModel.Cards[1].CardType.Should().Be("nimoca");
        _viewModel.Cards[1].CardNumber.Should().Be("002");
        _viewModel.Cards[2].CardType.Should().Be("はやかけん");
        _viewModel.Cards[2].CardNumber.Should().Be("001");
    }

    /// <summary>
    /// カード一覧が空の場合、空のコレクションになること
    /// </summary>
    [Fact]
    public async Task LoadCardsAsync_WithNoCards_ShouldHaveEmptyCollection()
    {
        // Arrange
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.LoadCardsAsync();

        // Assert
        _viewModel.Cards.Should().BeEmpty();
    }

    #endregion

    #region 新規登録モードテスト

    /// <summary>
    /// 新規登録モードが正しく開始されること
    /// </summary>
    [Fact]
    public void StartNewCard_ShouldSetEditingModeCorrectly()
    {
        // Arrange
        _viewModel.SelectedCard = new CardDto { CardIdm = "existing", CardType = "test", CardNumber = "001" };

        // Act
        _viewModel.StartNewCard();

        // Assert
        _viewModel.IsEditing.Should().BeTrue();
        _viewModel.IsNewCard.Should().BeTrue();
        _viewModel.IsWaitingForCard.Should().BeTrue();
        _viewModel.SelectedCard.Should().BeNull();
        _viewModel.EditCardIdm.Should().BeEmpty();
        _viewModel.EditCardType.Should().Be("はやかけん");
        _viewModel.EditCardNumber.Should().BeEmpty();
        _viewModel.EditNote.Should().BeEmpty();
        _viewModel.StatusMessage.Should().Contain("タッチ");
    }

    /// <summary>
    /// IDmを指定して新規登録モードを開始できること
    /// </summary>
    /// <remarks>
    /// カード種別はIDmから自動判定できないため、デフォルト値（nimoca）が設定される。
    /// ユーザーは必要に応じて手動でカード種別を変更する。
    /// </remarks>
    [Fact]
    public void StartNewCardWithIdm_ShouldSetIdmAndDefaultCardType()
    {
        // Arrange
        var idm = "0102030405060708";

        // Act
        _viewModel.StartNewCardWithIdm(idm);

        // Assert
        _viewModel.IsEditing.Should().BeTrue();
        _viewModel.IsNewCard.Should().BeTrue();
        _viewModel.IsWaitingForCard.Should().BeFalse(); // IDmがあるので待機しない
        _viewModel.EditCardIdm.Should().Be(idm);
        // カード種別はIDmから自動判定できないため、デフォルト値（nimoca）が設定される
        _viewModel.EditCardType.Should().Be("nimoca");
    }

    #endregion

    #region 編集モードテスト

    /// <summary>
    /// 編集モードが正しく開始されること
    /// </summary>
    [Fact]
    public void StartEdit_ShouldLoadSelectedCardData()
    {
        // Arrange
        var card = new CardDto
        {
            CardIdm = "0102030405060708",
            CardType = "はやかけん",
            CardNumber = "H-001",
            Note = "テストカード"
        };
        _viewModel.SelectedCard = card;

        // Act
        _viewModel.StartEdit();

        // Assert
        _viewModel.IsEditing.Should().BeTrue();
        _viewModel.IsNewCard.Should().BeFalse();
        _viewModel.IsWaitingForCard.Should().BeFalse();
        _viewModel.EditCardIdm.Should().Be("0102030405060708");
        _viewModel.EditCardType.Should().Be("はやかけん");
        _viewModel.EditCardNumber.Should().Be("H-001");
        _viewModel.EditNote.Should().Be("テストカード");
    }

    /// <summary>
    /// カード未選択時に編集モードを開始しても何も起きないこと
    /// </summary>
    [Fact]
    public void StartEdit_WithNoSelectedCard_ShouldDoNothing()
    {
        // Arrange
        _viewModel.SelectedCard = null;
        _viewModel.IsEditing = false;

        // Act
        _viewModel.StartEdit();

        // Assert
        _viewModel.IsEditing.Should().BeFalse();
    }

    #endregion

    #region 保存テスト

    /// <summary>
    /// 新規カードが正常に保存されること
    /// </summary>
    /// <remarks>
    /// 成功後にCancelEdit()が呼ばれStatusMessageがクリアされるため、
    /// リポジトリ呼び出しとIsEditing状態で成功を検証します。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_NewCard_ShouldInsertCard()
    {
        // Arrange
        _viewModel.StartNewCard();
        _viewModel.EditCardIdm = "0102030405060708";
        _viewModel.EditCardType = "はやかけん";
        _viewModel.EditCardNumber = "H-001";
        _viewModel.EditNote = "新規カード";

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync("0102030405060708", true)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<IcCard>())).ReturnsAsync(true);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.SaveAsync();

        // Assert - リポジトリが正しく呼ばれ、編集モードが終了していること
        _cardRepositoryMock.Verify(r => r.InsertAsync(It.Is<IcCard>(c =>
            c.CardIdm == "0102030405060708" &&
            c.CardType == "はやかけん" &&
            c.CardNumber == "H-001" &&
            c.Note == "新規カード"
        )), Times.Once);
        _viewModel.IsEditing.Should().BeFalse(); // CancelEdit()で編集モード終了
    }

    /// <summary>
    /// 重複するカードは登録できないこと
    /// </summary>
    [Fact]
    public async Task SaveAsync_NewCard_WithDuplicateIdm_ShouldShowError()
    {
        // Arrange
        _viewModel.StartNewCard();
        _viewModel.EditCardIdm = "0102030405060708";
        _viewModel.EditCardType = "はやかけん";
        _viewModel.EditCardNumber = "H-001";

        var existingCard = new IcCard { CardIdm = "0102030405060708", CardNumber = "H-999" };
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync("0102030405060708", true)).ReturnsAsync(existingCard);

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.StatusMessage.Should().Contain("既に登録");
        _viewModel.StatusMessage.Should().Contain("H-999");  // 管理番号が表示されること
        _cardRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<IcCard>()), Times.Never);
    }

    /// <summary>
    /// カードIDmが空の場合、保存できないこと
    /// </summary>
    [Fact]
    public async Task SaveAsync_WithEmptyIdm_ShouldShowError()
    {
        // Arrange
        _viewModel.StartNewCard();
        _viewModel.EditCardIdm = "";
        _viewModel.EditCardType = "はやかけん";

        // 空のIDmに対してエラーを返すようモックを設定
        _validationServiceMock.Setup(v => v.ValidateCardIdm(string.Empty))
            .Returns(ValidationResult.Failure("IDmを入力してください"));

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.StatusMessage.Should().Contain("IDm");
        _cardRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<IcCard>()), Times.Never);
    }

    /// <summary>
    /// カード種別が空の場合、保存できないこと
    /// </summary>
    [Fact]
    public async Task SaveAsync_WithEmptyCardType_ShouldShowError()
    {
        // Arrange
        _viewModel.StartNewCard();
        _viewModel.EditCardIdm = "0102030405060708";
        _viewModel.EditCardType = "";

        // 空の種別に対してエラーを返すようモックを設定
        _validationServiceMock.Setup(v => v.ValidateCardType(string.Empty))
            .Returns(ValidationResult.Failure("カード種別を選択してください"));

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.StatusMessage.Should().Contain("種別");
        _cardRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<IcCard>()), Times.Never);
    }

    /// <summary>
    /// カード番号が空の場合、自動採番されること
    /// </summary>
    [Fact]
    public async Task SaveAsync_WithEmptyCardNumber_ShouldAutoGenerateNumber()
    {
        // Arrange
        _viewModel.StartNewCard();
        _viewModel.EditCardIdm = "0102030405060708";
        _viewModel.EditCardType = "はやかけん";
        _viewModel.EditCardNumber = "";

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync("0102030405060708", true)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(r => r.GetNextCardNumberAsync("はやかけん")).ReturnsAsync("H-005");
        _cardRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<IcCard>())).ReturnsAsync(true);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _cardRepositoryMock.Verify(r => r.InsertAsync(It.Is<IcCard>(c => c.CardNumber == "H-005")), Times.Once);
    }

    /// <summary>
    /// カードが正常に更新されること
    /// </summary>
    /// <remarks>
    /// 成功後にCancelEdit()が呼ばれStatusMessageがクリアされるため、
    /// リポジトリ呼び出しとIsEditing状態で成功を検証します。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_ExistingCard_ShouldUpdateCard()
    {
        // Arrange
        var existingCard = new CardDto
        {
            CardIdm = "0102030405060708",
            CardType = "はやかけん",
            CardNumber = "H-001",
            IsLent = true,
            LentAt = DateTime.Now,
            LastLentStaff = "staff123"
        };
        _viewModel.SelectedCard = existingCard;
        _viewModel.StartEdit();
        _viewModel.EditNote = "更新後のメモ";

        _cardRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<IcCard>())).ReturnsAsync(true);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.SaveAsync();

        // Assert - リポジトリが正しく呼ばれ、編集モードが終了していること
        _cardRepositoryMock.Verify(r => r.UpdateAsync(It.Is<IcCard>(c =>
            c.Note == "更新後のメモ" &&
            c.IsLent == true  // 貸出状態は維持される
        )), Times.Once);
        _viewModel.IsEditing.Should().BeFalse(); // CancelEdit()で編集モード終了
    }

    #endregion

    #region 削除テスト

    /// <summary>
    /// カードが正常に削除されること
    /// </summary>
    /// <remarks>
    /// 削除後にStatusMessageはクリアされないが、
    /// リポジトリ呼び出しの検証を優先します。
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_ShouldDeleteCard()
    {
        // Arrange
        var card = new CardDto
        {
            CardIdm = "0102030405060708",
            CardType = "はやかけん",
            CardNumber = "H-001",
            IsLent = false
        };
        _viewModel.SelectedCard = card;

        _cardRepositoryMock.Setup(r => r.DeleteAsync("0102030405060708")).ReturnsAsync(true);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.DeleteAsync();

        // Assert - リポジトリが正しく呼ばれたことを検証
        _cardRepositoryMock.Verify(r => r.DeleteAsync("0102030405060708"), Times.Once);
        // 削除後にLoadCardsAsyncが呼ばれて一覧が更新される
        _cardRepositoryMock.Verify(r => r.GetAllAsync(), Times.Once);
    }

    /// <summary>
    /// 貸出中のカードは削除できないこと
    /// </summary>
    [Fact]
    public async Task DeleteAsync_LentCard_ShouldShowError()
    {
        // Arrange
        var card = new CardDto
        {
            CardIdm = "0102030405060708",
            CardType = "はやかけん",
            CardNumber = "H-001",
            IsLent = true
        };
        _viewModel.SelectedCard = card;

        // Act
        await _viewModel.DeleteAsync();

        // Assert
        _viewModel.StatusMessage.Should().Contain("貸出中");
        _cardRepositoryMock.Verify(r => r.DeleteAsync(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// カード未選択時に削除しても何も起きないこと
    /// </summary>
    [Fact]
    public async Task DeleteAsync_WithNoSelectedCard_ShouldDoNothing()
    {
        // Arrange
        _viewModel.SelectedCard = null;

        // Act
        await _viewModel.DeleteAsync();

        // Assert
        _cardRepositoryMock.Verify(r => r.DeleteAsync(It.IsAny<string>()), Times.Never);
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
        _viewModel.StartNewCard();
        _viewModel.EditCardIdm = "0102030405060708";
        _viewModel.EditCardType = "nimoca";
        _viewModel.EditCardNumber = "N-001";
        _viewModel.StatusMessage = "何かのメッセージ";

        // Act
        _viewModel.CancelEdit();

        // Assert
        _viewModel.IsEditing.Should().BeFalse();
        _viewModel.IsNewCard.Should().BeFalse();
        _viewModel.IsWaitingForCard.Should().BeFalse();
        _viewModel.EditCardIdm.Should().BeEmpty();
        _viewModel.EditCardType.Should().BeEmpty();
        _viewModel.EditCardNumber.Should().BeEmpty();
        _viewModel.EditNote.Should().BeEmpty();
        _viewModel.StatusMessage.Should().BeEmpty();
    }

    #endregion

    #region CardTypesテスト

    /// <summary>
    /// CardTypesが全てのカード種別を含むこと
    /// </summary>
    [Fact]
    public void CardTypes_ShouldContainAllTypes()
    {
        // Assert
        _viewModel.CardTypes.Should().Contain("はやかけん");
        _viewModel.CardTypes.Should().Contain("nimoca");
        _viewModel.CardTypes.Should().Contain("SUGOCA");
        _viewModel.CardTypes.Should().Contain("Suica");
        _viewModel.CardTypes.Should().Contain("PASMO");
        _viewModel.CardTypes.Should().Contain("ICOCA");
        _viewModel.CardTypes.Should().Contain("その他");
    }

    #endregion
}
