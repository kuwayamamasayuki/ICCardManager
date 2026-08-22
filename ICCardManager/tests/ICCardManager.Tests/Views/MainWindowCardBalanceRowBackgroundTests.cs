using System;
using System.IO;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #1822: カード残高一覧の行に、恒久的に効かない <c>Background</c> の Style Setter と
/// <c>IsBalanceWarning</c> の DataTrigger が残っていた（死にコード）回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// WPF の依存関係プロパティ値優先順位では、要素に直接書いたローカル値が Style Setter や
/// Style 内の DataTrigger より強い。この <c>Border</c> は
/// <c>Background="{Binding RowBackgroundResourceKey, Converter=...}"</c> をローカル値で持つため、
/// Style 側の Background 指定は一度も適用されない。
/// </para>
/// <para>
/// 実害は無い（残額警告の色は Issue #1461 の <c>RowBackgroundResourceKey</c> 経由で正しく出る）が、
/// 「通常行の背景を変えたいのに Style を直しても効かない」という罠になる。
/// </para>
/// <para>
/// 「禁止された形の不在」だけを検査すると、色の決定経路ごと消えた実装でも緑になるため、
/// 実際の色決定（ローカル値のバインド）と Background を触らない <c>IsMouseOver</c> トリガーが
/// 生きていることも対で表明する。
/// </para>
/// </remarks>
public class MainWindowCardBalanceRowBackgroundTests
{
    private static string ExtractRowBorderStyle()
    {
        var xaml = File.ReadAllText(ViewSourceLocator.Resolve(Path.Combine("Views", "MainWindow.xaml")));

        const string marker = "RowBackgroundResourceKey";
        var bindingIndex = xaml.IndexOf(marker, StringComparison.Ordinal);
        bindingIndex.Should().BeGreaterThan(
            0, "カード残高一覧の行背景バインドが見つからない（抽出の起点が失われている）");

        var styleStart = xaml.IndexOf("<Border.Style>", bindingIndex, StringComparison.Ordinal);
        styleStart.Should().BeGreaterThan(0, "行 Border の Style ブロックが見つからない");

        var styleEnd = xaml.IndexOf("</Border.Style>", styleStart, StringComparison.Ordinal);
        styleEnd.Should().BeGreaterThan(styleStart, "行 Border の Style ブロックが閉じていない");

        var style = xaml.Substring(styleStart, styleEnd - styleStart);

        // 抽出範囲の妥当性: 別のブロックを掴んでいないこと（空振り防止）
        style.Should().Contain("IsMouseOver", "抽出した範囲がカード残高一覧の行 Style であること");
        return style;
    }

    [Fact]
    public void 行スタイルに効かないBackground指定が無いこと()
    {
        var style = ExtractRowBorderStyle();

        style.Should().NotContain(
            "Property=\"Background\"",
            "行 Border は Background をローカル値で持つため、Style 側の Background 指定は" +
            "依存関係プロパティ値優先順位により恒久的に効かない（Issue #1822）");

        style.Should().NotContain(
            "IsBalanceWarning",
            "残額警告の背景色は CardBalanceDashboardItem.RowBackgroundResourceKey が決める（Issue #1461）。" +
            "Style 側の DataTrigger は効かないため置かないこと（Issue #1822）");
    }

    [Fact]
    public void 行の背景色がRowBackgroundResourceKey経由で決まること()
    {
        var xaml = File.ReadAllText(ViewSourceLocator.Resolve(Path.Combine("Views", "MainWindow.xaml")));

        xaml.Should().MatchRegex(
            @"Background\s*=\s*""\{Binding\s+RowBackgroundResourceKey,\s*Converter\s*=\s*\{StaticResource\s+ResourceKeyToBrushConverter\}\}""",
            "行の背景色はリソースキー → ブラシ解決（Issue #1461 の色値 SSOT）で決めること");
    }

    [Fact]
    public void Backgroundを触らないマウスオーバートリガーは残っていること()
    {
        var style = ExtractRowBorderStyle();

        style.Should().MatchRegex(
            @"<Trigger\s+Property\s*=\s*""IsMouseOver""\s+Value\s*=\s*""True"">",
            "IsMouseOver トリガーは BorderBrush / BorderThickness のみを設定するため有効。" +
            "死にコード除去の巻き添えで消していないこと（Issue #1822）");
    }
}
