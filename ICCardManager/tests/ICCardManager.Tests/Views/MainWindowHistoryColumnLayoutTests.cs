using System;
using System.IO;
using System.Linq;
using System.Threading;
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

        // Issue #2102: 常に効く値だけを見る。DataTrigger 内の Setter（空の行で ToolTip を外す {x:Null}）を
        // 拾うと、無条件の ToolTip を消しても「ToolTip がある」と判定してしまう
        var unconditional = UnconditionalStyleBody(column!);

        XamlElementInspection.GetSetterValue(unconditional, "TextTrimming")
            .Should().Be("CharacterEllipsis",
                $"Issue #2076: 「{header}」列は幅に収まらない値を黙って捨てず、" +
                "省略記号で切れていることを示すこと");

        // 期待値（セルのバインド名）が読めないまま比べると、ToolTip 側も null のとき null == null で合格する。
        // 先に読めていることを表明する（Issue #2102 のコードレビュー。Binding をプロパティ要素形へ書き換え、
        // ToolTip の Setter を消しても緑だった）
        var cellBinding = GetColumnBindingPropertyName(column!);
        cellBinding.Should().NotBeNull($"「{header}」列のセルのバインド先を読み取れること（比較の空振りを防ぐ）");

        XamlElementInspection.GetBindingPropertyName(XamlElementInspection.GetSetterValue(unconditional, "ToolTip"))
            .Should().Be(cellBinding,
                $"Issue #2076: 切り詰めた「{header}」の全文を読む手段（セルと同じ値の ToolTip）を対で用意すること");
    }

    /// <summary>
    /// 列のバインド名の読み取りが、属性形とプロパティ要素形の両方を受けることを合成入力で固定する（Issue #2102）。
    /// </summary>
    [Theory]
    [InlineData(@"<DataGridTextColumn Header=""利用者"" Binding=""{Binding DisplayStaffName}""/>", "DisplayStaffName")]
    [InlineData(@"<DataGridTextColumn Header=""利用者""><DataGridTextColumn.Binding><Binding Path=""DisplayStaffName""/></DataGridTextColumn.Binding></DataGridTextColumn>", "DisplayStaffName")]
    [InlineData(@"<DataGridTextColumn Header=""利用者""><DataGridTextColumn.Binding><Binding Path=""Ledger.DisplayStaffName"" Mode=""OneWay""/></DataGridTextColumn.Binding></DataGridTextColumn>", "DisplayStaffName")]
    [InlineData(@"<DataGridTextColumn Header=""利用者""/>", null)]
    public void 列のバインド名は属性形とプロパティ要素形の両方から読むこと(string xaml, string? expected)
    {
        var column = XamlElementInspection.EnumerateElements(xaml, "DataGridTextColumn").Single();

        GetColumnBindingPropertyName(column).Should().Be(expected, $"入力: {xaml}");
    }

    /// <summary>
    /// 列のセルのバインド名。<c>Binding="{Binding …}"</c> 属性と、
    /// <c>&lt;DataGridTextColumn.Binding&gt;&lt;Binding Path="…"/&gt;</c> のプロパティ要素形の両方を読む。
    /// </summary>
    private static string? GetColumnBindingPropertyName(XamlElementInspection.XamlElement column)
    {
        var attribute = XamlElementInspection.GetAttribute(column.StartTag, "Binding");
        if (attribute != null)
        {
            return XamlElementInspection.GetBindingPropertyName(attribute);
        }

        var path = XamlElementInspection.EnumerateElements(column.Body, "DataGridTextColumn.Binding")
            .SelectMany(p => XamlElementInspection.EnumerateElements(p.Body, "Binding"))
            .Select(b => XamlElementInspection.GetAttribute(b.StartTag, "Path"))
            .FirstOrDefault(p => !string.IsNullOrEmpty(p));

        return path == null ? null : XamlElementInspection.GetBindingPropertyName($"{{Binding {path}}}");
    }

    /// <summary>
    /// 列の <c>ElementStyle</c> の本体から <c>Style.Triggers</c> を取り除いたもの（常に効く Setter だけが残る）。
    /// </summary>
    private static string UnconditionalStyleBody(XamlElementInspection.XamlElement column)
    {
        var styles = XamlElementInspection.EnumerateElements(column.Body, "DataGridTextColumn.ElementStyle")
            .SelectMany(e => XamlElementInspection.EnumerateElements(e.Body, "Style"))
            .ToList();
        styles.Should().ContainSingle("列の ElementStyle がちょうど 1 つ存在すること");

        var body = styles[0].Body;
        foreach (var triggers in XamlElementInspection.EnumerateElementSpans(body, "Style.Triggers").Reverse().ToList())
        {
            body = body.Remove(triggers.Start, triggers.Length);
        }

        return body;
    }

    /// <summary>
    /// 値が空の行では、セルの <c>ToolTip</c> を外すこと。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #2076 のコードレビューで検出。害は 2 つある。①空文字を <c>ToolTip</c> に渡すと
    /// 中身の無いツールチップが出る ②<c>ToolTipService</c> は**最も近い祖先**の <c>ToolTip</c> を使うため、
    /// セルに置いた <c>ToolTip</c> が <c>DataGridRow</c> の <c>ToolTip</c> をこのセルの上でだけ**覆う** —
    /// 残高不整合行（#1052）の説明や「今回の返却で記録された行」（#1907）が、
    /// 空のポップアップに置き換わる。
    /// </para>
    /// <para>
    /// **両列とも空になり得る**。備考は任意入力で、利用者は導入行（新規購入・○月から繰越・
    /// 前年度より繰越）で空になる — <c>BuildInitialLedgerAsync</c> が <c>StaffName = null</c> /
    /// <c>CompanionCount = 0</c> で書き、<c>StaffNameFormatter.Format</c> が空文字を返すため。
    /// そして導入行こそ残高不整合の起点になりやすい（#2007）。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("利用者")]
    [InlineData("備考")]
    public void 値が空の行ではセルのToolTipを外すこと(string header)
    {
        var body = XamlElementInspection.StripXmlComments(FindColumn(header)!.Body);

        var clearedValues = XamlElementInspection.EnumerateElements(body, "DataTrigger")
            .Select(t => new
            {
                Value = XamlElementInspection.GetAttribute(t.StartTag, "Value"),
                ToolTip = XamlElementInspection.GetSetterValue(t.Body, "ToolTip"),
            })
            .Where(t => t.ToolTip == "{x:Null}")
            .Select(t => t.Value)
            .ToList();

        clearedValues.Should().BeEquivalentTo(new[] { string.Empty, "{x:Null}" },
            $"Issue #2076: 「{header}」が空文字／null の行では ToolTip を外すこと。" +
            "残すと中身の無いツールチップが出るうえ、行レベルの ToolTip（#1052 の残高不整合の説明・" +
            "#1907 の「今回の返却で記録された行」）をこのセルの上でだけ覆う");
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

    /// <summary>
    /// コメントを除いた MainWindow.xaml。Theory の各ケースで読み直さず、クラスで 1 回だけ読む（Issue #2108）。
    /// </summary>
    private static readonly Lazy<string> MainWindowXaml = new Lazy<string>(
        () => XamlElementInspection.StripXmlComments(
            File.ReadAllText(ViewSourceLocator.Resolve(Path.Combine("Views", "MainWindow.xaml")))),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static XamlElementInspection.XamlElement? FindColumn(string header)
        => XamlElementInspection.EnumerateElements(MainWindowXaml.Value, "DataGridTextColumn")
            .FirstOrDefault(c => XamlElementInspection.GetAttribute(c.StartTag, "Header") == header);
}
