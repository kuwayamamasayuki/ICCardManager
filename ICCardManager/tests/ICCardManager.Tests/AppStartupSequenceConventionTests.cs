using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #2160: 起動時に UI スレッドが DB を同期で待たないことの静的検査。
/// </summary>
/// <remarks>
/// <para>
/// <c>App.xaml.cs</c> は起動時に <c>Task.Run(...).GetAwaiter().GetResult()</c> で DB の読み書きを
/// 5 か所で同期待ちしていた。共有モードで SMB が遅いと、その間 UI スレッドが固まる。
/// <c>OnStartup</c> は既に <c>async void</c> なので、起動経路は <c>*Async</c> を await する形にした。
/// </para>
/// <para>
/// <c>App</c> は WPF のライフサイクルと DI に載っており xUnit から実行できないため、
/// ソーステキストで固定する（<c>SingleInstanceStartupConventionTests</c> と同じ方針）。
/// 検査は「同期待ちの不在」と「非同期の読みが実在し、正しい位置にあること」を<b>対で</b>表明する —
/// 前者だけだと、読みそのものを消した実装（#1905 の「起動直後だけ古い同一視グループ」の窓を
/// 開き直す実装）でも緑になる。
/// </para>
/// </remarks>
public class AppStartupSequenceConventionTests
{
    private const string InitializeDatabaseSignature = "private async Task InitializeDatabaseAsync";

    /// <summary>同期待ちの形（Task を完了まで止めて待つ）</summary>
    /// <remarks>
    /// <c>.Result</c> は呼び出しの直後（<c>…)\.Result</c>）に限る。識別子の後ろの <c>.Result</c>
    /// （<c>integrityReport.Result</c> のような Task 以外のプロパティ）まで数えると誤検出になる。
    /// </remarks>
    private static readonly Regex SyncWaitPattern = new Regex(
        @"\.GetAwaiter\s*\(\s*\)\s*\.GetResult\s*\(\s*\)|\.Wait\s*\(|\bTask\.Wait(All|Any)\s*\(|\)\s*\.Result\b",
        RegexOptions.Compiled);

    /// <summary>SummaryGenerator の DI 登録（ファクトリのラムダが第 1 引数）</summary>
    private static readonly Regex SummaryGeneratorRegistrationPattern = new Regex(
        @"AddSingleton\s*<\s*SummaryGenerator\s*>", RegexOptions.Compiled);

    private static readonly Regex AwaitAppSettingsPattern = new Regex(
        @"_startupSettings\s*=\s*await\s+\w+\.GetAppSettingsAsync\s*\(\s*\)", RegexOptions.Compiled);

    private static readonly Regex AwaitGroupsPattern = new Regex(
        @"TransferStationGroups\s*=\s*await\s+\w+\.GetGroupsAsync\s*\(\s*\)", RegexOptions.Compiled);

    private static readonly Regex StartupTasksPattern = new Regex(
        @"await\s+PerformStartupTasksAsync\s*\(\s*\)", RegexOptions.Compiled);

    private static readonly Regex DepartmentConfigPattern = new Regex(
        @"await\s+ApplyDepartmentConfigFromFileAsync\s*\(\s*\)", RegexOptions.Compiled);

    private static readonly Regex ReportOutputConfigPattern = new Regex(
        @"await\s+ApplyReportOutputConfigFromFileAsync\s*\(\s*\)", RegexOptions.Compiled);

    private static string ReadCodeOnlyAppSource()
        => TestSourceInspection.ToCodeOnly(
            File.ReadAllText(Path.Combine(TestPaths.GetProductionSourceRoot(), "App.xaml.cs")));

    /// <summary>
    /// 同期待ちの箇所数を数える（サニタイズ済みのソースを受け取る）。
    /// </summary>
    internal static int CountSyncWaits(string codeOnlySource) => SyncWaitPattern.Matches(codeOnlySource).Count;

    /// <summary>
    /// <c>InitializeDatabaseAsync</c> の本文で、起動時の読みが「部署種別ファイルの適用の後・起動時タスクの前」に
    /// 実在するかを判定する。満たさない理由を返す（満たすなら null）。
    /// </summary>
    internal static string CheckStartupReadOrder(string initializeBody)
    {
        var settingsRead = AwaitAppSettingsPattern.Match(initializeBody);
        var groupsRead = AwaitGroupsPattern.Match(initializeBody);
        var startupTasks = StartupTasksPattern.Match(initializeBody);
        var departmentApply = DepartmentConfigPattern.Match(initializeBody);

        if (!settingsRead.Success)
        {
            return "起動時の設定（_startupSettings）を GetAppSettingsAsync で await して読んでいない";
        }

        if (!groupsRead.Success)
        {
            return "同一視グループを GetGroupsAsync で await して反映していない（#1905 の窓が開く）";
        }

        if (!startupTasks.Success)
        {
            return "起動時タスク（PerformStartupTasksAsync）の await が見つからない";
        }

        if (!departmentApply.Success)
        {
            return "部署種別ファイルの適用（ApplyDepartmentConfigFromFileAsync）の await が見つからない";
        }

        if (settingsRead.Index > startupTasks.Index || groupsRead.Index > startupTasks.Index)
        {
            return "起動時の読みが PerformStartupTasksAsync より後にある";
        }

        if (settingsRead.Index < departmentApply.Index)
        {
            return "起動時の設定を部署種別ファイルの適用より前に読んでいる（ファイルの部署種別が摘要に反映されない）";
        }

        var reportOutputApply = ReportOutputConfigPattern.Match(initializeBody);
        if (!reportOutputApply.Success || settingsRead.Index < reportOutputApply.Index)
        {
            return "起動時の設定を帳票出力先ファイルの適用より前に読んでいる、または適用の await が見つからない";
        }

        return null;
    }

