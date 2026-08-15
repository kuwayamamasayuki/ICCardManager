using CommunityToolkit.Mvvm.Messaging;
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
using Microsoft.Extensions.Options;
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

        // OperationLoggerのモック（コンストラクタ引数が必要なためMock.Ofで作成）
        var operationLogRepositoryMock = new Mock<IOperationLogRepository>();
        _operationLoggerMock = new Mock<OperationLogger>(operationLogRepositoryMock.Object, Mock.Of<ICurrentOperatorContext>());

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
            Options.Create(new AppOptions()),
            NullLogger<LendingService>.Instance);

        // バリデーションはデフォルトで成功を返す
        _validationServiceMock.Setup(v => v.ValidateCardIdm(It.IsAny<string>())).Returns(ValidationResult.Success());
        _validationServiceMock.Setup(v => v.ValidateCardNumber(It.IsAny<string>())).Returns(ValidationResult.Success());
        _validationServiceMock.Setup(v => v.ValidateCardType(It.IsAny<string>())).Returns(ValidationResult.Success());

        // ダイアログはデフォルトでYes/Trueを返す（テストがブロックされないように）
        _dialogServiceMock.Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        _dialogServiceMock.Setup(d => d.ShowWarningConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        // カード登録モードダイアログはデフォルトで「新規購入」を返す（Issue #510）
        _dialogServiceMock.Setup(d => d.ShowCardRegistrationModeDialog(It.IsAny<int?>()))
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
            _validationServiceMock.Object,
            _operationLoggerMock.Object,
            _dialogServiceMock.Object,
            _staffAuthServiceMock.Object,
            _lendingService,
            new WeakReferenceMessenger());
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
        _viewModel.EditCardType.Should().Be("nimoca");
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
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync((IcCard?)null);

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
    /// 本テストはリポジトリ呼び出しと IsEditing 状態で成功を検証する。完了メッセージが
    /// 残ることは Issue #1759 の *_ShouldKeepCompletionMessage が担保する
    /// （かつては CancelEdit() がメッセージを消していたため検証できなかった）。
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
    /// 本テストはリポジトリ呼び出しと IsEditing 状態で成功を検証する。完了メッセージが
    /// 残ることは Issue #1759 の *_ShouldKeepCompletionMessage が担保する
    /// （かつては CancelEdit() がメッセージを消していたため検証できなかった）。
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

        // Issue #1726: 編集対象外の列は DB の最新値（GetByIdmAsync）から引き継ぐ
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync("0102030405060708", false)).ReturnsAsync(new IcCard
        {
            CardIdm = "0102030405060708",
            CardType = "はやかけん",
            CardNumber = "H-001",
            IsLent = true,
            LastLentAt = DateTime.Now,
            LastLentStaff = "staff123"
        });
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

    /// <summary>
    /// Issue #1757: 編集保存で管理番号が重複したとき、致命的エラーにせず
    /// 行動指示付きのメッセージを表示すること
    /// </summary>
    /// <remarks>
    /// <para>
    /// 修正前は <c>CardRepository.UpdateAsyncInternal</c> に UNIQUE 制約違反の catch が無く、
    /// 生の <c>SQLiteException</c> が <c>App.OnDispatcherUnhandledException</c> まで抜けて
    /// 「予期しないエラーが発生しました。／エラーコード: SYS999」という、原因も回復手段も
    /// 示さないモーダルダイアログになっていた。
    /// 登録経路（<c>SaveAsync_NewCard</c>）は同じ操作を親切に案内するため、非対称だった。
    /// </para>
    /// <para>
    /// 文言は登録経路と同一にし、`.claude/rules/error-messages.md` の3要素
    /// （何が＝管理番号、なぜ＝同じ種別で使用中、どうすれば＝別の番号を指定）を満たす。
    /// あわせて<b>編集モードが維持される</b>ことを表明する。ここで <c>CancelEdit()</c> が
    /// 走ると入力内容が消え、ユーザーは番号だけ直して再保存できない。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SaveAsync_ExistingCard_WithDuplicateCardNumber_ShouldShowActionableError()
    {
        // Arrange
        var existingCard = new CardDto
        {
            CardIdm = "0102030405060708",
            CardType = "nimoca",
            CardNumber = "N-002"
        };
        _viewModel.SelectedCard = existingCard;
        _viewModel.StartEdit();
        _viewModel.EditCardNumber = "N-001"; // 同一種別の別カードが使用中の番号

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync("0102030405060708", false)).ReturnsAsync(new IcCard
        {
            CardIdm = "0102030405060708",
            CardType = "nimoca",
            CardNumber = "N-002"
        });
        _cardRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<IcCard>()))
            .ThrowsAsync(new DuplicateCardNumberException(
                "nimoca", "N-001", new InvalidOperationException("UNIQUE constraint failed")));

        // Act
        var act = async () => await _viewModel.SaveAsync();

        // Assert: 例外が ViewModel の外へ漏れない（漏れると致命的エラーダイアログになる）
        await act.Should().NotThrowAsync();

        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.StatusMessage.Should().Contain("N-001");
        _viewModel.StatusMessage.Should().Contain("既に使用されています");
        _viewModel.StatusMessage.Should().EndWith("別の番号を指定してください。");

        // 入力内容を失わせない（番号だけ直して再保存できること）
        _viewModel.IsEditing.Should().BeTrue();
        _viewModel.EditCardNumber.Should().Be("N-001");
    }

    /// <summary>
    /// Issue #1726: 編集保存時、この画面で編集できない列がすべて DB の最新値から
    /// 引き継がれること（操作ログに虚偽の変更を残さない）
    /// </summary>
    /// <remarks>
    /// OperationLogger は IcCard 全体を JSON 化して BeforeData / AfterData に記録するため、
    /// 引き継がないと「開始ページ番号 7 → 1」「繰越累計受入 120,000 → 0」
    /// 「払戻済み: はい → いいえ」「貸出中: はい → いいえ」という実際には起きていない
    /// 変更が監査ログに残る。編集対象（カード種別・管理番号・備考）以外を網羅して表明する。
    /// 引き継ぎ元を一覧（SelectedCard）ではなく beforeCard にしているのは、一覧が
    /// GetAllAsync のキャッシュ由来で自動更新されず、共有モードでは他PCの貸出が
    /// 反映されないため。DB 側の値の保全は CardRepository 側で担保している
    /// （CardRepositoryTests.UpdateAsync_CardWithCarryoverInfo_DoesNotOverwriteRegistrationOnlyColumns）。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_ExistingCard_ShouldCarryOverAllNonEditableFieldsFromDb()
    {
        // Arrange: 紙の出納簿から年度途中で移行し（#510 / #1215）、かつ払戻済み・貸出中のカード
        var idm = "0102030405060708";
        var lentAt = new DateTime(2026, 8, 1, 9, 30, 0);
        var refundedAt = new DateTime(2026, 8, 5, 14, 0, 0);

        // 一覧（キャッシュ由来）は古い状態を持っている＝ここから引き継いではいけない
        _viewModel.SelectedCard = new CardDto
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001",
            IsLent = false,
            LentAt = null,
            LastLentStaff = null,
            IsRefunded = false,
            StartingPageNumber = 1,
            CarryoverIncomeTotal = 0,
            CarryoverExpenseTotal = 0,
            CarryoverFiscalYear = null
        };
        _viewModel.StartEdit();
        _viewModel.EditNote = "備考の誤字を修正";

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync(new IcCard
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001",
            IsLent = true,
            LastLentAt = lentAt,
            LastLentStaff = "STAFF00000000001",
            IsRefunded = true,
            RefundedAt = refundedAt,
            StartingPageNumber = 7,
            CarryoverIncomeTotal = 120000,
            CarryoverExpenseTotal = 95000,
            CarryoverFiscalYear = 2025
        });
        _cardRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<IcCard>())).ReturnsAsync(true);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.SaveAsync();

        // Assert: 編集した備考は反映され、編集対象外の列はすべて DB の値のまま
        _cardRepositoryMock.Verify(r => r.UpdateAsync(It.Is<IcCard>(c =>
            c.Note == "備考の誤字を修正" &&
            c.StartingPageNumber == 7 &&
            c.CarryoverIncomeTotal == 120000 &&
            c.CarryoverExpenseTotal == 95000 &&
            c.CarryoverFiscalYear == 2025 &&
            c.IsRefunded == true &&
            c.RefundedAt == refundedAt &&
            c.IsLent == true &&
            c.LastLentAt == lentAt &&
            c.LastLentStaff == "STAFF00000000001"
        )), Times.Once);
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
    /// 本テストはリポジトリ呼び出しで成功を検証する。完了メッセージが
    /// 残ることは Issue #1759 の DeleteAsync_WhenSucceeds_ShouldKeepCompletionMessage が担保する。
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

        _cardRepositoryMock.Setup(r => r.DeleteAsync("0102030405060708")).ReturnsAsync(ICCardManager.Data.Repositories.CardOperationResult.Success);
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

        // Assert - ダイアログでエラーが表示されること
        _dialogServiceMock.Verify(d => d.ShowError(
            It.Is<string>(s => s.Contains("貸出中")),
            It.IsAny<string>()), Times.Once);
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

    #region 払い戻しテスト（Issue #1603）

    /// <summary>
    /// 払い戻し時に Income=0／Expense=残高／Balance=0／Summary=「払戻しによる払出」の
    /// Ledger が作成され、カードが払戻済状態に更新されること
    /// </summary>
    [Fact]
    public async Task RefundAsync_ShouldCreateRefundLedgerWithBalanceAsExpense()
    {
        // Arrange
        const string idm = "0102030405060708";
        var card = new CardDto
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001",
            IsLent = false,
            IsRefunded = false
        };
        _viewModel.SelectedCard = card;

        // 最新残高 3,000 円
        _ledgerRepositoryMock.Setup(r => r.GetLatestLedgerAsync(idm))
            .ReturnsAsync(new Ledger { CardIdm = idm, Balance = 3000 });
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(1);
        _cardRepositoryMock.Setup(r => r.SetRefundedAsync(idm))
            .ReturnsAsync(ICCardManager.Data.Repositories.CardOperationResult.Success);
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, It.IsAny<bool>()))
            .ReturnsAsync(new IcCard { CardIdm = idm, CardType = "はやかけん", CardNumber = "H-001" });
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.RefundAsync();

        // Assert - 残高を払出金額として計上し残高 0 の払戻 Ledger が生成される
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.Is<Ledger>(l =>
            l.CardIdm == idm &&
            l.Income == 0 &&
            l.Expense == 3000 &&
            l.Balance == 0 &&
            l.Summary == "払戻しによる払出" &&
            l.IsLentRecord == false)), Times.Once);
        // 最新残高を取得していること
        _ledgerRepositoryMock.Verify(r => r.GetLatestLedgerAsync(idm), Times.Once);
        // カードが払戻済状態に更新されること
        _cardRepositoryMock.Verify(r => r.SetRefundedAsync(idm), Times.Once);
    }

    /// <summary>
    /// 残高が存在しない（Ledger なし）場合は Expense=0／Balance=0 で払い戻されること
    /// </summary>
    [Fact]
    public async Task RefundAsync_WithNoLedger_ShouldUseZeroBalance()
    {
        // Arrange
        const string idm = "0102030405060708";
        _viewModel.SelectedCard = new CardDto
        {
            CardIdm = idm,
            CardType = "nimoca",
            CardNumber = "N-001",
            IsLent = false,
            IsRefunded = false
        };

        // 履歴なし → 残高 0 とみなす
        _ledgerRepositoryMock.Setup(r => r.GetLatestLedgerAsync(idm)).ReturnsAsync((Ledger?)null);
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(1);
        _cardRepositoryMock.Setup(r => r.SetRefundedAsync(idm))
            .ReturnsAsync(ICCardManager.Data.Repositories.CardOperationResult.Success);
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, It.IsAny<bool>()))
            .ReturnsAsync(new IcCard { CardIdm = idm, CardType = "nimoca", CardNumber = "N-001" });
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.RefundAsync();

        // Assert
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.Is<Ledger>(l =>
            l.Income == 0 &&
            l.Expense == 0 &&
            l.Balance == 0 &&
            l.Summary == "払戻しによる払出")), Times.Once);
        _cardRepositoryMock.Verify(r => r.SetRefundedAsync(idm), Times.Once);
    }

    /// <summary>
    /// 貸出中のカードは払い戻しできず、エラーダイアログを表示して処理を中断すること
    /// </summary>
    [Fact]
    public async Task RefundAsync_LentCard_ShouldShowErrorAndNotRefund()
    {
        // Arrange
        _viewModel.SelectedCard = new CardDto
        {
            CardIdm = "0102030405060708",
            CardType = "はやかけん",
            CardNumber = "H-001",
            IsLent = true,
            IsRefunded = false
        };

        // Act
        await _viewModel.RefundAsync();

        // Assert - 「貸出中」を含むエラーダイアログが表示され、払戻処理は一切行われない
        _dialogServiceMock.Verify(d => d.ShowError(
            It.Is<string>(s => s.Contains("貸出中")),
            It.IsAny<string>()), Times.Once);
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Ledger>()), Times.Never);
        _cardRepositoryMock.Verify(r => r.SetRefundedAsync(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// カード未選択時に払い戻しを呼んでも何も起きないこと
    /// </summary>
    [Fact]
    public async Task RefundAsync_WithNoSelectedCard_ShouldDoNothing()
    {
        // Arrange
        _viewModel.SelectedCard = null;

        // Act
        await _viewModel.RefundAsync();

        // Assert
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Ledger>()), Times.Never);
        _cardRepositoryMock.Verify(r => r.SetRefundedAsync(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// 確認ダイアログでキャンセルした場合は払い戻し処理を行わないこと
    /// </summary>
    [Fact]
    public async Task RefundAsync_WhenUserCancelsConfirmation_ShouldNotRefund()
    {
        // Arrange
        const string idm = "0102030405060708";
        _viewModel.SelectedCard = new CardDto
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001",
            IsLent = false,
            IsRefunded = false
        };
        _ledgerRepositoryMock.Setup(r => r.GetLatestLedgerAsync(idm))
            .ReturnsAsync(new Ledger { CardIdm = idm, Balance = 1000 });
        // ユーザーが確認ダイアログで「いいえ」を選択
        _dialogServiceMock.Setup(d => d.ShowWarningConfirmation(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        // Act
        await _viewModel.RefundAsync();

        // Assert
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Ledger>()), Times.Never);
        _cardRepositoryMock.Verify(r => r.SetRefundedAsync(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// SetRefundedAsync が失敗した場合はエラーダイアログを表示すること
    /// </summary>
    [Fact]
    public async Task RefundAsync_WhenSetRefundedFails_ShouldShowError()
    {
        // Arrange
        const string idm = "0102030405060708";
        _viewModel.SelectedCard = new CardDto
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001",
            IsLent = false,
            IsRefunded = false
        };
        _ledgerRepositoryMock.Setup(r => r.GetLatestLedgerAsync(idm))
            .ReturnsAsync(new Ledger { CardIdm = idm, Balance = 500 });
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(1);
        // 払戻状態への更新が失敗
        _cardRepositoryMock.Setup(r => r.SetRefundedAsync(idm))
            .ReturnsAsync(ICCardManager.Data.Repositories.CardOperationResult.NotFound);
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, It.IsAny<bool>()))
            .ReturnsAsync(new IcCard { CardIdm = idm, CardType = "はやかけん", CardNumber = "H-001" });
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.RefundAsync();

        // Assert - 失敗時はエラーダイアログを表示
        _dialogServiceMock.Verify(d => d.ShowError(
            It.IsAny<string>(),
            It.Is<string>(title => title.Contains("払い戻し"))), Times.Once);
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

    #region Issue #756: 繰越額のユーザー入力

    /// <summary>
    /// CarryoverBalanceが指定されている場合、その値が繰越レコードの残高として使用されること
    /// </summary>
    [Fact]
    public async Task SaveAsync_CarryoverMode_WithCarryoverBalance_ShouldUseUserSpecifiedBalance()
    {
        // Arrange
        var idm = "0102030405060708";
        var preReadBalance = 4780; // カードの現在残高
        var userSpecifiedBalance = 5000; // ユーザーが入力した月初め残高

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<IcCard>())).ReturnsAsync(true);

        // 繰越モード + ユーザー指定の繰越額
        _dialogServiceMock.Setup(d => d.ShowCardRegistrationModeDialog(It.IsAny<int?>()))
            .Returns(new ICCardManager.Views.Dialogs.CardRegistrationModeResult
            {
                IsNewPurchase = false,
                CarryoverMonth = 5,
                StartingPageNumber = 1,
                CarryoverBalance = userSpecifiedBalance
            });

        _viewModel.SetPreReadBalance(preReadBalance);

        _viewModel.StartNewCard();
        _viewModel.EditCardIdm = idm;
        _viewModel.EditCardType = "はやかけん";
        _viewModel.EditCardNumber = "H-001";

        // Act
        await _viewModel.SaveAsync();

        // Assert: ユーザー指定の繰越額（5,000円）が残額に反映され、受入金額は0（空欄）であること
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.Is<Ledger>(l =>
            l.Income == 0 &&
            l.Balance == userSpecifiedBalance &&
            l.Summary == "5月から繰越"
        )), Times.Once);
    }

    /// <summary>
    /// 3月からの繰越は「前年度より繰越」として扱い、受入金額に残高を記録すること
    /// </summary>
    [Fact]
    public async Task SaveAsync_CarryoverMode_March_ShouldBeFiscalYearCarryover()
    {
        // Arrange
        var idm = "0102030405060708";
        var carryoverBalance = 6000;

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<IcCard>())).ReturnsAsync(true);

        // 繰越モード、3月を選択
        _dialogServiceMock.Setup(d => d.ShowCardRegistrationModeDialog(It.IsAny<int?>()))
            .Returns(new ICCardManager.Views.Dialogs.CardRegistrationModeResult
            {
                IsNewPurchase = false,
                CarryoverMonth = 3,
                StartingPageNumber = 1,
                CarryoverBalance = carryoverBalance
            });

        _viewModel.SetPreReadBalance(carryoverBalance);

        _viewModel.StartNewCard();
        _viewModel.EditCardIdm = idm;
        _viewModel.EditCardType = "はやかけん";
        _viewModel.EditCardNumber = "H-001";

        // Act
        await _viewModel.SaveAsync();

        // Assert: 3月繰越は「前年度より繰越」となり、受入金額に残高が入ること
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.Is<Ledger>(l =>
            l.Income == carryoverBalance &&
            l.Balance == carryoverBalance &&
            l.Summary.Contains("前年度")
        )), Times.Once);
    }

    /// <summary>
    /// CarryoverBalanceがnullの場合、事前読み取り残高にフォールバックすること
    /// </summary>
    [Fact]
    public async Task SaveAsync_CarryoverMode_WithoutCarryoverBalance_ShouldFallbackToPreReadBalance()
    {
        // Arrange
        var idm = "0102030405060708";
        var preReadBalance = 4780;

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<IcCard>())).ReturnsAsync(true);

        // 繰越モード、CarryoverBalance は null（未指定）
        _dialogServiceMock.Setup(d => d.ShowCardRegistrationModeDialog(It.IsAny<int?>()))
            .Returns(new ICCardManager.Views.Dialogs.CardRegistrationModeResult
            {
                IsNewPurchase = false,
                CarryoverMonth = 5,
                StartingPageNumber = 1,
                CarryoverBalance = null
            });

        _viewModel.SetPreReadBalance(preReadBalance);

        _viewModel.StartNewCard();
        _viewModel.EditCardIdm = idm;
        _viewModel.EditCardType = "はやかけん";
        _viewModel.EditCardNumber = "H-001";

        // Act
        await _viewModel.SaveAsync();

        // Assert: CarryoverBalanceがnullなので、事前読み取り残高（4,780円）が残額として使用され、受入は0
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.Is<Ledger>(l =>
            l.Income == 0 &&
            l.Balance == preReadBalance &&
            l.Summary == "5月から繰越"
        )), Times.Once);
    }

    /// <summary>
    /// ShowCardRegistrationModeDialogにカードの現在残高が渡されること
    /// </summary>
    [Fact]
    public async Task SaveAsync_NewCard_ShouldPassPreReadBalanceToDialog()
    {
        // Arrange
        var idm = "0102030405060708";
        var preReadBalance = 3500;

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<IcCard>())).ReturnsAsync(true);

        _viewModel.SetPreReadBalance(preReadBalance);

        _viewModel.StartNewCard();
        _viewModel.EditCardIdm = idm;
        _viewModel.EditCardType = "はやかけん";
        _viewModel.EditCardNumber = "H-001";

        // Act
        await _viewModel.SaveAsync();

        // Assert: ダイアログにカードの現在残高が渡されていること
        _dialogServiceMock.Verify(d => d.ShowCardRegistrationModeDialog(preReadBalance), Times.Once);
    }

    /// <summary>
    /// CardRegistrationModeResultのCarryoverBalanceプロパティのデフォルト値がnullであること
    /// </summary>
    [Fact]
    public void CardRegistrationModeResult_CarryoverBalance_DefaultShouldBeNull()
    {
        // Arrange & Act
        var result = new ICCardManager.Views.Dialogs.CardRegistrationModeResult();

        // Assert
        result.CarryoverBalance.Should().BeNull();
    }

    #endregion

    #region Issue #819: 繰越額が履歴逆算値で上書きされるバグの修正

    /// <summary>
    /// 履歴がある場合でもユーザー指定の繰越額が優先されること
    /// （履歴から逆算した初期残高で上書きされないこと）
    /// </summary>
    [Fact]
    public async Task SaveAsync_CarryoverMode_WithHistoryAndCarryoverBalance_ShouldUseUserSpecifiedBalance()
    {
        // Arrange
        var idm = "0102030405060708";
        var userSpecifiedBalance = 8000; // ユーザーが入力した繰越額
        var today = DateTime.Today;

        // 履歴データ（この履歴から逆算すると 4790 + 210 = 5000 になるが、
        // ユーザーが 8000 を指定しているのでそちらが優先されるべき）
        var preReadHistory = new List<LedgerDetail>
        {
            new() { UseDate = today, EntryStation = "博多", ExitStation = "天神", Amount = 210, Balance = 4790 }
        };

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<IcCard>())).ReturnsAsync(true);

        // 繰越モード + ユーザー指定の繰越額
        _dialogServiceMock.Setup(d => d.ShowCardRegistrationModeDialog(It.IsAny<int?>()))
            .Returns(new ICCardManager.Views.Dialogs.CardRegistrationModeResult
            {
                IsNewPurchase = false,
                CarryoverMonth = 1,
                StartingPageNumber = 1,
                CarryoverBalance = userSpecifiedBalance
            });

        _viewModel.SetPreReadBalance(4790);
        _viewModel.SetPreReadHistory(preReadHistory);

        _viewModel.StartNewCard();
        _viewModel.EditCardIdm = idm;
        _viewModel.EditCardType = "はやかけん";
        _viewModel.EditCardNumber = "H-001";

        // Act
        await _viewModel.SaveAsync();

        // Assert: ユーザー指定の繰越額（8,000円）が使用され、
        // 履歴から逆算した値（5,000円）ではないこと
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.Is<Ledger>(l =>
            l.Income == 0 &&
            l.Balance == userSpecifiedBalance &&
            l.Summary == "1月から繰越"
        )), Times.Once);
    }

    /// <summary>
    /// 履歴があり繰越額が未指定の場合、従来通り履歴から逆算した値が使用されること
    /// </summary>
    [Fact]
    public async Task SaveAsync_CarryoverMode_WithHistoryAndNoCarryoverBalance_ShouldUseCalculatedBalance()
    {
        // Arrange
        var idm = "0102030405060708";
        var today = DateTime.Today;

        // 履歴データ: 利用 210円、残高 4790円 → 逆算すると 4790 + 210 = 5000
        var preReadHistory = new List<LedgerDetail>
        {
            new() { UseDate = today, EntryStation = "博多", ExitStation = "天神", Amount = 210, Balance = 4790 }
        };

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<IcCard>())).ReturnsAsync(true);

        // 繰越モード、CarryoverBalance は null（未指定）
        _dialogServiceMock.Setup(d => d.ShowCardRegistrationModeDialog(It.IsAny<int?>()))
            .Returns(new ICCardManager.Views.Dialogs.CardRegistrationModeResult
            {
                IsNewPurchase = false,
                CarryoverMonth = 1,
                StartingPageNumber = 1,
                CarryoverBalance = null
            });

        _viewModel.SetPreReadBalance(4790);
        _viewModel.SetPreReadHistory(preReadHistory);

        _viewModel.StartNewCard();
        _viewModel.EditCardIdm = idm;
        _viewModel.EditCardType = "はやかけん";
        _viewModel.EditCardNumber = "H-001";

        // Act
        await _viewModel.SaveAsync();

        // Assert: CarryoverBalanceがnullなので、履歴から逆算した値（5,000円）が使用されること
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.Is<Ledger>(l =>
            l.Income == 0 &&
            l.Balance == 5000 &&
            l.Summary == "1月から繰越"
        )), Times.Once);
    }

    #endregion

    #region Issue #1727: カード登録時の履歴インポート失敗の通知

    /// <summary>
    /// 履歴インポートが失敗する状態を作る。
    /// </summary>
    /// <remarks>
    /// SQLITE_BUSY ではなく <see cref="InvalidOperationException"/> を使うのは、
    /// リトライ待機（ローカルモードで最大 2.6 秒）を挟まずに「任意の例外で無言失敗しない」
    /// ことを検証するため。Issue #1727 の故障は SQLITE_BUSY に限らない。
    /// </remarks>
    private void ArrangeFailingHistoryImport(string idm)
    {
        _ledgerRepositoryMock.Setup(r => r.GetExistingDetailKeysAsync(idm, It.IsAny<DateTime>()))
            .ReturnsAsync(new HashSet<(DateTime?, int?, bool)>());
        _ledgerRepositoryMock.Setup(r => r.GetLatestBeforeDateAsync(idm, It.IsAny<DateTime>()))
            .ReturnsAsync((Ledger?)null);
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));
    }

    /// <summary>
    /// 新規登録用の入力を整える（履歴あり・新規購入モード）。
    /// </summary>
    private void ArrangeNewCardWithHistory(string idm, string cardNumber)
    {
        var preReadHistory = new List<LedgerDetail>
        {
            new() { UseDate = DateTime.Today, EntryStation = "博多", ExitStation = "天神", Amount = 210, Balance = 4790 }
        };

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<IcCard>())).ReturnsAsync(true);

        _viewModel.SetPreReadBalance(4790);
        _viewModel.SetPreReadHistory(preReadHistory);

        _viewModel.StartNewCard();
        _viewModel.EditCardIdm = idm;
        _viewModel.EditCardType = "はやかけん";
        _viewModel.EditCardNumber = cardNumber;
    }

    /// <summary>
    /// 履歴インポートが失敗した場合、「登録しました」と成功扱いで表示しないこと。
    /// </summary>
    /// <remarks>
    /// Issue #1727: 初期残高行は「履歴が入る前提」で履歴最古エントリから逆算した値のため、
    /// 履歴が入らないまま成功表示すると、職員は残高チェーンが実カードとずれたことに気付けない。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_NewCard_WhenHistoryImportFails_ShouldNotReportSuccess()
    {
        // Arrange
        var idm = "0102030405060708";
        ArrangeNewCardWithHistory(idm, "H-001");
        ArrangeFailingHistoryImport(idm);

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.StatusMessage.Should().NotBe("登録しました",
            "履歴の取込に失敗しているため成功として表示してはならない");
        _viewModel.IsStatusError.Should().BeTrue(
            "取込失敗はエラーとして識別できる状態で表示する必要がある");
    }

    /// <summary>
    /// 履歴インポートが失敗した場合、復旧手段を示すエラーダイアログを表示すること。
    /// </summary>
    [Fact]
    public async Task SaveAsync_NewCard_WhenHistoryImportFails_ShouldShowErrorDialogWithRecoveryGuidance()
    {
        // Arrange
        var idm = "0102030405060708";
        ArrangeNewCardWithHistory(idm, "H-001");
        ArrangeFailingHistoryImport(idm);

        string? shownMessage = null;
        _dialogServiceMock.Setup(d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((message, _) => shownMessage = message);

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _dialogServiceMock.Verify(d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        shownMessage.Should().NotBeNull();
        shownMessage!.Should().Contain("H-001", "どの交通系ICカードで起きたかを特定できる必要がある");
        shownMessage.Should().Contain("CSVインポート", "復旧手段を提示する必要がある");
    }

    /// <summary>
    /// 履歴インポートが失敗しても、カード自体は登録済みのため一覧を更新し編集を終了すること。
    /// </summary>
    /// <remarks>
    /// Issue #1727: 失敗時に編集フォームを開いたまま残すと、職員が同じ内容で再保存して
    /// 「既に登録されています」に突き当たる。カード行の作成自体は成功しているため、
    /// 成功時と同じ後処理（一覧再読込・編集終了）を行ったうえでエラーを提示する。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_NewCard_WhenHistoryImportFails_ShouldStillRefreshListAndExitEditMode()
    {
        // Arrange
        var idm = "0102030405060708";
        ArrangeNewCardWithHistory(idm, "H-001");
        ArrangeFailingHistoryImport(idm);

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.IsEditing.Should().BeFalse("カード行は登録済みのため編集モードは終了する");
        _cardRepositoryMock.Verify(r => r.GetAllAsync(), Times.AtLeastOnce(),
            "登録済みのカードを一覧に反映する必要がある");
    }

    /// <summary>
    /// 履歴インポート失敗時のエラーメッセージがエラーメッセージ品質基準を満たすこと。
    /// </summary>
    /// <remarks>
    /// `.claude/rules/error-messages.md` の「何が／なぜ／どうすれば」3要素。
    /// 生の例外メッセージ（英語・技術用語）を露出していないことも併せて検証する（Issue #1614）。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_NewCard_HistoryImportFailureMessage_SatisfiesErrorMessageQualityCriteria()
    {
        // Arrange
        var idm = "0102030405060708";
        ArrangeNewCardWithHistory(idm, "H-001");
        ArrangeFailingHistoryImport(idm);

        string? shownMessage = null;
        _dialogServiceMock.Setup(d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((message, _) => shownMessage = message);

        // Act
        await _viewModel.SaveAsync();

        // Assert
        shownMessage.Should().NotBeNull();
        var message = shownMessage!;

        // 何が: 対象が交通系ICカードであることと、何に失敗したかが分かる
        message.Should().Contain("交通系ICカード");
        message.Should().Contain("利用履歴");

        // なぜ: 台帳がどういう状態になったかを説明している
        message.Should().Contain("台帳");

        // どうすれば: 行動指示で終わる
        message.TrimEnd().Should().EndWith("してください。");

        // 生の例外メッセージを露出しない（Issue #1614）
        message.Should().NotContain("database is locked");

        // 曖昧な定型文で終わらせない
        message.Should().NotContain("エラーが発生しました。\n");
        message.Length.Should().BeGreaterThan(20);

        // ステータス欄も同様に誤解を生まないこと
        _viewModel.StatusMessage.Should().Contain("履歴");
        _viewModel.StatusMessage.TrimEnd().Should().EndWith("してください。");
    }

    /// <summary>
    /// 一覧の再読込が失敗しても、履歴インポート失敗の通知は行われること。
    /// </summary>
    /// <remarks>
    /// Issue #1727 のレビュー指摘。取込が失敗する原因（共有フォルダの切断・DB のロック）は
    /// 直後の `LoadCardsAsync`（`GetAllAsync`）でも同じく例外になる。通知を後処理のあとに
    /// 置いたままだと、**この修正が対象とするまさにその状況でだけ通知が失われる**。
    /// カード行と操作ログはコミット済みのため、職員は登録失敗と誤解して再登録し
    /// 「既に登録されています」に突き当たる。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_NewCard_WhenHistoryImportFailsAndRefreshAlsoFails_StillNotifiesFailure()
    {
        // Arrange
        var idm = "0102030405060708";
        ArrangeNewCardWithHistory(idm, "H-001");
        ArrangeFailingHistoryImport(idm);

        // 取込失敗と同じ原因（DB 到達不能）で一覧の再読込も失敗する状況を模擬する
        _cardRepositoryMock.Setup(r => r.GetAllAsync())
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        string? shownMessage = null;
        _dialogServiceMock.Setup(d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((message, _) => shownMessage = message);

        // Act
        Func<Task> act = async () => await _viewModel.SaveAsync();

        // Assert
        await act.Should().NotThrowAsync("後処理の失敗で未処理例外にしない");
        shownMessage.Should().NotBeNull("一覧の再読込が失敗しても取込失敗は通知する");
        shownMessage!.Should().Contain("CSVインポート");
        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.IsEditing.Should().BeFalse("カード行は登録済みのため編集モードは終了する");
    }

    /// <summary>
    /// 一覧の再読込の失敗を、成功パスでは握りつぶさないこと。
    /// </summary>
    /// <remarks>
    /// Issue #1727 のレビュー指摘への対処は例外フィルタで失敗時のみに限定しており、
    /// 成功時の挙動（例外がそのまま伝播する）は変えていないことを固定する。
    /// 成功時まで握りつぶすと、一覧が古いまま「登録しました」と表示されてしまう。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_NewCard_WhenImportSucceedsButRefreshFails_DoesNotSwallowException()
    {
        // Arrange
        var idm = "0102030405060708";
        ArrangeNewCardWithHistory(idm, "H-001");

        _ledgerRepositoryMock.Setup(r => r.GetExistingDetailKeysAsync(idm, It.IsAny<DateTime>()))
            .ReturnsAsync(new HashSet<(DateTime?, int?, bool)>());
        _ledgerRepositoryMock.Setup(r => r.GetLatestBeforeDateAsync(idm, It.IsAny<DateTime>()))
            .ReturnsAsync((Ledger?)null);
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(1);
        _ledgerRepositoryMock.Setup(r => r.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);

        _cardRepositoryMock.Setup(r => r.GetAllAsync())
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        // Act
        Func<Task> act = async () => await _viewModel.SaveAsync();

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>(
            "成功パスの挙動は変更していない（例外フィルタで失敗時のみ握っている）");
    }

    /// <summary>
    /// 履歴インポートが成功した場合は従来どおり「登録しました」と表示し、
    /// エラーダイアログを出さないこと（回帰防止）。
    /// </summary>
    [Fact]
    public async Task SaveAsync_NewCard_WhenHistoryImportSucceeds_ShouldReportSuccess()
    {
        // Arrange
        var idm = "0102030405060708";
        ArrangeNewCardWithHistory(idm, "H-001");

        _ledgerRepositoryMock.Setup(r => r.GetExistingDetailKeysAsync(idm, It.IsAny<DateTime>()))
            .ReturnsAsync(new HashSet<(DateTime?, int?, bool)>());
        _ledgerRepositoryMock.Setup(r => r.GetLatestBeforeDateAsync(idm, It.IsAny<DateTime>()))
            .ReturnsAsync((Ledger?)null);
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(1);
        _ledgerRepositoryMock.Setup(r => r.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.StatusMessage.Should().Be("登録しました");
        _viewModel.IsStatusError.Should().BeFalse();
        _dialogServiceMock.Verify(d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    #endregion

    #region Issue #1759: 影響行数0（競合）を検出したときの案内と一覧再読込

    /// <summary>
    /// Issue #1759: 編集保存で UpdateAsync が false を返したとき、
    /// 3要素の案内を出し、カード一覧を再読込すること
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CardRepository.UpdateAsync</c> が false を返すのは
    /// <c>UPDATE ... WHERE card_idm = @cardIdm AND is_deleted = 0</c> が 0 行に一致した場合だけ、
    /// つまり編集中に対象カードが論理削除された（共有モードで他 PC が削除した等）ことを意味する。
    /// 修正前は「更新に失敗しました」の8文字だけを表示し一覧も再読込しなかったため、
    /// 何度保存し直しても同じ8文字が出るだけで、アプリを開き直すまで状況が変わらなかった。
    /// </para>
    /// <para>
    /// 再読込は <c>.claude/rules/development-conventions.md</c>（Issue #1753）の
    /// 「競合検出時は UI 側で一覧を再読込すること」に基づく。文言で「一覧を確認して
    /// やり直す」と案内する以上、再読込しないと同じエラーを繰り返す。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SaveAsync_ExistingCard_WhenUpdateMatchesNoRow_ShouldReloadCardsAndShowActionableError()
    {
        // Arrange
        var idm = "0102030405060708";
        _viewModel.SelectedCard = new CardDto
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001"
        };
        _viewModel.StartEdit();
        _viewModel.EditNote = "備考の誤字を直した";

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync(new IcCard
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001"
        });
        // 他PCがこのカードを論理削除した → WHERE is_deleted = 0 に 0 行 → false
        _cardRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<IcCard>())).ReturnsAsync(false);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.SaveAsync();

        // Assert: 一覧を再読込していること（削除済みカードが選択されたまま残らない）
        _cardRepositoryMock.Verify(r => r.GetAllAsync(), Times.Once);

        // 3要素（.claude/rules/error-messages.md）
        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.StatusMessage.Length.Should().BeGreaterOrEqualTo(20);
        _viewModel.StatusMessage.Should().Contain("H-001");             // 何が
        _viewModel.StatusMessage.Should().Contain("削除された可能性");   // なぜ
        _viewModel.StatusMessage.Should().EndWith("やり直してください。"); // どうすれば

        // 入力内容を失わせない（Issue #1757: エラー表示時に CancelEdit() を呼ばない）
        _viewModel.IsEditing.Should().BeTrue();
        _viewModel.EditNote.Should().Be("備考の誤字を直した");
    }

    /// <summary>
    /// Issue #1759: 削除済みカードの復元で RestoreAsync が false を返したとき、
    /// 3要素の案内を出し、カード一覧を再読込すること
    /// </summary>
    /// <remarks>
    /// <c>RestoreAsync</c> の WHERE は <c>is_deleted = 1</c> のため、false は
    /// 「他 PC が先に復元した」ことを意味する。更新分岐と同じ欠陥形状であり、
    /// 更新だけを直すと同じ再発を呼び込むため併せて是正する
    /// （<c>.claude/rules/development-conventions.md</c> Issue #1730 の横断洗い出し）。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_NewCard_WhenRestoreMatchesNoRow_ShouldReloadCardsAndShowActionableError()
    {
        // Arrange
        var idm = "0102030405060708";
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync(new IcCard
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001",
            IsDeleted = true
        });
        // 他PCが先に復元した → WHERE is_deleted = 1 に 0 行 → false
        _cardRepositoryMock.Setup(r => r.RestoreAsync(idm)).ReturnsAsync(false);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        _viewModel.StartNewCard();
        _viewModel.EditCardIdm = idm;
        _viewModel.EditCardNumber = "H-002";

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _cardRepositoryMock.Verify(r => r.GetAllAsync(), Times.Once);

        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.StatusMessage.Length.Should().BeGreaterOrEqualTo(20);
        _viewModel.StatusMessage.Should().Contain("H-001");                 // 何が（削除時点の管理番号）
        _viewModel.StatusMessage.Should().Contain("先に復元された可能性");   // なぜ
        _viewModel.StatusMessage.Should().EndWith("やり直してください。");   // どうすれば

        // 「削除された可能性」（更新側の理由）と取り違えていないこと
        _viewModel.StatusMessage.Should().NotContain("削除された可能性");
    }

    /// <summary>
    /// Issue #1759: 編集中に管理番号を書き換えていても、競合の案内は
    /// <b>一覧に載っている管理番号</b>で対象を名指しすること
    /// </summary>
    /// <remarks>
    /// 「カード一覧で状態を確認してからやり直してください」と案内する以上、
    /// 一覧に存在しない編集後の番号で名指しすると案内どおりの確認ができない。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_WhenUpdateMatchesNoRow_ShouldNameTargetByItsListedNumber()
    {
        // Arrange
        var idm = "0102030405060708";
        _viewModel.SelectedCard = new CardDto
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001"
        };
        _viewModel.StartEdit();
        _viewModel.EditCardNumber = "H-777";  // 番号を打ち直した直後に他PCが削除した

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<IcCard>())).ReturnsAsync(false);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.StatusMessage.Should().Contain("H-001", "一覧に載っている管理番号で名指しすること");
        _viewModel.StatusMessage.Should().NotContain("H-777", "未保存の入力値で名指ししないこと");
        _viewModel.EditCardNumber.Should().Be("H-777", "入力内容は消さないこと");
    }

    #endregion

    #region Issue #1759: 成功メッセージが CancelEdit() で消されないこと

    /// <summary>
    /// Issue #1759: 更新成功時、完了メッセージが表示されたまま残ること
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CancelEdit()</c> は <c>StatusMessage</c> を空にするため、完了メッセージを
    /// その<b>前</b>に設定すると一度も表示されない。Issue #1727 はこの順序問題を
    /// カード登録経路でのみ是正しており、更新・削除・復元・払戻の各経路は
    /// 「設定 → 再読込 → CancelEdit()」のままで**完了メッセージが消えていた**。
    /// </para>
    /// <para>
    /// ステータス欄の所在（Issue #1727 / #1759 の XAML 修正）と、この順序の両方が
    /// 揃って初めてメッセージが利用者に届く。片方だけでは不十分。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SaveAsync_ExistingCard_WhenUpdateSucceeds_ShouldKeepCompletionMessage()
    {
        // Arrange
        var idm = "0102030405060708";
        _viewModel.SelectedCard = new CardDto
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001"
        };
        _viewModel.StartEdit();
        _viewModel.EditNote = "更新後のメモ";

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync(new IcCard
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001"
        });
        _cardRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<IcCard>())).ReturnsAsync(true);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.StatusMessage.Should().Be("更新しました");
        _viewModel.IsStatusError.Should().BeFalse();
        _viewModel.IsEditing.Should().BeFalse(); // CancelEdit() は従来どおり呼ばれる
    }

    /// <summary>
    /// Issue #1759: 削除成功時、完了メッセージが表示されたまま残ること
    /// </summary>
    [Fact]
    public async Task DeleteAsync_WhenSucceeds_ShouldKeepCompletionMessage()
    {
        // Arrange
        var idm = "0102030405060708";
        _viewModel.SelectedCard = new CardDto
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001",
            IsLent = false
        };

        _cardRepositoryMock.Setup(r => r.DeleteAsync(idm))
            .ReturnsAsync(ICCardManager.Data.Repositories.CardOperationResult.Success);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.DeleteAsync();

        // Assert
        _viewModel.StatusMessage.Should().Be("削除しました");
        _viewModel.IsStatusError.Should().BeFalse();
    }

    /// <summary>
    /// Issue #1759: 削除済みカードの復元成功時、完了メッセージが表示されたまま残ること
    /// </summary>
    [Fact]
    public async Task SaveAsync_NewCard_WhenRestoreSucceeds_ShouldKeepCompletionMessage()
    {
        // Arrange
        var idm = "0102030405060708";
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync(new IcCard
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001",
            IsDeleted = true
        });
        _cardRepositoryMock.Setup(r => r.RestoreAsync(idm)).ReturnsAsync(true);
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync(new IcCard
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001"
        });
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        _viewModel.StartNewCard();
        _viewModel.EditCardIdm = idm;
        _viewModel.EditCardNumber = "H-002";

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.StatusMessage.Should().Be("H-001 を復元しました");
        _viewModel.IsStatusError.Should().BeFalse();
    }

    /// <summary>
    /// Issue #1759: 払い戻し成功時、完了メッセージが表示されたまま残ること
    /// </summary>
    [Fact]
    public async Task RefundAsync_WhenSucceeds_ShouldKeepCompletionMessage()
    {
        // Arrange
        const string idm = "0102030405060708";
        _viewModel.SelectedCard = new CardDto
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001",
            IsLent = false,
            IsRefunded = false
        };

        _ledgerRepositoryMock.Setup(r => r.GetLatestLedgerAsync(idm))
            .ReturnsAsync(new Ledger { CardIdm = idm, Balance = 3000 });
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(1);
        _cardRepositoryMock.Setup(r => r.SetRefundedAsync(idm))
            .ReturnsAsync(ICCardManager.Data.Repositories.CardOperationResult.Success);
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, It.IsAny<bool>()))
            .ReturnsAsync(new IcCard { CardIdm = idm, CardType = "はやかけん", CardNumber = "H-001" });
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.RefundAsync();

        // Assert
        _viewModel.StatusMessage.Should().Be("払い戻しが完了しました（払戻額: ¥3,000）");
        _viewModel.IsStatusError.Should().BeFalse();
    }

    #endregion
}
