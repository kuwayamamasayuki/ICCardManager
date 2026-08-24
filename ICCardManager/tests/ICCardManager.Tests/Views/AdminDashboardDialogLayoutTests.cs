using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #1692: 管理者ダッシュボード画面のレイアウト規約を XAML テキスト上で静的に検証する。
/// </summary>
/// <remarks>
/// <para>
/// 文字サイズが 4 段階（小/中/大/特大）で変わるため、幅を固定する対処は特大でまた破綻する。
/// ここでは「折り返しを殺すレイアウト」「色値リテラル」「StaticResource の FontSize」といった
/// 再発しやすい違反が混入していないことを軽量に固定する。
/// </para>
/// <para>
/// 実描画（棒の重なり・軸ラベルの衝突・凡例の折り返し）は UI オートメーションが必要なため
/// 手動検証とする。
/// </para>
/// </remarks>
public class AdminDashboardDialogLayoutTests
{
    private static readonly string XamlPath = ResolveXamlPath();
    private static readonly string Xaml = File.ReadAllText(XamlPath);

    #region 折り返しと幅

    [Fact]
    public void Status_message_should_wrap()
    {
        // ボタン行と幅を分け合うため、折り返さないと隣のボタンの下へはみ出す（Issue #1688）
        Xaml.Should().MatchRegex(
            @"<TextBlock[^>]*Text\s*=\s*""\{Binding\s+StatusMessage\}""[^>]*TextWrapping\s*=\s*""Wrap""",
            "ステータス欄は長文でも折り返して全文表示すべき");
    }

    [Fact]
    public void Summary_tiles_should_not_use_horizontal_stack_panel_inside()
    {
        var tiles = ExtractBlock(@"<WrapPanel\s+Grid\.Row=""0"".*?</WrapPanel>");

        tiles.Should().NotMatchRegex(
            @"<StackPanel\b[^>]*Orientation\s*=\s*""Horizontal""",
            "横方向 StackPanel は子を無限幅で測定するため TextWrapping が機能しなくなる（Issue #1687）。" +
            "タイル内部は DockPanel / Grid を使うこと");
    }

    [Fact]
    public void Summary_tiles_should_use_wrap_panel_not_uniform_grid()
    {
        // UniformGrid は列幅が均等になるため、特大文字でタイルの中身が切れる。
        // 検査は要素タグに限定する（設計意図を書いたコメントを違反として誤検出しないため）
        Xaml.Should().Contain("<WrapPanel");
        Xaml.Should().NotContain("<UniformGrid");
    }

    [Fact]
    public void Legend_should_use_wrap_panel()
    {
        var legend = ExtractBlock(@"<ItemsControl\s+Grid\.Row=""2""\s+ItemsSource\s*=\s*""\{Binding\s+UsageLegend\}"".*?</ItemsControl>");

        legend.Should().Contain("<WrapPanel", "凡例は系列名が長いと横一列に収まらない");
    }

    [Fact]
    public void Button_row_should_be_vertically_centered()
    {
        // ステータスが 2 行に折り返してもボタンが縦に引き伸ばされないようにする
        Xaml.Should().MatchRegex(
            @"<StackPanel\s+Grid\.Column=""1""\s+Orientation=""Horizontal""\s+VerticalAlignment=""Center""");
    }

    #endregion

    #region 色とフォント

    [Fact]
    public void Should_not_contain_color_literals()
    {
        // 色値の Single Source of Truth は AccessibilityStyles.xaml のブラシキー（Issue #1392、#1461）
        Xaml.Should().NotMatchRegex(
            @"(Background|Foreground|Fill|Stroke|BorderBrush)\s*=\s*""#[0-9A-Fa-f]{3,8}""",
            "色値リテラルではなく DynamicResource のブラシキーを参照すること");
    }

    [Fact]
    public void Chart_colors_should_be_resolved_from_resource_keys()
    {
        // 系列色は ViewModel からキー名で渡し、コンバーター経由でブラシへ解決する
        Xaml.Should().MatchRegex(
            @"Fill\s*=\s*""\{Binding\s+BrushKey,\s*Converter=\{StaticResource\s+ResourceKeyToBrushConverter\}\}""");
        Xaml.Should().MatchRegex(
            @"Stroke\s*=\s*""\{Binding\s+BrushKey,\s*Converter=\{StaticResource\s+ResourceKeyToBrushConverter\}\}""");
    }

