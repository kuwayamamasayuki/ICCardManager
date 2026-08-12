using System;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views.Dialogs;

/// <summary>
/// 操作ログダイアログの一覧列幅・絞り込みコンボ幅に関するレイアウトのリグレッションテスト（Issue #1787）。
/// </summary>
/// <remarks>
/// <para>
/// Issue #1787 で操作種別の表示名が「インポート」「エクスポート」「バックアップ」等の全角6文字になり、
/// 従来の固定幅（操作 60px / 対象 80px、コンボ 100px / 120px）では中サイズでも文字が切れるようになった。
/// 全角1文字はおおむね <c>BaseFontSize</c> px（小12 / 中14 / 大16）を占めるため、
/// 「バックアップ」は 84px @中 / 96px @大 を要する。
/// </para>
/// <para>
/// 実描画は WPF の Measure/Arrange に依存し単体テストでは検証できないため、ここでは
/// 「Issue #1787 修正時点で確立された XAML 構造的不変条件」を静的解析で固定する
/// （`.claude/rules/development-conventions.md` の「回帰は XAML テキスト上の静的検証で固定する」に従う。
/// 参考実装: <c>MainWindowWarningAreaLayoutTests</c> / <c>ReportDialogStatusAreaLayoutTests</c>）。
/// </para>
/// <para>
/// 固定するのは次の3点。①コンボは固定 Width ではなく MinWidth で内容に追随する
/// ②一覧の内容依存列は TextWrapping="Wrap" で文字切れを担保する
/// ③「操作」「対象」列に Width="Auto" を使わない — WPF DataGrid の Auto 幅は実体化済み行の
/// 最大値を保持して縮まないため、一度長い値を含むページを表示すると星列を圧迫し続ける。
/// </para>
/// </remarks>
public class OperationLogDialogColumnWidthLayoutTests
{
    private const string TargetXaml = "OperationLogDialog.xaml";

    private static readonly string DialogsDirectory = ResolveDialogsDirectory();

    /// <summary>
    /// 抽出の妥当性を先に固定する（対象 XAML を読めていない状態で他のテストが
    /// 空振りしたまま緑になるのを防ぐ）。
    /// </summary>
    [Fact]
    public void 対象XAMLにDataGrid列定義が存在すること()
    {
        var xaml = ReadDialog(TargetXaml);

        xaml.Should().Contain("<DataGrid.Columns>",
            "抽出対象の XAML が想定と異なると、以降の検査がすべて空振りする");
        Regex.Matches(xaml, @"<DataGridTextColumn\s").Count.Should().Be(6,
            "操作ログ一覧は 日時／操作／対象／対象詳細／操作者／詳細 の6列構成である");
    }

    [Theory]
    [InlineData("操作種別")]
    [InlineData("対象テーブル")]
    public void 絞り込みコンボは固定Widthではなく_MinWidth_を使うこと(string automationName)
    {
        var comboBox = ExtractElement(ReadDialog(TargetXaml), "ComboBox", automationName);

        comboBox.Should().MatchRegex(@"MinWidth=""\d+""",
            $"{automationName} コンボは MinWidth で内容と文字サイズに追随する必要がある（Issue #1787）");
        comboBox.Should().NotMatchRegex(@"\sWidth=""\d+""",
            $"{automationName} コンボに固定 Width を戻すと、「エクスポート」等の全角6文字の選択肢が " +
            "文字サイズ「大」以上で切れる（Issue #1787）");
    }

    [Theory]
    [InlineData("操作", "ActionDisplay")]
    [InlineData("対象", "TargetTableDisplay")]
    [InlineData("対象詳細", "TargetDisplayName")]
    [InlineData("詳細", "DetailSummary")]
    public void 内容依存列は_TextWrapping_Wrap_で文字切れを担保すること(string header, string binding)
    {
        var column = ExtractColumn(ReadDialog(TargetXaml), header, binding);

        column.Should().MatchRegex(@"<Setter\s+Property=""TextWrapping""\s+Value=""Wrap""\s*/>",
            $"「{header}」列は TextWrapping=\"Wrap\" で文字サイズ「大」以上の文字切れを担保する必要がある" +
            "（幅を広げる対処は特大でまた破綻するため。Issue #1787）");
    }

