using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using ICCardManager.Common.Messages;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// DataExportImportViewModel のメッセージング機能テスト（Issue #1514）
/// </summary>
/// <remarks>
/// データインポートのカードタッチ待機中に MainViewModel 側の OnCardRead を抑制するため、
/// IsWaitingForCardTouch の状態変化に応じて
/// CardReadingSuppressedMessage(CardReadingSource.DataImport) が送信されることを検証する。
/// </remarks>
public class DataExportImportViewModelMessagingTests : IDisposable
{
    private readonly Mock<ICardRepository> _cardRepositoryMock;
    private readonly Mock<IStaffRepository> _staffRepositoryMock;
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock;
    private readonly Mock<IValidationService> _validationServiceMock;
    private readonly Mock<DbContext> _dbContextMock;
    private readonly Mock<ICacheService> _cacheServiceMock;
    private readonly Mock<IDialogService> _dialogServiceMock;
    private readonly Mock<ICardReader> _cardReaderMock;
    private readonly Mock<CsvImportService> _importServiceMock;
    private readonly Mock<CsvExportService> _exportServiceMock;
    private readonly SQLiteConnection _connection;
    private readonly DbContext _realDbContext;
    private readonly WeakReferenceMessenger _messenger;
    private readonly List<CardReadingSuppressedMessage> _receivedMessages = new();
    private readonly object _recipient = new();
    /// <summary>
    /// Issue #1843: OnCardRead は fire-and-forget でディスパッチするため、例外を観測するのは
    /// 呼び出し元（IDispatcherService）の責務。本番の WpfDispatcherService と同じく
    /// 「記録して再スローしない」代役を使う。
    /// </summary>
    private readonly ICCardManager.Tests.Infrastructure.Timing.RecordingDispatcherService _dispatcher = new();
    private readonly DataExportImportViewModel _viewModel;

    public DataExportImportViewModelMessagingTests()
    {
        _cardRepositoryMock = new Mock<ICardRepository>();
        _staffRepositoryMock = new Mock<IStaffRepository>();
        _ledgerRepositoryMock = new Mock<ILedgerRepository>();
        _validationServiceMock = new Mock<IValidationService>();
        _dbContextMock = new Mock<DbContext>();
        _cacheServiceMock = new Mock<ICacheService>();
        _dialogServiceMock = new Mock<IDialogService>();
        _cardReaderMock = new Mock<ICardReader>();

        _cardReaderMock.SetupGet(r => r.ConnectionState).Returns(CardReaderConnectionState.Connected);
        _cardReaderMock.SetupGet(r => r.IsReading).Returns(true);
        _cardReaderMock.Setup(r => r.StartReadingAsync()).Returns(Task.CompletedTask);

        _connection = new SQLiteConnection("Data Source=:memory:");
        _connection.Open();
        _realDbContext = new DbContext(":memory:");
        _realDbContext.InitializeDatabase();

        _exportServiceMock = new Mock<CsvExportService>(
            _cardRepositoryMock.Object,
            _staffRepositoryMock.Object,
            _ledgerRepositoryMock.Object);

        _importServiceMock = new Mock<CsvImportService>(
            _cardRepositoryMock.Object,
            _staffRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _validationServiceMock.Object,
            _dbContextMock.Object,
            _cacheServiceMock.Object);

        var operationLogRepository = new OperationLogRepository(_realDbContext);
        var operatorContext = new CurrentOperatorContext(new SystemClock());
        var operationLogger = new OperationLogger(operationLogRepository, operatorContext);

        _messenger = new WeakReferenceMessenger();
        _messenger.Register<CardReadingSuppressedMessage>(_recipient, (_, m) => _receivedMessages.Add(m));

        _viewModel = new DataExportImportViewModel(
            _exportServiceMock.Object,
            _importServiceMock.Object,
            _dialogServiceMock.Object,
            _cardRepositoryMock.Object,
            operationLogger,
            _messenger,
            new Mock<ICCardManager.Services.ISafeFileLauncher>().Object,
            _dispatcher,
            _cardReaderMock.Object);
    }

    public void Dispose()
    {
        _messenger.UnregisterAll(_recipient);
        _connection?.Dispose();
        _realDbContext?.Dispose();
    }

