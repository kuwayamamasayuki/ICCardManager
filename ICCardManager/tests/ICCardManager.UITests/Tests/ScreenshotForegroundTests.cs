using System;
using System.Collections.Generic;
using FlaUI.Core.AutomationElements;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// 撮影の前・撮影中にウィンドウが前面に無ければ、画像を残さずに失敗することの検証（Issue #2478）。
    /// アプリは起動せず、前面化の結果・前面のウィンドウ・画像の削除を差し替えて、継ぎ目の挙動だけを検証する。
    /// GUI 不要のため Category=UI を付けず、CI でも実行される。
    /// </summary>
    /// <remarks>
    /// 前面にできないまま撮ると、タイトルバーが灰色（非アクティブ）の画像が正規の名前で保存され、テストは合格する
    /// （実測: タスクバー <c>Shell_TrayWnd</c> が前面を保持したまま、撮影した画像がすべて非アクティブになった）。
    /// 撮影の入口がこの継ぎ目を通ることは <c>ICCardManager.Tests</c> の <c>ScreenshotForegroundConventionTests</c> が見る。
    /// </remarks>
    public class ScreenshotForegroundTests
    {
        private const string AdminWindow = "Switch USB（SwitchUsb.exe、クラス WindowsForms10.Window）";

        [Fact]
        public void 撮影の前に前面にできなかったとき_前面のウィンドウを名指しして例外にし_画像を消すこと()
        {
            var deleted = new List<string>();
            var handleRead = false;

            Action act = () => ScreenshotHelper.EnsureForegroundForCapture(
                () => false, () => AdminWindow, () => { handleRead = true; return new IntPtr(100); }, deleted.Add, "staff.png");

            var thrown = act.Should().Throw<InvalidOperationException>().Which;
            thrown.Message.Should().Contain("staff.png", "何が: どの画像の撮影かを示す")
                .And.Contain("SwitchUsb.exe", "前面にあるウィンドウのプロセス名を示す")
                .And.Contain("WindowsForms10.Window", "前面にあるウィンドウのクラス名を示す")
                .And.Contain("灰色", "なぜ: 非アクティブの画像になることを示す")
                .And.Contain("管理者権限", "管理者権限のウィンドウならやり直しても直らないことを示す")
                .And.Contain("閉じるか最小化", "どうすれば: 前面のウィンドウをどけるよう案内する")
                .And.EndWith("撮影をやり直してください。", "どうすれば: 行動指示で終わる");
            deleted.Should().Equal(new[] { "staff.png" }, "撮れなかったのに正規の名前の画像（前回の撮影の残り）を残さない");
            handleRead.Should().BeFalse();
        }

        [Fact]
        public void 撮影の前に前面にできたとき_ハンドルを返し_画像を消さず前面のウィンドウも調べないこと()
        {
            var deleted = new List<string>();
            var described = false;

            var handle = ScreenshotHelper.EnsureForegroundForCapture(
                () => true, () => { described = true; return "使われない"; }, () => new IntPtr(100), deleted.Add, "staff.png");

            handle.Should().Be(new IntPtr(100), "前面にしたウィンドウを撮影中の確認へ渡す");
            deleted.Should().BeEmpty();
            described.Should().BeFalse();
        }

        [Fact]
        public void 撮影の前の前面化を1回だけ試みること()
        {
            // BringToForeground は内側で 3 回までやり直す。ここで重ねて呼ぶと、失敗時の待ちが何倍にも延びる
            var calls = 0;

            Action act = () => ScreenshotHelper.EnsureForegroundForCapture(
                () => { calls++; return false; }, () => AdminWindow, () => IntPtr.Zero, _ => { }, "main.png");

            act.Should().Throw<InvalidOperationException>();
            calls.Should().Be(1);
        }

        [Fact]
        public void 撮影中に前面を奪われたままなら_前面のウィンドウを名指しして例外にし_画像を消すこと()
        {
            var reads = 0;
            var deleted = new List<string>();

            Action act = () => ScreenshotHelper.EnsureStillForeground(
                new IntPtr(100), () => { reads++; return new IntPtr(200); },
                _ => "通知（explorer.exe、クラス Windows.UI.Core.CoreWindow）", "history.png", deleted.Add, pause: () => { });

            var thrown = act.Should().Throw<InvalidOperationException>().Which;
            thrown.Message.Should().Contain("history.png")
                .And.Contain("Windows.UI.Core.CoreWindow")
                .And.Contain("灰色")
                .And.EndWith("撮影をやり直してください。");
            reads.Should().Be(ScreenshotHelper.StillForegroundReads, "合わなければ上限まで読み直してから失敗する");
            deleted.Should().Equal(new[] { "history.png" }, "撮った画像（前回の撮り直しの分を含む）を正規の名前で残さない");
        }

        [Fact]
        public void 撮影中に前面が一瞬だけ外れても_読み直して戻っていれば例外にせず画像も消さないこと()
        {
            var sequence = new Queue<IntPtr>(new[] { IntPtr.Zero, new IntPtr(200), new IntPtr(100) });
            var deleted = new List<string>();

            ScreenshotHelper.EnsureStillForeground(
                new IntPtr(100), () => sequence.Dequeue(), _ => "使われない", "history.png", deleted.Add, pause: () => { });

            sequence.Should().BeEmpty("戻るまで読み直す");
            deleted.Should().BeEmpty();
        }

        [Fact]
        public void 前面にしない撮影では撮影中に前面を確かめないこと()
        {
            // bringToFront: false（トースト単体）と切り分け用の撮影は IntPtr.Zero を渡す
            var reads = 0;

            ScreenshotHelper.EnsureStillForeground(
                IntPtr.Zero, () => { reads++; return new IntPtr(200); }, _ => "使われない", "toast_lend.png", _ => { }, pause: () => { });

            reads.Should().Be(0);
        }

        [Fact]
        public void 入力の前に前面にできなかったとき_その他のウィンドウなら管理者権限と閉じるか最小化を案内すること()
        {
            Action act = () => ScreenshotHelper.EnsureForegroundForInput(() => false, () => AdminWindow);

            act.Should().Throw<InvalidOperationException>().Which.Message.Should()
                .Contain("SwitchUsb.exe").And.Contain("管理者権限").And.Contain("閉じるか最小化")
                .And.EndWith("テスト（撮影）をやり直してください。", "入力の前の確認は撮影でないテストでも使う");
        }

        [Theory]
        [InlineData("（explorer.exe、クラス Shell_TrayWnd）")]
        [InlineData(ScreenshotHelper.NoForegroundWindow)]
        public void タスクバーか前面のウィンドウが無いときは_できない操作を案内しないこと(string foreground)
        {
            // タスクバーは閉じる・最小化できず、前面のウィンドウが無いなら閉じる対象も無い。どちらもやり直せば直ることが多い
            Action before = () => ScreenshotHelper.EnsureForegroundForCapture(
                () => false, () => foreground, () => IntPtr.Zero, _ => { }, "staff.png");
            Action during = () => ScreenshotHelper.EnsureStillForeground(
                new IntPtr(100), () => new IntPtr(200), _ => foreground, "staff.png", _ => { }, pause: () => { });
            Action input = () => ScreenshotHelper.EnsureForegroundForInput(() => false, () => foreground);

            foreach (var act in new[] { before, during, input })
            {
                act.Should().Throw<InvalidOperationException>().Which.Message.Should()
                    .NotContain("閉じ").And.NotContain("最小化").And.NotContain("管理者権限")
                    .And.EndWith("やり直してください。");
            }
        }

        [Fact]
        public void 前面のウィンドウが無いときは_無いと示し文言が自己矛盾しないこと()
        {
            ScreenshotHelper.DescribeWindow(IntPtr.Zero).Should().Be(ScreenshotHelper.NoForegroundWindow,
                "ハンドル 0 をそのまま調べると「Idle.exe」と読めてしまう");

            Action before = () => ScreenshotHelper.EnsureForegroundForCapture(
                () => false, () => ScreenshotHelper.NoForegroundWindow, () => IntPtr.Zero, _ => { }, "main.png");
            Action during = () => ScreenshotHelper.EnsureStillForeground(
                new IntPtr(100), () => IntPtr.Zero, ScreenshotHelper.DescribeWindow, "main.png", _ => { }, pause: () => { });

            foreach (var act in new[] { before, during })
            {
                act.Should().Throw<InvalidOperationException>().Which.Message
                    .Should().NotContain("「" + ScreenshotHelper.NoForegroundWindow + "」",
                        "「前面にあるウィンドウ『前面のウィンドウなし』」のような文にしない");
            }
        }

        [Fact]
        public void 前面にする撮影は判定を通り_そのハンドルを撮影中の確認へ渡すこと()
        {
            var settled = false;

            var handle = ScreenshotHelper.PrepareForCapture(true, () => new IntPtr(100), () => settled = true);

            handle.Should().Be(new IntPtr(100));
            settled.Should().BeFalse("待ちは判定（前面化）の内側で行う");
        }

        [Fact]
        public void 前面にしない撮影は判定を通らず_待ってから撮影中の確認をしないこと()
        {
            var required = false;
            var settled = false;

            var handle = ScreenshotHelper.PrepareForCapture(
                false, () => { required = true; return new IntPtr(100); }, () => settled = true);

            required.Should().BeFalse();
            settled.Should().BeTrue("トーストのフェードインを待つ");
            handle.Should().Be(IntPtr.Zero);
        }

        [Fact]
        public void 撮影は既定で前面にすること()
        {
            // 引数 2 つの撮影（大半の呼び出し）は既定値で前面にする。既定値を false にすると、すべてが判定を通らずに撮る
            // （名前を nameof で書くと、静的検査のメソッド グループ検出が誤検出する。ScreenshotForegroundConventionTests の remarks）
            var parameter = typeof(ScreenshotHelper)
                .GetMethod("Capture", new[] { typeof(Window), typeof(string), typeof(bool) })!
                .GetParameters()[2];

            parameter.HasDefaultValue.Should().BeTrue();
            parameter.DefaultValue.Should().Be(true);
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