    [Theory]
    [InlineData("操作", "ActionDisplay")]
    [InlineData("対象", "TargetTableDisplay")]
    public void 操作と対象の列は_Width_Auto_を使わないこと(string header, string binding)
    {
        var column = ExtractColumn(ReadDialog(TargetXaml), header, binding);

        column.Should().NotMatchRegex(@"\sWidth=""Auto""",
            $"「{header}」列に Width=\"Auto\" を使うと、WPF DataGrid の Auto 幅は実体化済み行の最大値を " +
            "保持して縮まないため、一度「バックアップ」等の長い値を含むページを表示すると以後も " +
            "広がったままになり、星列（対象詳細・詳細）を圧迫し続ける（Issue #1787）");
    }

    [Fact]
    public void 可変長の内容列は星幅で残余を分け合うこと()
    {
        var xaml = ReadDialog(TargetXaml);

        // 「対象詳細」は Issue #1741 でファイル名を表示するようになった内容依存列。
        // 固定 250px のままだと最小幅（MinWidth=800）で「詳細」列に ~50px しか残らない。
        ExtractColumn(xaml, "対象詳細", "TargetDisplayName")
            .Should().MatchRegex(@"Width=""\*""");
        ExtractColumn(xaml, "詳細", "DetailSummary")
            .Should().MatchRegex(@"Width=""\*""");
    }

    [Theory]
    [InlineData("対象詳細", "TargetDisplayName")]
    [InlineData("詳細", "DetailSummary")]
    public void 星幅の列は_MinWidth_で最低限の可読幅を確保すること(string header, string binding)
    {
        var column = ExtractColumn(ReadDialog(TargetXaml), header, binding);

        var match = Regex.Match(column, @"MinWidth=""(\d+)""");
        match.Success.Should().BeTrue(
            $"「{header}」列は MinWidth で最低限の可読幅を確保する必要がある（Issue #1787）");
        int.Parse(match.Groups[1].Value).Should().BeGreaterThanOrEqualTo(150,
            $"「{header}」列が 150px を下回ると、摘要やファイル名が実質的に読めなくなる");
    }

    /// <summary>
    /// 指定ヘッダー・バインドを持つ DataGridTextColumn の定義範囲を切り出す。
    /// 自己終了タグ（&lt;… /&gt;）と開始～終了タグの両形式に対応する。
    /// </summary>
    private static string ExtractColumn(string xaml, string header, string binding)
    {
        var pattern =
            $@"<DataGridTextColumn\s+Header=""{Regex.Escape(header)}""\s+Binding=""\{{Binding {Regex.Escape(binding)}\}}""" +
            @"(?:[^>]*?/>|.*?</DataGridTextColumn>)";
        var match = Regex.Match(xaml, pattern, RegexOptions.Singleline);

        match.Success.Should().BeTrue(
            $"「{header}」列（Binding={binding}）の定義を XAML から抽出できませんでした。" +
            "列の Header / Binding を変更した場合は本テストの期待値も更新してください。");
        return match.Value;
    }

    /// <summary>
    /// 指定 AutomationProperties.Name を持つ要素の開始タグ範囲を切り出す。
    /// </summary>
    private static string ExtractElement(string xaml, string elementName, string automationName)
    {
        var pattern =
            $@"<{Regex.Escape(elementName)}\s[^>]*?AutomationProperties\.Name=""{Regex.Escape(automationName)}""[^>]*?/>";
        var match = Regex.Match(xaml, pattern, RegexOptions.Singleline);

        match.Success.Should().BeTrue(
            $"AutomationProperties.Name=\"{automationName}\" の {elementName} を XAML から抽出できませんでした。");
        return match.Value;
    }

    private static string ReadDialog(string fileName)
    {
        var path = Path.Combine(DialogsDirectory, fileName);
        File.Exists(path).Should().BeTrue($"{fileName} が {DialogsDirectory} に存在する必要があります");
        return File.ReadAllText(path);
    }

    private static string ResolveDialogsDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "src", "ICCardManager", "Views", "Dialogs");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }

        throw new InvalidOperationException(
            $"Views/Dialogs ディレクトリを {AppContext.BaseDirectory} の親階層から解決できませんでした");
    }
}
