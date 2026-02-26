using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using ICCardManager.Common.Messages;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using IOperationLogRepository = ICCardManager.Data.Repositories.IOperationLogRepository;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// CardManageViewModelのメッセージング機能テスト（Issue #852）
/// </summary>
public class CardManageViewModelMessagingTests
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
    private readonly WeakReferenceMessenger _messenger;
    private readonly CardManageViewModel _viewModel;

    public CardManageViewModelMessagingTests()
    {
        _cardRepositoryMock = new Mock<ICardRepository>();
        _ledgerRepositoryMock = new Mock<ILedgerRepository>();
        _cardReaderMock = new Mock<ICardReader>();
        _validationServiceMock = new Mock<IValidationService>();
        _staffRepositoryMock = new Mock<IStaffRepository>();
        _dialogServiceMock = new Mock<IDialogService>();
        _staffAuthServiceMock = new Mock<IStaffAuthService>();
        _cardTypeDetector = new CardTypeDetector();

        var operationLogRepositoryMock = new Mock<IOperationLogRepository>();
        _operationLoggerMock = new Mock<OperationLogger>(operationLogRepositoryMock.Object, _staffRepositoryMock.Object);

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

        _messenger = new WeakReferenceMessenger();

        _viewModel = new CardManageViewModel(
            _cardRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _cardReaderMock.Object,
            _cardTypeDetector,
            _validationServiceMock.Object,
            _operationLoggerMock.Object,
            _dialogServiceMock.Object,
            _staffAuthServiceMock.Object,
            _lendingService,
            _messenger);
    }

    /// <summary>
    /// StartNewCard で CardRegistration=true メッセージが送信されること
    /// </summary>
    [Fact]
    public void StartNewCard_ShouldSendSuppressionTrueMessage()
    {
        // Arrange
        CardReadingSuppressedMessage? receivedMessage = null;
        var recipient = new object();
        _messenger.Register<CardReadingSuppressedMessage>(recipient, (r, m) => receivedMessage = m);

        // Act
        _viewModel.StartNewCard();

        // Assert
        receivedMessage.Should().NotBeNull();
        receivedMessage!.Value.Should().BeTrue();
        receivedMessage.Source.Should().Be(CardReadingSource.CardRegistration);

        // Cleanup
        _messenger.UnregisterAll(recipient);
    }

    /// <summary>
    /// CancelEdit で CardRegistration=false メッセージが送信されること
    /// </summary>
    [Fact]
    public void CancelEdit_ShouldSendSuppressionFalseMessage()
    {
        // Arrange
        CardReadingSuppressedMessage? receivedMessage = null;
        var recipient = new object();

        // 先にStartNewCardで抑制開始
        _viewModel.StartNewCard();

        // 受信を登録（StartNewCardのメッセージは無視）
        _messenger.Register<CardReadingSuppressedMessage>(recipient, (r, m) => receivedMessage = m);

        // Act
        _viewModel.CancelEdit();

        // Assert
        receivedMessage.Should().NotBeNull();
        receivedMessage!.Value.Should().BeFalse();
        receivedMessage.Source.Should().Be(CardReadingSource.CardRegistration);

        // Cleanup
        _messenger.UnregisterAll(recipient);
    }

    /// <summary>
    /// Cleanup で CardRegistration=false メッセージが送信されること
    /// </summary>
    [Fact]
    public void Cleanup_ShouldSendSuppressionFalseMessage()
    {
        // Arrange
        CardReadingSuppressedMessage? receivedMessage = null;
        var recipient = new object();
        _messenger.Register<CardReadingSuppressedMessage>(recipient, (r, m) => receivedMessage = m);

        // Act
        _viewModel.Cleanup();

        // Assert
        receivedMessage.Should().NotBeNull();
        receivedMessage!.Value.Should().BeFalse();
        receivedMessage.Source.Should().Be(CardReadingSource.CardRegistration);

        // Cleanup
        _messenger.UnregisterAll(recipient);
    }
}
