using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
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
/// <para>
/// Issue #2102: 検査は XML コメントを除いたうえで<b>対象の要素を 1 つに絞ってから</b>属性を見る。
/// 以前はファイル全体への正規表現で、コメントを除かず、属性の記述順（<c>Header</c> → <c>Binding</c>）にも依存していた
/// （<c>Binding</c> を先に書いただけで抽出に失敗し、<c>Setter</c> の属性順を入れ替えると Wrap を見落とした）。
/// </para>
/// </remarks>
public class OperationLogDialogColumnWidthLayoutTests
{
    private const string TargetXaml = "OperationLogDialog.xaml";

    private static readonly string DialogsDirectory = ViewSourceLocator.ResolveDirectory(Path.Combine("Views", "Dialogs"));

    /// <summary>
    /// 抽出の妥当性を先に固定する（対象 XAML を読めていない状態で他のテストが
    /// 空振りしたまま緑になるのを防ぐ）。
    /// </summary>
    [Fact]
    public void 対象XAMLにDataGrid列定義が存在すること()
    {
        var xaml = ReadDialog(TargetXaml);

        XamlElementInspection.EnumerateElements(xaml, "DataGrid.Columns").Should().NotBeEmpty(
            "抽出対象の XAML が想定と異なると、以降の検査がすべて空振りする");
        XamlElementInspection.EnumerateElementsIncludingNested(xaml, "DataGridTextColumn").Should().HaveCount(6,
            "操作ログ一覧は 日時／操作／対象／対象詳細／操作者／詳細 の6列構成である");
    }

