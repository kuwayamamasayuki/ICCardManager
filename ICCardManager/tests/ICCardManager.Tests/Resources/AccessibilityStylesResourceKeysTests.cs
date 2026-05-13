using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Resources;

/// <summary>
/// AccessibilityStyles.xaml に必須リソースキーが定義されていることを保証する（Issue #1461）。
/// </summary>
/// <remarks>
/// Issue #1461 では SSOT 違反のカラーリテラル直書きを解消するにあたり、新規ブラシキー
/// （<c>HintForegroundBrush</c>）を導入した。本テストはそのキーが誤って削除・改名された場合に
/// 即座に失敗するセーフティネット。
///
/// XAML パーサーでロードする代わりに、ファイル本文を文字列で検証する軽量実装を採用している。
/// 理由: <c>XamlReader.Load</c> は WPF アセンブリの完全な初期化を要求し、xUnit のテストランナー
/// （MTA スレッド）では `SystemColors` 関連の参照解決で失敗する場合がある。SSOT のキー存在を
/// 保証するという目的に対しては、テキスト検証で十分かつ堅牢。
/// </remarks>
public class AccessibilityStylesResourceKeysTests
{
    private static string ReadAccessibilityStylesXaml([CallerFilePath] string thisFilePath = "")
    {
        // tests/ICCardManager.Tests/Resources/AccessibilityStylesResourceKeysTests.cs
        // → src/ICCardManager/Resources/Styles/AccessibilityStyles.xaml
        var testsDir = Path.GetDirectoryName(thisFilePath)!;
        var repoRoot = Path.GetFullPath(Path.Combine(testsDir, "..", "..", ".."));
        var xamlPath = Path.Combine(repoRoot, "src", "ICCardManager", "Resources", "Styles", "AccessibilityStyles.xaml");
        File.Exists(xamlPath).Should().BeTrue($"AccessibilityStyles.xaml が見つからない: {xamlPath}");
        return File.ReadAllText(xamlPath);
    }

    [Theory]
    [InlineData("LendingBackgroundBrush")]
    [InlineData("ReturnBackgroundBrush")]
    [InlineData("ErrorBackgroundBrush")]
    [InlineData("LendingForegroundBrush")]
    [InlineData("ReturnForegroundBrush")]
    [InlineData("ErrorForegroundBrush")]
    [InlineData("LendingBorderBrush")]
    [InlineData("ReturnBorderBrush")]
    [InlineData("ErrorBorderBrush")]
    [InlineData("WaitingForegroundBrush")]
    [InlineData("HintForegroundBrush")] // Issue #1461 で新規追加
    public void AccessibilityStyles_必須ブラシキーが定義されていること(string brushKey)
    {
        var xaml = ReadAccessibilityStylesXaml();

        // x:Key="..." の形でキー宣言が存在することを検証
        xaml.Should().Contain($"x:Key=\"{brushKey}\"",
            because: $"{brushKey} は AccessibilityStyles.xaml で SSOT として定義されているべき（Issue #1461）");
    }

    [Fact]
    public void HintForegroundBrush_Brown700の色値で定義されていること()
    {
        // Issue #1461: マテリアル Brown 700 (#795548) で定義。コントラスト比 7.5:1 を確保。
        var xaml = ReadAccessibilityStylesXaml();
        xaml.Should().MatchRegex("x:Key=\"HintForegroundBrush\"\\s+Color=\"#795548\"",
            because: "ヒント色は色覚多様性に配慮した茶系（Brown 700）で固定されているべき");
    }
}
