using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// 帳票の事前チェック（プリフライト）が、画面の選択年月ではなく呼び出し元のスナップショットを使うことの静的検査（Issue #2045）
/// </summary>
/// <remarks>
/// <para>
/// 事前チェック結果ダイアログに表示する「N年M月分」は <c>Window</c>（STA・XAML リソース依存）の内側で設定されるため、
/// ViewModel 単体テストからは観測できない。検査の待機中に年月を変えると、ダイアログが
/// <b>検査していない月の名前</b>で「問題なし」「警告N件」を表示し、職員はその月について確認したつもりになる。
/// 挙動テスト（<c>ReportViewModelTests</c> の Issue #2045 の 3 件）は検査対象の年月を固定し、本クラスは
/// 「現在の選択を読む手段」がプリフライトの内側に残っていないことを固定する。
/// </para>
/// </remarks>
public class ReportPreflightTargetPeriodConventionTests
{
    private static readonly Regex SelectedPeriodPattern = new(@"\bSelected(?:Year|Month)\b", RegexOptions.Compiled);

    private static string LoadReportViewModelCode()
    {
        var path = Path.Combine(TestPaths.GetProductionSourceRoot(), "ViewModels", "ReportViewModel.cs");
        File.Exists(path).Should().BeTrue("ReportViewModel.cs が存在すること（検査対象の空振り防止）");
        return TestSourceInspection.ToCodeOnly(File.ReadAllText(path));
    }

    /// <summary>
    /// 本体の中で、最初の await より後ろに現れる SelectedYear / SelectedMonth の参照を列挙する
    /// </summary>
    internal static string[] FindSelectedPeriodReadsAfterFirstAwait(string body)
    {
        var firstAwait = Regex.Match(body, @"\bawait\b");
        var start = firstAwait.Success ? firstAwait.Index : body.Length;
        return SelectedPeriodPattern.Matches(body)
            .Cast<Match>()
            .Where(m => m.Index > start)
            .Select(m => m.Value)
            .ToArray();
    }

    [Theory]
    [InlineData("private async Task<ReportPreflightResult> RunPreflightAsync(")]
    [InlineData("internal async Task<bool> RunPreflightBeforeCreateAsync(")]
    [InlineData("private bool? ShowPreflightDialog(")]
    public void 事前チェックの内側は選択年月を読まないこと(string signature)
    {
        var body = TestSourceInspection.ExtractMethodBody(LoadReportViewModelCode(), signature);

        // 抽出範囲の妥当性（式形式へ変えると別のブロックを返して空振りするため。#1794）
        body.Should().MatchRegex(@"\btarget(?:Year|Month)\b",
            "抽出した本体が、年月を引数で受け取るメソッドの本体であること（検査の空振り防止）");

        SelectedPeriodPattern.IsMatch(body).Should().BeFalse(
            "事前チェックは呼び出し元がスナップショットした年月を使うこと。現在の選択を読むと、" +
            "検査の待機中に年月を変えたとき検査対象・結果ダイアログの表示・作成対象が別の月へずれる（Issue #2045）");
    }

    [Fact]
    public void 事前チェックボタンは最初のawaitより前に年月をスナップショットすること()
    {
        var body = TestSourceInspection.ExtractMethodBody(
            LoadReportViewModelCode(), "public async Task RunPreflightCheckAsync(");

        // 正しい形の存在: await より前で年月を確定させている
        body.Should().MatchRegex(@"var\s+targetYear\s*=\s*SelectedYear\s*;");
        body.Should().MatchRegex(@"var\s+targetMonth\s*=\s*SelectedMonth\s*;");
        body.Should().Contain("await", "検査を待機していること（検査の空振り防止）");

        // 禁止された形の不在: 待機の後ろで選択年月を読み直していない
        FindSelectedPeriodReadsAfterFirstAwait(body).Should().BeEmpty(
            "検査の待機中に年月が変わり得るため、await の後ろで SelectedYear / SelectedMonth を読まないこと（Issue #2045）");
    }

