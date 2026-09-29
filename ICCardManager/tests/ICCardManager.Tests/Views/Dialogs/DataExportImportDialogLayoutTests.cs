using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views.Dialogs;

/// <summary>
/// Issue #2147: データ入出力ダイアログで、プレビューを表示していない間はインポート設定が
/// 残りの高さをすべて使えること（空のプレビュー領域が高さを確保しないこと）を固定する。
/// </summary>
/// <remarks>
/// <para>
/// ルート <c>Grid</c> はインポート設定の行を <c>*</c>、プレビュー結果の行を <c>2*</c> で分け合っていた。
/// プレビュー結果は <c>HasPreview</c> で <c>Collapsed</c> になるが、<b>行の高さの比率は中身の表示状態と
/// 無関係に効く</b>ため、プレビュー前（＝インポート設定を操作している最中）も残りの高さの 2/3 が
/// 何も描かれない空白として確保され、インポート設定は 1/3 のスクロール領域へ押し込まれていた。
/// ウィンドウを縦に広げても広がった分の 2/3 は空白へ回る。
/// </para>
/// <para>
/// <b>回帰は「非表示時は高さを取らない」と「表示時は伸縮する高さを取る」を対で置く</b>。
/// 前者だけだと、プレビュー行を常に <c>Auto</c> にした実装でも緑になる — その場合 <c>DataGrid</c> は
/// 無限の高さで測定されて全件ぶん縦に伸び、閉じるボタンを画面外へ押し出す（#2076）。
/// あわせて「行を切り替える条件」と「中身を表示する条件」が同じプロパティであることを表明する。
/// 別々の条件にすると、中身が見えているのに高さが 0、あるいは空白が確保される組み合わせが残る。
/// </para>
/// <para>
/// 実描画の高さ（何 px になるか）は UI オートメーションが要るため、XAML テキスト上の構造だけを検査する。
/// </para>
/// </remarks>
public class DataExportImportDialogLayoutTests
{
    /// <summary>インポート設定の中にだけ現れる文言（行の特定に使う）。</summary>
    private const string ImportSectionMarker = "CSVファイルからデータを取り込みます";

    /// <summary>プレビュー結果の一覧にだけ現れるバインディング（行の特定に使う）。</summary>
    private const string PreviewListMarker = "{Binding PreviewItems}";

    [Fact]
    public void プレビュー結果の行はプレビューを表示していない間は高さを確保しないこと()
    {
        var layout = LoadLayout();
        var previewRow = layout.RowDefinitions[layout.RowOf(PreviewListMarker)];

        XamlElementInspection.GetUnconditionalPropertyValue(previewRow, "Height").Should().Be("Auto",
            "Issue #2147: プレビュー結果が Collapsed の間も比率（2* 等）で高さを確保すると、" +
            "その分が空白になってインポート設定がスクロールしなければ見えなくなる");
    }

    [Fact]
    public void プレビュー結果の行はプレビューを表示している間は伸縮する高さを取ること()
    {
        var layout = LoadLayout();
        var previewRow = layout.RowDefinitions[layout.RowOf(PreviewListMarker)];

        var triggers = HeightTriggers(previewRow);

        triggers.Should().ContainSingle(
            "プレビュー結果の行は、プレビューの表示中だけ高さを切り替えること（対の表明: 常に Auto だと " +
            "DataGrid が全件ぶん縦に伸びて閉じるボタンを画面外へ押し出す。#2076）");
        triggers[0].Height.Should().EndWith("*", "表示中は残りの高さを分け合う（一覧自身がスクロールする）こと");

        var previewVisibility = layout.VisibilityBindingEnclosing(PreviewListMarker);
        triggers[0].Property.Should().NotBeNull().And.Be(previewVisibility,
            "行の高さを切り替える条件と、プレビュー結果を表示する条件は同じプロパティであること" +
            "（食い違うと「見えているのに高さ 0」か「空白の確保」が残る）");
    }