    [Fact]
    public void Stacked_usage_bars_should_have_separator_stroke()
    {
        // 色は唯一の手掛かりであってはならない（Issue #1274 の 4 要素原則）。
        // 積み上げ棒は区画どうしが接するため、隣接する系列の相対輝度が近いと
        // グレースケール印刷・ロービジョン・色覚多様性で 1 本の帯に見える（Issue #1855）
        var bars = ExtractBlock(@"<ItemsControl\s+ItemsSource\s*=\s*""\{Binding\s+UsageBars\}"".*?</ItemsControl>");

        bars.Should().MatchRegex(
            @"<Rectangle[^>]*Stroke\s*=\s*""\{DynamicResource\s+ChartSeriesOutlineBrush\}""",
            "積み上げ棒の区画には区切り線が要る");
        bars.Should().MatchRegex(
            @"<Rectangle[^>]*StrokeThickness\s*=\s*""1""",
            "区切り線は太さを指定しないと描画されない");
    }

    [Fact]
    public void Legend_swatches_should_have_outline()
    {
        // 白背景に対するコントラストが低い系列色（Okabe-Ito の橙は 2.25:1）でも
        // スウォッチの矩形そのものが見えるよう、輪郭線を引く（Issue #1855）
        var legend = ExtractBlock(@"<ItemsControl\s+Grid\.Row=""2""\s+ItemsSource\s*=\s*""\{Binding\s+UsageLegend\}"".*?</ItemsControl>");

        legend.Should().MatchRegex(
            @"<Rectangle[^>]*Stroke\s*=\s*""\{DynamicResource\s+\w+Brush\}""",
            "凡例スウォッチには輪郭線が要る");

        // ここで見るのは「輪郭線が引かれているか」という構造だけ。どのブラシなら輪郭として
        // 機能するかは色値でしか判定できないため、XAML から実際に使われているキーを取り出して
        // 解決する ChartSeriesPaletteTests が対で担う（ブラシ名の許可リストで書くと、
        // 名前を変えただけで検査が素通りする）
    }

    [Fact]
    public void Font_sizes_should_use_dynamic_resource()
    {
        // 文字サイズ変更（App.ApplyFontSize）はリソースを差し替えるため StaticResource では追随しない
        Xaml.Should().NotMatchRegex(
            @"FontSize\s*=\s*""\{StaticResource",
            "FontSize は DynamicResource で参照すること");
        Xaml.Should().NotMatchRegex(
            @"FontSize\s*=\s*""\d",
            "FontSize に数値を直接指定しないこと");
    }

    #endregion

    #region アクセシビリティ

    [Theory]
    [InlineData("カード別の稼働率グラフ")]
    [InlineData("職員別の月次利用額グラフ")]
    [InlineData("カード別の残高推移グラフ")]
    public void Charts_should_expose_automation_name(string expectedName)
    {
        // Canvas 上の図形はスクリーンリーダーから読めないため、領域に名前を与える
        Xaml.Should().Contain($@"AutomationProperties.Name=""{expectedName}""");
    }

    [Fact]
    public void Utilization_chart_should_be_accompanied_by_a_data_grid()
    {
        // 色・図形だけに依存しないよう、グラフと同じ内容を一覧でも提供する
        Xaml.Should().MatchRegex(
            @"<DataGrid[^>]*ItemsSource\s*=\s*""\{Binding\s+Analytics\.Utilizations\}""",
            "稼働率グラフと同じ内容を読み取れる一覧を併置すること");
    }

    [Fact]
    public void Status_message_should_be_announced_politely()
    {
        Xaml.Should().MatchRegex(
            @"<TextBlock[^>]*Text\s*=\s*""\{Binding\s+StatusMessage\}""[^>]*AutomationProperties\.LiveSetting\s*=\s*""Polite""",
            "集計完了やエラーをスクリーンリーダーへ通知するため");
    }

    [Fact]
    public void Close_button_should_be_cancel()
    {
        Xaml.Should().MatchRegex(
            @"<Button\s+Content=""閉じる""(.|\n)*?IsCancel\s*=\s*""True""",
            "Escape キーで閉じられること");
    }

    #endregion

    // 用語ガード（「ICカード」単独表記の禁止）は本クラスに置かない。
    // UserFacingTextConventionTests が Views/ 配下の *.xaml を再帰的に走査しており、
    // 本画面も自動で対象になる。ここに簡易版を重ねると許容複合語の定義が 2 か所に分かれ、
    // 片方だけ更新されたときに検査が食い違う。

    private static string ExtractBlock(string pattern)
    {
        var match = Regex.Match(Xaml, pattern, RegexOptions.Singleline);
        match.Success.Should().BeTrue($"AdminDashboardDialog.xaml に該当ブロックが存在すべき: {pattern}");
        return match.Value;
    }

    private static string ResolveXamlPath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(
                current.FullName, "src", "ICCardManager", "Views", "Dialogs", "AdminDashboardDialog.xaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            $"AdminDashboardDialog.xaml を {AppContext.BaseDirectory} の親階層から解決できませんでした");
    }
}
