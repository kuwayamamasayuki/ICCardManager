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

    /// <summary>
    /// 透明にしただけではヒットテストが生きたままで、画面下部に見えないクリック領域が残る。
    /// そこを踏むと撮影中の画面に意図しない貸出・返却が記録される（コードレビューで検出）。
    /// UIA の Invoke はヒットテストを経ないので、撮影側は引き続きボタンを押せる。
    /// </summary>
    [Fact]
    public void MainWindow_仮想タッチパネルは撮影モードでヒットテストを止めている()
    {
        var panel = ExtractDebugPanel(ReadProductionFile("Views", "MainWindow.xaml"));

        panel.Should().MatchRegex(
            "IsHitTestVisible=\"\\{Binding\\s+Source=\\{x:Static\\s+app:App\\.IsDebugPanelInteractive\\}\\}\"",
            "透明なパネルが見えないクリック領域として残らないよう、ヒットテストも撮影モードで落とすこと");
    }

    /// <summary>
    /// 撮影モードは起動時のテストデータ登録を止めるため、DB に居るのは撮影側が投入した IDm だけ。
    /// 一方で仮想タッチダイアログのカード・職員は本体の <c>DebugDataService</c> のハードコード一覧から作られ、
    /// 撮影側はその<b>既定選択（先頭）</b>をそのまま使う。先頭が入れ替わると、撮影は
    /// 「カードがデータベースに登録されていません」のモーダルで原因不明のタイムアウトになる。
    /// </summary>
    /// <remarks>
    /// UITests は本体への ProjectReference を持たないため（型で結べない）、両側をソーステキストとして
    /// 突き合わせる。「先頭であること」が仕様なので、リストの並べ替えはこの検査を赤にする。
    /// </remarks>
    [Fact]
    public void 仮想タッチの既定選択は撮影側が投入するIDmと一致する()
    {
        // 文字列リテラルは検査対象そのものなので、コメントだけを剥がす（ToCodeOnly はリテラルごと消す）
        var debugData = TestSourceInspection.RemoveCommentsPreservingLines(
            ReadProductionFile("Services", "DebugDataService.cs"));

        var firstCardIdm = ExtractFirstIdm(debugData, "TestCardList", "CardIdm");
        var firstStaffIdm = ExtractFirstIdm(debugData, "TestStaffList", "StaffIdm");

        var seedData = TestSourceInspection.RemoveCommentsPreservingLines(ReadUiTestFile("ScreenshotSeedData.cs"));
        var appFixture = TestSourceInspection.RemoveCommentsPreservingLines(ReadUiTestFile("AppFixture.cs"));

        ExtractConst(seedData, "VirtualTouchCardIdm").Should().Be(firstCardIdm,
            "撮影側が投入する仮想タッチ用カードの IDm は DebugDataService.TestCardList の先頭と一致すること" +
            "（撮影モードでは他のテストカードが DB に存在しない）");
        ExtractConst(appFixture, "SeededStaffIdm").Should().Be(firstStaffIdm,
            "撮影側が投入する職員の IDm は DebugDataService.TestStaffList の先頭と一致すること");
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

    private static string ReadUiTestFile(string fileName)
    {
        var path = Path.Combine(
            TestPaths.GetSolutionRoot(), "tests", "ICCardManager.UITests", "Infrastructure", fileName);
        File.Exists(path).Should().BeTrue($"検査対象のファイルが見つからない: {path}");
        return File.ReadAllText(path);
    }

    /// <summary>配列初期化子の先頭要素から IDm を取り出す。</summary>
    private static string ExtractFirstIdm(string source, string listName, string idmProperty)
    {
        var list = Regex.Match(source, Regex.Escape(listName) + @"\s*=\s*\{(?<body>.*?)\};", RegexOptions.Singleline);
        list.Success.Should().BeTrue($"{listName} の配列初期化子が見つかること");
        var idm = Regex.Match(list.Groups["body"].Value, Regex.Escape(idmProperty) + @"\s*=\s*""(?<idm>[0-9A-Fa-f]+)""");
        idm.Success.Should().BeTrue($"{listName} の先頭要素に {idmProperty} があること");
        return idm.Groups["idm"].Value;
    }

    private static string ExtractConst(string source, string constName)
    {
        var match = Regex.Match(source, Regex.Escape(constName) + @"\s*=\s*""(?<value>[^""]*)""");
        match.Success.Should().BeTrue($"定数 {constName} が見つかること");
        return match.Groups["value"].Value;
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
