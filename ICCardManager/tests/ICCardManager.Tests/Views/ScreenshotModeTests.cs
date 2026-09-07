using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// スクリーンショット撮影モード（Issue #2019。DEBUG ビルド限定）の検査。
/// 環境変数 <see cref="App.ScreenshotModeEnvironmentVariable"/> が <c>1</c> のとき、
/// ①メイン画面の仮想タッチ操作パネルを透明にし、②起動時のテストデータ自動登録を行わない。
/// </summary>
/// <remarks>
/// <para>
/// パネルは Visibility ではなく Opacity を変える。Visibility を Collapsed にすると UIA ツリーからも消え、
/// 撮影側が仮想タッチのボタンを押せなくなるため。
/// </para>
/// <para>
/// 値の解決（<see cref="App.ResolveScreenshotMode"/>）は純粋関数として固定し、XAML 側と起動処理側は
/// 「その値を実際に参照している」ことをソーステキスト上で表明する（#1817 / #1794 と同じ形）。
/// 後者が無いと、プロパティを残したまま参照を外した実装でも前者は緑のままになる。
/// </para>
/// </remarks>
public class ScreenshotModeTests
{
    [Fact]
    public void ResolveScreenshotMode_環境変数が1なら撮影モード()
    {
        App.ResolveScreenshotMode("1").Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("１")]
    public void ResolveScreenshotMode_1以外はすべて通常モード(string? value)
    {
        // 「1」以外を寛容に解釈すると、意図せずパネルが消えたまま気付けない（Debug の操作手段が失われる）
        App.ResolveScreenshotMode(value!).Should().BeFalse();
    }

    [Fact]
    public void MainWindow_仮想タッチパネルはOpacityをDebugPanelOpacityへバインドしている()
    {
        var panel = ExtractDebugPanel(ReadProductionFile("Views", "MainWindow.xaml"));

        panel.Should().Contain("x:Static app:App.IsDebugBuild",
            "パネルの表示自体は従来どおり DEBUG ビルド判定（#289）で制御すること");
        panel.Should().MatchRegex(
            "Opacity=\"\\{Binding\\s+Source=\\{x:Static\\s+app:App\\.DebugPanelOpacity\\}\\}\"",
            "撮影時にパネルを見えなくする切替は Opacity のバインドで行うこと（Issue #2019）");
    }

    [Fact]
    public void App_起動時のテストデータ登録は撮影モードでは行わない()
    {
        // コメントと文字列リテラルを剥がす（XML doc に書いた説明文が一致する極性の反転を避ける。#1692）。
        // 呼び出しは #if DEBUG の内側なので RemoveDebugOnlyRegions は使わない（対象ごと消える）
        var source = TestSourceInspection.ToCodeOnly(ReadProductionFile("App.xaml.cs"));

        // 呼び出しは 1 か所で、その直前の条件が撮影モードの否定であること
        var calls = Regex.Matches(source, "await\\s+RegisterTestDataAsync\\(\\)\\s*;");
        calls.Count.Should().Be(1, "テストデータ登録の呼び出しは起動処理の 1 か所だけであること");
        var before = source.Substring(0, calls[0].Index);
        var guard = Regex.Match(before, "if\\s*\\(\\s*!\\s*IsScreenshotMode\\s*\\)\\s*\\{\\s*$", RegexOptions.Singleline);
        guard.Success.Should().BeTrue(
            "RegisterTestDataAsync の呼び出しは if (!IsScreenshotMode) の内側にあること（撮影側が投入したデータだけを見せる）");
    }

    private static string ReadProductionFile(params string[] relativeParts)
    {
        var path = Path.Combine(TestPaths.GetProductionSourceRoot(), Path.Combine(relativeParts));
        File.Exists(path).Should().BeTrue($"検査対象のファイルが見つからない: {path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// XAML コメントを取り除いてから、DEBUG 判定で表示制御している StackPanel の開始タグを切り出す。
    /// 規約の理由を書いたコメント自体が一致する極性の反転を避ける（#1692 / #1818）
    /// </summary>
    private static string ExtractDebugPanel(string xaml)
    {
        xaml = Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);
        var match = Regex.Match(
            xaml,
            "<StackPanel[^>]*x:Static\\s+app:App\\.IsDebugBuild[^>]*>",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue("DEBUG ビルド判定で表示する仮想タッチ操作パネル（StackPanel）がメイン画面に存在すること");
        return match.Value;
    }
}
