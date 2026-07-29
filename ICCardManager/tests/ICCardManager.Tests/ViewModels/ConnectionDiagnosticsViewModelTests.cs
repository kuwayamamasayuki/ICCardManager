using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Dtos;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// ConnectionDiagnosticsViewModel の単体テスト（Issue #1690）
/// </summary>
/// <remarks>
/// 判定と文言はサービス側にあるため、本 ViewModel の責務は
/// 「実行 → 表示 → コピー」の導線に限られる。
/// 特に「対処が必要な項目を初期選択する」挙動と、
/// クリップボードのコピー失敗を利用者へ伝える挙動を固定する。
/// </remarks>
public class ConnectionDiagnosticsViewModelTests
{
    private readonly Mock<IConnectionDiagnosticsService> _diagnosticsService = new();
    private readonly Mock<IClipboardService> _clipboardService = new();

    private static readonly DateTime DiagnosedAt = new(2026, 7, 28, 14, 30, 15);

    public ConnectionDiagnosticsViewModelTests()
    {
        _clipboardService.Setup(c => c.TrySetText(It.IsAny<string>())).Returns(true);
        SetupReport(BuildReport(Item(DiagnosticItemKind.DatabaseReachability, DiagnosticStatus.Ok)));
    }

    private static DiagnosticItem Item(DiagnosticItemKind kind, DiagnosticStatus status) => new()
    {
        Kind = kind,
        Title = kind.ToString(),
        Status = status,
        SummaryText = "要約",
        DetailText = "詳細と対処方法です。確認してください。"
    };

    private static DiagnosticReport BuildReport(params DiagnosticItem[] items) => new()
    {
        DiagnosedAt = DiagnosedAt,
        AppVersion = "2.11.0",
        MachineName = "SOMU-PC01",
        OsDescription = "Windows",
        DatabasePath = @"C:\iccard.db",
        Items = new List<DiagnosticItem>(items)
    };

    private void SetupReport(DiagnosticReport report) =>
        _diagnosticsService.Setup(s => s.RunDiagnosticsAsync()).ReturnsAsync(report);

    private ConnectionDiagnosticsViewModel CreateViewModel() =>
        new(_diagnosticsService.Object, _clipboardService.Object);

    #region 診断の実行

    [Fact]
    public async Task 診断実行_結果の項目が一覧へ読み込まれること()
    {
        SetupReport(BuildReport(
            Item(DiagnosticItemKind.DatabaseReachability, DiagnosticStatus.Ok),
            Item(DiagnosticItemKind.CardReader, DiagnosticStatus.Error)));
        var vm = CreateViewModel();

        await vm.RunDiagnosticsAsync();

        vm.Items.Should().HaveCount(2);
        vm.HasResult.Should().BeTrue();
        vm.DiagnosedAtText.Should().Contain("2026年7月28日");
    }