    [Fact]
    public void AppのコードにTaskを同期で待つ箇所が無いこと()
    {
        var code = ReadCodeOnlyAppSource();

        CountSyncWaits(code).Should().Be(0,
            "起動時に UI スレッドで DB を同期で待つと、共有モードで SMB が遅いとき画面が固まる（Issue #2160）");

        // 空振り検出: 起動経路が実際に await していること（OnStartup が同期化されていないこと）
        code.Should().Contain("protected override async void OnStartup",
            "OnStartup が async であることが、起動経路で await できる前提");
    }

    [Fact]
    public void SummaryGeneratorのファクトリはDBを読まず起動時に読んだ設定を使うこと()
    {
        var code = ReadCodeOnlyAppSource();
        var registrations = TestSourceInspection
            .ExtractInvocationArguments(code, SummaryGeneratorRegistrationPattern)
            .ToList();

        registrations.Should().ContainSingle("SummaryGenerator のシングルトン登録はファクトリ 1 つであること");
        var factory = registrations[0].Arguments.Should().ContainSingle().Subject;

        factory.Should().NotContain("Task.Run", "ファクトリで DB の読みをオフロードして待たないこと");
        factory.Should().NotContain("GetAppSettings", "設定は InitializeDatabaseAsync で読み済み");
        factory.Should().NotContain("GetGroupsAsync", "同一視グループは InitializeDatabaseAsync で反映済み");

        // 正しい形の実在（対の表明）: 読み済みの値の部署種別を渡す
        factory.Should().MatchRegex(@"new\s+SummaryGenerator\s*\(\s*_startupSettings\.DepartmentType\s*,",
            "部署種別は起動時に DB から読んだ設定から渡すこと（#1955）");

        // 未読のまま解決されたら既定値へ倒さず例外にする（#1955）
        factory.Should().Contain("throw new InvalidOperationException",
            "InitializeDatabaseAsync より前に解決されたことを既定値で隠さないこと");
        factory.Should().NotMatchRegex(@"_startupSettings\s*\?\?",
            "読み済みの設定が無いとき既定値へ倒さないこと（#1955）");
    }

    [Fact]
    public void 起動時の読みは部署種別の適用の後かつ起動時タスクの前にawaitされること()
    {
        var code = ReadCodeOnlyAppSource();
        var body = TestSourceInspection.ExtractMethodBody(code, InitializeDatabaseSignature);

        CheckStartupReadOrder(body).Should().BeNull(
            "SummaryGenerator を生成する前に部署種別と同一視グループを読み終えていること（Issue #2160 / #1905）");
    }

    [Fact]
    public void 設定ファイルの適用は非同期で書き込むこと()
    {
        var code = ReadCodeOnlyAppSource();

        foreach (var signature in new[]
                 {
                     "private async Task ApplyDepartmentConfigFromFileAsync",
                     "private async Task ApplyReportOutputConfigFromFileAsync",
                 })
        {
            var body = TestSourceInspection.ExtractMethodBody(code, signature);
            body.Should().MatchRegex(@"await\s+\w+\.SetAsync\s*\(",
                $"{signature} は設定を await で書き込むこと");
            body.Should().NotContain("Task.Run", $"{signature} で書き込みをオフロードして待たないこと");
        }
    }

    [Fact]
    public void 保存済み設定の適用はDBを読み直さないこと()
    {
        var code = ReadCodeOnlyAppSource();
        var body = TestSourceInspection.ExtractMethodBody(code, "private void ApplySavedSettings(AppSettings settings)");

        body.Should().NotContain("GetAppSettings", "起動時の設定読みは InitializeDatabaseAsync の 1 回にする");

        // 適用に失敗したときのフォールバック（文字サイズ Medium・トースト TopRight）を維持する
        body.Should().Contain("ApplyFontSize(FontSizeOption.Medium)");
        body.Should().Contain("ApplyToastPosition(ToastPosition.TopRight)");

        var initializeBody = TestSourceInspection.ExtractMethodBody(code, InitializeDatabaseSignature);
        initializeBody.Should().MatchRegex(@"ApplySavedSettings\s*\(\s*_startupSettings\s*\)",
            "起動時に読んだ設定をそのまま適用すること");
    }

