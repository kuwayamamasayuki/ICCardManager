using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using ICCardManager.Common.Messages;
using Xunit;

namespace ICCardManager.Tests.Common.Messages;

/// <summary>
/// CardReadingSuppressedMessageの単体テスト
/// </summary>
public class CardReadingSuppressedMessageTests
{
    /// <summary>
    /// メッセージが正しいValue/Sourceを保持すること
    /// </summary>
    [Theory]
    [InlineData(true, CardReadingSource.StaffRegistration)]
    [InlineData(false, CardReadingSource.CardRegistration)]
    [InlineData(true, CardReadingSource.Authentication)]
    public void Constructor_ShouldSetValueAndSource(bool isSuppressed, CardReadingSource source)
    {
        // Act
        var message = new CardReadingSuppressedMessage(isSuppressed, source);

        // Assert
        message.Value.Should().Be(isSuppressed);
        message.Source.Should().Be(source);
    }

    /// <summary>
    /// メッセージ送信→受信で正しく伝達されること
    /// </summary>
    [Fact]
    public void Send_ShouldDeliverMessage()
    {
        // Arrange
        var messenger = new WeakReferenceMessenger();
        CardReadingSuppressedMessage? received = null;
        var recipient = new object();

        messenger.Register<CardReadingSuppressedMessage>(recipient, (r, m) => received = m);

        // Act
        messenger.Send(new CardReadingSuppressedMessage(true, CardReadingSource.StaffRegistration));

        // Assert
        received.Should().NotBeNull();
        received!.Value.Should().BeTrue();
        received.Source.Should().Be(CardReadingSource.StaffRegistration);

        // Cleanup
        messenger.UnregisterAll(recipient);
    }

    /// <summary>
    /// 複数ソースの同時抑制が正しく動作すること
    /// </summary>
    [Fact]
    public void MultipleSourceSuppression_ShouldTrackAllSources()
    {
        // Arrange
        var messenger = new WeakReferenceMessenger();
        var suppressionSources = new System.Collections.Generic.HashSet<CardReadingSource>();
        var recipient = new object();

        messenger.Register<CardReadingSuppressedMessage>(recipient, (r, m) =>
        {
            if (m.Value)
                suppressionSources.Add(m.Source);
            else
                suppressionSources.Remove(m.Source);
        });

        // Act - 2つのソースから抑制を開始
        messenger.Send(new CardReadingSuppressedMessage(true, CardReadingSource.StaffRegistration));
        messenger.Send(new CardReadingSuppressedMessage(true, CardReadingSource.CardRegistration));

        // Assert - 両方とも追跡されている
        suppressionSources.Should().HaveCount(2);
        suppressionSources.Should().Contain(CardReadingSource.StaffRegistration);
        suppressionSources.Should().Contain(CardReadingSource.CardRegistration);

        // Cleanup
        messenger.UnregisterAll(recipient);
    }

    /// <summary>
    /// 全ソース解除後に抑制が解除されること
    /// </summary>
    [Fact]
    public void AllSourcesReleased_ShouldClearSuppression()
    {
        // Arrange
        var messenger = new WeakReferenceMessenger();
        var suppressionSources = new System.Collections.Generic.HashSet<CardReadingSource>();
        var recipient = new object();

        messenger.Register<CardReadingSuppressedMessage>(recipient, (r, m) =>
        {
            if (m.Value)
                suppressionSources.Add(m.Source);
            else
                suppressionSources.Remove(m.Source);
        });

        // Act - 抑制開始 → 全解除
        messenger.Send(new CardReadingSuppressedMessage(true, CardReadingSource.StaffRegistration));
        messenger.Send(new CardReadingSuppressedMessage(true, CardReadingSource.CardRegistration));
        messenger.Send(new CardReadingSuppressedMessage(false, CardReadingSource.StaffRegistration));
        messenger.Send(new CardReadingSuppressedMessage(false, CardReadingSource.CardRegistration));

        // Assert
        suppressionSources.Should().BeEmpty();

        // Cleanup
        messenger.UnregisterAll(recipient);
    }

    /// <summary>
    /// 同一ソースの重複送信が冪等であること
    /// </summary>
    [Fact]
    public void DuplicateSend_ShouldBeIdempotent()
    {
        // Arrange
        var messenger = new WeakReferenceMessenger();
        var suppressionSources = new System.Collections.Generic.HashSet<CardReadingSource>();
        var recipient = new object();

        messenger.Register<CardReadingSuppressedMessage>(recipient, (r, m) =>
        {
            if (m.Value)
                suppressionSources.Add(m.Source);
            else
                suppressionSources.Remove(m.Source);
        });

        // Act - 同一ソースを複数回送信
        messenger.Send(new CardReadingSuppressedMessage(true, CardReadingSource.Authentication));
        messenger.Send(new CardReadingSuppressedMessage(true, CardReadingSource.Authentication));
        messenger.Send(new CardReadingSuppressedMessage(true, CardReadingSource.Authentication));

        // Assert - HashSetなので1つだけ
        suppressionSources.Should().HaveCount(1);
        suppressionSources.Should().Contain(CardReadingSource.Authentication);

        // Act - 解除も1回で十分
        messenger.Send(new CardReadingSuppressedMessage(false, CardReadingSource.Authentication));

        // Assert
        suppressionSources.Should().BeEmpty();

        // Cleanup
        messenger.UnregisterAll(recipient);
    }

    /// <summary>
    /// Unregister後はメッセージを受信しないこと
    /// </summary>
    [Fact]
    public void Unregister_ShouldStopReceivingMessages()
    {
        // Arrange
        var messenger = new WeakReferenceMessenger();
        int receiveCount = 0;
        var recipient = new object();

        messenger.Register<CardReadingSuppressedMessage>(recipient, (r, m) => receiveCount++);

        // Act - 1回送信
        messenger.Send(new CardReadingSuppressedMessage(true, CardReadingSource.StaffRegistration));
        receiveCount.Should().Be(1);

        // Unregister
        messenger.UnregisterAll(recipient);

        // Act - 再送信
        messenger.Send(new CardReadingSuppressedMessage(true, CardReadingSource.CardRegistration));

        // Assert - カウントは増えない
        receiveCount.Should().Be(1);
    }
}