    [Fact]
    public void 診断実行前_未実行であることが表示されコピーできないこと()
    {
        var vm = CreateViewModel();

        vm.HasResult.Should().BeFalse();
        vm.DiagnosedAtText.Should().Be("未実行");
        vm.CopyResultCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task 診断実行_問題のある項目が初期選択されること()
    {
        // 利用者が一覧を目で探して選び直す手間を省く
        SetupReport(BuildReport(
            Item(DiagnosticItemKind.DatabaseReachability, DiagnosticStatus.Ok),
            Item(DiagnosticItemKind.JournalMode, DiagnosticStatus.Warning),
            Item(DiagnosticItemKind.CardReader, DiagnosticStatus.Error)));
        var vm = CreateViewModel();

        await vm.RunDiagnosticsAsync();

        vm.SelectedItem.Kind.Should().Be(DiagnosticItemKind.JournalMode);
        vm.SelectedDetailText.Should().Be("詳細と対処方法です。確認してください。");
    }

    [Fact]
    public async Task 診断実行_全て正常なら先頭項目が選択されること()
    {
        SetupReport(BuildReport(
            Item(DiagnosticItemKind.DatabaseReachability, DiagnosticStatus.Ok),
            Item(DiagnosticItemKind.CardReader, DiagnosticStatus.Ok)));
        var vm = CreateViewModel();

        await vm.RunDiagnosticsAsync();

        vm.SelectedItem.Kind.Should().Be(DiagnosticItemKind.DatabaseReachability);
    }

    [Fact]
    public async Task 診断実行_問題件数がステータスに表示されること()
    {
        SetupReport(BuildReport(
            Item(DiagnosticItemKind.DatabaseReachability, DiagnosticStatus.Error),
            Item(DiagnosticItemKind.CardReader, DiagnosticStatus.Warning)));
        var vm = CreateViewModel();

        await vm.RunDiagnosticsAsync();

        vm.StatusMessage.Should().Contain("2件");
        vm.IsStatusError.Should().BeTrue();
    }

    [Fact]
    public async Task 診断実行_全て正常ならステータスがエラー扱いにならないこと()
    {
        var vm = CreateViewModel();

        await vm.RunDiagnosticsAsync();

        vm.IsStatusError.Should().BeFalse();
        vm.StatusMessage.Should().Contain("正常");
    }

    [Fact]
    public async Task 診断実行中はIsBusyがtrueになること()
    {
        var vm = CreateViewModel();
        var busyStates = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionDiagnosticsViewModel.IsBusy))
                busyStates.Add(vm.IsBusy);
        };

        await vm.RunDiagnosticsAsync();

        busyStates.Should().HaveCountGreaterOrEqualTo(2);
        busyStates.First().Should().BeTrue();
        busyStates.Last().Should().BeFalse();
    }

    [Fact]
    public async Task 再診断中_前回の結果が残らないこと()
    {
        // 再診断は数十秒かかり得る（切断時の DB 疎通確認は SMB のタイムアウトまでブロックする）。
        // その間に前回の「正常」が表示され続けると、切断中でも正常と読めてしまう。
        var vm = CreateViewModel();
        await vm.RunDiagnosticsAsync();
        vm.Items.Should().NotBeEmpty();

        var itemCountDuringRun = -1;
        var hasResultDuringRun = true;
        _diagnosticsService.Setup(s => s.RunDiagnosticsAsync()).Returns(() =>
        {
            itemCountDuringRun = vm.Items.Count;
            hasResultDuringRun = vm.HasResult;
            return Task.FromResult(BuildReport(
                Item(DiagnosticItemKind.DatabaseReachability, DiagnosticStatus.Error)));
        });

        await vm.RunDiagnosticsAsync();

        itemCountDuringRun.Should().Be(0, "実行中は前回の項目を残さない");
        hasResultDuringRun.Should().BeFalse("実行中は前回の総合判定を残さない");
    }

    [Fact]
    public async Task 再診断中_前回の結果をコピーできないこと()
    {
        // 実行中に古い結果をコピーして IT 担当へ送ると、誤った状況が伝わる
        var vm = CreateViewModel();
        await vm.RunDiagnosticsAsync();

        var canCopyDuringRun = true;
        _diagnosticsService.Setup(s => s.RunDiagnosticsAsync()).Returns(() =>
        {
            canCopyDuringRun = vm.CopyResultCommand.CanExecute(null);
            return Task.FromResult(BuildReport(
                Item(DiagnosticItemKind.DatabaseReachability, DiagnosticStatus.Ok)));
        });

        await vm.RunDiagnosticsAsync();

        canCopyDuringRun.Should().BeFalse();
    }

    [Fact]
    public async Task 診断実行_総合判定の見出しが更新されること()
    {
        SetupReport(BuildReport(Item(DiagnosticItemKind.CardReader, DiagnosticStatus.Error)));
        var vm = CreateViewModel();

        await vm.RunDiagnosticsAsync();

        vm.OverallSummaryText.Should()
            .Be(DiagnosticStatusPresenter.GetOverallSummary(DiagnosticStatus.Error));
        vm.OverallIcon.Should().Be(DiagnosticStatusPresenter.GetIcon(DiagnosticStatus.Error));
        vm.OverallForegroundResourceKey.Should()
            .Be(DiagnosticStatusPresenter.GetForegroundResourceKey(DiagnosticStatus.Error));
    }

    [Fact]
    public async Task 診断実行_結果がnullでも例外にならず案内を出すこと()
    {
        _diagnosticsService.Setup(s => s.RunDiagnosticsAsync()).ReturnsAsync((DiagnosticReport)null);
        var vm = CreateViewModel();

        Func<Task> act = () => vm.RunDiagnosticsAsync();

        await act.Should().NotThrowAsync();
        vm.Items.Should().BeEmpty();
        vm.StatusMessage.Should().EndWith("してください。");
    }

    #endregion

    #region 結果のコピー

    [Fact]
    public async Task コピー_整形済みテキストをクリップボードへ渡すこと()
    {
        var report = BuildReport(Item(DiagnosticItemKind.CardReader, DiagnosticStatus.Error));
        SetupReport(report);
        var vm = CreateViewModel();
        await vm.RunDiagnosticsAsync();

        vm.CopyResult();

        // Formatter と同じ出力を渡していること（ViewModel が独自に整形し直していないこと）
        _clipboardService.Verify(
            c => c.TrySetText(DiagnosticReportFormatter.Format(report)),
            Times.Once);
    }

    [Fact]
    public async Task コピー成功_共有を促すメッセージを表示すること()
    {
        var vm = CreateViewModel();
        await vm.RunDiagnosticsAsync();

        vm.CopyResult();

        vm.IsStatusError.Should().BeFalse();
        vm.StatusMessage.Should().Contain("コピーしました");
    }

    [Fact]
    public async Task コピー失敗_原因と次の行動を示すメッセージを表示すること()
    {
        // クリップボードは他プロセスにロックされ得るため、失敗は静かに握りつぶさない
        _clipboardService.Setup(c => c.TrySetText(It.IsAny<string>())).Returns(false);
        var vm = CreateViewModel();
        await vm.RunDiagnosticsAsync();

        vm.CopyResult();

        vm.IsStatusError.Should().BeTrue();
        vm.StatusMessage.Should().Contain("クリップボード");
        vm.StatusMessage.Should().EndWith("してください。");
        vm.StatusMessage.Length.Should().BeGreaterOrEqualTo(20);
    }

    [Fact]
    public async Task コピー_診断実行後に実行可能になること()
    {
        var vm = CreateViewModel();
        vm.CopyResultCommand.CanExecute(null).Should().BeFalse();

        await vm.RunDiagnosticsAsync();

        vm.CopyResultCommand.CanExecute(null).Should().BeTrue();
    }

    #endregion

    #region 詳細表示

    [Fact]
    public void 未選択時_一覧の操作を促す文言を返すこと()
    {
        var vm = CreateViewModel();

        vm.SelectedDetailText.Should().Contain("一覧から項目を選択");
    }

    [Fact]
    public async Task 選択変更_詳細文言が切り替わること()
    {
        SetupReport(BuildReport(
            Item(DiagnosticItemKind.DatabaseReachability, DiagnosticStatus.Ok),
            Item(DiagnosticItemKind.CardReader, DiagnosticStatus.Ok)));
        var vm = CreateViewModel();
        await vm.RunDiagnosticsAsync();

        var changed = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionDiagnosticsViewModel.SelectedDetailText))
                changed = true;
        };

        vm.SelectedItem = vm.Items.Last();

        changed.Should().BeTrue();
    }

    #endregion
}
