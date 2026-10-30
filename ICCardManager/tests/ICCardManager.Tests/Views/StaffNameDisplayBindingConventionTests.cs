using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2332: 利用者の表示は「外N名」を導出した値（<c>DisplayStaffName</c>）へ束縛し、
/// 生の氏名（<c>StaffName</c>）を直接表示しないことを固定する規約テスト。
/// </summary>
/// <remarks>
/// <para>
/// 「外N名」は <c>ledger.companion_count</c> から表示時に導出する（Issue #1906、business-logic.md）。
/// 履歴一覧は <c>LedgerDto.DisplayStaffName</c> を表示していたが、利用履歴詳細のヘッダーと
/// バス停名未入力の一覧は生の <c>StaffName</c> を表示しており、同じ行が画面によって「外1名」付きと
/// 本人の名前だけとで食い違っていた。
/// </para>
/// <para>
/// 個別の ViewModel のテストは「導出した値を持つこと」しか表明できず、XAML が生の氏名へ束縛し直されても
/// 緑のまま通る。束縛の側を本番の全 XAML から検査する。
/// </para>
/// <para>
/// <b>検査できない形</b>: 2 段以上入れ子にしたマークアップ拡張を <c>Path=</c> の前に挟んだ束縛
/// （<c>{Binding Converter={x:Static … {…}}, Path=StaffName}</c>）と、コードビハインドで組み立てる束縛
/// （<c>new Binding("StaffName")</c>）は見ない。1 段の入れ子（<c>Converter={StaticResource …}</c>）と、単引用符の
/// <c>&lt;Binding Path='…'/&gt;</c> は拾う（サンプル入力で固定）。
/// </para>
/// </remarks>
public class StaffNameDisplayBindingConventionTests
{
    /// <summary>
    /// 束縛のパス。マークアップ拡張（<c>{Binding X}</c> / <c>{Binding Path=X}</c> / <c>{Binding Mode=…, Path=X}</c>）と
    /// 要素の形（<c>&lt;Binding Path="X"/&gt;</c>。<c>MultiBinding</c> の子を含む）。<c>[^}]</c> / <c>[^&gt;]</c> は改行にも一致するので、
    /// 複数行に分けて書いた束縛も拾う。
    /// </summary>
    private static readonly Regex[] BindingPathPatterns =
    {
        new(@"\{Binding\s+(?!Path\s*=)(?![A-Za-z]+\s*=)(?<path>[^,}\s]+)", RegexOptions.Compiled),
        // 1 段の入れ子のマークアップ拡張（Converter={StaticResource …}）を Path= の前に挟んだ形も拾う
        new(@"\{Binding\b(?:[^{}]|\{[^{}]*\})*?\bPath\s*=\s*(?<path>[^,}\s]+)", RegexOptions.Compiled),
        // 属性値は二重引用符・単引用符のどちらでもよい
        new(@"<Binding\b[^>]*?\bPath\s*=\s*(?<q>[""'])(?<path>(?:(?!\k<q>).)+)\k<q>", RegexOptions.Compiled),
    };

