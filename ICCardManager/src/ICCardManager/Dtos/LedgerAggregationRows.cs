using System;

namespace ICCardManager.Dtos
{
    /// <summary>
    /// カード別の利用実績集計 1 行（管理者ダッシュボードの稼働状況、Issue #1692）
    /// </summary>
    /// <remarks>
    /// 台帳は 6 年分保持されるため、集計は SQL 側の GROUP BY で行い、
    /// アプリケーション層へは集計済みの行だけを渡す（全件を C# に読み出さない）。
    /// </remarks>
    public class CardUsageStatsRow
    {
        /// <summary>対象カードの IDm</summary>
        public string CardIdm { get; set; } = string.Empty;

        /// <summary>期間内に利用実績があった日数（同日に複数回利用しても 1 日と数える）</summary>
        public int UsedDayCount { get; set; }

        /// <summary>期間内の利用レコード件数</summary>
        public int UsageCount { get; set; }

        /// <summary>期間内の払出金額の合計</summary>
        public int TotalExpense { get; set; }

        /// <summary>期間内の受入金額（チャージ等）の合計</summary>
        public int TotalIncome { get; set; }

        /// <summary>期間内の最終利用日</summary>
        public DateTime? LastUsageDate { get; set; }
    }

    /// <summary>
    /// 月別 × 貸出職員別の利用額集計 1 行（管理者ダッシュボードの利用推移、Issue #1692）
    /// </summary>
    public class MonthlyUsageRow
    {
        /// <summary>年月（"yyyy-MM" 形式）</summary>
        public string YearMonth { get; set; } = string.Empty;

        /// <summary>貸出職員の IDm。過去のインポートデータでは空になり得る</summary>
        public string LenderIdm { get; set; } = string.Empty;

        /// <summary>台帳に記録された職員名。<see cref="LenderIdm"/> が空の行の識別に使う</summary>
        public string StaffName { get; set; } = string.Empty;

        /// <summary>払出金額の合計</summary>
        public int TotalExpense { get; set; }

        /// <summary>受入金額の合計</summary>
        public int TotalIncome { get; set; }

        /// <summary>利用レコード件数</summary>
        public int UsageCount { get; set; }
    }

    /// <summary>
    /// カード別 × 月別の月末残高 1 行（管理者ダッシュボードの残高推移、Issue #1692）
    /// </summary>
    /// <remarks>
    /// その月に取引が無かったカードは行が存在しない。折れ線を描く際は
    /// <c>CardUtilizationCalculator.CarryForward</c> で前月の残高を引き継ぐこと。
    /// </remarks>
    public class MonthEndBalanceRow
    {
        /// <summary>対象カードの IDm</summary>
        public string CardIdm { get; set; } = string.Empty;

        /// <summary>年月（"yyyy-MM" 形式）</summary>
        public string YearMonth { get; set; } = string.Empty;

        /// <summary>その月の最終レコード時点の残高</summary>
        public int Balance { get; set; }
    }
}
