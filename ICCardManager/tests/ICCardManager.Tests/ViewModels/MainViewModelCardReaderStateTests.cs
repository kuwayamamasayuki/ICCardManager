using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using ICCardManager.Common.Exceptions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Infrastructure.Sound;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure;
using ICCardManager.Tests.Infrastructure.CardReader;
using ICCardManager.Tests.Infrastructure.Timing;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #2327: カードリーダーの接続状態の表示と、起動時にカードリーダーの開始が失敗したときの起動処理を検証する。
/// </summary>
/// <remarks>
/// <para>
/// 起動処理（<see cref="MainViewModel.InitializeAsync"/>）は、カードリーダーの開始（<c>StartReadingAsync</c>）が
/// 起動時チェック・更新通知・共有モードの接続監視より前にあったため、開始が例外になる環境
/// （felicalib.dll はあるが Sony NFC ポートソフトウェアが無い等）ではそれらが一度も始まらなかった。
/// </para>
/// <para>
/// Issue #2377: 開始の失敗（<c>CardReaderException</c>）は起動処理で受け止め、「初期化エラー」のダイアログを出さない。
/// 案内は警告エリア（カードリーダーが通知する切断・エラー）だけで行う。それ以外の例外は従来どおり抜ける。
/// </para>
/// <para>
/// 接続状態は「接続中」「切断」の 2 つだけ。到達しない「再接続中」と、メッセージに「再接続」を含む
/// 接続中で出す「✅ 再接続しました」の分岐は撤去した。
/// </para>
/// </remarks>
public class MainViewModelCardReaderStateTests
{
    private readonly Mock<ICardReader> _cardReaderMock = new();
    private readonly Mock<ICardRepository> _cardRepositoryMock = new() { DefaultValue = DefaultValue.Empty };
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock = new() { DefaultValue = DefaultValue.Empty };
    private readonly Mock<IStaffRepository> _staffRepositoryMock = new() { DefaultValue = DefaultValue.Empty };
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock = new();
    private readonly Mock<IDatabaseInfo> _databaseInfoMock = new();
    private readonly Mock<IUpdateNotificationService> _updateNotificationMock = new();

    /// <summary>更新通知の確認（最新版ファイルの読み取り）が呼ばれたら完了する。バックグラウンド（Task.Run）で走るため待ち合わせに使う</summary>
    private readonly TaskCompletionSource<bool> _updateCheckCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TestTimerFactory _monitorTimerFactory = new();
    private readonly MainViewModel _viewModel;

    public MainViewModelCardReaderStateTests()
    {
        _settingsRepositoryMock.Setup(r => r.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());
        _updateNotificationMock.Setup(s => s.CheckForNewerVersion())
            .Callback(() => _updateCheckCalled.TrySetResult(true))
            .Returns((UpdateCheckResult)null!);
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());
        _cardRepositoryMock.Setup(r => r.GetLentAsync(It.IsAny<bool>())).ReturnsAsync(new List<IcCard>());
        _ledgerRepositoryMock.Setup(r => r.GetAllLentRecordsAsync()).ReturnsAsync(new List<Ledger>());
        _ledgerRepositoryMock.Setup(r => r.GetDetailsByLedgerIdsAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync(new Dictionary<int, List<LedgerDetail>>());
        _ledgerRepositoryMock.Setup(r => r.GetBySummaryContainingAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<Ledger>());

        _viewModel = CreateViewModel(_cardReaderMock.Object, _logger);
    }

    private readonly RecordingLogger<MainViewModel> _logger = new();

