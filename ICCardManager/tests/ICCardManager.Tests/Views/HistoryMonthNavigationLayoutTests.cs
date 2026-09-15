using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// 履歴の表示期間を矢印で前後の月へ移動する（Issue #2030）ボタンの XAML 静的検査。
/// </summary>
/// <remarks>
/// <para>
/// メイン画面は <c>Window</c> のコードビハインドを実体化しないと動作を確かめられない（STA 依存で xUnit から
/// 実行できない）ため、XAML のテキスト上で固定する（#1817 / #1907 と同じ形）。ViewModel のテストは
/// コマンドの挙動しか見ないため、ボタンが画面に無い／別のコマンドに結線されている状態は検出できない。
/// </para>
/// <para>
/// 矢印は表示期間テキストの <c>Border</c> の<b>外</b>に置く。Border は <c>MouseLeftButtonUp</c> で
/// 月選択ポップアップを開く（#945）ため、内側に置くと矢印のクリックでポップアップまで開く。
/// </para>
/// </remarks>
public class HistoryMonthNavigationLayoutTests
{
    private static string ReadMainWindowXaml()
    {
        var path = Path.Combine(TestPaths.GetProductionSourceRoot(), "Views", "MainWindow.xaml");
        File.Exists(path).Should().BeTrue($"検査対象の XAML が見つからない: {path}");
        // 規約の理由を書いたコメント自体が検出される極性の反転を避ける（#1692）
        return Regex.Replace(File.ReadAllText(path), "<!--.*?-->", string.Empty, RegexOptions.Singleline);
    }

    private static Match FindButton(string xaml, string commandName)
    {
        var match = Regex.Match(
            xaml,
            "<Button[^>]*Command=\"\\{Binding " + commandName + "\\}\"[^>]*/>",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue($"{commandName} に結線したボタンがメイン画面に存在すること");
        return match;
    }

    private static Match FindPeriodDisplayBorder(string xaml)
    {
        var match = Regex.Match(
            xaml,
            "<Border x:Name=\"HistoryPeriodDisplayBorder\".*?</Border>",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue("表示期間テキストの Border がメイン画面に存在すること");
        return match;
    }

    [Fact]
    public void 表示期間テキストの左に前の月_右に次の月の矢印が並ぶこと()
    {
        var xaml = ReadMainWindowXaml();

        var previous = FindButton(xaml, "HistoryGoToPreviousMonthCommand");
        var border = FindPeriodDisplayBorder(xaml);
        var next = FindButton(xaml, "HistoryGoToNextMonthCommand");

        previous.Value.Should().Contain("Content=\"◀\"");
        next.Value.Should().Contain("Content=\"▶\"");
        (previous.Index + previous.Length).Should().BeLessOrEqualTo(border.Index, "◀ は表示期間テキストの左に置く");
        next.Index.Should().BeGreaterOrEqualTo(border.Index + border.Length, "▶ は表示期間テキストの右に置く");

        // 矢印と表示期間テキストの間に他の要素を挟まない（「表示期間:」ラベルと月の間に矢印が並ぶ）
        xaml.Substring(previous.Index + previous.Length, border.Index - (previous.Index + previous.Length))
            .Should().NotContain("<", "◀ の直後が表示期間テキストであること");
        xaml.Substring(border.Index + border.Length, next.Index - (border.Index + border.Length))
            .Should().NotContain("<", "表示期間テキストの直後が ▶ であること");
    }

    [Fact]
    public void 矢印は月選択ポップアップを開くBorderの内側に置かないこと()
    {
        var xaml = ReadMainWindowXaml();

        var border = FindPeriodDisplayBorder(xaml);

        border.Value.Should().Contain("MouseLeftButtonUp=\"HistoryPeriodDisplay_MouseLeftButtonUp\"",
            "表示期間テキストのクリックで月選択ポップアップを開く既存の操作（#945）を残す");
        border.Value.Should().NotContain("HistoryGoToPreviousMonthCommand");
        border.Value.Should().NotContain("HistoryGoToNextMonthCommand");
    }

    [Fact]
    public void 矢印は記号だけでなく読み上げ名とツールチップで操作内容を示すこと()
    {
        var xaml = ReadMainWindowXaml();

        // ◀ / ▶ はスクリーンリーダーで意味を持たないため、名前で操作を伝える（色・記号だけに頼らない。#1274）
        var previous = FindButton(xaml, "HistoryGoToPreviousMonthCommand").Value;
        previous.Should().Contain("AutomationProperties.Name=\"前の月\"");
        previous.Should().Contain("ToolTip=\"前の月の履歴を表示\"");

        var next = FindButton(xaml, "HistoryGoToNextMonthCommand").Value;
        next.Should().Contain("AutomationProperties.Name=\"次の月\"");
        next.Should().Contain("ToolTip=\"次の月の履歴を表示\"");
    }
}
