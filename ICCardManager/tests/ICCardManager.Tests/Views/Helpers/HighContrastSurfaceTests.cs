using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using FluentAssertions;
using ICCardManager.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views.Helpers;

/// <summary>
/// Issue #2142: ハイコントラスト（黒）で、白いカード状の面が白地に白文字になっていたのを固定する。
/// </summary>
/// <remarks>
/// <para>
/// メイン画面の使い方ガイド・履歴表示エリア・月選択ポップアップ・ページ番号と、帳票作成のカード一覧は
/// <c>Background="White"</c> の直書きで、中の文字は色を指定せずシステムの文字色を継承していた。
/// 面の色を <c>SurfaceBrush</c> へ寄せ、ハイコントラスト時は <see cref="HighContrastSurface"/> が
/// システムのウィンドウ背景色へ差し替える。
/// </para>
/// <para>
/// <b>検査できない範囲</b>: 実際にハイコントラストへ切り替えたときの描画は xUnit から確かめられない
/// （<c>SystemParameters.HighContrast</c> は OS の設定）。差し替えの判断は <see cref="HighContrastSurface.Apply"/> の
/// 挙動テストで、起動時の結線と画面側の参照は静的検査で固定する。実機での確認は PR の手動テスト手順に記す。
/// </para>
/// </remarks>
public class HighContrastSurfaceTests
{
    private static readonly SolidColorBrush White = Freeze(new SolidColorBrush(Colors.White));
    private static readonly SolidColorBrush SystemBlack = Freeze(new SolidColorBrush(Colors.Black));

    [Fact]
    public void ハイコントラスト時はシステムのウィンドウ背景色へ差し替えること()
    {
        var resources = CreateAppResources();

        HighContrastSurface.Apply(resources, highContrast: true, SystemBlack);

        resources[HighContrastSurface.SurfaceBrushKey].Should().BeSameAs(SystemBlack);
    }

    [Fact]
    public void 通常時はスタイル辞書の白のままであること()
    {
        // 対の表明: 常にシステム色へ差し替える実装を落とす
        var resources = CreateAppResources();

        HighContrastSurface.Apply(resources, highContrast: false, SystemBlack);

        resources[HighContrastSurface.SurfaceBrushKey].Should().BeSameAs(White);
    }

    [Fact]
    public void ハイコントラストを解除したら白へ戻ること()
    {
        // 実行中に設定を戻したとき、最上位の上書きを取り除かないと黒い面が残る
        var resources = CreateAppResources();
        HighContrastSurface.Apply(resources, highContrast: true, SystemBlack);

        HighContrastSurface.Apply(resources, highContrast: false, SystemBlack);

        resources[HighContrastSurface.SurfaceBrushKey].Should().BeSameAs(White);
        resources.MergedDictionaries[0][HighContrastSurface.SurfaceBrushKey].Should().BeSameAs(
            White, "通常時の白（スタイル辞書）そのものは書き換えないこと");
    }

    [Fact]
    public void スタイル辞書が白い面のブラシを定義していること()
    {
        AccessibilityBrushes.Load().Should().ContainKey(HighContrastSurface.SurfaceBrushKey)
            .WhoseValue.Should().Be("#FFFFFF");
    }

    [Fact]
    public void 起動時にハイコントラストへの追随を結線していること()
    {
        var code = TestSourceInspection.ToCodeOnly(File.ReadAllText(
            Path.Combine(TestPaths.GetProductionSourceRoot(), "App.xaml.cs")));

        TestSourceInspection.ExtractMethodBody(code, "protected override async void OnStartup(")
            .Should().Contain("HighContrastSurface.Register(this)",
                "起動時に 1 回、現在の設定を適用して以後の切り替えに追随すること");
    }

    [Fact]
    public void 白い面をStaticResourceで参照しないこと()
    {
        // StaticResource は読み込み時に値を固定するので、ハイコントラストへの差し替えが届かない
        var offenders = FillForegroundPairs.EnumerateProductionXaml()
            .Where(f => f.Text.Contains("{StaticResource " + HighContrastSurface.SurfaceBrushKey + "}"))
            .Select(f => f.Name)
            .ToList();

        offenders.Should().BeEmpty("SurfaceBrush は DynamicResource で参照すること");
    }

    [Fact]
    public void 白いカード状の面がSurfaceBrushを使っていること()
    {
        // 対の表明: 直書きの不在（ColorLiteralSingleSourceOfTruthTests）だけだと、面の背景を丸ごと外した
        // （＝地色が透ける）実装でも緑になる。Issue #2142 が名指しした 5 箇所が面の色を持っていることを見る
        var usage = FillForegroundPairs.EnumerateProductionXaml()
            .ToDictionary(
                f => f.Name,
                f => XamlElementInspection.EnumerateStartTags(f.Text)
                    .Count(t => XamlElementInspection.GetAttribute(t.StartTag, "Background")
                                == "{DynamicResource " + HighContrastSurface.SurfaceBrushKey + "}"));

        usage["MainWindow.xaml"].Should().Be(4, "使い方ガイド・履歴表示エリア・月選択ポップアップ・ページ番号");
        usage["ReportDialog.xaml"].Should().Be(1, "帳票作成のカード一覧");
    }

    private static ResourceDictionary CreateAppResources()
    {
        // アプリのリソース辞書の構造（AccessibilityStyles.xaml をマージした最上位の辞書）を模す
        var styles = new ResourceDictionary { [HighContrastSurface.SurfaceBrushKey] = White };
        var app = new ResourceDictionary();
        app.MergedDictionaries.Add(styles);
        return app;
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
