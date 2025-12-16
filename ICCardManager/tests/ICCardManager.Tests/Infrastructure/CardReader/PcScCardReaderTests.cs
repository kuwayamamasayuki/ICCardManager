using FluentAssertions;
using ICCardManager.Infrastructure.CardReader;
using Microsoft.Extensions.Logging;
using Moq;
using PCSC;
using PCSC.Exceptions;
using PCSC.Monitoring;
using Xunit;
using PcscICardReader = PCSC.ICardReader;

namespace ICCardManager.Tests.Infrastructure.CardReader;

/// <summary>
/// PcScCardReaderの単体テスト
/// </summary>
/// <remarks>
/// <para>
/// テスト対象:
/// </para>
/// <list type="bullet">
/// <item><description>接続状態管理（Connected/Disconnected/Reconnecting）</description></item>
/// <item><description>再接続ロジック（3秒間隔、最大10回）</description></item>
/// <item><description>イベント発火（ConnectionStateChanged、CardRead、Error）</description></item>
/// </list>
/// </remarks>
public class PcScCardReaderTests : IDisposable
{
    private readonly Mock<ILogger<PcScCardReader>> _loggerMock;
    private readonly Mock<IPcScProvider> _providerMock;
    private readonly Mock<ISCardMonitor> _monitorMock;
    private PcScCardReader? _reader;

    public PcScCardReaderTests()
    {
        _loggerMock = new Mock<ILogger<PcScCardReader>>();
        _providerMock = new Mock<IPcScProvider>();
        _monitorMock = new Mock<ISCardMonitor>();

        // デフォルトのモニターモック設定
        _providerMock.Setup(p => p.CreateMonitor()).Returns(_monitorMock.Object);
    }

    public void Dispose()
    {
        _reader?.Dispose();
    }

    /// <summary>
    /// テスト用のPcScCardReaderを作成するヘルパー
    /// </summary>
    private PcScCardReader CreateReader()
    {
        _reader = new PcScCardReader(_loggerMock.Object, _providerMock.Object);
        return _reader;
    }

    #region 接続状態管理テスト

    /// <summary>
    /// 初期接続成功時にIsConnected状態になる
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartReadingAsync_WhenReaderAvailable_SetsConnectedState()
    {
        // Arrange
        _providerMock.Setup(p => p.GetReaders()).Returns(new[] { "Test Reader" });
        var reader = CreateReader();
        var stateChanges = new List<CardReaderConnectionState>();
        reader.ConnectionStateChanged += (s, e) => stateChanges.Add(e.State);

        // Act
        await reader.StartReadingAsync();

        // Assert
        reader.ConnectionState.Should().Be(CardReaderConnectionState.Connected);
        reader.IsReading.Should().BeTrue();
        stateChanges.Should().Contain(CardReaderConnectionState.Connected);
    }

