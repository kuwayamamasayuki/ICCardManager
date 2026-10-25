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
/// </remarks>
public class StaffNameDisplayBindingConventionTests
{
    /// <summary>
    /// 生の氏名（<c>StaffName</c>）への束縛。<c>DisplayStaffName</c> / <c>DisplayStaffNamePreview</c> など、
    /// 別の識別子の一部には一致しない。
    /// </summary>
    private static readonly Regex RawStaffNameBinding = new Regex(
        @"\{Binding\s+(?:Path\s*=\s*)?StaffName\s*[,}]|\bPath\s*=\s*""?StaffName""?(?![\w.])",
        RegexOptions.Compiled);

    [Fact]
    public void 本番のXAMLは生の氏名へ束縛しないこと()
    {
        var violations = ProductionSourceFiles.Xaml
            .SelectMany(f => f.Text.Split('\n')
                .Select((line, i) => (File: f.RelativePath, Line: i + 1, Text: line))
                .Where(x => RawStaffNameBinding.IsMatch(x.Text)))
            .Select(x => $"  {x.File}:{x.Line} {x.Text.Trim()}")
            .ToList();

        violations.Should().BeEmpty(
            "利用者の表示は「外N名」を導出した DisplayStaffName（StaffNameFormatter.Format）へ束縛してください。" +
            "生の StaffName を表示すると、同行者のいる行が履歴一覧と食い違います（Issue #1906 / #2332）。\n" +
            string.Join("\n", violations));
    }

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
    [InlineData("<Binding Path=\"StaffName\" />", true)]
    [InlineData("<TextBlock Text=\"{Binding DisplayStaffName}\"/>", false)]
    [InlineData("Text=\"{Binding DisplayStaffNamePreview, StringFormat=氏名欄: {0}}\"", false)]
    [InlineData("<Binding Path=\"SelectedStaff.Name\" />", false)]
    public void 検査ロジックは生の氏名への束縛だけを拾うこと(string markup, bool expected)
    {
        RawStaffNameBinding.IsMatch(markup).Should().Be(expected);
    }
}