    /// <summary>
    /// プレビュー結果の行は高さをローカル値（開始タグの属性・プロパティ要素）で持たないこと。
    /// </summary>
    /// <remarks>
    /// WPF ではローカル値が Style の Setter とトリガーより優先される。<c>&lt;RowDefinition Height="Auto"&gt;</c> と
    /// 書くと、トリガーを残したままでも行は常に <c>Auto</c> になり、プレビュー表示中に <c>DataGrid</c> が
    /// 全件ぶん伸びて閉じるボタンを押し出す（#2076）。上の 2 件は Style とトリガーの記述しか見ないため、
    /// この形では緑のまま通る（コードレビューで検出）。
    /// </remarks>
    [Fact]
    public void プレビュー結果の行は高さをローカル値で持たないこと()
    {
        var layout = LoadLayout();
        var previewRow = layout.RowDefinitions[layout.RowOf(PreviewListMarker)];

        XamlElementInspection.GetAttribute(previewRow.StartTag, "Height").Should().BeNull(
            "Issue #2147: ローカル値はトリガーより優先されるため、行の高さがプレビューの表示状態に追随しなくなる");
        XamlElementInspection.EnumerateElements(previewRow.Body, "RowDefinition.Height").Should().BeEmpty(
            "プロパティ要素形（<RowDefinition.Height>）もローカル値であり、同じくトリガーを無効にする");
    }

    [Fact]
    public void 無条件に伸縮する行はインポート設定の行だけであること()
    {
        var layout = LoadLayout();
        var importRow = layout.RowOf(ImportSectionMarker);

        var starRows = layout.RowDefinitions
            .Select((row, index) => new
            {
                Index = index,
                Height = XamlElementInspection.GetUnconditionalPropertyValue(row, "Height") ?? "*",
            })
            .Where(r => r.Height.EndsWith("*", StringComparison.Ordinal))
            .Select(r => r.Index)
            .ToList();

        starRows.Should().Equal(new[] { importRow },
            "Issue #2147: プレビュー前に余った高さはすべてインポート設定へ回すこと" +
            "（他の行が比率で高さを取ると、その分だけインポート設定がまた狭くなる）");
    }

    /// <summary>
    /// 検査ロジック自体の固定: 修正前の形（<c>*</c> と <c>2*</c> の常時分割）を与えると赤になること。
    /// </summary>
    /// <remarks>
    /// 行の特定（祖先を辿って <c>Grid.Row</c> を読む）が空振りすると、上の 3 件は
    /// 「対象の行が見つからないので何も検査しない」形で緑になり得る。既知のサンプル入力で
    /// 検出できることを固定しておけば、実ファイルの構造が変わっても空振りに気付ける（#1786）。
    /// </remarks>
    [Fact]
    public void 修正前の形は常時分割として検出されること()
    {
        const string before = @"<Window>
    <Grid Margin=""20"">
        <Grid.RowDefinitions>
            <RowDefinition Height=""*""/>
            <RowDefinition Height=""2*"" MinHeight=""0""/>
        </Grid.RowDefinitions>
        <ScrollViewer Grid.Row=""0""><TextBlock Text=""CSVファイルからデータを取り込みます""/></ScrollViewer>
        <Grid Grid.Row=""1"">
            <Grid Visibility=""{Binding HasPreview, Converter={StaticResource B}}"">
                <DataGrid ItemsSource=""{Binding PreviewItems}""/>
            </Grid>
        </Grid>
    </Grid>
</Window>";
        var layout = new DialogLayout(before);

        layout.RowOf(ImportSectionMarker).Should().Be(0);
        layout.RowOf(PreviewListMarker).Should().Be(1);
        XamlElementInspection.GetUnconditionalPropertyValue(layout.RowDefinitions[1], "Height")
            .Should().Be("2*", "修正前の形では、プレビュー結果の行が常に比率で高さを確保している");
        layout.VisibilityBindingEnclosing(PreviewListMarker).Should().Be("HasPreview");
    }

    /// <summary>
    /// 行定義の <c>DataTrigger</c>（<c>Value="True"</c>）のうち高さを設定するものを、条件のプロパティ名と値の組で返す。
    /// </summary>
    /// <remarks>
    /// Setter の <c>Property</c> は所有者付きの表記（<c>RowDefinition.Height</c>）も同じ指定として照合する
    /// （<see cref="XamlElementInspection.IsSetterFor"/>。書き方の違いで赤くなる誤検出を避ける。#1786）。
    /// </remarks>
    private static List<(string? Property, string Height)> HeightTriggers(XamlElementInspection.XamlElementSpan row)
        => XamlElementInspection.EnumerateElements(row.Body, "DataTrigger")
            .Where(t => XamlElementInspection.GetAttribute(t.StartTag, "Value") == "True")
            .SelectMany(t => XamlElementInspection.EnumerateElements(t.Body, "Setter")
                .Where(s => XamlElementInspection.IsSetterFor(XamlElementInspection.GetAttribute(s.StartTag, "Property"), "Height"))
                .Select(s => XamlElementInspection.GetAttribute(s.StartTag, "Value"))
                .Where(v => v != null)
                .Select(v => (
                    Property: XamlElementInspection.GetBindingPropertyName(XamlElementInspection.GetAttribute(t.StartTag, "Binding")),
                    Height: v!)))
            .ToList();

