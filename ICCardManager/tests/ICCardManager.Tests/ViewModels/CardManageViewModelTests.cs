using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Models;
using ICCardManager.Data;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure.Timing;
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
using System.Text.Json;
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
    /// <summary>
    /// 操作ログの記録先。<see cref="OperationLogger"/> のログ記録メソッドは virtual ではないため
    /// <see cref="_operationLoggerMock"/> では検証できない（モックの実体が本物の実装を実行する）。
    /// 「ログが残ったか」は本物の実装が書き込むこのリポジトリで検証する（Issue #1760）。
    /// </summary>
    private readonly Mock<IOperationLogRepository> _operationLogRepositoryMock;
    private readonly Mock<IDialogService> _dialogServiceMock;
    private readonly Mock<IStaffAuthService> _staffAuthServiceMock;
    private readonly LendingService _lendingService;
    /// <summary>
    /// ViewModel が MainViewModel へ送るカード読み取り抑制メッセージ（Issue #852）を記録する（Issue #1807）。
    /// </summary>
    private readonly List<ICCardManager.Common.Messages.CardReadingSuppressedMessage> _suppressionMessages = new();
    /// <summary>
    /// Issue #1843: OnCardRead は fire-and-forget でディスパッチするため、例外を観測するのは
    /// 呼び出し元（IDispatcherService）の責務。本番の WpfDispatcherService と同じく
    /// 「記録して再スローしない」代役を使う。
    /// </summary>
    private readonly RecordingDispatcherService _dispatcher = new();
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
        _operationLogRepositoryMock = new Mock<IOperationLogRepository>();
        _operationLoggerMock = new Mock<OperationLogger>(_operationLogRepositoryMock.Object, Mock.Of<ICurrentOperatorContext>());

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

        var messenger = new WeakReferenceMessenger();
        messenger.Register<ICCardManager.Common.Messages.CardReadingSuppressedMessage>(
            this, (_, message) => _suppressionMessages.Add(message));

        _viewModel = new CardManageViewModel(
            _cardRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _cardReaderMock.Object,
            _validationServiceMock.Object,
            _operationLoggerMock.Object,
            _dialogServiceMock.Object,
            _staffAuthServiceMock.Object,
            _lendingService,
            messenger,
            _dispatcher,
            Mock.Of<INavigationService>(),
            () => throw new InvalidOperationException("このテストは貸出記録作成ダイアログを使用しません"));
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

    /// <summary>
    /// Issue #1807: 未登録カード経由（<see cref="CardManageViewModel.StartNewCardWithIdmAsync"/>）で
    /// 登録モードに入ったときも、「新規登録」ボタン経由（<see cref="CardManageViewModel.StartNewCard"/>）と同様に
    /// MainViewModel のカード読み取りを抑制すること。解放は CancelEdit / Cleanup のみ。
    /// </summary>
    [Fact]
    public async Task StartNewCardWithIdmAsync_未登録カードでは抑制を取得したまま入力を待つこと()
    {
        // Arrange
        var idm = "0102030405060708";
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync((IcCard?)null);

        // Act
        var completed = await _viewModel.StartNewCardWithIdmAsync(idm);

        // Assert
        completed.Should().BeFalse("ダイアログは入力のために開いたまま");
        _suppressionMessages.Should().Contain(
            m => m.Value && m.Source == ICCardManager.Common.Messages.CardReadingSource.CardRegistration,
            "登録モードに入った時点で抑制を取得する");
        _suppressionMessages.Should().NotContain(m => !m.Value,
            "ダイアログが開いている間は抑制を解放しない");
    }

    /// <summary>
    /// Issue #1844: 判定（<c>GetByIdmAsync</c>）が失敗すると登録モードにも
    /// 「ダイアログを閉じる」経路にも到達しないため、解放を担う CancelEdit / Cleanup が走らない。
    /// 抑制を取得したまま抜けると、メイン画面は全カードタッチを無言で無視する。
    /// 取得側で取り消すこと。
    /// </summary>
    [Fact]
    public async Task StartNewCardWithIdmAsync_判定に失敗したら取得した抑制を取り消すこと()
    {
        // Arrange
        var idm = "0102030405060708";
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        // Act
        Func<Task> act = async () => await _viewModel.StartNewCardWithIdmAsync(idm);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>(
            "呼び出し元（ダイアログの Loaded）が失敗を観測して閉じられるよう、例外は握りつぶさない");
        _suppressionMessages.Should().Contain(
            m => !m.Value && m.Source == ICCardManager.Common.Messages.CardReadingSource.CardRegistration,
            "登録モードへ入れなかったので、入口で取得した抑制を取り消す");
        _viewModel.IsEditing.Should().BeFalse("登録モードには入っていない");
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

        // Issue #1760: 更新前データを読めないと更新自体を行わないため、
        // 対象行が存在する（実 DB で成立する）状態を仕掛ける
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false))
            .ReturnsAsync(new IcCard { CardIdm = idm, CardType = "はやかけん", CardNumber = "H-001" });
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
        // Issue #1760: 更新前データを読めないと更新自体を行わないため、
        // 対象行が存在する（実 DB で成立する）状態を仕掛ける
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false))
            .ReturnsAsync(new IcCard { CardIdm = idm, CardType = "はやかけん", CardNumber = "H-001" });
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

        // Issue #1760: 削除前データを読めないと削除自体を行わないため、
        // 対象行が存在する（実 DB で成立する）状態を仕掛ける
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync("0102030405060708", false))
            .ReturnsAsync(new IcCard { CardIdm = "0102030405060708", CardType = "はやかけん", CardNumber = "H-001" });
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

    #region Issue #1763: カード内履歴が無い登録での初期残高行の書込み失敗の通知

    /// <summary>
    /// 新規登録用の入力を整える（履歴なし・新規購入モード）。
    /// </summary>
    /// <remarks>
    /// 事前読み取り履歴を設定せず、カードからの読み取りも空にすることで
    /// 「カード内に取り込む履歴が無い」経路（<c>filteredHistory</c> が空）へ入れる。
    /// </remarks>
    private void ArrangeNewCardWithoutHistory(string idm, string cardNumber, int balance = 5000)
    {
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<IcCard>())).ReturnsAsync(true);
        _cardReaderMock.Setup(r => r.ReadHistoryAsync(idm)).ReturnsAsync(new List<LedgerDetail>());

        _viewModel.SetPreReadBalance(balance);

        _viewModel.StartNewCard();
        _viewModel.EditCardIdm = idm;
        _viewModel.EditCardType = "はやかけん";
        _viewModel.EditCardNumber = cardNumber;
    }

    /// <summary>
    /// 初期残高行の書込みが失敗した場合、「登録しました」と成功扱いで表示しないこと。
    /// </summary>
    /// <remarks>
    /// Issue #1763: 修正前はこの経路だけ <c>_ledgerRepository.InsertAsync</c> を直接呼び、
    /// 例外を <c>LogWarning</c> で握りつぶして「登録しました」と表示していた。
    /// ここで失われる行は「新規購入 / ○月から繰越」＝<b>そのカード唯一の受入行</b>で、
    /// 欠落すると台帳が 0 行のまま払出だけが積み上がり、月次帳票で
    /// 「受入 − 払出 = 残額」が年度を通して成立しなくなる。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_NewCard_WithoutHistory_WhenInitialLedgerWriteFails_ShouldNotReportSuccess()
    {
        // Arrange
        var idm = "0102030405060708";
        ArrangeNewCardWithoutHistory(idm, "H-002");
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.StatusMessage.Should().NotBe("登録しました",
            "唯一の受入行が記録できていないため成功として表示してはならない");
        _viewModel.IsStatusError.Should().BeTrue(
            "書込み失敗はエラーとして識別できる状態で表示する必要がある");
    }

    /// <summary>
    /// 初期残高行の書込みが失敗した場合、復旧手段を示すエラーダイアログを表示すること。
    /// </summary>
    [Fact]
    public async Task SaveAsync_NewCard_WithoutHistory_WhenInitialLedgerWriteFails_ShouldShowErrorDialogWithRecoveryGuidance()
    {
        // Arrange
        var idm = "0102030405060708";
        ArrangeNewCardWithoutHistory(idm, "H-002");
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        string? shownMessage = null;
        _dialogServiceMock.Setup(d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((message, _) => shownMessage = message);

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _dialogServiceMock.Verify(d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        shownMessage.Should().NotBeNull();
        shownMessage!.Should().Contain("H-002", "どの交通系ICカードで起きたかを特定できる必要がある");
        shownMessage.Should().Contain("残高の行を手動で追加してください", "復旧手段を提示する必要がある");

        // 生の例外メッセージを露出しない（Issue #1614）
        shownMessage.Should().NotContain("database is locked");

        // 取り込む利用履歴が存在しないため、CSVインポートは実行できない指示になる
        shownMessage.Should().NotContain("CSVインポート");
    }

    /// <summary>
    /// 初期残高行の書込みが失敗しても、カード自体は登録済みのため一覧を更新し編集を終了すること。
    /// </summary>
    [Fact]
    public async Task SaveAsync_NewCard_WithoutHistory_WhenInitialLedgerWriteFails_ShouldStillRefreshListAndExitEditMode()
    {
        // Arrange
        var idm = "0102030405060708";
        ArrangeNewCardWithoutHistory(idm, "H-002");
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.IsEditing.Should().BeFalse("カード行は登録済みのため編集モードは終了する");
        _cardRepositoryMock.Verify(r => r.GetAllAsync(), Times.AtLeastOnce(),
            "登録済みのカードを一覧に反映する必要がある");
    }

    /// <summary>
    /// 一覧の再読込が失敗しても、初期残高行の書込み失敗の通知は行われること。
    /// </summary>
    /// <remarks>
    /// Issue #1727 と同じ理由。書込みが失敗する原因（共有フォルダの切断・DB のロック）は
    /// 直後の <c>LoadCardsAsync</c>（<c>GetAllAsync</c>）でも同じく例外になるため、
    /// 通知を後処理のあとに置いたままだと本修正が対象とする状況でだけ通知が失われる。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_NewCard_WithoutHistory_WhenWriteFailsAndRefreshAlsoFails_StillNotifiesFailure()
    {
        // Arrange
        var idm = "0102030405060708";
        ArrangeNewCardWithoutHistory(idm, "H-002");
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        _cardRepositoryMock.Setup(r => r.GetAllAsync())
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        string? shownMessage = null;
        _dialogServiceMock.Setup(d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((message, _) => shownMessage = message);

        // Act
        Func<Task> act = async () => await _viewModel.SaveAsync();

        // Assert
        await act.Should().NotThrowAsync("後処理の失敗で未処理例外にしない");
        shownMessage.Should().NotBeNull("一覧の再読込が失敗しても書込み失敗は通知する");
        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.IsEditing.Should().BeFalse("カード行は登録済みのため編集モードは終了する");
    }

    /// <summary>
    /// 初期残高行の書込みが成功した場合は従来どおり「登録しました」と表示し、
    /// エラーダイアログを出さないこと（回帰防止）。
    /// </summary>
    [Fact]
    public async Task SaveAsync_NewCard_WithoutHistory_WhenInitialLedgerSucceeds_ShouldReportSuccess()
    {
        // Arrange
        var idm = "0102030405060708";
        ArrangeNewCardWithoutHistory(idm, "H-002");
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(1);

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.StatusMessage.Should().Be("登録しました");
        _viewModel.IsStatusError.Should().BeFalse();
        _dialogServiceMock.Verify(d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.Is<Ledger>(l =>
            l.CardIdm == idm && l.Summary == "新規購入" && l.Balance == 5000
        )), Times.Once, "初期残高行は従来どおり登録される");
    }

    /// <summary>
    /// 残額を読み取れなかった場合は、初期レコードを作らずカード登録のみ成功させること（Issue #1282 の維持）。
    /// </summary>
    /// <remarks>
    /// <c>BuildInitialLedgerAsync</c> が返す null は「残額の読み取りに失敗した」の表現であり、
    /// DB 書き込みの失敗ではない。Issue #1763 で書込み失敗を通知するようにしたあとも、
    /// こちらは通知の対象にしない（Issue #1282 の判断）。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_NewCard_WithoutHistory_WhenBalanceUnavailable_ShouldReportSuccessWithoutLedger()
    {
        // Arrange
        var idm = "0102030405060708";
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<IcCard>())).ReturnsAsync(true);
        _cardReaderMock.Setup(r => r.ReadHistoryAsync(idm)).ReturnsAsync(new List<LedgerDetail>());
        _cardReaderMock.Setup(r => r.ReadBalanceAsync(idm)).ReturnsAsync((int?)null);

        _viewModel.StartNewCard();
        _viewModel.EditCardIdm = idm;
        _viewModel.EditCardType = "はやかけん";
        _viewModel.EditCardNumber = "H-002";

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.StatusMessage.Should().Be("登録しました");
        _viewModel.IsStatusError.Should().BeFalse();
        _dialogServiceMock.Verify(d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Ledger>()), Times.Never,
            "残額が取得できない場合は初期レコードを作らない（Issue #1282）");
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

        // Issue #1760: 削除前データを読めないと削除自体を行わないため、
        // 対象行が存在する（実 DB で成立する）状態を仕掛ける
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false))
            .ReturnsAsync(new IcCard { CardIdm = idm, CardType = "はやかけん", CardNumber = "H-001" });
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

    #region Issue #1760: 監査ログを残せない書き込みを行わないこと

    /// <summary>
    /// Issue #1760: 更新前データを読めなかったときは更新自体を行わないこと
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>GetByIdmAsync</c>（<c>is_deleted = 0</c>）が null を返した時点で、対象カードは
    /// その時点で存在しないことが確定している。通常はこの後の <c>UpdateAsync</c> も
    /// 同じ WHERE で 0 行になるが、<b>読み取りと書き込みの間に他 PC がカードを復元する</b>と
    /// 1 行に一致して成功し得る。従来はこの経路で更新だけが通り、
    /// <c>if (beforeCard != null)</c> により操作ログが 1 行も残らなかった。
    /// </para>
    /// <para>
    /// <c>operation_log</c> は 6 年保存される唯一の監査記録であり、記録の漏れは
    /// 誤った記録が残るのと同等以上に問題になる。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SaveAsync_ExistingCard_WhenTargetRowMissing_ShouldNotUpdateWithoutAuditLog()
    {
        // Arrange
        const string idm = "0102030405060708";
        _viewModel.SelectedCard = new CardDto
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001"
        };
        _viewModel.StartEdit();
        _viewModel.EditNote = "更新後のメモ";

        // 読み取り時点では他 PC が論理削除済み
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync((IcCard?)null);
        // その直後に他 PC が復元した → UPDATE は 1 行に一致して成功し得る
        _cardRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<IcCard>())).ReturnsAsync(true);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.SaveAsync();

        // Assert - 監査ログを残せない更新は行わない
        _cardRepositoryMock.Verify(r => r.UpdateAsync(It.IsAny<IcCard>()), Times.Never,
            "更新前データを読めていない状態で書き込むと、変更が監査記録に残らない");
        _operationLogRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<OperationLog>()), Times.Never);

        // 競合として案内し、一覧を再読込していること
        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.StatusMessage.Should().Contain("H-001");
        _viewModel.StatusMessage.Should().EndWith("やり直してください。");
        _cardRepositoryMock.Verify(r => r.GetAllAsync(), Times.Once);
        _viewModel.EditNote.Should().Be("更新後のメモ", "入力内容は消さないこと");
    }

    /// <summary>
    /// Issue #1760: 更新が成功した経路では必ず操作ログが 1 行残ること（正常系の回帰固定）
    /// </summary>
    /// <remarks>
    /// 失敗パスだけを直すと、成功パスの記録を落とす退行に気付けない。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_ExistingCard_WhenUpdateSucceeds_ShouldWriteAuditLog()
    {
        // Arrange
        const string idm = "0102030405060708";
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
            CardNumber = "H-001",
            Note = "更新前のメモ"
        });
        _cardRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<IcCard>())).ReturnsAsync(true);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _operationLogRepositoryMock.Verify(r => r.InsertAsync(It.Is<OperationLog>(log =>
            log.TargetTable == OperationLogger.Tables.IcCard &&
            log.TargetId == idm &&
            log.Action == OperationLogger.Actions.Update &&
            log.BeforeData!.Contains("更新前のメモ") &&
            log.AfterData!.Contains("更新後のメモ"))), Times.Once);
    }

    /// <summary>
    /// Issue #1760: 払い戻し前データを読めなかったときは払い戻し自体を行わないこと
    /// </summary>
    /// <remarks>
    /// カード更新と同じ競合（読み取りが null → 他 PC が復元 → <c>SetRefundedAsync</c> が成功）で、
    /// 払戻済への変更が監査記録に残らないまま確定してしまう。あわせて、払戻台帳だけが作られて
    /// カードは払戻済にならない中途半端な状態も防ぐため、台帳の作成より前に判定する。
    /// </remarks>
    [Fact]
    public async Task RefundAsync_WhenTargetRowMissing_ShouldNotRefundWithoutAuditLog()
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
        // 読み取り時点では他 PC が論理削除済み
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync((IcCard?)null);
        // その直後に他 PC が復元した → 払戻済への更新は成功し得る
        _cardRepositoryMock.Setup(r => r.SetRefundedAsync(idm))
            .ReturnsAsync(ICCardManager.Data.Repositories.CardOperationResult.Success);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.RefundAsync();

        // Assert
        _cardRepositoryMock.Verify(r => r.SetRefundedAsync(idm), Times.Never,
            "払い戻し前データを読めていない状態で払戻済にすると、変更が監査記録に残らない");
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Ledger>()), Times.Never,
            "払戻台帳だけが残る中途半端な状態を作らないこと");
        _operationLogRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<OperationLog>()), Times.Never);

        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.StatusMessage.Should().Contain("H-001");
        _viewModel.StatusMessage.Should().Contain("払い戻しできませんでした",
            "「何が」は利用者が実際に行った操作で述べること（更新ではない）");
        _viewModel.StatusMessage.Should().EndWith("やり直してください。");
    }

    /// <summary>
    /// Issue #1760: 払い戻し直後に他 PC がカードを削除しても、操作ログは残ること
    /// </summary>
    /// <remarks>
    /// <c>SetRefundedAsync</c> の成功後に行う再読取が null になるのは、その直後に
    /// 他 PC がカードを論理削除した場合だけ。払い戻しは既に確定しているため、
    /// 再読取の失敗を理由に記録を落としてはならない。
    /// </remarks>
    [Fact]
    public async Task RefundAsync_WhenCardDeletedRightAfterRefund_ShouldStillWriteAuditLog()
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
        _cardRepositoryMock.SetupSequence(r => r.GetByIdmAsync(idm, false))
            .ReturnsAsync(new IcCard
            {
                CardIdm = idm,
                CardType = "はやかけん",
                CardNumber = "H-001",
                StartingPageNumber = 7,
                IsRefunded = false
            })
            .ReturnsAsync((IcCard?)null);   // 払戻の直後に他 PC が削除した
        _cardRepositoryMock.Setup(r => r.SetRefundedAsync(idm))
            .ReturnsAsync(ICCardManager.Data.Repositories.CardOperationResult.Success);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        OperationLog? recorded = null;
        _operationLogRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<OperationLog>()))
            .Callback<OperationLog>(log => recorded = log)
            .ReturnsAsync(1);

        // Act
        await _viewModel.RefundAsync();

        // Assert
        recorded.Should().NotBeNull("払い戻しが確定した以上、監査記録を落としてはならない");
        recorded!.Action.Should().Be(OperationLogger.Actions.Update);
        recorded.TargetId.Should().Be(idm);

        var before = JsonSerializer.Deserialize<IcCard>(recorded.BeforeData!)!;
        var after = JsonSerializer.Deserialize<IcCard>(recorded.AfterData!)!;
        before.IsRefunded.Should().BeFalse();
        after.IsRefunded.Should().BeTrue("この操作が変えたのは払戻状態であること");
        after.RefundedAt.Should().NotBeNull();
        after.StartingPageNumber.Should().Be(7, "この操作が変えていない列は払戻前の値を保つこと");
    }

    /// <summary>
    /// Issue #1760: 削除前データを読めなかったときは削除自体を行わないこと
    /// </summary>
    /// <remarks>
    /// 更新・払い戻しと同型。<c>DeleteAsync</c> の WHERE も <c>is_deleted = 0</c> のため、
    /// 読み取り後に他 PC が復元すると論理削除だけが確定して監査記録が残らない。
    /// 削除は監査上もっとも重要な操作であり、記録の漏れは特に問題になる。
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_WhenTargetRowMissing_ShouldNotDeleteWithoutAuditLog()
    {
        // Arrange
        const string idm = "0102030405060708";
        _viewModel.SelectedCard = new CardDto
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001",
            IsLent = false
        };

        // 読み取り時点では他 PC が論理削除済み
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync((IcCard?)null);
        // その直後に他 PC が復元した → 論理削除は 1 行に一致して成功し得る
        _cardRepositoryMock.Setup(r => r.DeleteAsync(idm))
            .ReturnsAsync(ICCardManager.Data.Repositories.CardOperationResult.Success);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.DeleteAsync();

        // Assert
        _cardRepositoryMock.Verify(r => r.DeleteAsync(idm), Times.Never,
            "削除前データを読めていない状態で削除すると、変更が監査記録に残らない");
        _operationLogRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<OperationLog>()), Times.Never);

        // 一覧を再読込し、キャッシュも破棄していること（書き込みを通らないため #1759 の破棄が働かない）
        _cardRepositoryMock.Verify(r => r.InvalidateCache(), Times.Once);
        _cardRepositoryMock.Verify(r => r.GetAllAsync(), Times.Once);
        _dialogServiceMock.Verify(d => d.ShowError(
            It.Is<string>(m => m.Contains("H-001") && m.EndsWith("やり直してください。")),
            It.IsAny<string>()), Times.Once);
    }

    /// <summary>
    /// Issue #1760: 削除が成功した経路では必ず操作ログが 1 行残ること（正常系の回帰固定）
    /// </summary>
    [Fact]
    public async Task DeleteAsync_WhenSucceeds_ShouldWriteAuditLog()
    {
        // Arrange
        const string idm = "0102030405060708";
        _viewModel.SelectedCard = new CardDto
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001",
            IsLent = false
        };

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync(new IcCard
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001"
        });
        _cardRepositoryMock.Setup(r => r.DeleteAsync(idm))
            .ReturnsAsync(ICCardManager.Data.Repositories.CardOperationResult.Success);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.DeleteAsync();

        // Assert
        _operationLogRepositoryMock.Verify(r => r.InsertAsync(It.Is<OperationLog>(log =>
            log.TargetTable == OperationLogger.Tables.IcCard &&
            log.TargetId == idm &&
            log.Action == OperationLogger.Actions.Delete)), Times.Once);
    }

    /// <summary>
    /// Issue #1760: 復元の直後に他 PC がカードを削除しても、操作ログは残ること
    /// </summary>
    /// <remarks>
    /// <c>RestoreAsync</c> の成功後に行う再読取が null になるのは、その直後に
    /// 他 PC がカードを論理削除した場合だけ。復元は既に確定しているため、
    /// 再読取の失敗を理由に記録を落としてはならない（払い戻しと同じ判断）。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_WhenRestoredCardCannotBeReRead_ShouldStillWriteAuditLog()
    {
        // Arrange
        const string idm = "0102030405060708";
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync(new IcCard
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001",
            StartingPageNumber = 7,
            IsDeleted = true
        });
        _cardRepositoryMock.Setup(r => r.RestoreAsync(idm)).ReturnsAsync(true);
        // 復元の直後に他 PC が削除した → 再読取は null
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        OperationLog? recorded = null;
        _operationLogRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<OperationLog>()))
            .Callback<OperationLog>(log => recorded = log)
            .ReturnsAsync(1);

        _viewModel.StartNewCard();
        _viewModel.EditCardIdm = idm;
        _viewModel.EditCardNumber = "H-002";

        // Act
        await _viewModel.SaveAsync();

        // Assert
        recorded.Should().NotBeNull("復元が確定した以上、監査記録を落としてはならない");
        recorded!.Action.Should().Be(OperationLogger.Actions.Restore);
        recorded.TargetId.Should().Be(idm);

        var after = JsonSerializer.Deserialize<IcCard>(recorded.AfterData!)!;
        after.IsDeleted.Should().BeFalse("この操作が変えたのは削除状態であること");
        after.DeletedAt.Should().BeNull();
        after.StartingPageNumber.Should().Be(7, "この操作が変えていない列は復元前の値を保つこと");
    }

    /// <summary>
    /// Issue #1760: 書き込みを行わずに競合を案内する経路でも、一覧の再読込が
    /// キャッシュではなく DB を読むこと
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #1759 は影響行数 0 のときのキャッシュ破棄を <c>CardRepository.UpdateAsync</c> の
    /// 内側に置いた。更新前データを読めなかった経路は <b>UpdateAsync を呼ばない</b>ため
    /// その契機が無く、<c>LoadCardsAsync()</c> が <c>GetAllAsync</c> のキャッシュ
    /// （既定 TTL 60 秒／共有モード 15 秒）から削除済みのカードを含む古い一覧を返す。
    /// 文言が「カード一覧を再読み込みしました」と述べる以上、事実にしなければならない。
    /// </para>
    /// <para>
    /// 破棄と再読込の<b>順序</b>まで固定する。逆順だと古い一覧を読んでから破棄することになり、
    /// 画面には削除済みのカードが残る。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SaveAsync_ExistingCard_WhenTargetRowMissing_ShouldInvalidateCacheBeforeReload()
    {
        // Arrange
        const string idm = "0102030405060708";
        _viewModel.SelectedCard = new CardDto
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001"
        };
        _viewModel.StartEdit();
        _viewModel.EditNote = "更新後のメモ";

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync((IcCard?)null);

        var callOrder = new List<string>();
        _cardRepositoryMock.Setup(r => r.InvalidateCache())
            .Callback(() => callOrder.Add("invalidate"));
        _cardRepositoryMock.Setup(r => r.GetAllAsync())
            .Callback(() => callOrder.Add("reload"))
            .ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.SaveAsync();

        // Assert
        callOrder.Should().Equal(new[] { "invalidate", "reload" },
            "書き込みを 1 回も行わない経路にはリポジトリ側のキャッシュ破棄（Issue #1759）が" +
            "働かないため、再読込より前に ViewModel から破棄すること");
    }

    #endregion

    #region Issue #1761: 一覧の選択が外れても編集を継続できること（SelectedCard 非依存）

    /// <summary>
    /// Issue #1761: 編集中に一覧の選択が外れても、編集フォームと入力内容が保持されること
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SelectedItem="{Binding SelectedCard}"</c> は TwoWay バインドのため、選択行の
    /// Ctrl+クリック（<c>SelectionMode=Single</c> でも選択解除できる）や <c>Cards.Clear()</c>
    /// による選択解除の書き戻しで <see cref="CardManageViewModel.SelectedCard"/> だけが null に戻る。
    /// 編集フォームは <c>IsEditing</c> にのみ連動するため開いたままになる。
    /// </para>
    /// <para>
    /// 本 Issue の方針（案A）は「編集対象は <c>EditCardIdm</c>（主キー）であり
    /// <c>SelectedCard</c> ではない」を不変条件にすることであって、フォームを閉じることではない。
    /// 閉じる案は入力途中の備考が予告なく消えるため採らない。
    /// </para>
    /// </remarks>
    [Fact]
    public void OnSelectedCardChanged_WhenSelectionClearedDuringEdit_ShouldKeepEditFormAndInput()
    {
        // Arrange
        const string idm = "0102030405060708";
        _viewModel.SelectedCard = new CardDto
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001",
            Note = "編集前のメモ"
        };
        _viewModel.StartEdit();
        _viewModel.EditNote = "入力途中のメモ";

        // Act - 選択行を Ctrl+クリックして選択解除した
        _viewModel.SelectedCard = null;

        // Assert - 編集は継続し、編集対象は EditCardIdm が保持している
        _viewModel.IsEditing.Should().BeTrue("選択解除でフォームを閉じると入力内容が予告なく消える");
        _viewModel.IsNewCard.Should().BeFalse("既存カードの編集モードのままであること");
        _viewModel.EditCardIdm.Should().Be(idm, "編集対象を特定するのは主キーであること");
        _viewModel.EditCardType.Should().Be("はやかけん");
        _viewModel.EditCardNumber.Should().Be("H-001");
        _viewModel.EditNote.Should().Be("入力途中のメモ");
    }

    /// <summary>
    /// Issue #1761: 選択が外れた状態で保存しても、<c>EditCardIdm</c> のカードが更新され
    /// 監査ログも残ること（例外にならないこと）
    /// </summary>
    /// <remarks>
    /// 「例外が出ない」だけでは何も検証していない（<c>.claude/rules/testing.md</c>）ため、
    /// 更新対象の IDm と <c>operation_log</c> の中身まで具体値で表明する。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_WhenSelectionClearedDuringEdit_ShouldUpdateTargetIdentifiedByEditCardIdm()
    {
        // Arrange
        const string idm = "0102030405060708";
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
            CardNumber = "H-001",
            Note = "更新前のメモ"
        });
        _cardRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<IcCard>())).ReturnsAsync(true);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // 保存を押す直前に一覧の選択が外れた
        _viewModel.SelectedCard = null;

        // Act
        await _viewModel.SaveAsync();

        // Assert - EditCardIdm のカードが更新される
        _cardRepositoryMock.Verify(r => r.UpdateAsync(It.Is<IcCard>(c =>
            c.CardIdm == idm && c.Note == "更新後のメモ")), Times.Once);

        // 監査ログも欠けない
        _operationLogRepositoryMock.Verify(r => r.InsertAsync(It.Is<OperationLog>(log =>
            log.TargetTable == OperationLogger.Tables.IcCard &&
            log.TargetId == idm &&
            log.Action == OperationLogger.Actions.Update &&
            log.AfterData!.Contains("更新後のメモ"))), Times.Once);

        _viewModel.StatusMessage.Should().Be("更新しました");
        _viewModel.IsStatusError.Should().BeFalse();
    }

    /// <summary>
    /// Issue #1761: 選択が外れた状態で競合しても、案内は<b>一覧に載っていた管理番号</b>で
    /// 対象を名指しすること
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #1759 で「未保存の入力値で名指ししない」ことを固定したが、その実装は
    /// <c>SelectedCard</c> を優先し null のときだけ入力値へ退避する形だった。
    /// つまり<b>選択が外れた瞬間に、まさに禁じた「未保存の入力値による名指し」へ落ちていた</b>。
    /// </para>
    /// <para>
    /// 編集開始時に一覧の表記を退避しておけば、選択状態に関係なく正しく名指しできる。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SaveAsync_WhenSelectionClearedAndUpdateConflicts_ShouldNameTargetByItsListedNumber()
    {
        // Arrange
        const string idm = "0102030405060708";
        _viewModel.SelectedCard = new CardDto
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001"
        };
        _viewModel.StartEdit();
        _viewModel.EditCardNumber = "H-777";  // 番号を打ち直した

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // 一覧の再読込などで選択が外れた
        _viewModel.SelectedCard = null;

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.StatusMessage.Should().Contain("H-001",
            "選択が外れていても、編集開始時に一覧へ載っていた管理番号で名指しすること");
        _viewModel.StatusMessage.Should().NotContain("H-777",
            "未保存の入力値は一覧のどこにも存在せず、案内どおりの確認ができない");
        _viewModel.StatusMessage.Should().EndWith("やり直してください。");
        _viewModel.EditCardNumber.Should().Be("H-777", "入力内容は消さないこと");
    }

    /// <summary>
    /// Issue #1761: 編集中に一覧で別の行を選び直したら、名指しに使う表記も切替後の行に追随すること
    /// </summary>
    /// <remarks>
    /// 退避値の更新漏れは「選択解除の場合だけ」を直したときに残りやすい。
    /// <c>OnSelectedCardChanged</c> は編集中の選択切替でフォームの中身を差し替えるため、
    /// 退避値だけ古いままだと切替前のカードで名指しすることになる。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_WhenEditTargetSwitchedThenConflicts_ShouldNameSwitchedTarget()
    {
        // Arrange
        const string firstIdm = "0102030405060708";
        const string secondIdm = "0807060504030201";
        _viewModel.SelectedCard = new CardDto
        {
            CardIdm = firstIdm,
            CardType = "はやかけん",
            CardNumber = "H-001"
        };
        _viewModel.StartEdit();

        // 編集中に一覧で別のカードを選び直した（フォームの中身も差し替わる）
        _viewModel.SelectedCard = new CardDto
        {
            CardIdm = secondIdm,
            CardType = "nimoca",
            CardNumber = "N-002"
        };
        _viewModel.EditCardIdm.Should().Be(secondIdm, "前提: 選択切替で編集対象が差し替わること");

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(secondIdm, false)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // 保存直前に選択が外れる（再読込による書き戻し）
        _viewModel.SelectedCard = null;

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.StatusMessage.Should().Contain("N-002", "切替後のカードで名指しすること");
        _viewModel.StatusMessage.Should().NotContain("H-001", "切替前のカードで名指ししないこと");
    }

    /// <summary>
    /// Issue #1761: 認証ダイアログの待機中に選択が外れても、削除は開始時点の対象に対して行われること
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>DeleteAsync</c> は <c>RequestAuthenticationAsync</c>（職員証タッチ待ち）を挟んでから
    /// 確認ダイアログの文言で <c>SelectedCard</c> を逆参照していた。待機中に選択が外れると
    /// <c>NullReferenceException</c> になり、非同期コマンドから例外が抜けて致命的エラーダイアログになる。
    /// </para>
    /// <para>
    /// 「削除するのはボタンを押した時点で選択されていた行」であり、その後の選択状態には依存しない。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_WhenSelectionClearedDuringAuthentication_ShouldStillDeleteInitialTarget()
    {
        // Arrange
        const string idm = "0102030405060708";
        _viewModel.SelectedCard = new CardDto
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001",
            IsLent = false
        };

        // 認証（職員証タッチ待ち）の最中に一覧の選択が外れた
        _staffAuthServiceMock.Setup(s => s.RequestAuthenticationAsync(It.IsAny<string>()))
            .Callback(() => _viewModel.SelectedCard = null)
            .ReturnsAsync(new StaffAuthResult { Idm = "TEST_OPERATOR_IDM", StaffName = "テスト操作者" });

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false))
            .ReturnsAsync(new IcCard { CardIdm = idm, CardType = "はやかけん", CardNumber = "H-001" });
        _cardRepositoryMock.Setup(r => r.DeleteAsync(idm))
            .ReturnsAsync(ICCardManager.Data.Repositories.CardOperationResult.Success);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.DeleteAsync();

        // Assert - 確認ダイアログは開始時点の対象を名指しできている
        _dialogServiceMock.Verify(d => d.ShowWarningConfirmation(
            It.Is<string>(s => s.Contains("はやかけん") && s.Contains("H-001")),
            It.IsAny<string>()), Times.Once);

        _cardRepositoryMock.Verify(r => r.DeleteAsync(idm), Times.Once);
        _operationLogRepositoryMock.Verify(r => r.InsertAsync(It.Is<OperationLog>(log =>
            log.TargetTable == OperationLogger.Tables.IcCard &&
            log.TargetId == idm &&
            log.Action == OperationLogger.Actions.Delete)), Times.Once);
    }

    /// <summary>
    /// Issue #1761: 残高取得の待機中に選択が外れても、払い戻しは開始時点の対象に対して行われること
    /// </summary>
    /// <remarks>
    /// <c>RefundAsync</c> は残高取得（await）のあと確認ダイアログの文言と
    /// <c>refundCardIdm</c> の決定で <c>SelectedCard</c> を逆参照していた。
    /// 削除と同型のため同じ扱いにする（<c>.claude/rules/development-conventions.md</c> の横断洗い出し）。
    /// </remarks>
    [Fact]
    public async Task RefundAsync_WhenSelectionClearedDuringBalanceLookup_ShouldStillRefundInitialTarget()
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

        // 残高取得の最中に一覧の選択が外れた
        _ledgerRepositoryMock.Setup(r => r.GetLatestLedgerAsync(idm))
            .Callback(() => _viewModel.SelectedCard = null)
            .ReturnsAsync(new Ledger { CardIdm = idm, Balance = 3000 });
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(1);
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, It.IsAny<bool>()))
            .ReturnsAsync(new IcCard { CardIdm = idm, CardType = "はやかけん", CardNumber = "H-001" });
        _cardRepositoryMock.Setup(r => r.SetRefundedAsync(idm))
            .ReturnsAsync(ICCardManager.Data.Repositories.CardOperationResult.Success);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.RefundAsync();

        // Assert - 確認ダイアログは開始時点の対象を名指しできている
        _dialogServiceMock.Verify(d => d.ShowWarningConfirmation(
            It.Is<string>(s => s.Contains("はやかけん") && s.Contains("H-001")),
            It.IsAny<string>()), Times.Once);

        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.Is<Ledger>(l =>
            l.CardIdm == idm && l.Expense == 3000 && l.Balance == 0)), Times.Once);
        _cardRepositoryMock.Verify(r => r.SetRefundedAsync(idm), Times.Once);
    }

    /// <summary>
    /// Issue #1761: 払い戻しの競合案内も、選択状態ではなく開始時点の対象で名指しすること
    /// </summary>
    /// <remarks>
    /// 「なぜ」は更新と同じでも「何が」は利用者が行った操作で述べる（Issue #1760 の <c>ForRefund</c>）。
    /// 選択が外れた状態でも操作名と対象が食い違わないことを固定する。
    /// </remarks>
    [Fact]
    public async Task RefundAsync_WhenSelectionClearedAndTargetRowMissing_ShouldNameTargetByItsListedNumber()
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
            .Callback(() => _viewModel.SelectedCard = null)
            .ReturnsAsync(new Ledger { CardIdm = idm, Balance = 3000 });
        // 払い戻し前データが読めない（他 PC が論理削除した）
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.RefundAsync();

        // Assert
        _cardRepositoryMock.Verify(r => r.SetRefundedAsync(It.IsAny<string>()), Times.Never);
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Ledger>()), Times.Never);

        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.StatusMessage.Should().Contain("H-001", "選択が外れていても対象を名指しできること");
        _viewModel.StatusMessage.Should().Contain("払い戻し", "利用者が行った操作で述べること");
        _viewModel.StatusMessage.Should().EndWith("やり直してください。");
    }

    #endregion

    #region モーダル表示中は処理中オーバーレイを出さないこと（Issue #1793）

    // IDialogService の実装は同期モーダル（MessageBox.Show）で、職員が閉じるまで
    // 呼び出しスレッドをブロックする。BeginBusy スコープの内側から呼ぶと BusyScope.Dispose() が
    // 走らず IsBusy=true のまま残り、全面オーバーレイと不確定 ProgressBar が
    // ダイアログの背後で回り続ける。
    //
    // 「値」ではなく「呼び出し時点のスナップショット」を見る必要があるため、
    // Callback で IsBusy を捕捉する（メソッド終了後に見ても IsBusy は false に戻っている）。
    //
    // 静的検査（BusyScopeDialogConventionTests）はヘルパーメソッド経由の経路を見られない。
    // No.2 / No.3 がその 2 経路を挙動側で守る。

    [Fact]
    public async Task SaveAsync_削除済みカードの復元確認ダイアログ表示中はIsBusyがfalseであること()
    {
        // Arrange - 復元を提案する確認ダイアログ（Issue #1793 の故障シナリオそのもの）
        const string idm = "0102030405060708";
        bool? isBusyAtDialog = null;

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync(new IcCard
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001",
            IsDeleted = true
        });
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());
        _dialogServiceMock
            .Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>()))
            .Callback(() => isBusyAtDialog = _viewModel.IsBusy)
            .Returns(false);

        _viewModel.StartNewCard();
        _viewModel.EditCardIdm = idm;
        _viewModel.EditCardNumber = "H-002";

        // Act
        await _viewModel.SaveAsync();

        // Assert
        isBusyAtDialog.Should().BeFalse(
            "確認ダイアログは職員の判断を待つ設計であり、背後で回り続ける「保存中...」の表示はその判断を妨げる");
    }

    [Fact]
    public async Task SaveAsync_登録モード選択ダイアログ表示中はIsBusyがfalseであること()
    {
        // ヘルパー（ShowRegistrationModeDialog）経由の経路。静的検査では検出できない。
        const string idm = "0102030405060708";
        bool? isBusyAtDialog = null;

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync((IcCard)null);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());
        _dialogServiceMock
            .Setup(d => d.ShowCardRegistrationModeDialog(It.IsAny<int?>()))
            .Callback(() => isBusyAtDialog = _viewModel.IsBusy)
            .Returns((ICCardManager.Views.Dialogs.CardRegistrationModeResult)null);

        _viewModel.StartNewCard();
        _viewModel.EditCardIdm = idm;
        _viewModel.EditCardNumber = "H-002";

        // Act
        await _viewModel.SaveAsync();

        // Assert
        isBusyAtDialog.Should().BeFalse(
            "登録モードの選択も職員の判断を待つ。ヘルパーの内側で SuspendBusy すること");
    }

    /// <summary>
    /// Issue #1836: NotifyDeleteConflictAsync（ヘルパー経由の経路）の挙動テスト。
    /// </summary>
    /// <remarks>
    /// このヘルパーは冒頭で LoadCardsAsync() を呼び、LoadCardsAsync は自前の
    /// BeginBusy("読み込み中...") を持つ。Issue #1836 以前の BusyScope.Dispose() は入れ子の深さを
    /// 数えず無条件に SetBusy(false) していたため、内側スコープの Dispose が外側（削除中...）の
    /// IsBusy まで落としており、「ダイアログ表示時点の IsBusy」だけを見るテストは
    /// SuspendBusy の有無にかかわらず緑になった（Issue #1793 で挙動テストを置けなかった理由）。
    /// そこで<b>一覧再読込中は IsBusy=true であること</b>を併せて表明し、入れ子の解除が
    /// 退行したときに赤になるようにする。
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_競合エラーダイアログ表示中はIsBusyがfalseであること()
    {
        // Arrange - 読み取り時点で対象行が消えている（他 PC が先に削除した）
        const string idm = "0102030405060708";
        bool? isBusyDuringReload = null;
        bool? isBusyAtDialog = null;

        _viewModel.SelectedCard = new CardDto
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001",
            IsLent = false
        };
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(r => r.GetAllAsync())
            .Callback(() => isBusyDuringReload = _viewModel.IsBusy)
            .ReturnsAsync(new List<IcCard>());
        _dialogServiceMock
            .Setup(d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()))
            .Callback(() => isBusyAtDialog = _viewModel.IsBusy);

        // Act
        await _viewModel.DeleteAsync();

        // Assert
        isBusyDuringReload.Should().BeTrue(
            "内側スコープ（読み込み中...）は外側の処理中状態を解除しない（Issue #1836）");
        isBusyAtDialog.Should().BeFalse(
            "競合の案内も職員の判断を待つ。ヘルパーの内側で SuspendBusy すること（Issue #1793）");
    }

    /// <summary>
    /// Issue #1836: 一覧再読込を挟んだ後もオーバーレイが戻っていること
    /// （SuspendBusy は中断であって終了ではない、の入れ子版）
    /// </summary>
    [Fact]
    public async Task DeleteAsync_競合案内のあとも処理中オーバーレイが戻ること()
    {
        const string idm = "0102030405060708";

        _viewModel.SelectedCard = new CardDto
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001",
            IsLent = false
        };
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        await _viewModel.DeleteAsync();

        _viewModel.IsBusy.Should().BeFalse(
            "最外スコープを抜けた後は処理中状態が確実に解除されていること（深さが漏れていない）");
    }

    [Fact]
    public async Task SaveAsync_ダイアログを閉じた後は処理中オーバーレイが戻ること()
    {
        // SuspendBusy は「一時中断」であって「終了」ではない。復元しないと、
        // ダイアログ以降の DB 書き込み中にオーバーレイが消えて操作を受け付けてしまう。
        const string idm = "0102030405060708";
        bool? isBusyAfterDialog = null;

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync(new IcCard
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001",
            IsDeleted = true
        });
        _cardRepositoryMock.Setup(r => r.RestoreAsync(idm))
            .Callback(() => isBusyAfterDialog = _viewModel.IsBusy)
            .ReturnsAsync(true);
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync(new IcCard
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001"
        });
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());
        _dialogServiceMock
            .Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        _viewModel.StartNewCard();
        _viewModel.EditCardIdm = idm;
        _viewModel.EditCardNumber = "H-002";

        // Act
        await _viewModel.SaveAsync();

        // Assert
        isBusyAfterDialog.Should().BeTrue(
            "「はい」を押した後の復元処理中はオーバーレイを戻すこと（中断であって終了ではない）");
    }

    #endregion

    #region Issue #1816: カード読み取りの fire-and-forget が例外を握りつぶさないこと

    /// <summary>
    /// 読み取り中に DB 例外が出たら、例外を呼び出し元へ抜かずステータスへ案内すること
    /// </summary>
    /// <remarks>
    /// <see cref="CardManageViewModel.HandleCardReadAsync"/> の呼び出し元は
    /// <c>Dispatcher.InvokeAsync</c> の戻り値を破棄する fire-and-forget であり、
    /// 例外がここを抜けると通知は GC 契機の <c>UnobservedTaskException</c> まで遅れる。
    /// </remarks>
    [Fact]
    public async Task HandleCardReadAsync_読み取り中の例外_ステータスへ案内し例外を伝播しないこと()
    {
        // Arrange
        var idm = "0102030405060708";
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true))
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        _viewModel.StartNewCard();

        // Act
        Func<Task> act = () => _viewModel.HandleCardReadAsync(idm);

        // Assert
        await act.Should().NotThrowAsync("fire-and-forget の呼び出し元は例外を観測できないため");
        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.StatusMessage.Should().NotBeEmpty();
        _viewModel.StatusMessage.Should().NotContain(
            "database is locked", "生の例外メッセージを職員へ出さないこと（Issue #1614）");
        _viewModel.StatusMessage.Should().EndWith(
            "してください。", "行動指示で終わること（.claude/rules/error-messages.md）");
        _viewModel.IsWaitingForCard.Should().BeTrue("タッチ待ちへ戻して再試行できること");
        _viewModel.EditCardIdm.Should().BeEmpty("確認の済んでいない IDm をフォームに残さないこと");
    }

    /// <summary>
    /// 復元が確定した後の後処理で例外が出ても、読み取り失敗として案内しないこと
    /// </summary>
    /// <remarks>
    /// Issue #1816 のコードレビューで判明。<c>RestoreAsync</c> は既にコミット済みなので、
    /// 「もう一度カードをタッチしてください」と案内すると、職員は復元済みのカードを再タッチして
    /// 「既に登録されています」を見ることになる（.claude/rules/development-conventions.md
    /// 「コミット確定後の後処理を、成否の判定に巻き込まない」#1727 / #1805）。
    /// </remarks>
    [Fact]
    public async Task HandleCardReadAsync_復元後の後処理で例外_復元は記録済みと案内し再タッチを促さないこと()
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
        // 復元は確定済み。その後の一覧再読込が共有モードのロックで失敗する
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync(new IcCard
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001"
        });
        _cardRepositoryMock.Setup(r => r.GetAllAsync())
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        _viewModel.StartNewCard();

        // Act
        Func<Task> act = () => _viewModel.HandleCardReadAsync(idm);

        // Assert
        await act.Should().NotThrowAsync();
        _viewModel.StatusMessage.Should().Contain(
            "記録済み", "復元は確定しているため、失敗したかのように案内しないこと");
        _viewModel.StatusMessage.Should().NotContain(
            "もう一度カードをタッチ", "再タッチを促すと「既に登録されています」に行き着く");
        _viewModel.StatusMessage.Should().NotContain(
            "database is locked", "生の例外メッセージを職員へ出さないこと（Issue #1614）");
        _viewModel.StatusMessage.Should().EndWith("してください。", "行動指示で終わること");
        _viewModel.IsWaitingForCard.Should().BeFalse("再タッチを待たないこと");
    }

    /// <summary>
    /// 対のテスト: 正常に読み取れた場合はタッチ待ちを解除しエラーにしないこと
    /// </summary>
    [Fact]
    public async Task HandleCardReadAsync_未登録カード_タッチ待ちを解除しエラーにしないこと()
    {
        // Arrange
        var idm = "0102030405060708";
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync((IcCard?)null);
        _viewModel.StartNewCard();

        // Act
        await _viewModel.HandleCardReadAsync(idm);

        // Assert
        _viewModel.IsStatusError.Should().BeFalse();
        _viewModel.IsWaitingForCard.Should().BeFalse();
        _viewModel.EditCardIdm.Should().Be(idm);
    }

    /// <summary>
    /// Issue #1816: タッチ待ちでない状態で本体が実行されても、状態を書き換えないこと
    /// </summary>
    /// <remarks>
    /// 入口ゲート（<c>OnCardRead</c>）はカードリーダースレッドで判定され、解除は UI スレッドの
    /// 本体で初めて行われる。連続タッチでは 2 件目もゲートを通過済みで queue されているため、
    /// 本体の先頭で再判定しないと 1 件目の結果を 2 件目が上書きする（#1807 と同じ形）。
    /// </remarks>
    [Fact]
    public async Task HandleCardReadAsync_タッチ待ちでなければ何もしないこと()
    {
        // Arrange: 1 件目の読み取りが終わってタッチ待ちが解除された状態
        var firstIdm = "0102030405060708";
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(It.IsAny<string>(), true)).ReturnsAsync((IcCard?)null);
        _viewModel.StartNewCard();
        await _viewModel.HandleCardReadAsync(firstIdm);
        _viewModel.IsWaitingForCard.Should().BeFalse("前提: 1 件目の読み取りでタッチ待ちが解除される");

        // Act: queue されていた 2 件目が届く
        await _viewModel.HandleCardReadAsync("0807060504030201");

        // Assert
        _viewModel.EditCardIdm.Should().Be(firstIdm, "2 件目が 1 件目の読み取り結果を上書きしないこと");
        _cardRepositoryMock.Verify(r => r.GetByIdmAsync("0807060504030201", true), Times.Never);
    }

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
    /// <c>catch</c> ブロック自身が投げれば再び無言になる
    /// （.claude/rules/development-conventions.md Issue #1745）。
    /// </para>
    /// </remarks>
    [Fact]
    public void OnCardRead_本体のcatchが失敗しても_ディスパッチャが例外を観測すること()
    {
        // Arrange
        var idm = "0102030405060708";
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true))
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        _viewModel.StartNewCard();

        // catch ブロック末尾の IsStatusError = true で例外が出る状況を作る
        // （バインディング側の失敗に相当。catch の中の後始末は、それ自体が失敗し得る＝#1745）
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CardManageViewModel.IsStatusError) && _viewModel.IsStatusError)
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
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true))
            .ReturnsAsync((ICCardManager.Models.IcCard)null);
        _viewModel.StartNewCard();

        // Act
        _cardReaderMock.Raise(r => r.CardRead += null, new CardReadEventArgs { Idm = idm });

        // Assert
        _dispatcher.ObservedExceptions.Should().BeEmpty();
        _viewModel.EditCardIdm.Should().Be(idm);
        _viewModel.IsWaitingForCard.Should().BeFalse();
    }

    #endregion

    #endregion
}