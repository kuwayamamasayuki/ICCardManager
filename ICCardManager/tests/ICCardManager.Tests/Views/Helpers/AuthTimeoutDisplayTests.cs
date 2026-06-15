using FluentAssertions;
using ICCardManager.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views.Helpers
{
    /// <summary>
    /// Issue #1613: 職員証認証ダイアログのタイムアウト残り秒数表示ロジックを検証する。
    /// </summary>
    /// <remarks>
    /// 「色・アイコン・テキスト・音の4要素」原則に基づき、残り10秒以下では色変化だけでなく
    /// ⚠ アイコン（テキスト）でも警告することを保証する。コードビハインド（WPF Window）は
    /// 単体テストが困難なため、純粋関数として切り出した表示判定・文言生成のみを検証する。
    /// </remarks>
    public class AuthTimeoutDisplayTests
    {
        [Theory]
        [InlineData(10, true)]  // 閾値ちょうどは警告域
        [InlineData(1, true)]   // 警告域の下限
        [InlineData(11, false)] // 閾値直上は通常表示
        [InlineData(60, false)] // デフォルト初期値は通常表示
        [InlineData(0, false)]  // 0 はタイムアウト確定であり警告域に含めない
        [InlineData(-1, false)] // 負値も警告域に含めない
        public void IsWarning_警告閾値10秒の境界で正しく判定すること(int remainingSeconds, bool expected)
        {
            AuthTimeoutDisplay.IsWarning(remainingSeconds).Should().Be(expected,
                $"残り{remainingSeconds}秒の警告判定は {expected} であるべき（閾値={AuthTimeoutDisplay.WarningThresholdSeconds}秒）");
        }

        [Theory]
        [InlineData(60, "60秒")]
        [InlineData(11, "11秒")]
        public void FormatRemaining_通常域ではアイコンなしで秒数のみ表示すること(int remainingSeconds, string expected)
        {
            AuthTimeoutDisplay.FormatRemaining(remainingSeconds).Should().Be(expected);
        }

        [Theory]
        [InlineData(10, "⚠ 10秒")]
        [InlineData(5, "⚠ 5秒")]
        [InlineData(1, "⚠ 1秒")]
        public void FormatRemaining_警告域では警告アイコンを前置すること(int remainingSeconds, string expected)
        {
            // 色覚多様性・無音環境でも警告が伝わるよう、⚠ アイコン（テキスト）で補助伝達する
            AuthTimeoutDisplay.FormatRemaining(remainingSeconds).Should().Be(expected);
            AuthTimeoutDisplay.FormatRemaining(remainingSeconds).Should().StartWith("⚠",
                "残り10秒以下は色変化のみに依存せずアイコンでも警告すべき（Issue #1613）");
        }

        [Theory]
        [InlineData(0, "0秒")]
        [InlineData(-3, "0秒")]
        public void FormatRemaining_0以下は0秒に丸めて表示崩れを防ぐこと(int remainingSeconds, string expected)
        {
            AuthTimeoutDisplay.FormatRemaining(remainingSeconds).Should().Be(expected);
        }
    }
}
