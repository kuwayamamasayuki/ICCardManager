using FluentAssertions;
using ICCardManager.Infrastructure.CardReader;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Xunit.Abstractions;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.Infrastructure.CardReader;

/// <summary>
/// PcScCardReaderの統合テスト
/// </summary>
/// <remarks>
/// <para>
/// このテストクラスは実際のPaSoRiカードリーダーを使用します。
/// </para>
/// <para>
/// <strong>実行条件:</strong>
/// </para>
/// <list type="bullet">
/// <item><description>PaSoRi（RC-S380等）がUSB接続されていること</description></item>
/// <item><description>Windowsスマートカードサービスが起動していること</description></item>
/// </list>
/// <para>
/// <strong>テストの実行方法:</strong>
/// </para>
/// <code>
/// dotnet test --filter "Category=Integration"
/// </code>
/// <para>
/// ハードウェアがない場合、テストは自動的にスキップ（成功扱い）されます。
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public class PcScCardReaderIntegrationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly Mock<ILogger<PcScCardReader>> _loggerMock;
    private PcScCardReader? _reader;
    private static bool? _hardwareAvailable;
    private static string? _skipReason;

    public PcScCardReaderIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _loggerMock = new Mock<ILogger<PcScCardReader>>();
    }

    public void Dispose()
    {
        _reader?.Dispose();
    }

    /// <summary>
    /// ハードウェアが利用可能かどうかをチェック（キャッシュ付き）
    /// </summary>
    private bool IsHardwareAvailable()
    {
        if (_hardwareAvailable.HasValue)
        {
            if (!_hardwareAvailable.Value)
            {
                _output.WriteLine($"[SKIP] {_skipReason}");
            }
            return _hardwareAvailable.Value;
        }

        try
        {
            using var provider = new DefaultPcScProvider();
            var readers = provider.GetReaders();
            _hardwareAvailable = readers != null && readers.Length > 0;

            if (_hardwareAvailable.Value)
            {
                _output.WriteLine($"検出されたカードリーダー: {string.Join(", ", readers!)}");
            }
            else
            {
                _skipReason = "カードリーダーが検出されませんでした";
                _output.WriteLine($"[SKIP] {_skipReason}");
            }
        }
        catch (Exception ex)
        {
            _skipReason = $"ハードウェアチェックエラー: {ex.Message}";
            _output.WriteLine($"[SKIP] {_skipReason}");
            _hardwareAvailable = false;
        }

        return _hardwareAvailable.Value;
    }

    /// <summary>
    /// テスト用のPcScCardReaderを作成するヘルパー
    /// </summary>
    private PcScCardReader CreateReader()
    {
        _reader = new PcScCardReader(_loggerMock.Object);
        return _reader;
    }

    #region 接続テスト

    /// <summary>
    /// 実機でStartReadingAsyncが成功する
    /// </summary>
    [Fact]
    public async Task StartReadingAsync_WithRealHardware_Succeeds()
    {
        // ハードウェアがない場合はスキップ（成功扱い）
        if (!IsHardwareAvailable())
        {
            return;
        }

        // Arrange
        var reader = CreateReader();
        var stateChanges = new List<CardReaderConnectionState>();
        reader.ConnectionStateChanged += (s, e) =>
        {
            stateChanges.Add(e.State);
            _output.WriteLine($"接続状態変更: {e.State} - {e.Message}");
        };

        // Act
        await reader.StartReadingAsync();

        // Assert
        reader.ConnectionState.Should().Be(CardReaderConnectionState.Connected);
        reader.IsReading.Should().BeTrue();
        stateChanges.Should().Contain(CardReaderConnectionState.Connected);

        _output.WriteLine("カードリーダー接続成功");
    }

    /// <summary>
    /// 実機でStopReadingAsyncが成功する
    /// </summary>
    [Fact]
    public async Task StopReadingAsync_WithRealHardware_Succeeds()
    {
        // ハードウェアがない場合はスキップ
        if (!IsHardwareAvailable())
        {
            return;
        }

        // Arrange
        var reader = CreateReader();
        await reader.StartReadingAsync();

        // Act
        await reader.StopReadingAsync();

        // Assert
        reader.ConnectionState.Should().Be(CardReaderConnectionState.Disconnected);
        reader.IsReading.Should().BeFalse();

        _output.WriteLine("カードリーダー停止成功");
    }

    /// <summary>
    /// 実機でCheckConnectionAsyncがtrueを返す
    /// </summary>
    [Fact]
    public async Task CheckConnectionAsync_WithRealHardware_ReturnsTrue()
    {
        // ハードウェアがない場合はスキップ
        if (!IsHardwareAvailable())
        {
            return;
        }

        // Arrange
        var reader = CreateReader();

        // Act
        var isConnected = await reader.CheckConnectionAsync();

        // Assert
        isConnected.Should().BeTrue("PaSoRiが接続されている場合はtrueを返すべき");

        _output.WriteLine($"接続確認結果: {isConnected}");
    }

    /// <summary>
    /// 実機でReconnectAsyncが成功する
    /// </summary>
    [Fact]
    public async Task ReconnectAsync_WithRealHardware_Succeeds()
    {
        // ハードウェアがない場合はスキップ
        if (!IsHardwareAvailable())
        {
            return;
        }

        // Arrange
        var reader = CreateReader();
        var stateChanges = new List<CardReaderConnectionState>();
        reader.ConnectionStateChanged += (s, e) =>
        {
            stateChanges.Add(e.State);
            _output.WriteLine($"接続状態変更: {e.State} - {e.Message}");
        };

        // Act
        await reader.ReconnectAsync();

        // Assert
        reader.ConnectionState.Should().Be(CardReaderConnectionState.Connected);
        stateChanges.Should().Contain(CardReaderConnectionState.Reconnecting);
        stateChanges.Should().Contain(CardReaderConnectionState.Connected);

        _output.WriteLine("再接続成功");
    }

    #endregion

    #region ヘルスチェック・自動再接続テスト

    /// <summary>
    /// ヘルスチェックが機能する
    /// </summary>
    [Fact]
    public async Task HealthCheck_WithRealHardware_Works()
    {
        // ハードウェアがない場合はスキップ
        if (!IsHardwareAvailable())
        {
            return;
        }

        // Arrange
        var reader = CreateReader();
        await reader.StartReadingAsync();

        // Act - ヘルスチェック間隔より短い時間待機して状態確認
        await Task.Delay(1000);
        var isConnected = await reader.CheckConnectionAsync();

        // Assert
        isConnected.Should().BeTrue("ヘルスチェック中も接続は維持されるべき");
        reader.ConnectionState.Should().Be(CardReaderConnectionState.Connected);

        _output.WriteLine($"ヘルスチェック確認: 接続状態={reader.ConnectionState}");
    }

    #endregion

    #region カード読み取りテスト（手動）

    /// <summary>
    /// カード読み取り待機テスト（手動テスト用）
    /// </summary>
    /// <remarks>
    /// このテストは手動でカードをタッチする必要があります。
    /// 環境変数 PASORI_MANUAL_TEST=1 を設定して実行してください。
    ///
    /// 実行例:
    /// PASORI_MANUAL_TEST=1 dotnet test --filter "FullyQualifiedName~WaitForCardRead_Manual"
    /// </remarks>
    [Fact]
    public async Task WaitForCardRead_Manual_ReadsCardSuccessfully()
    {
        // 手動テストモードが有効でない場合はスキップ
        var manualTestEnabled = Environment.GetEnvironmentVariable("PASORI_MANUAL_TEST") == "1";
        if (!manualTestEnabled)
        {
            _output.WriteLine("[SKIP] 手動テスト: PASORI_MANUAL_TEST=1 を設定して実行してください");
            return;
        }

        // ハードウェアがない場合はスキップ
        if (!IsHardwareAvailable())
        {
            return;
        }

        // Arrange
        var reader = CreateReader();
        var cardReadTcs = new TaskCompletionSource<CardReadEventArgs>();

        reader.CardRead += (s, e) =>
        {
            _output.WriteLine($"カード検出: IDm={e.Idm}, SystemCode={e.SystemCode}");
            cardReadTcs.TrySetResult(e);
        };

        reader.Error += (s, e) =>
        {
            _output.WriteLine($"エラー: {e.Message}");
        };

        await reader.StartReadingAsync();
        _output.WriteLine("カードリーダー監視開始。10秒以内にカードをタッチしてください...");

        // Act - 10秒間カード待機
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10));
        var completedTask = await Task.WhenAny(cardReadTcs.Task, timeoutTask);

        // Assert
        if (completedTask == cardReadTcs.Task)
        {
            var args = await cardReadTcs.Task;
            args.Idm.Should().NotBeNullOrEmpty("IDmが読み取られるべき");
            args.Idm.Should().HaveLength(16, "IDmは16桁の16進数文字列であるべき");
            _output.WriteLine($"成功: カードを読み取りました - IDm={args.Idm}");
        }
        else
        {
            _output.WriteLine("タイムアウト: 10秒以内にカードがタッチされませんでした");
            Assert.Fail("カードがタッチされませんでした");
        }
    }

    #endregion

    #region Disposeテスト

    /// <summary>
    /// Disposeが正しくリソースを解放する
    /// </summary>
    [Fact]
    public async Task Dispose_WithRealHardware_ReleasesResources()
    {
        // ハードウェアがない場合はスキップ
        if (!IsHardwareAvailable())
        {
            return;
        }

        // Arrange
        var reader = CreateReader();
        await reader.StartReadingAsync();

        // Act
        reader.Dispose();

        // Assert
        reader.IsReading.Should().BeFalse("Dispose後はIsReadingがfalseであるべき");

        // 再度Disposeしてもエラーにならない
        var action = () => reader.Dispose();
        action.Should().NotThrow("複数回のDisposeはエラーにならないべき");

        _output.WriteLine("リソース解放成功");
    }

    #endregion
}