    [Theory]
    [InlineData("操作種別")]
    [InlineData("対象テーブル")]
    public void 絞り込みコンボは固定Widthではなく_MinWidth_を使うこと(string automationName)
    {
        var comboBox = ExtractElement(ReadDialog(TargetXaml), "ComboBox", automationName);

        XamlElementInspection.GetAttribute(comboBox, "MinWidth").Should().MatchRegex(@"^\d+$",
            $"{automationName} コンボは MinWidth で内容と文字サイズに追随する必要がある（Issue #1787）");
        XamlElementInspection.GetAttribute(comboBox, "Width").Should().BeNull(
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

        ElementStyleSetterValue(column, "TextWrapping").Should().Be("Wrap",
            $"「{header}」列は TextWrapping=\"Wrap\" で文字サイズ「大」以上の文字切れを担保する必要がある" +
            "（幅を広げる対処は特大でまた破綻するため。Issue #1787）");
    }

    [Theory]
    [InlineData("操作", "ActionDisplay")]
    [InlineData("対象", "TargetTableDisplay")]
    public void 操作と対象の列は_Width_Auto_を使わないこと(string header, string binding)
    {
        var column = ExtractColumn(ReadDialog(TargetXaml), header, binding);

        XamlElementInspection.GetAttribute(column.StartTag, "Width").Should().NotBe("Auto",
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
        XamlElementInspection.GetAttribute(ExtractColumn(xaml, "対象詳細", "TargetDisplayName").StartTag, "Width")
            .Should().Be("*");
        XamlElementInspection.GetAttribute(ExtractColumn(xaml, "詳細", "DetailSummary").StartTag, "Width")
            .Should().Be("*");
    }

    [Theory]
    [InlineData("対象詳細", "TargetDisplayName")]
    [InlineData("詳細", "DetailSummary")]
    public void 星幅の列は_MinWidth_で最低限の可読幅を確保すること(string header, string binding)
    {
        var column = ExtractColumn(ReadDialog(TargetXaml), header, binding);

        var minWidth = XamlElementInspection.GetAttribute(column.StartTag, "MinWidth");
        int.TryParse(minWidth, out var value).Should().BeTrue(
            $"「{header}」列は MinWidth で最低限の可読幅を確保する必要がある（Issue #1787）");
        value.Should().BeGreaterThanOrEqualTo(150,
            $"「{header}」列が 150px を下回ると、摘要やファイル名が実質的に読めなくなる");
    }

    /// <summary>
    /// 列の抽出と Setter の読み取りが、コメント・属性の記述順に左右されないことを合成入力で固定する（Issue #2102）。
    /// </summary>
    [Theory]
    // Binding を Header より先に書いても見つかり、Setter の属性順が逆でも Wrap を読める
    [InlineData(@"<DataGrid.Columns><DataGridTextColumn Binding=""{Binding ActionDisplay}"" Header=""操作""><DataGridTextColumn.ElementStyle><Style><Setter Value=""Wrap"" Property=""TextWrapping""/></Style></DataGridTextColumn.ElementStyle></DataGridTextColumn></DataGrid.Columns>", "Wrap")]
    // コメントアウトした Setter は数えない
    [InlineData(@"<DataGrid.Columns><DataGridTextColumn Header=""操作"" Binding=""{Binding ActionDisplay}""><DataGridTextColumn.ElementStyle><Style><!-- <Setter Property=""TextWrapping"" Value=""Wrap""/> --></Style></DataGridTextColumn.ElementStyle></DataGridTextColumn></DataGrid.Columns>", null)]
    // トリガーの中だけの Setter は常には効かないので数えない
    [InlineData(@"<DataGrid.Columns><DataGridTextColumn Header=""操作"" Binding=""{Binding ActionDisplay}""><DataGridTextColumn.ElementStyle><Style><Style.Triggers><DataTrigger Binding=""{Binding Action}"" Value=""INSERT""><Setter Property=""TextWrapping"" Value=""Wrap""/></DataTrigger></Style.Triggers></Style></DataGridTextColumn.ElementStyle></DataGridTextColumn></DataGrid.Columns>", null)]
    // 次の列の Setter を拾わない（旧実装は自己終了タグの列から次の列の終了タグまでをまたいで一致し得た）
    [InlineData(@"<DataGrid.Columns><DataGridTextColumn Header=""操作"" Binding=""{Binding ActionDisplay}""/><DataGridTextColumn Header=""対象"" Binding=""{Binding TargetTableDisplay}""><DataGridTextColumn.ElementStyle><Style><Setter Property=""TextWrapping"" Value=""Wrap""/></Style></DataGridTextColumn.ElementStyle></DataGridTextColumn></DataGrid.Columns>", null)]
    public void 列の抽出と折り返しの読み取りが対象の列そのものを見ていること(string rawXaml, string? expected)
    {
        var column = ExtractColumn(XamlElementInspection.StripXmlComments(rawXaml), "操作", "ActionDisplay");

        ElementStyleSetterValue(column, "TextWrapping").Should().Be(expected, $"入力: {rawXaml}");
    }

    /// <summary>
    /// 指定ヘッダー・バインドを持つ DataGridTextColumn を 1 つに絞って返す（属性の記述順を問わない）。
    /// </summary>
    private static XamlElementInspection.XamlElementSpan ExtractColumn(string xaml, string header, string binding)
    {
        var columns = XamlElementInspection.EnumerateElementsIncludingNested(xaml, "DataGridTextColumn")
            .Where(c => XamlElementInspection.GetAttribute(c.StartTag, "Header") == header
                        && XamlElementInspection.GetBindingPropertyName(
                            XamlElementInspection.GetAttribute(c.StartTag, "Binding")) == binding)
            .ToList();

        columns.Should().ContainSingle(
            $"「{header}」列（Binding={binding}）の定義が XAML にちょうど 1 つ存在すべき。" +
            "列の Header / Binding を変更した場合は本テストの期待値も更新してください。");
        return columns[0];
    }

    /// <summary>
    /// 列の <c>ElementStyle</c> で<b>常に効く</b> Setter の値（<c>Style.Triggers</c> の中は数えない）。
    /// </summary>
    private static string? ElementStyleSetterValue(XamlElementInspection.XamlElementSpan column, string propertyName)
    {
        foreach (var style in XamlElementInspection.EnumerateElements(column.Body, "DataGridTextColumn.ElementStyle")
                     .SelectMany(e => XamlElementInspection.EnumerateElements(e.Body, "Style")))
        {
            var body = style.Body;
            foreach (var triggers in XamlElementInspection.EnumerateElementSpans(body, "Style.Triggers").Reverse().ToList())
            {
                body = body.Remove(triggers.Start, triggers.Length);
            }

            var setter = XamlElementInspection.EnumerateElements(body, "Setter")
                .FirstOrDefault(s => XamlElementInspection.IsSetterFor(
                    XamlElementInspection.GetAttribute(s.StartTag, "Property"), propertyName));
            if (setter != null)
            {
                return XamlElementInspection.GetAttribute(setter.StartTag, "Value");
            }
        }

        return null;
    }

    /// <summary>
    /// 指定 AutomationProperties.Name を持つ要素の開始タグを 1 つに絞って返す（属性の記述順を問わない）。
    /// </summary>
    private static string ExtractElement(string xaml, string elementName, string automationName)
    {
        var elements = XamlElementInspection.EnumerateElementsIncludingNested(xaml, elementName)
            .Where(e => XamlElementInspection.GetAttribute(e.StartTag, "AutomationProperties.Name") == automationName)
            .ToList();

        elements.Should().ContainSingle(
            $"AutomationProperties.Name=\"{automationName}\" の {elementName} が XAML にちょうど 1 つ存在すべき。");
        return elements[0].StartTag;
    }

    private static string ReadDialog(string fileName)
    {
        var path = Path.Combine(DialogsDirectory, fileName);
        File.Exists(path).Should().BeTrue($"{fileName} が {DialogsDirectory} に存在する必要があります");
        return XamlElementInspection.StripXmlComments(File.ReadAllText(path));
    }
}
