using System;
using System.Collections.Generic;
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
                () => false, () => "Switch USB（SwitchUsb.exe、クラス WindowsForms10.Window）", "staff.png");

            var thrown = act.Should().Throw<InvalidOperationException>().Which;
            thrown.Message.Should().Contain("staff.png", "何が: どの画像の撮影かを示す")
                .And.Contain("SwitchUsb.exe", "前面にあるウィンドウのプロセス名を示す")
                .And.Contain("WindowsForms10.Window", "前面にあるウィンドウのクラス名を示す")
                .And.Contain("灰色", "なぜ: 非アクティブの画像になることを示す")
                .And.Contain("管理者権限", "管理者権限のウィンドウなら撮り直しても直らないことを示す")
                .And.Contain("閉じるか最小化", "どうすれば: 前面のウィンドウをどけるよう案内する")
                .And.EndWith("撮影をやり直してください。", "どうすれば: 行動指示で終わる");
        }

        [Theory]
        [InlineData("（explorer.exe、クラス Shell_TrayWnd）")]
        [InlineData(ScreenshotHelper.NoForegroundWindow)]
        public void タスクバーか前面のウィンドウが無いときは_できない操作を案内しないこと(string foreground)
        {
            // タスクバーは閉じる・最小化できず、前面のウィンドウが無いなら閉じる対象も無い。どちらも撮り直せば直ることが多い
            Action before = () => ScreenshotHelper.EnsureForegroundForCapture(() => false, () => foreground, "staff.png");
            Action during = () => ScreenshotHelper.EnsureStillForeground(
                new IntPtr(100), () => new IntPtr(200), _ => foreground, "staff.png", pause: () => { });

            foreach (var act in new[] { before, during })
            {
                act.Should().Throw<InvalidOperationException>().Which.Message.Should()
                    .NotContain("閉じ").And.NotContain("最小化").And.NotContain("管理者権限")
                    .And.EndWith("撮影をやり直してください。");
            }
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
        public void 撮影中に前面を奪われたままなら_前面のウィンドウを名指しして例外にすること()
        {
            var reads = 0;

            Action act = () => ScreenshotHelper.EnsureStillForeground(
                new IntPtr(100), () => { reads++; return new IntPtr(200); },
                _ => "通知（explorer.exe、クラス Windows.UI.Core.CoreWindow）", "history.png", pause: () => { });

            var thrown = act.Should().Throw<InvalidOperationException>().Which;
            thrown.Message.Should().Contain("history.png")
                .And.Contain("Windows.UI.Core.CoreWindow")
                .And.Contain("灰色")
                .And.EndWith("撮影をやり直してください。");
            reads.Should().Be(ScreenshotHelper.StillForegroundReads, "合わなければ上限まで読み直してから失敗する");
        }

        [Fact]
        public void 前面が一瞬だけ外れても_読み直して戻っていれば例外にしないこと()
        {
            var sequence = new Queue<IntPtr>(new[] { IntPtr.Zero, new IntPtr(200), new IntPtr(100) });

            ScreenshotHelper.EnsureStillForeground(
                new IntPtr(100), () => sequence.Dequeue(), _ => "使われない", "history.png", pause: () => { });

            sequence.Should().BeEmpty("戻るまで読み直す");
        }

        [Fact]
        public void 前面にしない撮影では前面を確かめないこと()
        {
            // bringToFront: false（トースト単体）と切り分け用の撮影は IntPtr.Zero を渡す
            var reads = 0;

            ScreenshotHelper.EnsureStillForeground(
                IntPtr.Zero, () => { reads++; return new IntPtr(200); }, _ => "使われない", "toast_lend.png", pause: () => { });

            reads.Should().Be(0);
        }

        [Fact]
        public void 前面のウィンドウが無いときは_無いと示し文言が自己矛盾しないこと()
        {
            ScreenshotHelper.DescribeWindow(IntPtr.Zero).Should().Be(ScreenshotHelper.NoForegroundWindow,
                "ハンドル 0 をそのまま調べると「Idle.exe」と読めてしまう");

            Action before = () => ScreenshotHelper.EnsureForegroundForCapture(
                () => false, () => ScreenshotHelper.NoForegroundWindow, "main.png");
            Action during = () => ScreenshotHelper.EnsureStillForeground(
                new IntPtr(100), () => IntPtr.Zero, ScreenshotHelper.DescribeWindow, "main.png", pause: () => { });

            foreach (var act in new[] { before, during })
            {
                act.Should().Throw<InvalidOperationException>().Which.Message
                    .Should().NotContain("「" + ScreenshotHelper.NoForegroundWindow + "」",
                        "「前面にあるウィンドウ『前面のウィンドウなし』」のような文にしない");
            }
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
