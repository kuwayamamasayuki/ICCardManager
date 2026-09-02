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
    }
}
