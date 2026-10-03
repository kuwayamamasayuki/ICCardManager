using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure;
using ICCardManager.Tests.Infrastructure.Timing;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #2221: スレッドプールが同期的な待ちで塞がっていても、帳票作成画面の出力状況の判定が始まること。
/// </summary>
/// <remarks>
/// <para>
/// 判定は以前 <c>Task.Run</c>（スレッドプール）で起動していた。全件テストの実行中に同期的に待つテストがプールを
/// 塞ぐと、判定は空きが出るまで始まらず、<c>ReportViewModelTests</c> の
/// 「古い更新の失敗で新しい結果を消さないこと」が判定の開始を 10 秒待って間欠的に失敗していた。
/// 計測では、プールを塞ぐと判定は 30 秒たっても始まらなかった。
/// 本番でも、判定は出力先フォルダ（共有フォルダーのこともある）の同期的な走査で、応答しない共有では
/// SMB のタイムアウトまでプールのスレッドを塞ぐ。判定は専用スレッド（LongRunning）で走らせる。
/// </para>
/// <para>
/// プールを塞ぐため、他のテストと並列に走らせない（<see cref="ThreadPoolSaturationCollection"/>）。
/// 判定の完了（<c>await</c> の続き）はプールに依存し得るので、塞いでいる間に確かめるのは「判定が始まったこと」だけにし、
/// 結果の反映はプールを解放してから確かめる。
/// </para>
/// </remarks>
[Collection(ThreadPoolSaturationCollection.Name)]
public sealed class ReportViewModelExportStatusThreadPoolTests
{
    /// <summary>
    /// 判定が始まるまでの待ちの上限。プールを塞いだ状態で Task.Run の判定が始まらないことを確かめられる長さ
    /// （予備の塞ぎ役 <see cref="ReserveBlockers"/> 本は、この間に足されるスレッド約 10 本を十分に上回る）。
    /// </summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);

    /// <summary>塞がったと判定した後に足す予備の塞ぎ役の数。</summary>
    private const int ReserveBlockers = 40;

    private readonly Mock<ICardRepository> _cardRepositoryMock = new();
    private readonly Mock<IReportExportStatusService> _exportStatusServiceMock = new();
    private readonly ReportViewModel _viewModel;

    public ReportViewModelExportStatusThreadPoolTests()
    {
        var ledgerRepositoryMock = new Mock<ILedgerRepository>();
        var settingsRepositoryMock = new Mock<ISettingsRepository>();
        settingsRepositoryMock.Setup(s => s.GetAppSettings()).Returns(new AppSettings());
        settingsRepositoryMock.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());
        var reportDataBuilder = new ReportDataBuilder(_cardRepositoryMock.Object, ledgerRepositoryMock.Object);

        _viewModel = new ReportViewModel(
            new ReportService(_cardRepositoryMock.Object, ledgerRepositoryMock.Object, settingsRepositoryMock.Object, reportDataBuilder, NullLogger<ReportService>.Instance),
            new PrintService(reportDataBuilder, NullLogger<PrintService>.Instance),
            _cardRepositoryMock.Object,
            new Mock<INavigationService>().Object,
            settingsRepositoryMock.Object,
            new Mock<ISafeFileLauncher>().Object,
            new ReportPreflightChecker(new Mock<IReportDataBuilder>().Object, ledgerRepositoryMock.Object, new ReportFileNameFactory()),
            _exportStatusServiceMock.Object,
            new FixedSystemClock(new DateTime(2026, 6, 15, 10, 0, 0)));
    }

    [Fact]
    public async Task RefreshExportStatusAsync_スレッドプールが塞がっていても判定が始まること()
    {
        // Arrange
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>
        {
            new() { CardIdm = "01", CardType = "nimoca", CardNumber = "N-001" },
            new() { CardIdm = "02", CardType = "はやかけん", CardNumber = "H-001" },
        });
        await _viewModel.LoadCardsAsync();
        _viewModel.SelectedYear = 2026;
        _viewModel.SelectedMonth = 5;

        // 待機ハンドルは破棄しない。失敗して抜けた後に、キューに残った修正前の判定が Set を呼ぶと
        // ObjectDisposedException になる（本体の catch に吸収されるが、失敗時だけの雑音になる）
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        _exportStatusServiceMock
            .Setup(s => s.GetStatuses(
                It.IsAny<IEnumerable<ReportExportTarget>>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .Returns((IEnumerable<ReportExportTarget> targets, string _, int _, int _) =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(60));
                return targets
                    .Select(t => new ReportExportStatus { CardIdm = t.CardIdm, State = ReportExportState.Exported })
                    .ToList();
            });

        Task refresh;
        using (var saturator = ThreadPoolSaturator.Saturate(ReserveBlockers))
        {
            saturator.IsSaturated.Should().BeTrue(
                "前提: プールが塞がっていて、Task.Run の処理が始まらないこと（塞げていなければ本テストは何も検証しない）");

            // Act
            refresh = _viewModel.RefreshExportStatusAsync();
            var started = entered.Wait(StartTimeout);
            var poolStillSaturated = !saturator.SentinelStarted;
            release.Set();

            // Assert: 判定はプールの空きを待たずに始まる
            started.Should().BeTrue("判定は専用スレッドで走るため、プールが塞がっていても始まる（Issue #2221）");

            // 対の表明: 判定を待っている間もプールは塞がったままだった（空きが出ていれば、Task.Run の判定でも
            // 上限内に始まり得て、本テストは修正前のコードを検出できない）
            poolStillSaturated.Should().BeTrue("判定を待っている間も、プールが塞がったままであること");
        }

        // 判定の結果が反映されること（専用スレッドへ移しても、結果の受け渡しと世代判定は変わらない）
        await refresh;
        _viewModel.Cards.Should().OnlyContain(c => c.ExportState == ReportExportState.Exported);
        _viewModel.ExportStatusSummary.Should().Be("2026年5月: 出力済み 2件 / 未出力 0件");
    }
}
