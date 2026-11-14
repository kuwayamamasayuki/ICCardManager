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

        [Fact]
        public void 前面にできなかったときの文言は管理者権限の可能性にも触れること()
        {
            Action act = () => ScreenshotHelper.EnsureForegroundForCapture(() => false, () => "Switch USB（SwitchUsb.exe、クラス X）", "main.png");

            act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("管理者権限",
                "管理者権限のウィンドウが前面にあると撮り直しても直らないので、閉じるか最小化する必要があると伝える");
        }

        [Fact]
        public void 保存の直前に前面を奪われていたら_前面のウィンドウを名指しして例外にすること()
        {
            Action act = () => ScreenshotHelper.EnsureStillForeground(
                new IntPtr(100), new IntPtr(200), () => "通知（explorer.exe、クラス Windows.UI.Core.CoreWindow）", "history.png");

            var thrown = act.Should().Throw<InvalidOperationException>().Which;
            thrown.Message.Should().Contain("history.png")
                .And.Contain("Windows.UI.Core.CoreWindow")
                .And.Contain("灰色")
                .And.EndWith("撮影をやり直してください。");
        }

        [Fact]
        public void 保存の直前もまだ前面なら例外にしないこと()
        {
            var described = false;

            ScreenshotHelper.EnsureStillForeground(
                new IntPtr(100), new IntPtr(100), () => { described = true; return "使われない"; }, "history.png");

            described.Should().BeFalse();
        }

        [Fact]
        public void 前面にしない撮影では保存の直前に前面を確かめないこと()
        {
            // bringToFront: false（トースト単体）と切り分け用の撮影は IntPtr.Zero を渡す
            ScreenshotHelper.EnsureStillForeground(IntPtr.Zero, new IntPtr(200), () => "使われない", "toast_lend.png");
        }

        [Fact]
        public void 前面のウィンドウが無いときは_無いと示すこと()
        {
            ScreenshotHelper.DescribeWindow(IntPtr.Zero).Should().Be("前面のウィンドウなし",
                "ハンドル 0 をそのまま調べると「Idle.exe」と読めてしまう");
        }

        [Theory]
        [InlineData("history.png")]
        [InlineData("history_FAILED.png.bak")]
        [InlineData("")]
        public void 切り分け用の撮影は_FAILEDで終わらない名前を拒むこと(string fileName)
        {
            // 名前の検査はウィンドウより先に行うので、ウィンドウなしで検証できる
            Action act = () => ScreenshotHelper.CaptureForDiagnosis(null!, fileName);

            act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("fileName",
                "前面にできないまま撮った画像を、公開され得る正規の名前で残さない");
        }

        [Fact]
        public void 切り分け用の撮影は_FAILEDで終わる名前を受け付けること()
        {
            Action act = () => ScreenshotHelper.CaptureForDiagnosis(null!, "history_FAILED.png");

            act.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("window",
                "名前の検査を通り、ウィンドウの検査まで進む");
        }
    }
}
