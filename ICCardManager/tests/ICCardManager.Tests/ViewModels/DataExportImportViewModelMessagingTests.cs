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
}