    private static DialogLayout LoadLayout()
        => new(File.ReadAllText(ViewSourceLocator.Resolve(
            Path.Combine("Views", "Dialogs", "DataExportImportDialog.xaml"))));

    /// <summary>ルート <c>Grid</c> の行定義と、各要素がどの行に置かれているかを引く。</summary>
    private sealed class DialogLayout
    {
        private readonly string _xaml;

        public DialogLayout(string xaml)
        {
            _xaml = XamlElementInspection.StripXmlComments(xaml);
            // 子孫の Grid（プレビュー結果の内側の Grid 等）も Grid.RowDefinitions を持つため、
            // 取り除いてからルート自身の行定義を 1 つに絞る
            var rootBody = RootGrid.Body;
            foreach (var nested in XamlElementInspection.EnumerateElementSpans(rootBody, "Grid").Reverse().ToList())
            {
                rootBody = rootBody.Remove(nested.Start, nested.Length);
            }

            RowDefinitions = XamlElementInspection.EnumerateElementSpans(
                    XamlElementInspection.EnumerateElements(rootBody, "Grid.RowDefinitions").Single().Body,
                    "RowDefinition")
                .ToList();
            RowDefinitions.Should().NotBeEmpty("ルート Grid は行定義を持つ");
        }

        public IReadOnlyList<XamlElementInspection.XamlElementSpan> RowDefinitions { get; }

        /// <summary>Window 直下の Grid。</summary>
        private XamlElementInspection.XamlElementSpan RootGrid
            => XamlElementInspection.EnumerateElementSpans(RootBody, "Grid").First();

        private string RootBody
        {
            get
            {
                var rootTag = XamlElementInspection.GetRootStartTag(_xaml)!;
                var start = _xaml.IndexOf(rootTag, StringComparison.Ordinal);
                return XamlElementInspection.ElementStartingAt(_xaml, start)!.Body;
            }
        }

        /// <summary>
        /// <paramref name="marker"/> を含む、ルート Grid の直接の子が置かれている行番号。
        /// </summary>
        public int RowOf(string marker)
        {
            var child = RootChildEnclosing(marker);
            var row = XamlElementInspection.GetAttribute(child.StartTag, "Grid.Row");
            return row == null ? 0 : int.Parse(row);
        }

        /// <summary>
        /// <paramref name="marker"/> を囲む要素のうち、<c>Visibility</c> をバインドしている最も内側のもののプロパティ名。
        /// </summary>
        public string? VisibilityBindingEnclosing(string marker)
            => Enclosing(marker)
                .Select(e => XamlElementInspection.GetBindingPropertyName(
                    XamlElementInspection.GetAttribute(e.StartTag, "Visibility")))
                .LastOrDefault(p => p != null);

        private XamlElementInspection.XamlElementSpan RootChildEnclosing(string marker)
        {
            var enclosing = Enclosing(marker);
            var rootGridIndex = enclosing.FindIndex(e => e.StartTag.StartsWith("<Grid", StringComparison.Ordinal));
            rootGridIndex.Should().BeGreaterOrEqualTo(0, $"「{marker}」はルート Grid の内側にあること");
            enclosing.Count.Should().BeGreaterThan(rootGridIndex + 1, $"「{marker}」はルート Grid の子要素の内側にあること");
            return enclosing[rootGridIndex + 1];
        }

        private List<XamlElementInspection.XamlElementSpan> Enclosing(string marker)
        {
            var index = _xaml.IndexOf(marker, StringComparison.Ordinal);
            index.Should().BeGreaterOrEqualTo(0, $"「{marker}」が XAML に存在すること（行の特定に使う目印）");
            return XamlElementInspection.EnumerateEnclosingElements(_xaml, index).ToList();
        }
    }
}