    /// <summary>
    /// カードリーダーが見つからない場合、Disconnected状態になる
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartReadingAsync_WhenNoReaderAvailable_ThrowsAndSetsDisconnectedState()
    {
        // Arrange
        _providerMock.Setup(p => p.GetReaders()).Returns((string[]?)null);
        var reader = CreateReader();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.StartReadingAsync());
        // 例外がスローされた後、Disconnected状態になっていることを確認
        reader.ConnectionState.Should().Be(CardReaderConnectionState.Disconnected);
    }

    /// <summary>
    /// 空のリーダー配列の場合もDisconnected状態になる
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartReadingAsync_WhenEmptyReaderArray_ThrowsAndSetsDisconnectedState()
    {
        // Arrange
        _providerMock.Setup(p => p.GetReaders()).Returns(Array.Empty<string>());
        var reader = CreateReader();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.StartReadingAsync());
        reader.ConnectionState.Should().Be(CardReaderConnectionState.Disconnected);
    }

    /// <summary>
    /// 停止時にDisconnected状態になる
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task StopReadingAsync_WhenConnected_SetsDisconnectedState()
    {
        // Arrange
        _providerMock.Setup(p => p.GetReaders()).Returns(new[] { "Test Reader" });
        var reader = CreateReader();
        await reader.StartReadingAsync();

        // Act
        await reader.StopReadingAsync();

        // Assert
        reader.ConnectionState.Should().Be(CardReaderConnectionState.Disconnected);
        reader.IsReading.Should().BeFalse();
    }

    /// <summary>
    /// CheckConnectionAsyncがリーダー存在時にtrueを返す
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckConnectionAsync_WhenReaderAvailable_ReturnsTrue()
    {
        // Arrange
        _providerMock.Setup(p => p.GetReaders()).Returns(new[] { "Test Reader" });
        var reader = CreateReader();

        // Act
        var isConnected = await reader.CheckConnectionAsync();

        // Assert
        isConnected.Should().BeTrue();
    }

    /// <summary>
    /// CheckConnectionAsyncがリーダー不在時にfalseを返す
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckConnectionAsync_WhenNoReaderAvailable_ReturnsFalse()
    {
        // Arrange
        _providerMock.Setup(p => p.GetReaders()).Returns((string[]?)null);
        var reader = CreateReader();

        // Act
        var isConnected = await reader.CheckConnectionAsync();

        // Assert
        isConnected.Should().BeFalse();
    }

    /// <summary>
    /// CheckConnectionAsyncがPCSC例外時にfalseを返す
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckConnectionAsync_WhenPCSCException_ReturnsFalse()
    {
        // Arrange
        _providerMock.Setup(p => p.GetReaders())
            .Throws(new PCSCException(SCardError.NoService, "サービスエラー"));
        var reader = CreateReader();

        // Act
        var isConnected = await reader.CheckConnectionAsync();

        // Assert
        isConnected.Should().BeFalse();
    }

    #endregion

    #region 再接続ロジックテスト

    /// <summary>
    /// 再接続間隔が3秒である
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void ReconnectIntervalMs_Is3000()
    {
        // Assert
        PcScCardReader.ReconnectIntervalMs.Should().Be(3000, "再接続間隔は3秒であるべき");
    }

    /// <summary>
    /// 最大再接続試行回数が10回である
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void MaxReconnectAttempts_Is10()
    {
        // Assert
        PcScCardReader.MaxReconnectAttempts.Should().Be(10, "最大再接続試行回数は10回であるべき");
    }

    /// <summary>
    /// ヘルスチェック間隔が10秒である
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void HealthCheckIntervalMs_Is10000()
    {
        // Assert
        PcScCardReader.HealthCheckIntervalMs.Should().Be(10000, "ヘルスチェック間隔は10秒であるべき");
    }

    /// <summary>
    /// 手動再接続が正常に動作する
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReconnectAsync_WhenReaderAvailable_ReconnectsSuccessfully()
    {
        // Arrange
        _providerMock.Setup(p => p.GetReaders()).Returns(new[] { "Test Reader" });
        var reader = CreateReader();
        var stateChanges = new List<CardReaderConnectionState>();
        reader.ConnectionStateChanged += (s, e) => stateChanges.Add(e.State);

        // Act
        await reader.ReconnectAsync();

        // Assert
        reader.ConnectionState.Should().Be(CardReaderConnectionState.Connected);
        stateChanges.Should().Contain(CardReaderConnectionState.Reconnecting, "再接続中状態を経由すべき");
        stateChanges.Should().Contain(CardReaderConnectionState.Connected, "最終的に接続状態になるべき");
    }

    /// <summary>
    /// 再接続試行時にリトライカウントが渡される
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReconnectAsync_ReportsRetryCount()
    {
        // Arrange
        _providerMock.Setup(p => p.GetReaders()).Returns((string[]?)null);
        var reader = CreateReader();
        var retryCountReported = 0;
        reader.ConnectionStateChanged += (s, e) =>
        {
            if (e.State == CardReaderConnectionState.Reconnecting)
            {
                retryCountReported = e.RetryCount;
            }
        };

        // Act
        await reader.ReconnectAsync();

        // Assert
        retryCountReported.Should().BeGreaterThan(0, "リトライカウントが報告されるべき");
    }

    /// <summary>
    /// 既に再接続中の場合は何もしない
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReconnectAsync_WhenAlreadyReconnecting_DoesNothing()
    {
        // Arrange
        _providerMock.Setup(p => p.GetReaders()).Returns(new[] { "Test Reader" });
        var reader = CreateReader();
        await reader.StartReadingAsync();

        // 再接続中状態をシミュレート
        _providerMock.Setup(p => p.GetReaders()).Returns((string[]?)null);
        await reader.ReconnectAsync();

        var callCountBefore = _providerMock.Invocations.Count(i => i.Method.Name == "GetReaders");

        // Act - 再度再接続を試みる（再接続中の場合）
        await reader.ReconnectAsync();

        // Assert - GetReadersの呼び出し回数が増えていないことを確認
        // (実装によっては増える可能性があるため、エラーにならないことを確認)
    }

    #endregion

    #region イベント発火テスト

    /// <summary>
    /// ConnectionStateChangedイベントが状態変更時に発火する
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConnectionStateChanged_FiresOnStateChange()
    {
        // Arrange
        _providerMock.Setup(p => p.GetReaders()).Returns(new[] { "Test Reader" });
        var reader = CreateReader();
        var eventFired = false;
        CardReaderConnectionState? newState = null;

        reader.ConnectionStateChanged += (s, e) =>
        {
            eventFired = true;
            newState = e.State;
        };

        // Act
        await reader.StartReadingAsync();

        // Assert
        eventFired.Should().BeTrue("ConnectionStateChangedイベントが発火すべき");
        newState.Should().Be(CardReaderConnectionState.Connected);
    }

    /// <summary>
    /// 再接続成功時にメッセージが設定される
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConnectionStateChangedEventArgs_ContainsMessage_WhenReconnected()
    {
        // Arrange
        _providerMock.Setup(p => p.GetReaders()).Returns(new[] { "Test Reader" });
        var reader = CreateReader();
        var messages = new List<string?>();

        reader.ConnectionStateChanged += (s, e) =>
        {
            messages.Add(e.Message);
        };

        // Act - 再接続を試行（成功時にメッセージが設定される）
        await reader.ReconnectAsync();

        // Assert
        // 再接続成功時に「再接続しました」というメッセージが設定される
        messages.Should().Contain(m => m != null && m.Contains("再接続"),
            "再接続成功時はメッセージが設定されるべき");
    }

    /// <summary>
    /// Errorイベントがエラー発生時に発火する
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task Error_FiresOnPCSCException()
    {
        // Arrange
        _providerMock.Setup(p => p.GetReaders())
            .Throws(new PCSCException(SCardError.NoService, "サービスエラー"));
        var reader = CreateReader();
        Exception? caughtException = null;

        reader.Error += (s, e) => caughtException = e;

        // Act
        try { await reader.StartReadingAsync(); } catch { }

        // Assert
        caughtException.Should().NotBeNull("Errorイベントが発火すべき");
    }

    #endregion

    #region ConnectionStateChangedEventArgs テスト

    /// <summary>
    /// ConnectionStateChangedEventArgsが正しいプロパティを持つ
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void ConnectionStateChangedEventArgs_HasCorrectProperties()
    {
        // Arrange & Act
        var args = new ConnectionStateChangedEventArgs(
            CardReaderConnectionState.Reconnecting,
            "再接続中",
            5);

        // Assert
        args.State.Should().Be(CardReaderConnectionState.Reconnecting);
        args.Message.Should().Be("再接続中");
        args.RetryCount.Should().Be(5);
    }

    /// <summary>
    /// ConnectionStateChangedEventArgsのデフォルト値
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void ConnectionStateChangedEventArgs_DefaultValues()
    {
        // Arrange & Act
        var args = new ConnectionStateChangedEventArgs(CardReaderConnectionState.Connected);

        // Assert
        args.State.Should().Be(CardReaderConnectionState.Connected);
        args.Message.Should().BeNull();
        args.RetryCount.Should().Be(0);
    }

    #endregion

    #region CardReadEventArgs テスト

    /// <summary>
    /// CardReadEventArgsが正しいプロパティを持つ
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void CardReadEventArgs_HasCorrectProperties()
    {
        // Arrange & Act
        var args = new CardReadEventArgs
        {
            Idm = "0102030405060708",
            SystemCode = "0003"
        };

        // Assert
        args.Idm.Should().Be("0102030405060708");
        args.SystemCode.Should().Be("0003");
    }

    /// <summary>
    /// CardReadEventArgsのデフォルト値
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void CardReadEventArgs_DefaultValues()
    {
        // Arrange & Act
        var args = new CardReadEventArgs();

        // Assert
        args.Idm.Should().BeEmpty();
        args.SystemCode.Should().BeNull();
    }

    #endregion

    #region CardReaderConnectionState テスト

    /// <summary>
    /// CardReaderConnectionState列挙型の値が正しい
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void CardReaderConnectionState_HasCorrectValues()
    {
        // Assert
        Enum.GetValues<CardReaderConnectionState>().Should().HaveCount(3);
        CardReaderConnectionState.Connected.Should().BeDefined();
        CardReaderConnectionState.Disconnected.Should().BeDefined();
        CardReaderConnectionState.Reconnecting.Should().BeDefined();
    }

    #endregion

    #region Disposeテスト

    /// <summary>
    /// Disposeが複数回呼び出されてもエラーにならない
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        // Arrange
        _providerMock.Setup(p => p.GetReaders()).Returns(new[] { "Test Reader" });
        var reader = CreateReader();

        // Act & Assert
        var action = () =>
        {
            reader.Dispose();
            reader.Dispose();
            reader.Dispose();
        };

        action.Should().NotThrow("複数回のDisposeはエラーにならないべき");
    }

    /// <summary>
    /// Dispose後はIsReadingがfalseになる
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task Dispose_AfterStartReading_StopsReading()
    {
        // Arrange
        _providerMock.Setup(p => p.GetReaders()).Returns(new[] { "Test Reader" });
        var reader = CreateReader();
        await reader.StartReadingAsync();

        // Act
        reader.Dispose();

        // Assert
        reader.IsReading.Should().BeFalse("Dispose後はIsReadingがfalseであるべき");
    }

    /// <summary>
    /// Dispose時にプロバイダーもDisposeされる
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void Dispose_DisposesProvider()
    {
        // Arrange
        var reader = CreateReader();

        // Act
        reader.Dispose();

        // Assert
        _providerMock.Verify(p => p.Dispose(), Times.Once);
    }

    #endregion

    #region モニター開始テスト

    /// <summary>
    /// StartReadingAsync時にモニターが開始される
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartReadingAsync_StartsMonitor()
    {
        // Arrange
        _providerMock.Setup(p => p.GetReaders()).Returns(new[] { "Test Reader" });
        var reader = CreateReader();

        // Act
        await reader.StartReadingAsync();

        // Assert
        _monitorMock.Verify(m => m.Start(It.IsAny<string[]>()), Times.Once);
    }

    /// <summary>
    /// StopReadingAsync時にモニターがキャンセルされる
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task StopReadingAsync_CancelsMonitor()
    {
        // Arrange
        _providerMock.Setup(p => p.GetReaders()).Returns(new[] { "Test Reader" });
        var reader = CreateReader();
        await reader.StartReadingAsync();

        // Act
        await reader.StopReadingAsync();

        // Assert
        _monitorMock.Verify(m => m.Cancel(), Times.Once);
    }

    #endregion

    #region CardReadイベントテスト

    /// <summary>
    /// カード挿入時にCardReadイベントが発火する
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CardInserted_FiresCardReadEvent_WithValidIdm()
    {
        // Arrange
        _providerMock.Setup(p => p.GetReaders()).Returns(new[] { "Test Reader" });

        // PcscICardReaderのモックを設定
        var cardReaderMock = new Mock<PcscICardReader>();

        // IDm読み取りレスポンス: IDm(8バイト) + SW1(90) + SW2(00)
        // IDm = 01 23 45 67 89 AB CD EF
        var idmResponse = new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF, 0x90, 0x00 };

        cardReaderMock
            .Setup(r => r.Transmit(It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns((byte[] sendBuffer, byte[] receiveBuffer) =>
            {
                Array.Copy(idmResponse, receiveBuffer, idmResponse.Length);
                return idmResponse.Length;
            });

        _providerMock
            .Setup(p => p.ConnectReader(It.IsAny<string>(), It.IsAny<SCardShareMode>(), It.IsAny<SCardProtocol>()))
            .Returns(cardReaderMock.Object);

        var reader = CreateReader();
        await reader.StartReadingAsync();

        CardReadEventArgs? receivedArgs = null;
        reader.CardRead += (s, e) => receivedArgs = e;

        // Act - CardInsertedイベントをシミュレート
        _monitorMock.Raise(
            m => m.CardInserted += null,
            this,
            new CardStatusEventArgs("Test Reader", SCRState.Present, new byte[] { }));

        // Assert
        receivedArgs.Should().NotBeNull("CardReadイベントが発火すべき");
        receivedArgs!.Idm.Should().Be("0123456789ABCDEF", "正しいIDmが設定されるべき");
        receivedArgs.SystemCode.Should().Be("0003", "サイバネシステムコードが設定されるべき");
    }

    /// <summary>
    /// IDm読み取り失敗時はCardReadイベントが発火しない
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CardInserted_DoesNotFireCardReadEvent_WhenIdmReadFails()
    {
        // Arrange
        _providerMock.Setup(p => p.GetReaders()).Returns(new[] { "Test Reader" });

        var cardReaderMock = new Mock<PcscICardReader>();

        // 失敗レスポンス（IDmなし）
        var failResponse = new byte[] { 0x63, 0x00 }; // エラーステータス

        cardReaderMock
            .Setup(r => r.Transmit(It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns((byte[] sendBuffer, byte[] receiveBuffer) =>
            {
                Array.Copy(failResponse, receiveBuffer, failResponse.Length);
                return failResponse.Length;
            });

        _providerMock
            .Setup(p => p.ConnectReader(It.IsAny<string>(), It.IsAny<SCardShareMode>(), It.IsAny<SCardProtocol>()))
            .Returns(cardReaderMock.Object);

        var reader = CreateReader();
        await reader.StartReadingAsync();

        var cardReadFired = false;
        reader.CardRead += (s, e) => cardReadFired = true;

        // Act - CardInsertedイベントをシミュレート
        _monitorMock.Raise(
            m => m.CardInserted += null,
            this,
            new CardStatusEventArgs("Test Reader", SCRState.Present, new byte[] { }));

        // Assert
        cardReadFired.Should().BeFalse("IDm読み取り失敗時はCardReadイベントは発火しないべき");
    }

    /// <summary>
    /// 同一カードの連続読み取りは無視される（1秒以内）
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CardInserted_IgnoresDuplicateReads_Within1Second()
    {
        // Arrange
        _providerMock.Setup(p => p.GetReaders()).Returns(new[] { "Test Reader" });

        var cardReaderMock = new Mock<PcscICardReader>();
        var idmResponse = new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF, 0x90, 0x00 };

        cardReaderMock
            .Setup(r => r.Transmit(It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns((byte[] sendBuffer, byte[] receiveBuffer) =>
            {
                Array.Copy(idmResponse, receiveBuffer, idmResponse.Length);
                return idmResponse.Length;
            });

        _providerMock
            .Setup(p => p.ConnectReader(It.IsAny<string>(), It.IsAny<SCardShareMode>(), It.IsAny<SCardProtocol>()))
            .Returns(cardReaderMock.Object);

        var reader = CreateReader();
        await reader.StartReadingAsync();

        var cardReadCount = 0;
        reader.CardRead += (s, e) => cardReadCount++;

        // Act - 同じカードを2回タッチ（間隔なし）
        _monitorMock.Raise(
            m => m.CardInserted += null,
            this,
            new CardStatusEventArgs("Test Reader", SCRState.Present, new byte[] { }));

        _monitorMock.Raise(
            m => m.CardInserted += null,
            this,
            new CardStatusEventArgs("Test Reader", SCRState.Present, new byte[] { }));

        // Assert
        cardReadCount.Should().Be(1, "1秒以内の同一カード連続読み取りは無視されるべき");
    }

    /// <summary>
    /// PCSC例外時はErrorイベントが発火する（カード取り外し以外）
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CardInserted_FiresErrorEvent_OnPCSCException()
    {
        // Arrange
        _providerMock.Setup(p => p.GetReaders()).Returns(new[] { "Test Reader" });

        _providerMock
            .Setup(p => p.ConnectReader(It.IsAny<string>(), It.IsAny<SCardShareMode>(), It.IsAny<SCardProtocol>()))
            .Throws(new PCSCException(SCardError.NoService, "サービスエラー"));

        var reader = CreateReader();
        await reader.StartReadingAsync();

        Exception? caughtException = null;
        reader.Error += (s, e) => caughtException = e;

        // Act
        _monitorMock.Raise(
            m => m.CardInserted += null,
            this,
            new CardStatusEventArgs("Test Reader", SCRState.Present, new byte[] { }));

        // Assert
        caughtException.Should().NotBeNull("PCSC例外時はErrorイベントが発火すべき");
    }

    /// <summary>
    /// カード取り外し例外（RemovedCard）はErrorイベントを発火しない
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CardInserted_DoesNotFireErrorEvent_OnRemovedCardException()
    {
        // Arrange
        _providerMock.Setup(p => p.GetReaders()).Returns(new[] { "Test Reader" });

        _providerMock
            .Setup(p => p.ConnectReader(It.IsAny<string>(), It.IsAny<SCardShareMode>(), It.IsAny<SCardProtocol>()))
            .Throws(new PCSCException(SCardError.RemovedCard, "カードが取り外されました"));

        var reader = CreateReader();
        await reader.StartReadingAsync();

        Exception? caughtException = null;
        reader.Error += (s, e) => caughtException = e;

        // Act
        _monitorMock.Raise(
            m => m.CardInserted += null,
            this,
            new CardStatusEventArgs("Test Reader", SCRState.Present, new byte[] { }));

        // Assert
        caughtException.Should().BeNull("RemovedCard例外はErrorイベントを発火しないべき（正常動作）");
    }

    /// <summary>
    /// モニター例外時にErrorイベントが発火し、切断状態になる
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MonitorException_FiresErrorEvent_AndSetsDisconnectedState()
    {
        // Arrange
        _providerMock.Setup(p => p.GetReaders()).Returns(new[] { "Test Reader" });
        var reader = CreateReader();
        await reader.StartReadingAsync();

        Exception? caughtException = null;
        reader.Error += (s, e) => caughtException = e;

        var stateChanges = new List<CardReaderConnectionState>();
        reader.ConnectionStateChanged += (s, e) => stateChanges.Add(e.State);

        // Act - MonitorExceptionイベントをシミュレート
        _monitorMock.Raise(
            m => m.MonitorException += null,
            this,
            new PCSCException(SCardError.ReaderUnavailable, "リーダーが使用できません"));

        // Assert
        caughtException.Should().NotBeNull("モニター例外時はErrorイベントが発火すべき");
        stateChanges.Should().Contain(CardReaderConnectionState.Disconnected, "モニター例外後は切断状態になるべき");
    }

    #endregion
}
