using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common
{
    /// <summary>
    /// Issue #1998: 残額警告のしきい値判定（境界は「以下」）を固定する。
    /// </summary>
    public class BalanceWarningPolicyTests
    {
        [Fact]
        public void しきい値ちょうどの残額を警告対象に含めること()
        {
            // Issue #1998 の本体。LendingService だけが厳密な < で、返却トーストが
            // 警告を出さない一方、直後のダッシュボードは同じカードを赤く表示していた。
            BalanceWarningPolicy.IsLowBalance(10000, 10000).Should().BeTrue();
        }

        [Fact]
        public void しきい値を下回る残額を警告対象に含めること()
        {
            BalanceWarningPolicy.IsLowBalance(9999, 10000).Should().BeTrue();
        }

        [Fact]
        public void しきい値を上回る残額を警告対象に含めないこと()
        {
            // 「常に true」へ退行した実装を検出する対の表明。
            BalanceWarningPolicy.IsLowBalance(10001, 10000).Should().BeFalse();
        }

        [Theory]
        // しきい値 0（警告を実質無効化する設定。ValidationService は 0〜20,000 を許す）でも
        // 「残額 0 円ちょうど」は境界として警告対象になる。
        [InlineData(0, 0, true)]
        [InlineData(1, 0, false)]
        // 残高不足で払い戻し直後などに起きる 0 円のカード。
        [InlineData(0, 10000, true)]
        // ValidationService が許す上限（20,000 円）での境界。
        [InlineData(20000, 20000, true)]
        [InlineData(20001, 20000, false)]
        public void 境界がしきい値の大小によらず一貫していること(int balance, int warningBalance, bool expected)
        {
            BalanceWarningPolicy.IsLowBalance(balance, warningBalance).Should().Be(expected);
        }

        // ------------------------------------------------------------------
        // Issue #2077: 返却トーストの文言が判定（以下）と一致していること
        // ------------------------------------------------------------------

        [Fact]
        public void 残額警告の文言がしきい値を含む表記になっていること()
        {
            // Issue #2077 の本体。#1998 で判定は「以下」へ統一されたのに、表記だけが
            // "残額不足（<10,000円）" のまま残っていた。10,000 円ちょうどのカードを返却すると
            // 警告は出るのに、その理由として述べている条件（10,000 円未満）を満たしていない。
            BalanceWarningPolicy.FormatLowBalanceNotice(10000)
                .Should().Be("⚠️ 残額不足（10,000円以下）");
        }

        [Fact]
        public void 境界の表記が装飾を含まないこと()
        {
            // 核（見出し＋境界）と装飾（トーストの ⚠️）を分ける。装飾を焼き込むと
            // Excel の集計見出しから再利用できず、境界の表記が 2 か所に分かれる
            // （コードレビューで検出）。
            BalanceWarningPolicy.FormatLowBalanceThresholdLabel(10000)
                .Should().Be("残額不足（10,000円以下）");
        }

        [Fact]
        public void トーストの文言が境界の表記をそのまま含むこと()
        {
            // 2 つが別々に書かれていないことを表明する。片方だけを直せる形にしない。
            var label = BalanceWarningPolicy.FormatLowBalanceThresholdLabel(3000);

            BalanceWarningPolicy.FormatLowBalanceNotice(3000).Should().Contain(label);
        }

        [Fact]
        public void 残額警告の文言が厳密不等号や未満を含まないこと()
        {
            var notice = BalanceWarningPolicy.FormatLowBalanceNotice(10000);

            notice.Should().NotContain("<");
            notice.Should().NotContain("＜");
            notice.Should().NotContain("未満");
        }

        [Theory]
        [InlineData(10000, 10000, true)]
        [InlineData(10001, 10000, false)]
        [InlineData(20000, 20000, true)]
        [InlineData(0, 0, true)]
        public void 文言が示す境界と判定の境界が一致していること(int balance, int warningBalance, bool expectedLow)
        {
            // 文言は「しきい値<b>以下</b>」と述べる。述べたとおりに読んだ結果
            //（残額 <= しきい値）が、実際の判定と一致することを表明する。
            // 文言のリテラルだけを固定すると、判定側を「未満」へ戻した実装でも緑になる。
            var notice = BalanceWarningPolicy.FormatLowBalanceNotice(warningBalance);
            notice.Should().Contain("以下");

            var impliedByNotice = balance <= warningBalance;
            BalanceWarningPolicy.IsLowBalance(balance, warningBalance)
                .Should().Be(impliedByNotice).And.Be(expectedLow);
        }

        [Fact]
        public void 残額警告の文言が桁区切りを含むこと()
        {
            // しきい値をそのまま埋め込む実装（"20000円以下"）への退行を検出する。
            BalanceWarningPolicy.FormatLowBalanceNotice(20000).Should().Contain("20,000円");
        }

        [Fact]
        public void 残額警告の文言が短く保たれていること()
        {
            // 対の表明。#1273 はトーストが文字サイズ「大/特大」で折り返し過多になる問題を
            // 文言の簡潔化で解いた。「以下」表記へ直すついでに
            // 「残額が少なくなっています（しきい値: 10,000円）」のような長文へ戻さない。
            BalanceWarningPolicy.FormatLowBalanceNotice(10000).Length
                .Should().BeLessOrEqualTo(20);
        }
    }
}