    /// <summary>
    /// 作成・プレビューの経路も、待機の後ろで選択年月を読み直していないこと（Issue #1949 / #2045。コードレビューで追加）
    /// </summary>
    [Theory]
    [InlineData("public async Task CreateReportAsync(")]
    [InlineData("public async Task PreviewReportAsync(")]
    [InlineData("public async Task PreviewSelectedAsync(")]
    public void 作成とプレビューはawaitの後ろで選択年月を読まないこと(string signature)
    {
        var body = TestSourceInspection.ExtractMethodBody(LoadReportViewModelCode(), signature);

        body.Should().Contain("await", "待機を含むメソッドの本体であること（検査の空振り防止）");
        SelectedPeriodPattern.IsMatch(body).Should().BeTrue(
            "開始時点で選択年月をスナップショットしていること（抽出範囲の妥当性）");

        // 「最初の await より後ろ」はテキスト順で判定するため、早期 return の分岐にある await
        // （PreviewSelectedAsync の単一カード経路）より後ろのスナップショットを誤検出する。
        // この 3 経路では、参照がすべてローカル変数へのスナップショット代入であることを表明する。
        FindNonSnapshotSelectedPeriodReads(body).Should().BeEmpty(
            "待機中に年月が変わり得るため、SelectedYear / SelectedMonth はローカル変数へ確定させてから使うこと（Issue #1949 / #2045）");
    }

    /// <summary>
    /// <c>var x = SelectedYear;</c> 形のスナップショット代入以外で現れる SelectedYear / SelectedMonth の参照を列挙する
    /// </summary>
    internal static string[] FindNonSnapshotSelectedPeriodReads(string body)
    {
        var snapshotSpans = Regex.Matches(body, @"var\s+\w+\s*=\s*Selected(?:Year|Month)\s*;")
            .Cast<Match>()
            .ToArray();
        return SelectedPeriodPattern.Matches(body)
            .Cast<Match>()
            .Where(m => !snapshotSpans.Any(s => m.Index >= s.Index && m.Index < s.Index + s.Length))
            .Select(m => m.Value)
            .ToArray();
    }

    [Fact]
    public void 結果ダイアログには検査に使った年月を渡すこと()
    {
        var body = TestSourceInspection.ExtractMethodBody(
            LoadReportViewModelCode(), "private bool? ShowPreflightDialog(");

        body.Should().MatchRegex(@"SetResult\s*\(\s*result\s*,\s*targetYear\s*,\s*targetMonth\s*,",
            "ダイアログの「N年M月分」は検査に使った年月で表示すること（Issue #2045）");
    }

    [Fact]
    public void 作成フローは事前チェックへスナップショットの年月を渡すこと()
    {
        var body = TestSourceInspection.ExtractMethodBody(
            LoadReportViewModelCode(), "public async Task CreateReportAsync(");

        body.Should().MatchRegex(
            @"RunPreflightBeforeCreateAsync\s*\(\s*targetCards\s*,\s*targetYear\s*,\s*targetMonth\s*\)",
            "作成フローは開始時点のスナップショット（カード・年月）を事前チェックへ渡すこと（Issue #1949 / #2045）");
    }

    /// <summary>
    /// 検査ロジック自体をサンプル入力で固定する（実データが変わっても空振りしないため。#1786）
    /// </summary>
    [Fact]
    public void 検査ロジック_awaitの後ろの参照だけを検出すること()
    {
        FindSelectedPeriodReadsAfterFirstAwait(
                "{ var y = SelectedYear; await Foo(y); Show(SelectedMonth); }")
            .Should().Equal("SelectedMonth");

        FindSelectedPeriodReadsAfterFirstAwait(
                "{ var y = SelectedYear; var m = SelectedMonth; await Foo(y, m); }")
            .Should().BeEmpty();

        FindNonSnapshotSelectedPeriodReads(
                "{ var y = SelectedYear; var m = SelectedMonth; await Foo(y, m); }")
            .Should().BeEmpty();
        FindNonSnapshotSelectedPeriodReads(
                "{ var y = SelectedYear; await Foo(y, SelectedMonth); }")
            .Should().Equal("SelectedMonth");

        // 語境界: 別名の識別子を拾わない
        FindSelectedPeriodReadsAfterFirstAwait(
                "{ await Foo(); var x = SelectedYearText + IsSelectedMonthly; }")
            .Should().BeEmpty();
    }
}
