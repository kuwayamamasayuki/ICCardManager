using System.Windows;
using FluentAssertions;
using ICCardManager.Tests.Infrastructure;
using ICCardManager.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views.Helpers;

/// <summary>
/// Issue #2258: ウィンドウが載っているモニターの作業領域（<see cref="MonitorWorkArea"/>）。
/// </summary>
/// <remarks>
/// どのモニターに載るかは実機の画面構成に依存するので、ここでは「取れないときは null を返し、呼び出し元が
/// 位置を変えない」側だけを固定する。寄せる計算そのものは <c>WindowLayoutCalculator.ClampStart</c> の
/// 単体テスト（サブモニターの負の座標を含む）が受け持つ。
/// </remarks>
[Collection(StaThreadCollection.Name)]
public class MonitorWorkAreaTests
{
    [Fact]
    public void Of_表示前でハンドルの無いウィンドウではnullを返すこと()
    {
        StaTestRunner.Run(() =>
        {
            var window = new Window();

            MonitorWorkArea.Of(window).Should().BeNull("ハンドルが無ければモニターを決められないので、位置を変えない");
        });
    }
}
