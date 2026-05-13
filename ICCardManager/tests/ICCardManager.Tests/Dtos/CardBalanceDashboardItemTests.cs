using FluentAssertions;
using ICCardManager.Dtos;
using Xunit;

namespace ICCardManager.Tests.Dtos;

/// <summary>
/// <see cref="CardBalanceDashboardItem"/> の表示用プロパティテスト。
/// Issue #1461: カラーリテラル直書きから AccessibilityStyles の SSOT へ。
/// </summary>
public class CardBalanceDashboardItemTests
{
    [Fact]
    public void RowBackgroundResourceKey_残高警告ありの場合エラーブラシキーを返すこと()
    {
        var item = new CardBalanceDashboardItem { IsBalanceWarning = true };

        item.RowBackgroundResourceKey.Should().Be("ErrorBackgroundBrush",
            because: "残高警告時は AccessibilityStyles.xaml の ErrorBackgroundBrush を SSOT として参照すべき");
    }

    [Fact]
    public void RowBackgroundResourceKey_残高警告なしの場合Transparentを返すこと()
    {
        var item = new CardBalanceDashboardItem { IsBalanceWarning = false };

        item.RowBackgroundResourceKey.Should().Be("Transparent",
            because: "通常時はカード行の背景を透過させ、親 ListView の背景が見えるようにする");
    }

    [Fact]
    public void RowBackgroundResourceKey_カラーリテラル直書きを返さないこと()
    {
        var warning = new CardBalanceDashboardItem { IsBalanceWarning = true };
        var normal = new CardBalanceDashboardItem { IsBalanceWarning = false };

        warning.RowBackgroundResourceKey.Should().NotStartWith("#",
            because: "Issue #1461: カラーリテラル（#FFEBEE 等）を返してはならず、SSOT のリソースキー名を返す");
        normal.RowBackgroundResourceKey.Should().NotStartWith("#");
    }
}