    /// <summary>
    /// カードタッチ待機を開始すると、抑制 ON のメッセージが送信されること。
    /// </summary>
    [Fact]
    public async Task StartCardTouchAsync_WhenReaderConnected_ShouldSendSuppressionOn()
    {
        await _viewModel.StartCardTouchAsync();

        _viewModel.IsWaitingForCardTouch.Should().BeTrue();
        _receivedMessages.Should().ContainSingle(m =>
            m.Value == true && m.Source == CardReadingSource.DataImport);
    }

    /// <summary>
    /// Issue #1817: カードリーダーの開始に失敗しても、生の <c>ex.Message</c> を
    /// ステータスへ出さないこと（Issue #1614）。
    /// </summary>
    /// <remarks>
    /// PaSoRi の開始失敗はネイティブ由来（<c>DllNotFoundException</c> / SEH / Win32）で
    /// <c>ex.Message</c> が英語になるため、職員には解読できない。
    /// 修正前は <c>$"カードリーダーの開始に失敗しました: {ex.Message}"</c> をそのまま表示し、
    /// かつログにも一切残していなかった。
    /// </remarks>
    [Fact]
    public async Task StartCardTouchAsync_開始失敗時_生の例外メッセージを表示しないこと()
    {
        _cardReaderMock.SetupGet(r => r.IsReading).Returns(false);
        _cardReaderMock.Setup(r => r.StartReadingAsync())
            .ThrowsAsync(new DllNotFoundException("Unable to load DLL 'felicalib.dll'"));

        await _viewModel.StartCardTouchAsync();

        _viewModel.StatusMessage.Should().NotContain("felicalib.dll",
            "生の ex.Message は英語・技術用語を含みうるため UI へ出さない（Issue #1614）");
        _viewModel.StatusMessage.Should().NotContain("Unable to load");
        _viewModel.StatusMessage.Should().Contain("カードリーダーの開始に失敗しました",
            "何が: 失敗した操作を職員の言葉で示す");
        _viewModel.StatusMessage.Should().MatchRegex("してください。?$",
            "どうすれば: 行動指示で終わる");

        // #1817 のコードレビュー指摘: ネイティブ由来の失敗は ToUserMessage の default 分岐
        //（「しばらく待ってから再度実行してください」）へ落ちるが、PaSoRi の未接続や
        // felicalib.dll の欠落は待っても解消しない＝実行できない行動指示になる。
        _viewModel.StatusMessage.Should().NotContain("しばらく待って",
            "待っても解消しない失敗に「待ってください」と案内しない");
        _viewModel.StatusMessage.Should().Contain("接続",
            "どうすれば: カードリーダーの接続確認という実行可能な行動を示す");
        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.IsWaitingForCardTouch.Should().BeFalse(
            "開始に失敗したらタッチ待機を解除する（既存挙動の維持）");
    }

    /// <summary>
    /// カードリーダー未接続時はカードタッチ待機が開始されず、抑制メッセージも送信されないこと。
    /// </summary>
    [Fact]
    public async Task StartCardTouchAsync_WhenReaderDisconnected_ShouldNotSendSuppression()
    {
        _cardReaderMock.SetupGet(r => r.ConnectionState).Returns(CardReaderConnectionState.Disconnected);

        await _viewModel.StartCardTouchAsync();

        _viewModel.IsWaitingForCardTouch.Should().BeFalse();
        _receivedMessages.Should().BeEmpty();
    }

    /// <summary>
    /// 待機開始後にキャンセルすると、抑制 OFF のメッセージが送信されること。
    /// </summary>
    [Fact]
    public async Task CancelCardTouch_AfterWaiting_ShouldSendSuppressionOff()
    {
        await _viewModel.StartCardTouchAsync();
        _receivedMessages.Clear();

        _viewModel.CancelCardTouch();

        _viewModel.IsWaitingForCardTouch.Should().BeFalse();
        _receivedMessages.Should().ContainSingle(m =>
            m.Value == false && m.Source == CardReadingSource.DataImport);
    }

    /// <summary>
    /// 待機開始後に ClearTargetCard を呼ぶと、抑制 OFF のメッセージが送信されること。
    /// </summary>
    [Fact]
    public async Task ClearTargetCard_AfterWaiting_ShouldSendSuppressionOff()
    {
        await _viewModel.StartCardTouchAsync();
        _receivedMessages.Clear();

        _viewModel.ClearTargetCard();

        _viewModel.IsWaitingForCardTouch.Should().BeFalse();
        _receivedMessages.Should().ContainSingle(m =>
            m.Value == false && m.Source == CardReadingSource.DataImport);
    }