    private MainViewModel CreateViewModel(ICardReader cardReader, ILogger<MainViewModel> logger)
    {
        var staffAuthServiceMock = new Mock<IStaffAuthService>();
        var navigationServiceMock = new Mock<INavigationService>();
        var toastMock = new Mock<IToastNotificationService>();
        var operationLogger = new OperationLogger(Mock.Of<IOperationLogRepository>(), Mock.Of<ICurrentOperatorContext>());

        var summaryGenerator = new SummaryGenerator();
        var dbContext = new DbContext(":memory:");
        dbContext.InitializeDatabase();

        var lendingService = new LendingService(
            dbContext,
            _cardRepositoryMock.Object,
            _staffRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            operationLogger,
            summaryGenerator,
            new CardLockManager(NullLogger<CardLockManager>.Instance),
            Options.Create(new AppOptions()),
            NullLogger<LendingService>.Instance);

        var ledgerMergeService = new LedgerMergeService(
            _ledgerRepositoryMock.Object,
            summaryGenerator,
            operationLogger,
            dbContext,
            NullLogger<LedgerMergeService>.Instance);

        // 共有モードの接続監視は専用のタイマーファクトリで作らせ、開始されたかを観測する
        var sharedModeMonitor = new SharedModeMonitor(_databaseInfoMock.Object, _monitorTimerFactory, new SystemClock());

        return new MainViewModel(
            cardReader,
            new Mock<ISoundPlayer>().Object,
            _staffRepositoryMock.Object,
            _cardRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            lendingService,
            toastMock.Object,
            new Mock<IMessenger>().Object,
            navigationServiceMock.Object,
            Options.Create(new AppOptions { StaffCardTimeoutSeconds = 60 }),
            new TestTimerFactory(),
            new SynchronousDispatcherService(),
            _databaseInfoMock.Object,
            new Mock<ICacheService>().Object,
            sharedModeMonitor,
            new WarningService(_ledgerRepositoryMock.Object, _databaseInfoMock.Object, _cardRepositoryMock.Object, _updateNotificationMock.Object),
            new DashboardService(_cardRepositoryMock.Object, _ledgerRepositoryMock.Object,
                _staffRepositoryMock.Object, _settingsRepositoryMock.Object),
            new Mock<ISafeFileLauncher>().Object,
            new HistoryPanelViewModel(_ledgerRepositoryMock.Object, _cardRepositoryMock.Object, dbContext,
                staffAuthServiceMock.Object, ledgerMergeService, navigationServiceMock.Object, operationLogger,
                new LedgerConsistencyChecker(_ledgerRepositoryMock.Object), toastMock.Object),
            logger);
    }

    #region 起動時にカードリーダーの開始が失敗しても、残りの起動処理を始めること