    /// <summary>
    /// 同期待ちの検出ロジック自体を既知のサンプル入力で固定する（実データが変わっても空振りしない）。
    /// </summary>
    [Theory]
    [InlineData("var s = Task.Run(() => repo.GetAppSettings()).GetAwaiter().GetResult();", 1)]
    [InlineData("Task.Run(() => repo.SetAsync(k, v)).GetAwaiter()\n    .GetResult();", 1)]
    [InlineData("task.Wait();", 1)]
    [InlineData("task.Wait(TimeSpan.FromSeconds(5));", 1)]
    [InlineData("Task.WaitAll(a, b);", 1)]
    [InlineData("var s = Task.Run(() => repo.GetAppSettings()).Result;", 1)]
    [InlineData("var s = await repo.GetAppSettingsAsync();", 0)]
    // Task 以外のプロパティ名としての Result は数えない（誤検出はガードの寿命を縮める）
    [InlineData("var ok = integrityReport.Result == CheckResult.Ok;", 0)]
    // 規約の理由を書いたコメントは数えない（極性の反転を避ける。#1692）
    [InlineData("// 以前は Task.Run(...).GetAwaiter().GetResult() で待っていた\nvar s = await repo.GetAppSettingsAsync();", 0)]
    public void 同期待ちの検出はコメントを除いたコードだけを数えること(string source, int expected)
    {
        CountSyncWaits(TestSourceInspection.ToCodeOnly(source)).Should().Be(expected);
    }

    /// <summary>
    /// 順序の判定ロジック自体を既知のサンプル入力で固定する。読みを消した実装・順序を崩した実装を落とすこと。
    /// </summary>
    [Theory]
    [InlineData(
        "{ await ApplyDepartmentConfigFromFileAsync(); await ApplyReportOutputConfigFromFileAsync(); _startupSettings = await repo.GetAppSettingsAsync(); " +
        "o.SummaryRules.TransferStationGroups = await g.GetGroupsAsync(); await PerformStartupTasksAsync(); }",
        null)]
    // 読みを消した（ファクトリが未読の設定で例外になる／同一視グループが既定値のまま）
    [InlineData(
        "{ await ApplyDepartmentConfigFromFileAsync(); await ApplyReportOutputConfigFromFileAsync(); o.SummaryRules.TransferStationGroups = await g.GetGroupsAsync(); " +
        "await PerformStartupTasksAsync(); }",
        "GetAppSettingsAsync")]
    [InlineData(
        "{ await ApplyDepartmentConfigFromFileAsync(); await ApplyReportOutputConfigFromFileAsync(); _startupSettings = await repo.GetAppSettingsAsync(); " +
        "await PerformStartupTasksAsync(); }",
        "GetGroupsAsync")]
    // 起動時タスクの後へ回した
    [InlineData(
        "{ await ApplyDepartmentConfigFromFileAsync(); await ApplyReportOutputConfigFromFileAsync(); await PerformStartupTasksAsync(); " +
        "_startupSettings = await repo.GetAppSettingsAsync(); o.SummaryRules.TransferStationGroups = await g.GetGroupsAsync(); }",
        "PerformStartupTasksAsync より後")]
    // 部署種別ファイルの適用より前に読んだ
    [InlineData(
        "{ _startupSettings = await repo.GetAppSettingsAsync(); await ApplyDepartmentConfigFromFileAsync(); await ApplyReportOutputConfigFromFileAsync(); " +
        "o.SummaryRules.TransferStationGroups = await g.GetGroupsAsync(); await PerformStartupTasksAsync(); }",
        "部署種別ファイルの適用より前")]
    // 帳票出力先ファイルの適用より前に読んだ
    [InlineData(
        "{ await ApplyDepartmentConfigFromFileAsync(); _startupSettings = await repo.GetAppSettingsAsync(); " +
        "await ApplyReportOutputConfigFromFileAsync(); o.SummaryRules.TransferStationGroups = await g.GetGroupsAsync(); " +
        "await PerformStartupTasksAsync(); }",
        "帳票出力先ファイルの適用より前")]
    // 同期で待つ形は await の形に一致しない
    [InlineData(
        "{ await ApplyDepartmentConfigFromFileAsync(); await ApplyReportOutputConfigFromFileAsync(); _startupSettings = Task.Run(() => repo.GetAppSettings()).GetAwaiter().GetResult(); " +
        "o.SummaryRules.TransferStationGroups = await g.GetGroupsAsync(); await PerformStartupTasksAsync(); }",
        "GetAppSettingsAsync")]
    public void 起動時の読みの順序判定は読みの欠落と順序の崩れを検出すること(string body, string expectedReasonFragment)
    {
        var reason = CheckStartupReadOrder(body);

        if (expectedReasonFragment == null)
        {
            reason.Should().BeNull();
        }
        else
        {
            reason.Should().NotBeNull().And.Contain(expectedReasonFragment);
        }
    }
}