/// <summary>
/// DefaultPcScProviderの統合テスト
/// </summary>
[Trait("Category", "Integration")]
public class DefaultPcScProviderIntegrationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private DefaultPcScProvider? _provider;
    private string? _skipReason;

    public DefaultPcScProviderIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose()
    {
        _provider?.Dispose();
    }

    /// <summary>
    /// プロバイダーの初期化を試みる
    /// </summary>
    private bool TryInitializeProvider()
    {
        if (_provider != null)
        {
            return true;
        }

        try
        {
            _provider = new DefaultPcScProvider();
            return true;
        }
        catch (Exception ex)
        {
            _skipReason = $"スマートカードサービスが利用できません: {ex.Message}";
            _output.WriteLine($"[SKIP] {_skipReason}");
            return false;
        }
    }

    /// <summary>
    /// GetReadersがリーダー一覧を返す
    /// </summary>
    [Fact]
    public void GetReaders_ReturnsReaderList()
    {
        // サービスが利用できない場合はスキップ
        if (!TryInitializeProvider())
        {
            return;
        }

        // Act
        var readers = _provider!.GetReaders();

        // Assert
        // リーダーがない場合もnullまたは空配列を返す（例外にはならない）
        if (readers != null && readers.Length > 0)
        {
            _output.WriteLine($"検出されたリーダー: {string.Join(", ", readers)}");
            readers.Should().NotBeEmpty();
        }
        else
        {
            _output.WriteLine("カードリーダーが検出されませんでした（これは正常な動作です）");
        }
    }

    /// <summary>
    /// CreateMonitorがモニターを作成する
    /// </summary>
    [Fact]
    public void CreateMonitor_ReturnsMonitor()
    {
        // サービスが利用できない場合はスキップ
        if (!TryInitializeProvider())
        {
            return;
        }

        // Act
        var monitor = _provider!.CreateMonitor();

        // Assert
        monitor.Should().NotBeNull("モニターが作成されるべき");
        monitor.Dispose();

        _output.WriteLine("モニター作成成功");
    }

    /// <summary>
    /// Disposeが正しく動作する
    /// </summary>
    [Fact]
    public void Dispose_ReleasesResources()
    {
        // サービスが利用できない場合はスキップ
        if (!TryInitializeProvider())
        {
            return;
        }

        // Act
        _provider!.Dispose();

        // 再度Disposeしてもエラーにならない
        var action = () => _provider.Dispose();
        action.Should().NotThrow("複数回のDisposeはエラーにならないべき");

        _output.WriteLine("リソース解放成功");
    }
}
