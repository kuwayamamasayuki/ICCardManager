namespace ICCardManager.Common
{
    /// <summary>
    /// 残額警告のしきい値判定（Issue #1998）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>境界は「以下」（<c>&lt;=</c>）である。</b>しきい値ちょうどの残額も警告の対象に含める。
    /// 設定値の表（<c>04_機能設計書</c> §7.1「この金額<b>以下</b>で警告表示」）、画面設計書、
    /// 管理者マニュアル、Excel 出力の見出し（<c>残額不足（N円以下）</c>）がいずれも「以下」で書かれており、
    /// 過去に <c>DashboardService</c> と <c>WarningService</c> の食い違いを「以下」へ統一した経緯もある。
    /// </para>
    /// <para>
    /// <b>この判定を呼び出し元へ配らない。</b>Issue #1998 の時点で同じ比較が 4 か所
    /// （<c>LendingService</c> / <c>DashboardService</c> / <c>AdminDashboardService</c> /
    /// <c>WarningService</c>）に書かれており、<c>LendingService</c> の 1 か所だけが厳密な
    /// <c>&lt;</c> のまま取り残されていた。結果、残額がちょうどしきい値のカードを返却すると
    /// <b>返却トーストは警告を出さないのに、直後のダッシュボード更新と警告一覧は同じカードを
    /// 残額不足として表示する</b>という、同じ操作の直後に矛盾した表示が並ぶ状態になっていた。
    /// 同じ判断を複数箇所へ書き直させない（<c>.claude/rules/db-write-conventions.md</c> #1763、
    /// <c>IcCard.IsInOperation</c> へ寄せた #1947 と同じ形）。
    /// </para>
    /// <para>
    /// しきい値は交通系ICカードに固有の概念ではない（「補充が必要な物品を見つける」判定であり、
    /// 交通系語彙で分岐しない）ため、汎用コアである <c>Common</c> に置く
    /// （<c>.claude/rules/domain-boundaries.md</c> の決定木①）。
    /// </para>
    /// </remarks>
    internal static class BalanceWarningPolicy
    {
        /// <summary>
        /// 残額が警告しきい値に達しているか（＝補充を促すべきか）を返す。
        /// </summary>
        /// <param name="balance">カードの残額（円）。</param>
        /// <param name="warningBalance">残額警告しきい値（円。<c>AppSettings.WarningBalance</c>）。</param>
        /// <returns>残額がしきい値<b>以下</b>のとき true。</returns>
        internal static bool IsLowBalance(int balance, int warningBalance)
        {
            return balance <= warningBalance;
        }
    }
}
