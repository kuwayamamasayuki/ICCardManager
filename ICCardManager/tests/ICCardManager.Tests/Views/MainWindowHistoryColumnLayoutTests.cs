using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2076: 履歴一覧の自由入力列（利用者・備考）が、幅に収まらない値を隠さないことを固定する。
/// </summary>
/// <remarks>
/// <para>
/// 「博多 花子 外１名」（#1906 の同行者表記）は文字サイズ「中」でも約 126px あり、
/// 幅 80 固定の利用者列では「外１名」が入らない。<c>DataGrid</c> のセルは既定で
/// 切り詰めも省略記号も行わないため、**入らなかった文字は黙って消える** ―
/// 切れていること自体が見て分からないので、職員は 1 人で利用したと読む。
/// </para>
/// <para>
/// 文字サイズは 4 段階で変わるため幅の調整だけでは特大でまた破綻する
/// （ui-conventions.md「長文の可能性があるテキストは『幅』ではなく『折り返し』で担保する」#1687）。
/// 行の高さが揃う一覧では折り返しを使えないので、代わりに
/// <c>TextTrimming</c>（切れていることが見て分かる）と <c>ToolTip</c>（全文を読める）を対で置く。
/// </para>
/// <para>
/// **対の表明**（幅を広げただけの実装を許さない）として、両方の属性が揃っていることを求める。
/// <c>ToolTip</c> だけだとマウス操作が前提になり、<c>TextTrimming</c> だけだと全文を読む手段が無い。
/// </para>
/// </remarks>
public class MainWindowHistoryColumnLayoutTests
{
    [Theory]
    [InlineData("利用者")]
    [InlineData("備考")]
    public void 自由入力列は切り詰めと全文表示の手段を持つこと(string header)
    {
        var column = FindColumn(header);

        column.Should().NotBeNull($"履歴一覧に「{header}」列が存在すること（走査が空振りしていない）");

        XamlElementInspection.GetSetterValue(column!.Body, "TextTrimming")
            .Should().Be("CharacterEllipsis",
                $"Issue #2076: 「{header}」列は幅に収まらない値を黙って捨てず、" +
                "省略記号で切れていることを示すこと");

        XamlElementInspection.GetSetterValue(column.Body, "ToolTip")
            .Should().NotBeNullOrEmpty(
                $"Issue #2076: 切り詰めた「{header}」の全文を読む手段（ToolTip）を対で用意すること");
    }

    /// <summary>
    /// 切り詰めを前提にしても、既定幅は通常の値が収まる程度に確保しておくこと。
    /// </summary>
    /// <remarks>
    /// 切り詰めと ToolTip は「収まらなかったとき」の手当てであって、
    /// 既定で毎行が切れてよい理由にはならない。是正前の 80px は文字サイズ「中」でも
    /// 同行者表記が入らなかったため、下限をテストで固定する。
    /// </remarks>
    [Theory]
    [InlineData("利用者", 120)]
    [InlineData("備考", 100)]
    public void 自由入力列の既定幅が是正前の値へ戻っていないこと(string header, int minimumWidth)
    {
        var width = XamlElementInspection.GetAttribute(FindColumn(header)!.StartTag, "Width");

        int.Parse(width!).Should().BeGreaterThanOrEqualTo(minimumWidth,
            $"Issue #2076: 「{header}」列の既定幅 80 では、文字サイズ「中」でも" +
            "「博多 花子 外１名」（#1906）が収まらなかった");
    }

    private static XamlElementInspection.XamlElement? FindColumn(string header)
    {
        var xaml = XamlElementInspection.StripXmlComments(
            File.ReadAllText(ViewSourceLocator.Resolve(Path.Combine("Views", "MainWindow.xaml"))));

        return XamlElementInspection.EnumerateElements(xaml, "DataGridTextColumn")
            .FirstOrDefault(c => XamlElementInspection.GetAttribute(c.StartTag, "Header") == header);
    }
}
