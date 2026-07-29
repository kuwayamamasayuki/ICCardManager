using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// ConnectionDiagnosticsService の単体テスト（Issue #1690）
/// </summary>
/// <remarks>
/// 本サービスは障害時の一次切り分けを庶務担当が自力で行うための機能であり、
/// 「判定を誤らないこと」と「文言から次の行動が分かること」が品質の中心になる。
/// そのため各項目の全分岐と、警告・異常文言の 3 要素準拠を固定する。
/// OS へ問い合わせる部分（書込可否・空き容量）は protected 継ぎ目を差し替えて
/// 権限不足やディスク満杯を決定的に再現する。
/// </remarks>
public class ConnectionDiagnosticsServiceTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly Mock<IDatabaseInfo> _databaseInfo = new();
    private readonly Mock<ICardReader> _cardReader = new();
    private readonly Mock<BackupService> _backupService;
    private readonly Mock<IBackupHealthService> _backupHealth = new();
    private readonly Mock<ISharedDbConnectionStateProvider> _connectionState = new();
    private readonly Mock<ISystemClock> _clock = new();

    private static readonly DateTime Now = new(2026, 7, 28, 14, 30, 15);

    public ConnectionDiagnosticsServiceTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();

        _backupService = new Mock<BackupService>(
            _dbContext,
            new Mock<ISettingsRepository>().Object,
            new Mock<ILogger<BackupService>>().Object);

        // 既定はすべて正常。各テストが必要な 1 点だけを崩す
        _databaseInfo.Setup(d => d.CheckConnection()).Returns(true);
        _databaseInfo.Setup(d => d.CheckWritable()).Returns(true);
        _databaseInfo.SetupGet(d => d.DatabasePath).Returns(@"C:\ProgramData\ICCardManager\iccard.db");
        _databaseInfo.SetupGet(d => d.IsSharedMode).Returns(false);
        _databaseInfo.SetupGet(d => d.IsJournalModeDegraded).Returns(false);
        _databaseInfo.SetupGet(d => d.CurrentJournalMode).Returns("delete");

        _cardReader.SetupGet(r => r.ConnectionState).Returns(CardReaderConnectionState.Connected);
        _connectionState.SetupGet(p => p.CurrentConnectionState).Returns(SharedDbConnectionState.Connected);
        _backupService.Setup(s => s.ResolveBackupFolderAsync()).ReturnsAsync(Path.GetTempPath());
        _backupHealth.Setup(s => s.GetHealthAsync()).ReturnsAsync(new BackupHealthInfo
        {
            LastSuccessAt = Now.AddDays(-1)
        });
        _clock.SetupGet(c => c.Now).Returns(Now);
    }

    public void Dispose() => _dbContext?.Dispose();

    /// <summary>
    /// OS 依存の実測部分を差し替えられるテスト用サブクラス
    /// </summary>
    private sealed class TestableService : ConnectionDiagnosticsService
    {
        public FolderWriteAccess FolderAccess { get; set; } = FolderWriteAccess.Writable;
        public long? FreeSpace { get; set; } = 50L * 1024 * 1024 * 1024;
        public bool FileReachable { get; set; } = true;

        public TestableService(
            IDatabaseInfo databaseInfo,
            ICardReader cardReader,
            BackupService backupService,
            IBackupHealthService backupHealthService,
            ISharedDbConnectionStateProvider connectionStateProvider,
            ISystemClock clock)
            : base(databaseInfo, cardReader, backupService, backupHealthService,
                   connectionStateProvider, clock,
                   NullLogger<ConnectionDiagnosticsService>.Instance)
        {
        }

        protected override FolderWriteAccess ProbeFolderWriteAccess(string folder) => FolderAccess;

        protected override long? GetFreeSpaceBytes(string folder) => FreeSpace;

        protected override bool ProbeDatabaseFileReachable(string databasePath) => FileReachable;
    }

    private TestableService CreateService() => new(
        _databaseInfo.Object,
        _cardReader.Object,
        _backupService.Object,
        _backupHealth.Object,
        _connectionState.Object,
        _clock.Object);

    private async Task<DiagnosticItem> RunAndGet(DiagnosticItemKind kind, TestableService service = null)
    {
        var report = await (service ?? CreateService()).RunDiagnosticsAsync();
        return report.Items.Single(i => i.Kind == kind);
    }

    #region レポート全体

    [Fact]
    public async Task RunDiagnosticsAsync_ReturnsEveryDiagnosticItemKindExactlyOnce()
    {
        var report = await CreateService().RunDiagnosticsAsync();

        var expected = Enum.GetValues(typeof(DiagnosticItemKind)).Cast<DiagnosticItemKind>();
        report.Items.Select(i => i.Kind).Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task RunDiagnosticsAsync_OrdersItemsByKindDeclarationOrder()
    {
        // 一覧とコピー結果の並びが「DB→周辺機器→保存先」の切り分け順になっていること
        var report = await CreateService().RunDiagnosticsAsync();

        report.Items.Select(i => (int)i.Kind).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task RunDiagnosticsAsync_FillsEnvironmentInformation()
    {
        var report = await CreateService().RunDiagnosticsAsync();

        report.DiagnosedAt.Should().Be(Now);
        report.AppVersion.Should().Be(AppVersionInfo.CurrentString);
        report.MachineName.Should().Be(Environment.MachineName);
        report.OsDescription.Should().NotBeNullOrWhiteSpace();
        report.DatabasePath.Should().Be(@"C:\ProgramData\ICCardManager\iccard.db");
        report.IsSharedMode.Should().BeFalse();
    }

    [Fact]
    public async Task RunDiagnosticsAsync_WithAllHealthy_ReportsOkOverall()
    {
        var report = await CreateService().RunDiagnosticsAsync();

        report.OverallStatus.Should().Be(DiagnosticStatus.Ok);
        report.ProblemCount.Should().Be(0);
    }

    [Fact]
    public async Task RunDiagnosticsAsync_EveryItemHasTitleSummaryAndDetail()
    {
        var report = await CreateService().RunDiagnosticsAsync();

        report.Items.Should().OnlyContain(i =>
            !string.IsNullOrWhiteSpace(i.Title) &&
            !string.IsNullOrWhiteSpace(i.SummaryText) &&
            !string.IsNullOrWhiteSpace(i.DetailText));
    }

    #endregion

    #region 項目 1・2: データベース

    [Fact]
    public async Task DatabaseReachability_WhenConnectable_IsOk()
    {
        var item = await RunAndGet(DiagnosticItemKind.DatabaseReachability);

        item.Status.Should().Be(DiagnosticStatus.Ok);
    }

    [Fact]
    public async Task DatabaseReachability_WhenNotConnectable_IsErrorAndNamesThePath()
    {
        _databaseInfo.Setup(d => d.CheckConnection()).Returns(false);

        var item = await RunAndGet(DiagnosticItemKind.DatabaseReachability);

        item.Status.Should().Be(DiagnosticStatus.Error);
        item.DetailText.Should().Contain(@"C:\ProgramData\ICCardManager\iccard.db");
    }

    [Fact]
    public async Task DatabaseReachability_InSharedMode_MentionsNetworkAsCause()
    {
        // 共有モードとローカルモードでは原因も対処も異なるため文言を分けている
        _databaseInfo.SetupGet(d => d.IsSharedMode).Returns(true);
        _databaseInfo.Setup(d => d.CheckConnection()).Returns(false);

        var item = await RunAndGet(DiagnosticItemKind.DatabaseReachability);

        item.DetailText.Should().Contain("ネットワーク");
    }

    [Fact]
    public async Task DatabaseReachability_InLocalMode_DoesNotBlameNetwork()
    {
        _databaseInfo.Setup(d => d.CheckConnection()).Returns(false);

        var item = await RunAndGet(DiagnosticItemKind.DatabaseReachability);

        item.DetailText.Should().NotContain("ネットワーク接続と共有フォルダ");
        item.DetailText.Should().Contain("ディスク");
    }

    [Fact]
    public async Task DatabaseReachability_WhenFileUnreachableButQuerySucceedsFromCache_IsError()
    {
        // 実機で確認した事象（PR #1714 レビュー）。DbContext は接続を開きっぱなしで保持するため、
        // sqlite_master の読み取りは SQLite のページキャッシュから返り、ネットワーク切断後も
        // CheckConnection() が true を返し続ける。書き込み（BEGIN IMMEDIATE）だけが
        // 実際にファイルへ到達する必要があるため失敗し、
        // 「到達性 正常 ／ 書込権限 異常」という誤った組み合わせが表示されていた。
        _databaseInfo.Setup(d => d.CheckConnection()).Returns(true);
        var service = CreateService();
        service.FileReachable = false;

        var item = await RunAndGet(DiagnosticItemKind.DatabaseReachability, service);

        item.Status.Should().Be(DiagnosticStatus.Error);
    }

    [Fact]
    public async Task DatabaseWritable_WhenFileUnreachable_IsNotApplicableInsteadOfPermissionError()
    {
        // 到達できないのに「書込権限がありません」と報告すると、
        // 原因がアクセス権にあると誤解させ、権限設定の確認へ誘導してしまう。
        _databaseInfo.Setup(d => d.CheckConnection()).Returns(true);
        _databaseInfo.Setup(d => d.CheckWritable()).Returns(false);
        var service = CreateService();
        service.FileReachable = false;

        var item = await RunAndGet(DiagnosticItemKind.DatabaseWritable, service);

        item.Status.Should().Be(DiagnosticStatus.NotApplicable);
    }

    [Fact]
    public async Task DatabaseWritable_WhenWritable_IsOk()
    {
        var item = await RunAndGet(DiagnosticItemKind.DatabaseWritable);

        item.Status.Should().Be(DiagnosticStatus.Ok);
    }

    [Fact]
    public async Task DatabaseWritable_WhenNotWritable_IsError()
    {
        _databaseInfo.Setup(d => d.CheckWritable()).Returns(false);

        var item = await RunAndGet(DiagnosticItemKind.DatabaseWritable);

        item.Status.Should().Be(DiagnosticStatus.Error);
        item.DetailText.Should().Contain("変更");
    }

    [Fact]
    public async Task DatabaseWritable_InSharedMode_ListsNetworkAsAPossibleCause()
    {
        // 実機検証では相手PCの電源断が原因だったが、文言はアクセス権と復元処理しか挙げておらず、
        // 「共有フォルダのあるパソコンが落ちている」を疑えなかった（PR #1714 レビュー）。
        _databaseInfo.SetupGet(d => d.IsSharedMode).Returns(true);
        _databaseInfo.Setup(d => d.CheckWritable()).Returns(false);

        var item = await RunAndGet(DiagnosticItemKind.DatabaseWritable);

        item.DetailText.Should().Contain("ネットワーク");
        item.DetailText.Should().Contain("起動している", "相手PCの電源断を疑えるようにする");
    }

    [Fact]
    public async Task DatabaseWritable_InLocalMode_DoesNotBlameOtherPcs()
    {
        // ローカルモードでは他のパソコンは存在しないため、原因候補に挙げるのは誤誘導
        _databaseInfo.Setup(d => d.CheckWritable()).Returns(false);

        var item = await RunAndGet(DiagnosticItemKind.DatabaseWritable);

        item.DetailText.Should().NotContain("他のパソコン");
        item.DetailText.Should().NotContain("ネットワーク");
    }

    [Fact]
    public async Task DatabaseWritable_WhenUnreachable_IsNotApplicableAndPointsToReachability()
    {
        // 到達できない DB に書込確認まで走らせると、同じ原因で異常が 2 件出て
        // 「原因が 2 つある」と誤解させる
        _databaseInfo.Setup(d => d.CheckConnection()).Returns(false);

        var item = await RunAndGet(DiagnosticItemKind.DatabaseWritable);

        item.Status.Should().Be(DiagnosticStatus.NotApplicable);
        item.DetailText.Should().Contain("データベース到達性");
    }

    [Fact]
    public async Task DatabaseWritable_WhenUnreachable_DoesNotCallCheckWritable()
    {
        _databaseInfo.Setup(d => d.CheckConnection()).Returns(false);

        await CreateService().RunDiagnosticsAsync();

        _databaseInfo.Verify(d => d.CheckWritable(), Times.Never);
    }

    #endregion

    #region 項目 3: ジャーナルモード

    [Fact]
    public async Task JournalMode_WhenNotDegraded_IsOkAndShowsMode()
    {
        var item = await RunAndGet(DiagnosticItemKind.JournalMode);

        item.Status.Should().Be(DiagnosticStatus.Ok);
        item.SummaryText.Should().Contain("delete");
    }

    [Fact]
    public async Task JournalMode_WhenDegraded_IsWarningNotError()
    {
        // クラッシュ耐性の低下は「今すぐ使えない」わけではないため異常にしない
        _databaseInfo.SetupGet(d => d.IsJournalModeDegraded).Returns(true);
        _databaseInfo.SetupGet(d => d.CurrentJournalMode).Returns("wal");

        var item = await RunAndGet(DiagnosticItemKind.JournalMode);

        item.Status.Should().Be(DiagnosticStatus.Warning);
        item.DetailText.Should().Contain("wal");
    }

    [Fact]
    public async Task JournalMode_WithUnknownMode_ShowsUnknownInsteadOfBlank()
    {
        _databaseInfo.SetupGet(d => d.CurrentJournalMode).Returns((string)null);

        var item = await RunAndGet(DiagnosticItemKind.JournalMode);

        item.SummaryText.Should().Contain("不明");
    }

    #endregion

    #region 項目 4: 共有フォルダ接続状態

    [Fact]
    public async Task SharedFolderConnection_InLocalMode_IsNotApplicable()
    {
        var item = await RunAndGet(DiagnosticItemKind.SharedFolderConnection);

        item.Status.Should().Be(DiagnosticStatus.NotApplicable);
        item.SummaryText.Should().Contain("ローカルモード");
    }

    [Theory]
    [InlineData(SharedDbConnectionState.Connected, DiagnosticStatus.Ok)]
    [InlineData(SharedDbConnectionState.Reconnecting, DiagnosticStatus.Warning)]
    [InlineData(SharedDbConnectionState.Disconnected, DiagnosticStatus.Error)]
    public async Task SharedFolderConnection_InSharedMode_MapsStateToStatus(
        SharedDbConnectionState state, DiagnosticStatus expected)
    {
        _databaseInfo.SetupGet(d => d.IsSharedMode).Returns(true);
        _connectionState.SetupGet(p => p.CurrentConnectionState).Returns(state);

        var item = await RunAndGet(DiagnosticItemKind.SharedFolderConnection);

        item.Status.Should().Be(expected);
    }

    [Fact]
    public async Task SharedFolderConnection_InLocalMode_DoesNotConsultTheMonitor()
    {
        // ローカルモードではヘルスチェック自体が動かず、値は意味を持たない
        await CreateService().RunDiagnosticsAsync();

        _connectionState.VerifyGet(p => p.CurrentConnectionState, Times.Never);
    }

    [Fact]
    public async Task SharedFolderConnection_WhenMonitorSaysConnectedButDatabaseUnreachable_IsWarningNotOk()
    {
        // 監視は15秒周期のため、ネットワークを抜いた直後は Connected のまま残る。
        // これをそのまま「正常」と表示すると、同じ診断結果の中で
        // 「項目1: 到達できません」と「項目4: 正常」が併存する自己矛盾になる。
        _databaseInfo.SetupGet(d => d.IsSharedMode).Returns(true);
        _databaseInfo.Setup(d => d.CheckConnection()).Returns(false);
        _connectionState.SetupGet(p => p.CurrentConnectionState).Returns(SharedDbConnectionState.Connected);

        var item = await RunAndGet(DiagnosticItemKind.SharedFolderConnection);

        item.Status.Should().Be(DiagnosticStatus.Warning);
        item.DetailText.Should().Contain("15秒", "監視間隔ゆえに未反映であることを利用者へ伝える");
    }

    [Fact]
    public async Task SharedFolderConnection_WhenMonitorSaysDisconnected_StaysErrorEvenIfDatabaseReachable()
    {
        // 断続的な切断は「今は到達できるが直近で切れていた」形で現れる。
        // 即時確認が通ったからといって監視の検知結果を握りつぶさない。
        _databaseInfo.SetupGet(d => d.IsSharedMode).Returns(true);
        _databaseInfo.Setup(d => d.CheckConnection()).Returns(true);
        _connectionState.SetupGet(p => p.CurrentConnectionState).Returns(SharedDbConnectionState.Disconnected);

        var item = await RunAndGet(DiagnosticItemKind.SharedFolderConnection);

        item.Status.Should().Be(DiagnosticStatus.Error);
    }

    [Fact]
    public async Task SharedFolderConnection_WhenConnectedAndReachable_DetailDisclosesMonitoringInterval()
    {
        // 「正常」が意味するのは「最大15秒前の確認が成功していた」であり、
        // 「今この瞬間つながっている」ではない。その限界を文言で明示する。
        _databaseInfo.SetupGet(d => d.IsSharedMode).Returns(true);
        _connectionState.SetupGet(p => p.CurrentConnectionState).Returns(SharedDbConnectionState.Connected);

        var item = await RunAndGet(DiagnosticItemKind.SharedFolderConnection);

        item.Status.Should().Be(DiagnosticStatus.Ok);
        item.DetailText.Should().Contain("15秒");
    }

    [Fact]
    public async Task SharedFolderConnection_WhenFileUnreachableButMonitorSaysConnected_IsNotOk()
    {
        // 監視（SharedModeMonitor）も同じ CheckConnection() を使うため、切断後も Connected のまま残る。
        // 項目1がファイル実測で異常になることで、項目4との突き合わせが初めて正しく働く。
        _databaseInfo.SetupGet(d => d.IsSharedMode).Returns(true);
        _databaseInfo.Setup(d => d.CheckConnection()).Returns(true);
        _connectionState.SetupGet(p => p.CurrentConnectionState).Returns(SharedDbConnectionState.Connected);
        var service = CreateService();
        service.FileReachable = false;

        var item = await RunAndGet(DiagnosticItemKind.SharedFolderConnection, service);

        item.Status.Should().NotBe(DiagnosticStatus.Ok);
    }

    [Fact]
    public async Task Report_NeverClaimsSharedFolderIsHealthyWhileDatabaseIsUnreachable()
    {
        // 診断結果全体としての整合性。到達不可のときに「正常」の項目が
        // 共有フォルダ接続状態として残っていてはならない。
        _databaseInfo.SetupGet(d => d.IsSharedMode).Returns(true);
        _databaseInfo.Setup(d => d.CheckConnection()).Returns(false);
        _connectionState.SetupGet(p => p.CurrentConnectionState).Returns(SharedDbConnectionState.Connected);

        var report = await CreateService().RunDiagnosticsAsync();

        report.Items.Single(i => i.Kind == DiagnosticItemKind.SharedFolderConnection)
            .Status.Should().NotBe(DiagnosticStatus.Ok);
    }

    #endregion

    #region 項目 5: ICカードリーダー

    [Theory]
    [InlineData(CardReaderConnectionState.Connected, DiagnosticStatus.Ok)]
    [InlineData(CardReaderConnectionState.Reconnecting, DiagnosticStatus.Warning)]
    [InlineData(CardReaderConnectionState.Disconnected, DiagnosticStatus.Error)]
    public async Task CardReader_MapsConnectionStateToStatus(
        CardReaderConnectionState state, DiagnosticStatus expected)
    {
        _cardReader.SetupGet(r => r.ConnectionState).Returns(state);

        var item = await RunAndGet(DiagnosticItemKind.CardReader);

        item.Status.Should().Be(expected);
    }

    [Fact]
    public async Task CardReader_WhenDisconnected_SuggestsPhysicalChecks()
    {
        _cardReader.SetupGet(r => r.ConnectionState).Returns(CardReaderConnectionState.Disconnected);

        var item = await RunAndGet(DiagnosticItemKind.CardReader);

        item.DetailText.Should().Contain("USB");
    }

    #endregion

    #region 項目 6: バックアップ保存先

    [Fact]
    public async Task BackupFolderWritable_WhenWritable_IsOk()
    {
        var item = await RunAndGet(DiagnosticItemKind.BackupFolderWritable);

        item.Status.Should().Be(DiagnosticStatus.Ok);
    }

    [Theory]
    [InlineData(FolderWriteAccess.PathNotSpecified)]
    [InlineData(FolderWriteAccess.FolderNotFound)]
    [InlineData(FolderWriteAccess.AccessDenied)]
    [InlineData(FolderWriteAccess.Failed)]
    public async Task BackupFolderWritable_WhenNotWritable_IsErrorWithDistinctMessage(FolderWriteAccess access)
    {
        var service = CreateService();
        service.FolderAccess = access;

        var item = await RunAndGet(DiagnosticItemKind.BackupFolderWritable, service);

        item.Status.Should().Be(DiagnosticStatus.Error);
        item.DetailText.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task BackupFolderWritable_ForEachFailure_ProducesDifferentGuidance()
    {
        // 原因ごとに対処が異なる（設定し直す／フォルダを作る／権限を依頼する）ため、
        // 同じ文言を使い回していないことを固定する
        var details = new List<string>();
        foreach (var access in new[]
                 {
                     FolderWriteAccess.PathNotSpecified,
                     FolderWriteAccess.FolderNotFound,
                     FolderWriteAccess.AccessDenied,
                     FolderWriteAccess.Failed
                 })
        {
            var service = CreateService();
            service.FolderAccess = access;
            details.Add((await RunAndGet(DiagnosticItemKind.BackupFolderWritable, service)).DetailText);
        }

        details.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task BackupFolderWritable_WhenFolderResolutionThrows_StillReportsWithoutThrowing()
    {
        _backupService.Setup(s => s.ResolveBackupFolderAsync())
            .ThrowsAsync(new InvalidOperationException("設定の読み取りに失敗"));
        var service = CreateService();
        service.FolderAccess = FolderWriteAccess.PathNotSpecified;

        var item = await RunAndGet(DiagnosticItemKind.BackupFolderWritable, service);

        item.Status.Should().Be(DiagnosticStatus.Error);
        item.DetailText.Should().Contain("F5");
    }

    #endregion

    #region 項目 7: 空きディスク容量

    [Fact]
    public async Task DiskFreeSpace_WithAmpleSpace_IsOk()
    {
        var item = await RunAndGet(DiagnosticItemKind.DiskFreeSpace);

        item.Status.Should().Be(DiagnosticStatus.Ok);
    }

    [Fact]
    public async Task DiskFreeSpace_BelowThreshold_IsWarningNotError()
    {
        // 空き容量不足はバックアップ失敗の予兆であり、本体動作を即座に妨げるものではない
        var service = CreateService();
        service.FreeSpace = AppConstants.DiagnosticsLowDiskSpaceWarningBytes - 1;

        var item = await RunAndGet(DiagnosticItemKind.DiskFreeSpace, service);

        item.Status.Should().Be(DiagnosticStatus.Warning);
        item.DetailText.Should().Contain(AppConstants.MaxBackupGenerations.ToString());
    }

    [Fact]
    public async Task DiskFreeSpace_ExactlyAtThreshold_IsOk()
    {
        // しきい値ちょうどは「下回っていない」ため正常
        var service = CreateService();
        service.FreeSpace = AppConstants.DiagnosticsLowDiskSpaceWarningBytes;

        var item = await RunAndGet(DiagnosticItemKind.DiskFreeSpace, service);

        item.Status.Should().Be(DiagnosticStatus.Ok);
    }

    [Fact]
    public async Task DiskFreeSpace_WhenUnknown_IsWarningAndPointsToFolderDiagnosis()
    {
        var service = CreateService();
        service.FreeSpace = null;

        var item = await RunAndGet(DiagnosticItemKind.DiskFreeSpace, service);

        item.Status.Should().Be(DiagnosticStatus.Warning);
        item.SummaryText.Should().Be("不明");
        item.DetailText.Should().Contain("バックアップ保存先");
    }

    #endregion

    #region 項目 8: バックアップ実施状況

    [Fact]
    public async Task BackupHealth_WithRecentSuccess_IsOk()
    {
        var item = await RunAndGet(DiagnosticItemKind.BackupHealth);

        item.Status.Should().Be(DiagnosticStatus.Ok);
    }

    [Fact]
    public async Task BackupHealth_WithNoRecord_IsNotApplicableNotWarning()
    {
        // 導入前からの既存環境で必ず警告が出る「オオカミ少年」化の防止（Issue #1689 の方針）
        _backupHealth.Setup(s => s.GetHealthAsync()).ReturnsAsync(new BackupHealthInfo());

        var item = await RunAndGet(DiagnosticItemKind.BackupHealth);

        item.Status.Should().Be(DiagnosticStatus.NotApplicable);
    }

    [Fact]
    public async Task BackupHealth_ExactlyAtWarningDays_IsStillOk()
    {
        // 「日数を超えて経過」で判定する（#1689 と同じ境界）
        _backupHealth.Setup(s => s.GetHealthAsync()).ReturnsAsync(new BackupHealthInfo
        {
            LastSuccessAt = Now.AddDays(-AppConstants.BackupStaleWarningDays)
        });

        var item = await RunAndGet(DiagnosticItemKind.BackupHealth);

        item.Status.Should().Be(DiagnosticStatus.Ok);
    }

    [Fact]
    public async Task BackupHealth_BeyondWarningDays_IsWarningWithElapsedDaysAndF6Guidance()
    {
        _backupHealth.Setup(s => s.GetHealthAsync()).ReturnsAsync(new BackupHealthInfo
        {
            LastSuccessAt = Now.AddDays(-(AppConstants.BackupStaleWarningDays + 1))
        });

        var item = await RunAndGet(DiagnosticItemKind.BackupHealth);

        item.Status.Should().Be(DiagnosticStatus.Warning);
        item.DetailText.Should().Contain((AppConstants.BackupStaleWarningDays + 1) + "日");
        item.DetailText.Should().Contain("F6");
    }

    [Fact]
    public async Task BackupHealth_WhenServiceThrows_IsErrorAndOtherItemsSurvive()
    {
        _backupHealth.Setup(s => s.GetHealthAsync())
            .ThrowsAsync(new IOException("設定テーブルを読めません"));

        var report = await CreateService().RunDiagnosticsAsync();

        report.Items.Single(i => i.Kind == DiagnosticItemKind.BackupHealth)
            .Status.Should().Be(DiagnosticStatus.Error);
        report.Items.Single(i => i.Kind == DiagnosticItemKind.DatabaseReachability)
            .Status.Should().Be(DiagnosticStatus.Ok);
    }

    #endregion

    #region 例外耐性

    [Fact]
    public async Task RunDiagnosticsAsync_WhenOneItemThrows_KeepsThatItemsKindAndTitle()
    {
        // どの項目が確認できなかったのかを利用者が特定できることが要件
        _cardReader.SetupGet(r => r.ConnectionState).Throws(new InvalidOperationException("リーダー異常"));

        var item = await RunAndGet(DiagnosticItemKind.CardReader);

        item.Status.Should().Be(DiagnosticStatus.Error);
        item.Title.Should().Be("ICカードリーダー");
        item.DetailText.Should().Contain("ICカードリーダーの確認");
    }

    [Fact]
    public async Task RunDiagnosticsAsync_WhenOneItemThrows_StillReturnsAllItems()
    {
        _databaseInfo.SetupGet(d => d.IsJournalModeDegraded).Throws(new InvalidOperationException());

        var report = await CreateService().RunDiagnosticsAsync();

        report.Items.Should().HaveCount(Enum.GetValues(typeof(DiagnosticItemKind)).Length);
    }

    [Fact]
    public async Task RunDiagnosticsAsync_WhenDatabasePathThrows_ReportsUnknownPathInsteadOfFailing()
    {
        _databaseInfo.SetupGet(d => d.DatabasePath).Throws(new InvalidOperationException());

        var report = await CreateService().RunDiagnosticsAsync();

        report.DatabasePath.Should().Be("不明");
    }

    [Fact]
    public async Task RunDiagnosticsAsync_DoesNotLeakRawExceptionMessageToUser()
    {
        // 生の ex.Message は英語・技術用語を含みうるため UI に出さない（Issue #1614）
        _cardReader.SetupGet(r => r.ConnectionState)
            .Throws(new InvalidOperationException("SQLITE_BUSY: database is locked"));

        var item = await RunAndGet(DiagnosticItemKind.CardReader);

        item.DetailText.Should().NotContain("SQLITE_BUSY");
    }

    #endregion

    #region エラーメッセージ品質（.claude/rules/error-messages.md）

    /// <summary>
    /// 警告・異常の文言が「何が／なぜ／どうすれば」の 3 要素を満たすことを検証する
    /// </summary>
    /// <remarks>
    /// データの状態を警告する文言もガイドラインの対象（Issue #1688 で確立）。
    /// 全項目を一度に問題状態へ落として一括検証することで、
    /// 項目が追加されたときに品質テストの追随漏れが起きないようにしている。
    /// </remarks>
    [Fact]
    public async Task AllProblemItems_SatisfyErrorMessageQualityCriteria()
    {
        var problems = await CollectAllProblemItemsAsync();

        // 全 8 項目それぞれの問題状態を集めていること（網羅の担保）
        problems.Select(i => i.Kind).Distinct()
            .Should().HaveCount(Enum.GetValues(typeof(DiagnosticItemKind)).Length);

        foreach (var item in problems)
        {
            item.DetailText.Length.Should().BeGreaterOrEqualTo(20,
                $"{item.Kind} の詳細文言が短すぎると情報不足になる");

            Regex.IsMatch(item.DetailText, "してください。$|ください。$")
                .Should().BeTrue($"{item.Kind} の詳細文言は行動指示で終わること: {item.DetailText}");
        }
    }

    [Fact]
    public async Task AllProblemItems_AvoidVagueWording()
    {
        var problems = await CollectAllProblemItemsAsync();

        foreach (var item in problems)
        {
            item.DetailText.Should().NotContain("エラーが発生しました。",
                $"{item.Kind}: 曖昧な文言は禁止（.claude/rules/error-messages.md）");
            item.DetailText.Should().NotContain("不正な値です");
        }
    }

    /// <summary>
    /// 全診断項目を「対処が必要な状態」に落として結果を集める
    /// </summary>
    private async Task<List<DiagnosticItem>> CollectAllProblemItemsAsync()
    {
        var collected = new List<DiagnosticItem>();

        // 到達性 NG は書込権限を NotApplicable にしてしまうため、2 回に分けて収集する。
        // (1) 到達性のみ NG
        _databaseInfo.Setup(d => d.CheckConnection()).Returns(false);
        var firstRun = await CreateService().RunDiagnosticsAsync();
        collected.Add(firstRun.Items.Single(i => i.Kind == DiagnosticItemKind.DatabaseReachability));

        // (2) 到達性は OK・それ以外をすべて問題状態へ
        _databaseInfo.Setup(d => d.CheckConnection()).Returns(true);
        _databaseInfo.Setup(d => d.CheckWritable()).Returns(false);
        _databaseInfo.SetupGet(d => d.IsSharedMode).Returns(true);
        _databaseInfo.SetupGet(d => d.IsJournalModeDegraded).Returns(true);
        _databaseInfo.SetupGet(d => d.CurrentJournalMode).Returns("wal");
        _connectionState.SetupGet(p => p.CurrentConnectionState).Returns(SharedDbConnectionState.Disconnected);
        _cardReader.SetupGet(r => r.ConnectionState).Returns(CardReaderConnectionState.Disconnected);
        _backupHealth.Setup(s => s.GetHealthAsync()).ReturnsAsync(new BackupHealthInfo
        {
            LastSuccessAt = Now.AddDays(-(AppConstants.BackupStaleWarningDays + 30))
        });

        var service = CreateService();
        service.FolderAccess = FolderWriteAccess.AccessDenied;
        service.FreeSpace = 100L * 1024 * 1024;

        var secondRun = await service.RunDiagnosticsAsync();
        collected.AddRange(secondRun.Items.Where(i => i.IsProblem));

        // (3) 項目4の「監視は Connected だが即時確認は失敗」分岐。
        // (2) は Disconnected で発生させるため、この警告文言だけ検証から漏れる。
        _databaseInfo.Setup(d => d.CheckConnection()).Returns(false);
        _connectionState.SetupGet(p => p.CurrentConnectionState).Returns(SharedDbConnectionState.Connected);
        var thirdRun = await CreateService().RunDiagnosticsAsync();
        collected.Add(thirdRun.Items.Single(i => i.Kind == DiagnosticItemKind.SharedFolderConnection));

        return collected;
    }

    #endregion
}
