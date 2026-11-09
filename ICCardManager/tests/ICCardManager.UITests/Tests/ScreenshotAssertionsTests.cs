using System;
using System.IO;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// 撮影の後の確認で更新の案内が見つかったとき、撮った画像を正規の名前で残さないことの検証（Issue #2394）。
    /// アプリは起動せず、一時フォルダー上でファイルの改名だけを検証する。GUI 不要のため Category=UI を付けず、CI でも実行される。
    /// </summary>
    /// <remarks>
    /// 撮影スクリプトのマニフェストはファイルの有無で「撮れた」と数えるため、案内の写った画像が正規の名前で残ると
    /// <c>-Publish</c> で公開され得る。<see cref="ScreenshotHelper"/> の未描画の扱い（<c>_FAILED.png</c> へ改名）と同じ規約。
    /// </remarks>
    public class ScreenshotAssertionsTests : IDisposable
    {
        private readonly string _directory;

        public ScreenshotAssertionsTests()
        {
            _directory = Path.Combine(Path.GetTempPath(), "ICCardManager.UITests.ScreenshotAssertions." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); } catch { /* 後始末の失敗は検証対象外 */ }
        }

        [Fact]
        public void 更新の案内が出ていたとき_撮った画像をFAILEDへ改名して例外にすること()
        {
            var captured = Path.Combine(_directory, "lend.png");
            File.WriteAllText(captured, "案内の写った画像");
            var notices = new[] { "ℹ️ 新しいバージョン 99.0.0 が公開されています（このPCは 2.13.0）。管理者に更新をご確認ください。" };

            Action act = () => ScreenshotAssertions.RejectCapturesIfNoticeShown(notices, new[] { captured });

            var thrown = act.Should().Throw<InvalidOperationException>().Which;
            thrown.Message.Should().Contain("99.0.0", "何が: 写っていた案内を示す")
                .And.Contain("lend_FAILED.png", "撮れた画像の残し場所を示す")
                .And.EndWith("撮り直してください。", "どうすれば: 行動指示で終わる");
            File.Exists(captured).Should().BeFalse("正規の名前で残すと、撮影スクリプトが「撮れた」と数えて公開し得る");
            File.ReadAllText(Path.Combine(_directory, "lend_FAILED.png")).Should().Be("案内の写った画像");
        }

        [Fact]
        public void 更新の案内が出ていないとき_撮った画像に触れないこと()
        {
            var captured = Path.Combine(_directory, "history.png");
            File.WriteAllText(captured, "正しい画像");

            ScreenshotAssertions.RejectCapturesIfNoticeShown(Array.Empty<string>(), new[] { captured });

            Directory.GetFiles(_directory).Should().BeEquivalentTo(new[] { captured });
            File.ReadAllText(captured).Should().Be("正しい画像");
        }

        [Fact]
        public void 残高チェーンが切れていたとき_撮った画像をFAILEDへ改名して例外にすること()
        {
            var captured = Path.Combine(_directory, "return.png");
            var toast = Path.Combine(_directory, "toast_return.png");
            File.WriteAllText(captured, "残高が減っていない画像");
            File.WriteAllText(toast, "残額 20,000 円のトースト");
            var breaks = new[] { "カード 07FE112233445566 の 2026-11-09「鉄道（博多～天神）」（id 9）: 前行の残額 20,000 ＋ 受入 0 − 払出 200 ＝ 19,800 のはずが、残額は 20,000" };

            Action act = () => ScreenshotAssertions.RejectCapturesIfLedgerChainBroken(breaks, new[] { captured, toast });

            var thrown = act.Should().Throw<InvalidOperationException>().Which;
            thrown.Message.Should().Contain("19,800 のはずが、残額は 20,000", "何が: 切れている行と期待した残額を示す")
                .And.Contain("現在残高", "なぜ: 仮想タッチの現在残高が原因になり得ることを示す")
                .And.Contain("return_FAILED.png").And.Contain("toast_return_FAILED.png")
                .And.EndWith("撮り直してください。", "どうすれば: 行動指示で終わる");
            File.Exists(captured).Should().BeFalse("正規の名前で残すと、撮影スクリプトが「撮れた」と数えて公開し得る");
            File.Exists(toast).Should().BeFalse();
            File.ReadAllText(Path.Combine(_directory, "return_FAILED.png")).Should().Be("残高が減っていない画像");
        }

        [Fact]
        public void 残高チェーンが整合しているとき_撮った画像に触れないこと()
        {
            var captured = Path.Combine(_directory, "return.png");
            File.WriteAllText(captured, "正しい画像");

            ScreenshotAssertions.RejectCapturesIfLedgerChainBroken(Array.Empty<string>(), new[] { captured });

            Directory.GetFiles(_directory).Should().BeEquivalentTo(new[] { captured });
            File.ReadAllText(captured).Should().Be("正しい画像");
        }
    }
}
