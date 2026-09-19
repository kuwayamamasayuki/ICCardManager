using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views.Dialogs;

/// <summary>
/// Issue #2076: 縦に長いダイアログが 03_画面設計書 §5.6「入力フォーム型ダイアログの構造」に
/// 従っていること（内容は <c>ScrollViewer</c>、ボタン行は最下行に固定）を固定する。
/// </summary>
/// <remarks>
/// <para>
/// 文字サイズ「大」以上や 1366×768 では、内容が伸びた分だけボタン行が下へ押し出される。
/// ボタンが画面外に出ると押す手段が無く、Escape が効かない画面では操作不能になる。
/// 「ウィンドウの高さを抑える」だけでは足りず、**押し出される側（ボタン）を
/// スクロール領域の外に出す**ことが対で必要になる。
/// </para>
/// <para>
/// **回帰は「ボタンがスクロール領域の外にあること」と「内容がスクロール領域の中にあること」を
/// 対で置く**。前者だけだと <c>ScrollViewer</c> を丸ごと外して内容を固定配置に戻した実装でも
/// 緑になる（#1757「正当な操作を塞いでいないことを対で固定する」の裏返し）。
/// </para>
/// <para>
/// <c>SizeToContent="Height"</c> のダイアログ（カード登録方法・カード種別選択・職員証認証）も
/// 同じ対象になる。上限（<c>MaxHeight</c>）を付けた以上、そこで押し出される側はスクロール領域の
/// 外に置かなければならない — 上限だけを付けると「画面外へ出る」が「切れて見えない」に変わるだけで、
/// **押す手段が無いことは変わらない**（Issue #2076 のコードレビューで検出）。
/// </para>
/// <para>
/// 実描画（実際に何 px になるか）の確認には UI オートメーションが要るため、ここでは
/// XAML テキスト上の構造だけを検査する。文字サイズ「特大」での表示は手動検証する。
/// </para>
/// </remarks>
public class LowResolutionDialogLayoutTests
{
    /// <summary>
    /// 対象ダイアログで、ボタン行が <c>ScrollViewer</c> の外側にあること。
    /// </summary>
    [Theory]
    [InlineData("SystemManageDialog.xaml", "閉じる")]
    [InlineData("CardRegistrationModeDialog.xaml", "OK")]
    [InlineData("CardTypeSelectionDialog.xaml", "キャンセル")]
    [InlineData("StaffAuthDialog.xaml", "キャンセル")]
    public void ボタン行がスクロール領域の外にあること(string fileName, string buttonContent)
    {
        var xaml = XamlElementInspection.StripXmlComments(ReadDialog(fileName));

        var buttonsInsideScroll = XamlElementInspection.EnumerateElements(xaml, "ScrollViewer")
            .SelectMany(sv => XamlElementInspection.EnumerateElements(sv.Body, "Button"))
            .Select(b => XamlElementInspection.GetAttribute(b.StartTag, "Content"))
            .ToList();

        buttonsInsideScroll.Should().NotContain(buttonContent,
            $"Issue #2076: {fileName} の「{buttonContent}」がスクロール領域の中にあると、" +
            "文字サイズ「大」以上や 1366×768 で内容が伸びたときに画面外へ押し出され、押す手段が無くなる");
    }

    /// <summary>
    /// 対の表明: 内容のほうは <c>ScrollViewer</c> の中にあること。
    /// </summary>
    /// <remarks>
    /// これが無いと、<c>ScrollViewer</c> を丸ごと撤去した実装（＝内容もボタンも固定配置で、
    /// 直そうとしていた欠陥がそのまま残る）でも上のテストが緑になる。
    /// </remarks>
    [Theory]
    [InlineData("SystemManageDialog.xaml", "リストア（データ復元）")]
    [InlineData("CardRegistrationModeDialog.xaml", "紙の出納簿からの繰越")]
    [InlineData("CardTypeSelectionDialog.xaml", "未登録のカードです")]
    [InlineData("StaffAuthDialog.xaml", "StatusText")]
    public void 伸び得る内容がスクロール領域の中にあること(string fileName, string marker)
    {
        var xaml = XamlElementInspection.StripXmlComments(ReadDialog(fileName));

        var scrollBodies = XamlElementInspection.EnumerateElements(xaml, "ScrollViewer")
            .Select(sv => sv.Body)
            .ToList();

        scrollBodies.Should().NotBeEmpty($"{fileName} は §5.6 の入力フォーム型ダイアログである");
        // .NET Framework 4.8 には string.Contains(string, StringComparison) が無いため IndexOf で書く
        scrollBodies.Should().Contain(body => body.IndexOf(marker, StringComparison.Ordinal) >= 0,
            $"Issue #2076: 「{marker}」を含む領域は文字サイズに応じて縦に伸びるため、" +
            "スクロール領域の内側に置くこと");
    }

    /// <summary>
    /// カード種別選択のボタン行は <c>WrapPanel</c>（幅が足りなければ段を分ける）であること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 未登録カードをタッチした直後に出る画面で、幅 450 の中に最小幅 120/140/100 のボタンを
    /// 横並びに置いていたため、文字サイズ「大」以上で右端の「キャンセル」が切れていた。
    /// </para>
    /// <para>
    /// <c>UniformGrid</c> は全ボタンを同じ幅に揃えるため、最も長い「交通系ICカード」に合わせると
    /// 特大でまた溢れる（#1692 で同じ判断をしている）。タグで照合するので、
    /// この理由を書いたコメント自体が違反として検出されることはない（#1692 の「極性の反転」）。
    /// </para>
    /// </remarks>
    [Fact]
    public void カード種別選択のボタン行が折り返せるパネルであること()
    {
        var xaml = XamlElementInspection.StripXmlComments(ReadDialog("CardTypeSelectionDialog.xaml"));

        var wrapPanelButtons = XamlElementInspection.EnumerateElements(xaml, "WrapPanel")
            .SelectMany(p => XamlElementInspection.EnumerateElements(p.Body, "Button"))
            .Select(b => XamlElementInspection.GetAttribute(b.StartTag, "Content"))
            .ToList();

        wrapPanelButtons.Should().BeEquivalentTo(new[] { "職員証", "交通系ICカード", "キャンセル" },
            "Issue #2076: 3 つのボタンは幅が足りなければ段を分けられるパネルに置くこと" +
            "（固定幅の中の横並びでは「大」以上で右端が切れる）");

        xaml.Should().NotContain("<UniformGrid",
            "UniformGrid は全ボタンを同幅に揃えるため、最も長いラベルに合わせると特大でまた溢れる（#1692）");
    }

    private static string ReadDialog(string fileName)
        => File.ReadAllText(ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", fileName)));
}