    [Fact]
    public async Task InitializeAsync_カードリーダーの開始が例外になっても_共有モードの接続監視と起動時チェックと更新通知を始めること()
    {
        _databaseInfoMock.Setup(d => d.IsSharedMode).Returns(true);
        _cardReaderMock.Setup(r => r.StartReadingAsync())
            .ThrowsAsync(CardReaderException.ReadFailed("Sony NFC ポートソフトウェアが無い環境を注入"));

        Func<Task> act = () => _viewModel.InitializeAsync();

        // Issue #2377: カードリーダーの開始の失敗は起動処理で受け止める（「初期化エラー」のダイアログを出さない）
        await act.Should().NotThrowAsync();

        _monitorTimerFactory.LastCreatedTimer.Should().NotBeNull("共有モードの接続監視（15 秒ごとのヘルスチェック）が開始されていること");
        _monitorTimerFactory.LastCreatedTimer!.IsRunning.Should().BeTrue();
        _ledgerRepositoryMock.Verify(
            r => r.GetBySummaryContainingAsync(It.IsAny<string>()),
            Times.AtLeastOnce(),
            "起動時のデータチェック（バス停名未入力）が始まっていること");

        // 更新通知の確認はバックグラウンド（Task.Run）で走るので、呼ばれるのを待つ（始まらなければ時間切れで赤）
        var completed = await Task.WhenAny(_updateCheckCalled.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        completed.Should().BeSameAs(_updateCheckCalled.Task, "新バージョンの通知の確認（最新版ファイルの読み取り）が始まっていること");
    }

    [Fact]
    public async Task InitializeAsync_カードリーダーの開始が成功すれば_読み取りを開始し接続監視も始めること()
    {
        // 対: 順序を入れ替えたことで、正常時にカードリーダーの開始が抜けていないこと
        _databaseInfoMock.Setup(d => d.IsSharedMode).Returns(true);
        _cardReaderMock.Setup(r => r.StartReadingAsync()).Returns(Task.CompletedTask);

        await _viewModel.InitializeAsync();

        _cardReaderMock.Verify(r => r.StartReadingAsync(), Times.Once());
        _monitorTimerFactory.LastCreatedTimer.Should().NotBeNull();
        _monitorTimerFactory.LastCreatedTimer!.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task InitializeAsync_ローカルモードでは_カードリーダーの開始が失敗しても接続監視を始めないこと()
    {
        // 対: 例外時に監視を「無条件に」始める実装を検出する（共有モードの条件は維持する）
        _databaseInfoMock.Setup(d => d.IsSharedMode).Returns(false);
        _cardReaderMock.Setup(r => r.StartReadingAsync())
            .ThrowsAsync(CardReaderException.ReadFailed("注入"));

        Func<Task> act = () => _viewModel.InitializeAsync();

        await act.Should().NotThrowAsync();
        _monitorTimerFactory.LastCreatedTimer.Should().BeNull();
    }

    #endregion

    #region 起動時にカードリーダーを使えなくても「初期化エラー」を出さず、警告エリアだけで伝えること（Issue #2377）

    [Fact]
    public async Task InitializeAsync_felicalibを読み込めない経路を模すと_例外を出さず警告エリアに切断の行だけが出ること()
    {
        // FelicaCardReader の felicalib.dll を読み込めない経路を模す: 例外を投げる前に切断（理由つき）だけを通知し、Error は発火しない
        _cardReaderMock.Setup(r => r.StartReadingAsync())
            .Callback(() => _cardReaderMock.Raise(r => r.ConnectionStateChanged += null,
                new ConnectionStateChangedEventArgs(CardReaderConnectionState.Disconnected, "注入した切断の理由")))
            .ThrowsAsync(CardReaderException.LibraryUnavailable());

        Func<Task> act = () => _viewModel.InitializeAsync();

        await act.Should().NotThrowAsync("カードリーダーを使えなくても閲覧・帳票は使えるため、「初期化エラー」まで抜けさせない");
        _viewModel.CardReaderConnectionState.Should().Be(CardReaderConnectionState.Disconnected);
        _viewModel.WarningMessages.Where(w => w.Type == WarningType.CardReaderConnection)
            .Should().ContainSingle().Which.DisplayText.Should().Be("⚠️ カードリーダー切断: 注入した切断の理由");
        _viewModel.WarningMessages.Should().NotContain(w => w.Type == WarningType.CardReaderError,
            "起動処理は受け止めた例外から警告を足さない");
    }

    [Fact]
    public async Task InitializeAsync_想定外の失敗の経路を模すと_例外を出さず警告エリアにカードリーダーエラーの行が1行だけ出ること()
    {
        // FelicaCardReader の felicalib 以外の想定外の失敗（catch の経路）を模す: Error だけを発火する
        //（接続状態は SetConnectionState のため、初期状態の Disconnected のままなら切断は通知されない）
        var failure = CardReaderException.ReadFailed("想定外の失敗を注入");
        _cardReaderMock.Setup(r => r.StartReadingAsync())
            .Callback(() => _cardReaderMock.Raise(r => r.Error += null, _cardReaderMock.Object, (Exception)failure))
            .ThrowsAsync(failure);

        Func<Task> act = () => _viewModel.InitializeAsync();

        await act.Should().NotThrowAsync();
        _viewModel.WarningMessages.Should().ContainSingle(w => w.Type == WarningType.CardReaderError,
            "カードリーダーの通知した Error が 1 行だけ出ること（起動処理が警告を重ねて足さない）");
    }

    [Fact]
    public async Task InitializeAsync_カードリーダーの開始がCardReaderExceptionなら_起動を続けたことをWarningで記録すること()
    {
        _cardReaderMock.Setup(r => r.StartReadingAsync())
            .ThrowsAsync(CardReaderException.ReadFailed("注入"));

        await _viewModel.InitializeAsync();

        var warning = _logger.Entries.Where(e => e.Level == LogLevel.Warning).Should().ContainSingle(
            "起動時にカードリーダーを開始できなかったことが本番のログに残ること。実際: " + _logger.FormatEntries()).Subject;
        warning.Message.Should().Contain("ErrorCode=CR002").And.Contain("起動を続けます");
        warning.Exception.Should().BeNull("スタックトレースはカードリーダー（FelicaCardReader）が記録済みで、二重に出さない");
    }

    [Fact]
    public async Task InitializeAsync_カードリーダーの開始が成功すれば_Warningを記録しないこと()
    {
        // 対: 「常に記録する」実装を検出する
        _cardReaderMock.Setup(r => r.StartReadingAsync()).Returns(Task.CompletedTask);

        await _viewModel.InitializeAsync();

        _logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning, _logger.FormatEntries());
    }

    [Fact]
    public async Task InitializeAsync_カードリーダーの開始がCardReaderException以外の例外なら_従来どおり初期化エラーまで抜けること()
    {
        // 対: 受け止めるのはカードリーダーの失敗（CardReaderException）だけ。契約外の例外まで握りつぶす実装を検出する
        _cardReaderMock.Setup(r => r.StartReadingAsync())
            .ThrowsAsync(new InvalidOperationException("カードリーダーの契約外の失敗を注入"));

        Func<Task> act = () => _viewModel.InitializeAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task InitializeAsync_felicalibを読み込めないとき_本物のカードリーダーの通知で警告エリアに切断の案内だけが出て例外を出さないこと()
    {
        // 本物の FelicaCardReader（felicalib 呼び出しだけ差し替え）で、起動処理が受け止めた後も
        // 警告エリアに案内が残ることを確かめる。案内が出ない形で例外だけ握りつぶすと、利用者には何も伝わらない
        using var reader = new FelicaCardReader(
            NullLogger<FelicaCardReader>.Instance, Mock.Of<IStationMasterService>(), new DllNotFoundFelicaDevice());
        var viewModel = CreateViewModel(reader, _logger);

        Func<Task> act = () => viewModel.InitializeAsync();

        await act.Should().NotThrowAsync();
        viewModel.CardReaderConnectionState.Should().Be(CardReaderConnectionState.Disconnected);
        viewModel.WarningMessages.Where(w => w.Type == WarningType.CardReaderConnection)
            .Should().ContainSingle().Which.DisplayText.Should().Be(
                "⚠️ カードリーダー切断: " + FelicaCardReader.FelicaLibUnavailableMessage);
        viewModel.WarningMessages.Should().NotContain(w => w.Type == WarningType.CardReaderError,
            "この経路の案内は切断の行（インストールして再起動）だけ。カードリーダーエラーの行は「置き直す・抜き差しする」と" +
            "食い違う行動指示を並べるため出さない。実際: " +
            string.Join(" / ", viewModel.WarningMessages.Select(w => w.DisplayText)));
    }

    [Fact]
    public async Task ReconnectCardReader_felicalibを読み込めないとき_本物のカードリーダーで切断の理由が再接続では直らないことを伝え例外を出さないこと()
    {
        // 「再接続」を押しても同じ理由で失敗し、同じ文言の行に置き換わるだけで表示は変わらない。
        // 押した結果が分からないままにならないよう、切断の理由そのものが「再接続では直らない」ことを伝える
        using var reader = new FelicaCardReader(
            NullLogger<FelicaCardReader>.Instance, Mock.Of<IStationMasterService>(), new DllNotFoundFelicaDevice());
        var viewModel = CreateViewModel(reader, _logger);
        await viewModel.InitializeAsync();

        Func<Task> act = () => viewModel.ReconnectCardReaderAsync();

        await act.Should().NotThrowAsync("再接続の失敗は FelicaCardReader.ReconnectAsync が受け止めてログに残す");
        viewModel.CardReaderConnectionState.Should().Be(CardReaderConnectionState.Disconnected);
        viewModel.WarningMessages.Where(w => w.Type == WarningType.CardReaderConnection)
            .Should().ContainSingle().Which.DisplayText.Should()
            .Be("⚠️ カードリーダー切断: " + FelicaCardReader.FelicaLibUnavailableMessage)
            .And.Contain("「再接続」ボタンでは直りません。")
            .And.EndWith("アプリを再起動してください。", "行動指示で終える（error-messages.md）")
            .And.Contain("アプリを再起動してください");
        viewModel.WarningMessages.Should().NotContain(w => w.Type == WarningType.CardReaderError);
    }

    [Fact]
    public async Task StartReadingAsync_felicalibを読み込めないとき_読み込めなかった例外ごとErrorログに残し専用のエラーコードで投げること()
    {
        var logger = new RecordingLogger<FelicaCardReader>();
        using var reader = new FelicaCardReader(logger, Mock.Of<IStationMasterService>(), new DllNotFoundFelicaDevice());

        Func<Task> act = () => reader.StartReadingAsync();

        var thrown = (await act.Should().ThrowAsync<CardReaderException>()).Which;
        thrown.ErrorCode.Should().Be("CR010", "一時的な読み取り失敗（CR002）とは利用者の取る行動が違う");
        thrown.UserFriendlyMessage.Should().Be(FelicaCardReader.FelicaLibUnavailableMessage,
            "例外の利用者向けの文言が表示されても、切断の行と食い違わないこと");
        thrown.InnerException.Should().BeOfType<DllNotFoundException>("読み込めなかった原因を内部例外として運ぶこと");
        logger.Entries.Should().Contain(
            e => e.Level == LogLevel.Error && e.Exception is DllNotFoundException,
            "どの DLL を読み込めなかったかを IT 担当者が切り分けられるよう、例外ごと残すこと。実際: " + logger.FormatEntries());
    }

    [Fact]
    public async Task ReconnectAsync_felicalibを読み込めないとき_原因のErrorは1行だけで再接続の失敗はWarningで残すこと()
    {
        var logger = new RecordingLogger<FelicaCardReader>();
        using var reader = new FelicaCardReader(logger, Mock.Of<IStationMasterService>(), new DllNotFoundFelicaDevice());

        await reader.ReconnectAsync();

        var errors = logger.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        errors.Should().ContainSingle("原因（DllNotFoundException）の Error は IsFelicaLibLoaded の 1 行だけ。実際: " + logger.FormatEntries())
            .Which.Exception.Should().BeOfType<DllNotFoundException>();
        var warning = logger.Entries.Should().ContainSingle(
            e => e.Level == LogLevel.Warning && e.Message.Contains("再接続に失敗しました"),
            logger.FormatEntries()).Subject;
        warning.Message.Should().Contain("CR010");
        warning.Exception.Should().BeNull("原因は Error の行が例外ごと残している");
    }

    [Fact]
    public async Task InitializeAsync_想定外の失敗のとき_本物のカードリーダーのErrorで警告エリアにカードリーダーエラーの行が出て例外を出さないこと()
    {
        // felicalib は読み込め PaSoRi も接続済み。読み取りの開始の途中（開始のログ）で想定外の例外を起こす
        using var reader = new FelicaCardReader(
            new ThrowOnInformationLogger(), Mock.Of<IStationMasterService>(), new FakeFelicaDevice());
        var viewModel = CreateViewModel(reader, _logger);

        Func<Task> act = () => viewModel.InitializeAsync();

        await act.Should().NotThrowAsync();
        viewModel.WarningMessages.Should().ContainSingle(w => w.Type == WarningType.CardReaderError,
            "想定外の失敗は Error（カードリーダーエラーの行）で伝える。実際: " +
            string.Join(" / ", viewModel.WarningMessages.Select(w => w.DisplayText)));
    }

    /// <summary>Information のログで例外を投げる（読み取りの開始の途中で想定外の失敗を起こすため）。</summary>
    private sealed class ThrowOnInformationLogger : ILogger<FelicaCardReader>
    {
        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
            => throw new NotSupportedException();

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Information)
            {
                throw new InvalidOperationException("想定外の失敗を注入");
            }
        }
    }

