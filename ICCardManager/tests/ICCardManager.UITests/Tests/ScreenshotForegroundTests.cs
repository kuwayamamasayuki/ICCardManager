using System;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// 撮影の前にウィンドウを前面にできなかったとき、画像を保存せずに失敗することの検証（Issue #2478）。
    /// アプリは起動せず、前面化の結果と前面のウィンドウの説明を差し替えて判定だけを検証する。
    /// GUI 不要のため Category=UI を付けず、CI でも実行される。
    /// </summary>
    /// <remarks>
    /// 前面にできないまま撮ると、タイトルバーが灰色（非アクティブ）の画像が正規の名前で保存され、テストは合格する
    /// （実測: タスクバー <c>Shell_TrayWnd</c> が前面を保持したまま、撮影した画像がすべて非アクティブになった）。
    /// 撮影の経路がこの判定を通ることは <c>ICCardManager.Tests</c> の <c>ScreenshotForegroundConventionTests</c> が固定する。
    /// </remarks>
    public class ScreenshotForegroundTests
    {
        [Fact]
        public void 前面にできなかったとき_前面のウィンドウを名指しして例外にすること()
        {
            Action act = () => ScreenshotHelper.EnsureForegroundForCapture(
                () => false, () => "（explorer.exe、クラス Shell_TrayWnd）", "staff.png");

            var thrown = act.Should().Throw<InvalidOperationException>().Which;
            thrown.Message.Should().Contain("staff.png", "何が: どの画像の撮影かを示す")
                .And.Contain("explorer.exe", "前面にあるウィンドウのプロセス名を示す")
                .And.Contain("Shell_TrayWnd", "前面にあるウィンドウのクラス名を示す（タイトルの無いタスクバーを見分ける）")
                .And.Contain("灰色", "なぜ: 非アクティブの画像になることを示す")
                .And.EndWith("撮影をやり直してください。", "どうすれば: 行動指示で終わる");
        }

        [Fact]
        public void 前面にできたとき_例外にせず前面のウィンドウも調べないこと()
        {
            var described = false;

            ScreenshotHelper.EnsureForegroundForCapture(
                () => true, () => { described = true; return "使われない"; }, "staff.png");

            described.Should().BeFalse("前面にできたなら、失敗の文言を組み立てる必要は無い");
        }

        [Fact]
        public void 前面化を1回だけ試みること()
        {
            // BringToForeground は内側で 3 回までやり直す。ここで重ねて呼ぶと、失敗時の待ちが何倍にも延びる
            var calls = 0;

            Action act = () => ScreenshotHelper.EnsureForegroundForCapture(
                () => { calls++; return false; }, () => "前面のウィンドウ", "main.png");

            act.Should().Throw<InvalidOperationException>();
            calls.Should().Be(1);
        }
    }
}