    private static readonly Regex XmlComment = new(@"<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void 本番のXAMLは生の氏名へ束縛しないこと()
    {
        var violations = ProductionSourceFiles.Xaml
            .SelectMany(f => FindRawStaffNameBindings(f.Text).Select(v => $"  {f.RelativePath}:{v.Line} {v.Path}"))
            .ToList();

        violations.Should().BeEmpty(
            "利用者の表示は「外N名」を導出した DisplayStaffName（StaffNameFormatter.Format）へ束縛してください。" +
            "生の StaffName を表示すると、同行者のいる行が履歴一覧と食い違います（Issue #1906 / #2332）。\n" +
            string.Join("\n", violations));
    }

    /// <summary>
    /// 束縛のパスの最後の区切りが <c>StaffName</c> のもの（<c>Ledger.StaffName</c> のようなドット付きのパスを含む）。
    /// </summary>
    private static IReadOnlyList<(int Line, string Path)> FindRawStaffNameBindings(string xaml)
    {
        // コメントは同じ長さの空白へ置き換え、行番号を保つ（設計意図を書いたコメントを違反にしない）
        var code = XmlComment.Replace(xaml, m => Regex.Replace(m.Value, @"[^\n]", " "));
        return BindingPathPatterns
            .SelectMany(p => p.Matches(code).Cast<Match>())
            .Select(m => (m.Groups["path"].Index, Path: m.Groups["path"].Value.Trim('"', '\'')))
            .Where(x => LastSegment(x.Path) == "StaffName")
            .Select(x => (Line: code.Take(x.Index).Count(c => c == '\n') + 1, x.Path))
            .Distinct()
            .ToList();
    }

    private static string LastSegment(string path) => path.Split('.').Last().Trim();

    /// <summary>
    /// 走査が空振りしていないこと（導出した値への束縛が実在すること）。
    /// </summary>
    [Fact]
    public void 走査対象に導出した氏名への束縛が実在すること()
    {
        ProductionSourceFiles.Xaml
            .Where(f => f.Text.Contains("{Binding DisplayStaffName}"))
            .Select(f => f.Name)
            .Should().Contain(new[] { "MainWindow.xaml", "LedgerDetailDialog.xaml", "IncompleteBusStopDialog.xaml" });
    }

    /// <summary>
    /// 検査ロジック自体を既知のサンプルで固定する（実データが違反ゼロでも空振り検出が働き続けるように）。
    /// </summary>
    [Theory]
    [InlineData("<TextBlock Text=\"{Binding StaffName}\"/>", true)]
    [InlineData("Binding=\"{Binding StaffName, Mode=OneWay}\"", true)]
    [InlineData("Binding=\"{Binding Path=StaffName}\"", true)]
    [InlineData("Binding=\"{Binding Mode=OneWay, Path=StaffName}\"", true)]
    [InlineData("<Binding Path=\"StaffName\" />", true)]
    // ドット付きのパス（最後の区切りが StaffName）
    [InlineData("<TextBlock Text=\"{Binding Ledger.StaffName}\"/>", true)]
    [InlineData("<TextBlock Text=\"{Binding Path=SelectedItem.StaffName}\"/>", true)]
    [InlineData("<MultiBinding><Binding Path=\"Item.StaffName\"/></MultiBinding>", true)]
    // 複数行に分けた束縛
    [InlineData("<TextBlock Text=\"{Binding\r\n    StaffName,\r\n    Mode=OneWay}\"/>", true)]
    [InlineData("<Binding\r\n    Mode=\"OneWay\"\r\n    Path=\"Ledger.StaffName\"/>", true)]
    // 適合
    [InlineData("<TextBlock Text=\"{Binding DisplayStaffName}\"/>", false)]
    [InlineData("<TextBlock Text=\"{Binding Ledger.DisplayStaffName}\"/>", false)]
    [InlineData("Text=\"{Binding DisplayStaffNamePreview, StringFormat=氏名欄: {0}}\"", false)]
    [InlineData("<Binding Path=\"SelectedStaff.Name\" />", false)]
    [InlineData("<TextBlock Text=\"{Binding StaffNameLabel}\"/>", false)]
    // 入れ子のマークアップ拡張を Path= の前に挟んだ形・単引用符の要素形
    [InlineData("<TextBlock Text=\"{Binding Converter={StaticResource X}, Path=Ledger.StaffName}\"/>", true)]
    [InlineData("<Binding Path='StaffName'/>", true)]
    [InlineData("<Binding Path='Item.DisplayStaffName'/>", false)]
    // コメントの中は対象外
    [InlineData("<!-- 以前は {Binding StaffName} だった -->", false)]
    public void 検査ロジックは生の氏名への束縛だけを拾うこと(string markup, bool expected)
    {
        FindRawStaffNameBindings(markup).Any().Should().Be(expected);
    }
}