    /// <summary>felicalib.dll を読み込めない環境（P/Invoke が <see cref="DllNotFoundException"/>）を再現する。</summary>
    private sealed class DllNotFoundFelicaDevice : IFelicaDevice
    {
        public byte[]? GetIDm(int systemCode) => throw new DllNotFoundException("felicalib.dll");

        public byte[]? ReadWithoutEncryption(int systemCode, int serviceCode, int address)
            => throw new DllNotFoundException("felicalib.dll");

        public IEnumerable<byte[]?> ReadBlocksWithoutEncryption(int systemCode, int serviceCode, int addressStart, int addressCount)
            => throw new DllNotFoundException("felicalib.dll");
    }

    #endregion

    #region 接続状態の警告

    [Fact]
    public void ConnectionStateChanged_切断で警告を出し_接続中で取り除くこと()
    {
        _cardReaderMock.Raise(r => r.ConnectionStateChanged += null,
            new ConnectionStateChangedEventArgs(CardReaderConnectionState.Disconnected, "PaSoRi が見つかりません"));

        _viewModel.CardReaderConnectionState.Should().Be(CardReaderConnectionState.Disconnected);
        _viewModel.WarningMessages.Where(w => w.Type == WarningType.CardReaderConnection)
            .Should().ContainSingle().Which.DisplayText.Should().Be("⚠️ カードリーダー切断: PaSoRi が見つかりません");

        _cardReaderMock.Raise(r => r.ConnectionStateChanged += null,
            new ConnectionStateChangedEventArgs(CardReaderConnectionState.Connected, "接続が回復しました"));

        _viewModel.CardReaderConnectionState.Should().Be(CardReaderConnectionState.Connected);
        _viewModel.WarningMessages.Should().NotContain(w => w.Type == WarningType.CardReaderConnection);
    }

    [Fact]
    public void ConnectionStateChanged_接続中は_メッセージに再接続を含んでも警告エリアに何も出さないこと()
    {
        // 旧実装はメッセージの文言（「再接続」を含むか）で成功表示を出し分けていたが、
        // その文言を通知する実装は無かった。接続中は文言によらず警告エリアに何も残さない。
        _cardReaderMock.Raise(r => r.ConnectionStateChanged += null,
            new ConnectionStateChangedEventArgs(CardReaderConnectionState.Connected, "再接続しました"));

        _viewModel.WarningMessages.Should().NotContain(w => w.Type == WarningType.CardReaderConnection);
    }

    #endregion
}
