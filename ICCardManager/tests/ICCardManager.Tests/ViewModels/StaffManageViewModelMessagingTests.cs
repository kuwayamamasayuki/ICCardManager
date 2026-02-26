using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using ICCardManager.Common.Messages;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Moq;
using Xunit;
using IOperationLogRepository = ICCardManager.Data.Repositories.IOperationLogRepository;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// StaffManageViewModelのメッセージング機能テスト（Issue #852）
/// </summary>
public class StaffManageViewModelMessagingTests
{
    private readonly Mock<IStaffRepository> _staffRepositoryMock;
    private readonly Mock<ICardReader> _cardReaderMock;
    private readonly Mock<IValidationService> _validationServiceMock;
    private readonly Mock<OperationLogger> _operationLoggerMock;
    private readonly Mock<IDialogService> _dialogServiceMock;
    private readonly Mock<IStaffAuthService> _staffAuthServiceMock;
    private readonly WeakReferenceMessenger _messenger;
    private readonly StaffManageViewModel _viewModel;

    public StaffManageViewModelMessagingTests()
    {
        _staffRepositoryMock = new Mock<IStaffRepository>();
        _cardReaderMock = new Mock<ICardReader>();
        _validationServiceMock = new Mock<IValidationService>();
        _dialogServiceMock = new Mock<IDialogService>();
        _staffAuthServiceMock = new Mock<IStaffAuthService>();

        var operationLogRepositoryMock = new Mock<IOperationLogRepository>();
        _operationLoggerMock = new Mock<OperationLogger>(operationLogRepositoryMock.Object, _staffRepositoryMock.Object);

        _messenger = new WeakReferenceMessenger();

        _viewModel = new StaffManageViewModel(
            _staffRepositoryMock.Object,
            _cardReaderMock.Object,
            _validationServiceMock.Object,
            _operationLoggerMock.Object,
            _dialogServiceMock.Object,
            _staffAuthServiceMock.Object,
            _messenger);
    }

    /// <summary>
    /// StartNewStaff で StaffRegistration=true メッセージが送信されること
    /// </summary>
    [Fact]
    public void StartNewStaff_ShouldSendSuppressionTrueMessage()
    {
        // Arrange
        CardReadingSuppressedMessage? receivedMessage = null;
        var recipient = new object();
        _messenger.Register<CardReadingSuppressedMessage>(recipient, (r, m) => receivedMessage = m);

        // Act
        _viewModel.StartNewStaff();

        // Assert
        receivedMessage.Should().NotBeNull();
        receivedMessage!.Value.Should().BeTrue();
        receivedMessage.Source.Should().Be(CardReadingSource.StaffRegistration);

        // Cleanup
        _messenger.UnregisterAll(recipient);
    }

    /// <summary>
    /// CancelEdit で StaffRegistration=false メッセージが送信されること
    /// </summary>
    [Fact]
    public void CancelEdit_ShouldSendSuppressionFalseMessage()
    {
        // Arrange
        CardReadingSuppressedMessage? receivedMessage = null;
        var recipient = new object();

        // 先にStartNewStaffで抑制開始
        _viewModel.StartNewStaff();

        // 受信を登録（StartNewStaffのメッセージは無視）
        _messenger.Register<CardReadingSuppressedMessage>(recipient, (r, m) => receivedMessage = m);

        // Act
        _viewModel.CancelEdit();

        // Assert
        receivedMessage.Should().NotBeNull();
        receivedMessage!.Value.Should().BeFalse();
        receivedMessage.Source.Should().Be(CardReadingSource.StaffRegistration);

        // Cleanup
        _messenger.UnregisterAll(recipient);
    }

    /// <summary>
    /// Cleanup で StaffRegistration=false メッセージが送信されること
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
        receivedMessage.Source.Should().Be(CardReadingSource.StaffRegistration);

        // Cleanup
        _messenger.UnregisterAll(recipient);
    }
}
