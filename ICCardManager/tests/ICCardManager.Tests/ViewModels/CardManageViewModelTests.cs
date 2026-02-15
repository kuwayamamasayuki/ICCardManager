using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Models;
using ICCardManager.Data;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using IOperationLogRepository = ICCardManager.Data.Repositories.IOperationLogRepository;

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
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock;
    private readonly Mock<ICardReader> _cardReaderMock;
    private readonly Mock<IValidationService> _validationServiceMock;
    private readonly Mock<IStaffRepository> _staffRepositoryMock;
    private readonly Mock<OperationLogger> _operationLoggerMock;
    private readonly Mock<IDialogService> _dialogServiceMock;
    private readonly Mock<IStaffAuthService> _staffAuthServiceMock;
    private readonly CardTypeDetector _cardTypeDetector;
    private readonly LendingService _lendingService;
    private readonly CardManageViewModel _viewModel;

    public CardManageViewModelTests()
    {
        _cardRepositoryMock = new Mock<ICardRepository>();
        _ledgerRepositoryMock = new Mock<ILedgerRepository>();
        _cardReaderMock = new Mock<ICardReader>();
        _validationServiceMock = new Mock<IValidationService>();
        _staffRepositoryMock = new Mock<IStaffRepository>();
        _dialogServiceMock = new Mock<IDialogService>();
        _staffAuthServiceMock = new Mock<IStaffAuthService>();
        _cardTypeDetector = new CardTypeDetector();

        // OperationLoggerのモック（コンストラクタ引数が必要なためMock.Ofで作成）
        var operationLogRepositoryMock = new Mock<IOperationLogRepository>();
        _operationLoggerMock = new Mock<OperationLogger>(operationLogRepositoryMock.Object, _staffRepositoryMock.Object);

        // LendingServiceの作成（Issue #596対応）
        var settingsRepositoryMock = new Mock<ISettingsRepository>();
        var summaryGenerator = new SummaryGenerator();
        var lockManager = new CardLockManager(NullLogger<CardLockManager>.Instance);
        var dbContext = new DbContext(":memory:");
        dbContext.InitializeDatabase();
        _lendingService = new LendingService(
            dbContext,
            _cardRepositoryMock.Object,
            _staffRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            settingsRepositoryMock.Object,
            summaryGenerator,
            lockManager,
            NullLogger<LendingService>.Instance);

        // バリデーションはデフォルトで成功を返す
        _validationServiceMock.Setup(v => v.ValidateCardIdm(It.IsAny<string>())).Returns(ValidationResult.Success());
        _validationServiceMock.Setup(v => v.ValidateCardNumber(It.IsAny<string>())).Returns(ValidationResult.Success());
        _validationServiceMock.Setup(v => v.ValidateCardType(It.IsAny<string>())).Returns(ValidationResult.Success());

        // ダイアログはデフォルトでYes/Trueを返す（テストがブロックされないように）
        _dialogServiceMock.Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        _dialogServiceMock.Setup(d => d.ShowWarningConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        // カード登録モードダイアログはデフォルトで「新規購入」を返す（Issue #510）
        _dialogServiceMock.Setup(d => d.ShowCardRegistrationModeDialog())
            .Returns(new ICCardManager.Views.Dialogs.CardRegistrationModeResult
            {
                IsNewPurchase = true,
                CarryoverMonth = 4,
                StartingPageNumber = 1
            });

        // 認証はデフォルトで成功を返す（Issue #429）
        _staffAuthServiceMock.Setup(s => s.RequestAuthenticationAsync(It.IsAny<string>()))
            .ReturnsAsync(new StaffAuthResult { Idm = "TEST_OPERATOR_IDM", StaffName = "テスト操作者" });

        _viewModel = new CardManageViewModel(
            _cardRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _cardReaderMock.Object,
            _cardTypeDetector,
            _validationServiceMock.Object,
            _operationLoggerMock.Object,
            _dialogServiceMock.Object,
            _staffAuthServiceMock.Object,
            _lendingService);
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
    /// カード種別はIDmから自動判定できないため、デフォルト値が設定される。
    /// ユーザーは必要に応じて手動でカード種別を変更する。
    /// </remarks>
    [Fact]
    public async Task StartNewCardWithIdmAsync_ShouldSetIdmAndDefaultCardType()
    {
        // Arrange
        var idm = "0102030405060708";
        // 未登録カード（既存カードなし）のシナリオ
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync((IcCard)null);

        // Act
        var completed = await _viewModel.StartNewCardWithIdmAsync(idm);

        // Assert
        completed.Should().BeFalse(); // 新規登録モードに入るのでfalse
        _viewModel.IsEditing.Should().BeTrue();
        _viewModel.IsNewCard.Should().BeTrue();
        _viewModel.IsWaitingForCard.Should().BeFalse(); // IDmがあるので待機しない
        _viewModel.EditCardIdm.Should().Be(idm);
        // カード種別はIDmから自動判定できないため、デフォルト値（nimoca）が設定される
        // ※利用頻度が最も高いためnimocaがデフォルト
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

    #region ハイライト表示テスト（Issue #707）

    /// <summary>
    /// 新規カード保存後、NewlyRegisteredIdmが保存IDmに設定されること
    /// </summary>
    [Fact]
    public async Task SaveAsync_NewCard_ShouldSetNewlyRegisteredIdm()
    {
        // Arrange
        var idm = "0102030405060708";
        _viewModel.StartNewCard();
        _viewModel.EditCardIdm = idm;
        _viewModel.EditCardType = "はやかけん";
        _viewModel.EditCardNumber = "H-001";

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<IcCard>())).ReturnsAsync(true);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>
        {
            new() { CardIdm = idm, CardType = "はやかけん", CardNumber = "H-001" }
        });

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.NewlyRegisteredIdm.Should().Be(idm);
    }

    /// <summary>
    /// 既存カード更新後、NewlyRegisteredIdmが更新したIDmに設定されること
    /// </summary>
    [Fact]
    public async Task SaveAsync_UpdateCard_ShouldSetNewlyRegisteredIdm()
    {
        // Arrange
        var idm = "0102030405060708";
        var existingCard = new CardDto
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001",
            IsLent = false
        };
        _viewModel.SelectedCard = existingCard;
        _viewModel.StartEdit();
        _viewModel.EditNote = "更新後のメモ";

        _cardRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<IcCard>())).ReturnsAsync(true);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>
        {
            new() { CardIdm = idm, CardType = "はやかけん", CardNumber = "H-001" }
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
        var idm = "0102030405060708";
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<IcCard>())).ReturnsAsync(true);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>
        {
            new() { CardIdm = idm, CardType = "はやかけん", CardNumber = "H-001" }
        });

        // Act: 1回目
        _viewModel.StartNewCard();
        _viewModel.EditCardIdm = idm;
        _viewModel.EditCardType = "はやかけん";
        _viewModel.EditCardNumber = "H-001";
        await _viewModel.SaveAsync();

        // PropertyChangedイベントの発火を確認するためトラッキング
        var propertyChangedCount = 0;
        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(CardManageViewModel.NewlyRegisteredIdm)
                && _viewModel.NewlyRegisteredIdm != null)
                propertyChangedCount++;
        };

        // Act: 2回目（同じIDm）— 更新として
        var existingCard = new CardDto
        {
            CardIdm = idm, CardType = "はやかけん", CardNumber = "H-001", IsLent = false
        };
        _viewModel.SelectedCard = existingCard;
        _viewModel.StartEdit();
        _viewModel.EditNote = "更新後のメモ";
        _cardRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<IcCard>())).ReturnsAsync(true);
        await _viewModel.SaveAsync();

        // Assert: 2回目でもPropertyChangedが発火していること
        propertyChangedCount.Should().BeGreaterOrEqualTo(1);
        _viewModel.NewlyRegisteredIdm.Should().Be(idm);
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

    #region Issue #443: 新規カード登録時の残高テスト

    /// <summary>
    /// 新規カード登録時に残高が正しく読み取られ、新規購入レコードに反映されること
    /// </summary>
    [Fact]
    public async Task SaveAsync_NewCard_ShouldCreatePurchaseLedgerWithPreReadBalance()
    {
        // Arrange
        var idm = "0102030405060708";
        var balance = 5000;

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<IcCard>())).ReturnsAsync(true);
        _cardReaderMock.Setup(r => r.ReadBalanceAsync(idm)).ReturnsAsync(balance);

        // SetPreReadBalanceを使用して事前読み取り残高を設定（MainViewModelからの呼び出しをシミュレート）
        _viewModel.SetPreReadBalance(balance);

        _viewModel.StartNewCard();
        _viewModel.EditCardIdm = idm;
        _viewModel.EditCardType = "nimoca";
        _viewModel.EditCardNumber = "N-001";

        // Act
        await _viewModel.SaveAsync();

        // Assert
        // 新規購入レコードが作成されること
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.Is<Ledger>(l =>
            l.CardIdm == idm &&
            l.Summary == "新規購入" &&
            l.Income == balance &&
            l.Balance == balance
        )), Times.Once);
    }

    /// <summary>
    /// 残高が事前読み取りされていない場合でも、保存時にカードから読み取りを試みること
    /// </summary>
    [Fact]
    public async Task SaveAsync_NewCard_WithoutPreReadBalance_ShouldTryReadBalanceAtSaveTime()
    {
        // Arrange
        var idm = "0102030405060708";
        var balance = 3000;

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<IcCard>())).ReturnsAsync(true);
        _cardReaderMock.Setup(r => r.ReadBalanceAsync(idm)).ReturnsAsync(balance);

        // 事前読み取り残高は設定しない（手動新規登録のフォールバックケース）

        _viewModel.StartNewCard();
        _viewModel.EditCardIdm = idm;
        _viewModel.EditCardType = "nimoca";
        _viewModel.EditCardNumber = "N-001";

        // Act
        await _viewModel.SaveAsync();

        // Assert
        // 保存時にReadBalanceAsyncが呼び出されること
        _cardReaderMock.Verify(r => r.ReadBalanceAsync(idm), Times.Once);

        // 新規購入レコードが作成されること
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.Is<Ledger>(l =>
            l.CardIdm == idm &&
            l.Summary == "新規購入" &&
            l.Income == balance &&
            l.Balance == balance
        )), Times.Once);
    }

    /// <summary>
    /// 残高読み取りに失敗した場合は新規購入レコードが作成されないこと
    /// </summary>
    [Fact]
    public async Task SaveAsync_NewCard_WhenBalanceReadFails_ShouldNotCreatePurchaseLedger()
    {
        // Arrange
        var idm = "0102030405060708";

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<IcCard>())).ReturnsAsync(true);
        _cardReaderMock.Setup(r => r.ReadBalanceAsync(idm)).ReturnsAsync((int?)null);  // 残高読み取り失敗

        _viewModel.StartNewCard();
        _viewModel.EditCardIdm = idm;
        _viewModel.EditCardType = "nimoca";
        _viewModel.EditCardNumber = "N-001";

        // Act
        await _viewModel.SaveAsync();

        // Assert
        // カード自体は登録される
        _cardRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<IcCard>()), Times.Once);

        // 残高が取得できないため新規購入レコードは作成されない
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.Is<Ledger>(l =>
            l.Summary == "新規購入"
        )), Times.Never);
    }

    #endregion

    #region Issue #657: GetImportFromDateテスト

    /// <summary>
    /// 新規購入時、GetImportFromDateが当日を返すこと（月初めではない）
    /// </summary>
    [Fact]
    public void GetImportFromDate_NewPurchase_ShouldReturnToday()
    {
        // Arrange
        var modeResult = new ICCardManager.Views.Dialogs.CardRegistrationModeResult
        {
            IsNewPurchase = true
        };

        // Act
        var result = CardManageViewModel.GetImportFromDate(modeResult);

        // Assert
        result.Should().Be(DateTime.Today);
    }

    /// <summary>
    /// 繰越時、GetImportFromDateがSummaryGenerator.GetMidYearCarryoverDateと同じ値を返すこと
    /// </summary>
    [Fact]
    public void GetImportFromDate_Carryover_ShouldReturnMidYearCarryoverDate()
    {
        // Arrange
        var carryoverMonth = 10; // 10月繰越
        var modeResult = new ICCardManager.Views.Dialogs.CardRegistrationModeResult
        {
            IsNewPurchase = false,
            CarryoverMonth = carryoverMonth
        };
        var expected = SummaryGenerator.GetMidYearCarryoverDate(carryoverMonth, DateTime.Now);

        // Act
        var result = CardManageViewModel.GetImportFromDate(modeResult);

        // Assert
        result.Should().Be(expected);
    }

    #endregion

    #region Issue #658: 新規購入カードに購入日を指定可能にする

    /// <summary>
    /// 購入日を明示的に指定した場合、GetImportFromDateがその日付を返すこと
    /// </summary>
    [Fact]
    public void GetImportFromDate_NewPurchaseWithExplicitDate_ShouldReturnSpecifiedDate()
    {
        // Arrange
        var purchaseDate = new DateTime(2026, 2, 5);
        var modeResult = new ICCardManager.Views.Dialogs.CardRegistrationModeResult
        {
            IsNewPurchase = true,
            PurchaseDate = purchaseDate
        };

        // Act
        var result = CardManageViewModel.GetImportFromDate(modeResult);

        // Assert
        result.Should().Be(purchaseDate.Date);
    }

    /// <summary>
    /// 購入日がnull（未指定）の場合、GetImportFromDateが当日を返すこと（後方互換性）
    /// </summary>
    [Fact]
    public void GetImportFromDate_NewPurchaseWithNullDate_ShouldReturnToday()
    {
        // Arrange
        var modeResult = new ICCardManager.Views.Dialogs.CardRegistrationModeResult
        {
            IsNewPurchase = true,
            PurchaseDate = null
        };

        // Act
        var result = CardManageViewModel.GetImportFromDate(modeResult);

        // Assert
        result.Should().Be(DateTime.Today);
    }

    #endregion

    #region Issue #665: カード新規登録時の履歴事前読み取り

    /// <summary>
    /// 事前読み取り履歴が設定されている場合、SaveAsyncがその履歴を使用し
    /// カードリーダーへの再読み取りを行わないこと
    /// </summary>
    [Fact]
    public async Task SaveAsync_NewCard_WithPreReadHistory_ShouldUsePreReadHistoryWithoutReReading()
    {
        // Arrange
        var idm = "0102030405060708";
        var balance = 5000;
        var today = DateTime.Today;

        var preReadHistory = new List<LedgerDetail>
        {
            new() { UseDate = today, EntryStation = "博多", ExitStation = "天神", Amount = 210, Balance = 4790 }
        };

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<IcCard>())).ReturnsAsync(true);

        _viewModel.SetPreReadBalance(balance);
        _viewModel.SetPreReadHistory(preReadHistory);

        _viewModel.StartNewCard();
        _viewModel.EditCardIdm = idm;
        _viewModel.EditCardType = "nimoca";
        _viewModel.EditCardNumber = "N-001";

        // Act
        await _viewModel.SaveAsync();

        // Assert
        // 事前読み取り履歴が使用されるため、カードリーダーのReadHistoryAsyncは呼ばれないこと
        _cardReaderMock.Verify(r => r.ReadHistoryAsync(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// 事前読み取り履歴がnullの場合、SaveAsyncがカードリーダーから直接読み取りを試みること
    /// （フォールバック動作の確認）
    /// </summary>
    [Fact]
    public async Task SaveAsync_NewCard_WithoutPreReadHistory_ShouldFallbackToCardReader()
    {
        // Arrange
        var idm = "0102030405060708";
        var balance = 5000;

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<IcCard>())).ReturnsAsync(true);
        _cardReaderMock.Setup(r => r.ReadBalanceAsync(idm)).ReturnsAsync(balance);
        _cardReaderMock.Setup(r => r.ReadHistoryAsync(idm))
            .ReturnsAsync(new List<LedgerDetail>());

        _viewModel.SetPreReadBalance(balance);
        // SetPreReadHistoryを呼ばない（_preReadHistoryはnull）

        _viewModel.StartNewCard();
        _viewModel.EditCardIdm = idm;
        _viewModel.EditCardType = "nimoca";
        _viewModel.EditCardNumber = "N-001";

        // Act
        await _viewModel.SaveAsync();

        // Assert
        // 事前読み取り履歴がないため、カードリーダーから直接読み取りを試みること
        _cardReaderMock.Verify(r => r.ReadHistoryAsync(idm), Times.Once);
    }

    #endregion
}