    /// <summary>
    /// 待機開始後に Cleanup を呼ぶと、抑制 OFF のメッセージが送信されること。
    /// </summary>
    [Fact]
    public async Task Cleanup_AfterWaiting_ShouldSendSuppressionOff()
    {
        await _viewModel.StartCardTouchAsync();
        _receivedMessages.Clear();

        _viewModel.Cleanup();

        _viewModel.IsWaitingForCardTouch.Should().BeFalse();
        _receivedMessages.Should().ContainSingle(m =>
            m.Value == false && m.Source == CardReadingSource.DataImport);
    }

    /// <summary>
    /// 利用履歴以外のデータ種別に切り替えると、待機中であれば抑制 OFF が送信されること。
    /// </summary>
    [Fact]
    public async Task SelectedImportTypeChanged_WhileWaiting_ShouldSendSuppressionOff()
    {
        _viewModel.SelectedImportType = DataType.Ledgers;
        await _viewModel.StartCardTouchAsync();
        _receivedMessages.Clear();

        _viewModel.SelectedImportType = DataType.Cards;

        _viewModel.IsWaitingForCardTouch.Should().BeFalse();
        _receivedMessages.Should().ContainSingle(m =>
            m.Value == false && m.Source == CardReadingSource.DataImport);
    }

    /// <summary>
    /// StartCardTouchAsync 中に StartReadingAsync が例外を投げても、
    /// IsWaitingForCardTouch が false へ戻り、抑制 ON のあとに OFF が送信されること。
    /// </summary>
    [Fact]
    public async Task StartCardTouchAsync_WhenStartReadingFails_ShouldSendSuppressionOnThenOff()
    {
        _cardReaderMock.SetupGet(r => r.IsReading).Returns(false);
        _cardReaderMock.Setup(r => r.StartReadingAsync())
            .ThrowsAsync(new System.InvalidOperationException("test failure"));

        await _viewModel.StartCardTouchAsync();

        _viewModel.IsWaitingForCardTouch.Should().BeFalse();
        _receivedMessages.Should().HaveCount(2);
        _receivedMessages[0].Value.Should().BeTrue();
        _receivedMessages[0].Source.Should().Be(CardReadingSource.DataImport);
        _receivedMessages[1].Value.Should().BeFalse();
        _receivedMessages[1].Source.Should().Be(CardReadingSource.DataImport);
    }

    #region Issue #1843: 読み取りのディスパッチ自体が例外を観測すること

    /// <summary>
    /// カード読み取りイベントが IDispatcherService 経由でディスパッチされ、
    /// 本体の catch 自体が失敗しても例外が観測される（無言で失われない）こと
    /// </summary>
    /// <remarks>
    /// Issue #1843: 生の <c>Application.Current.Dispatcher.InvokeAsync</c> は
    /// <c>DispatcherOperation&lt;Task&gt;</c> を返すため、await しても内側の Task の例外は
    /// 観測されない（<c>Unwrap()</c> が要る。Issue #1725）。Issue #1816 の「本体全体を try/catch」は
    /// 受け皿としては正しいが、catch ブロック自身が投げれば再び無言になる（#1745）。
    /// </remarks>
    [Fact]
    public void OnCardRead_本体のcatchが失敗しても_ディスパッチャが例外を観測すること()
    {
        // Arrange
        var idm = "0102030405060708";
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false))
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        _viewModel.IsWaitingForCardTouch = true;

        // catch ブロック末尾の IsStatusError = true で例外が出る状況を作る
        // （バインディング側の失敗に相当。catch の中の後始末は、それ自体が失敗し得る＝#1745）
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DataExportImportViewModel.IsStatusError) && _viewModel.IsStatusError)
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
    [Fact]
    public void OnCardRead_正常な読み取り_例外を観測せずIDmを反映すること()
    {
        // Arrange
        var idm = "0102030405060708";
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false))
            .ReturnsAsync(new ICCardManager.Models.IcCard
            {
                CardIdm = idm,
                CardType = "はやかけん",
                CardNumber = "H-001"
            });
        _viewModel.IsWaitingForCardTouch = true;

        // Act
        _cardReaderMock.Raise(r => r.CardRead += null, new CardReadEventArgs { Idm = idm });

        // Assert
        _dispatcher.ObservedExceptions.Should().BeEmpty();
        _viewModel.TouchedCardIdm.Should().Be(idm);
        _viewModel.IsWaitingForCardTouch.Should().BeFalse();
    }

    #endregion
}
