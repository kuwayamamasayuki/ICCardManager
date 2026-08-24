using System;
using System.Collections.Generic;

namespace ICCardManager.Dtos
{
    /// <summary>
    /// 管理者ダッシュボードの利用分析データ（Issue #1692）
    /// </summary>
    /// <remarks>
    /// 台帳は 6 年分保持されるため、起動時には集計せず分析タブを開いたときに遅延ロードする。
    /// </remarks>
    public class AdminDashboardAnalytics
    {
        /// <summary>集計期間の開始日</summary>
        public DateTime FromDate { get; set; }

        /// <summary>集計期間の終了日</summary>
        public DateTime ToDate { get; set; }

        /// <summary>集計期間の日数（稼働率の分母）</summary>
        public int PeriodDayCount { get; set; }

        /// <summary>集計期間に含まれる年月のラベル（"yyyy/MM" 形式、昇順）</summary>
        public IReadOnlyList<string> MonthLabels { get; set; } = new string[0];

        /// <summary>カード別の稼働状況（稼働率の低い順）</summary>
        public IReadOnlyList<CardUtilizationItem> Utilizations { get; set; } = new CardUtilizationItem[0];

        /// <summary>職員別の月次利用額系列（利用額の多い順、上位以外は「その他」に集約）</summary>
        public IReadOnlyList<MonthlyUsageSeries> UsageSeries { get; set; } = new MonthlyUsageSeries[0];

        /// <summary>カード別の月末残高系列</summary>
        public IReadOnlyList<MonthlyBalanceSeries> BalanceSeries { get; set; } = new MonthlyBalanceSeries[0];
    }

    /// <summary>
    /// カード 1 枚の稼働状況（Issue #1692）
    /// </summary>
    /// <remarks>
    /// 稼働率は「利用実績のあった日数 ÷ 期間日数」。貸出日数ベースではないため、
    /// 単独では実態を誤読させる。利用回数・利用総額・未使用日数を必ず併記すること。
    /// </remarks>
    public class CardUtilizationItem
    {
        /// <summary>カードIDm</summary>
        public string CardIdm { get; set; } = string.Empty;

        /// <summary>表示用のカード名</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>稼働率（0.0〜1.0）</summary>
        public double UtilizationRate { get; set; }

        /// <summary>期間内に利用実績があった日数</summary>
        public int UsedDayCount { get; set; }

        /// <summary>期間内の利用回数</summary>
        public int UsageCount { get; set; }

        /// <summary>期間内の払出金額の合計</summary>
        public int TotalExpense { get; set; }

        /// <summary>期間内の最終利用日</summary>
        public DateTime? LastUsageDate { get; set; }

        /// <summary>最終利用日からの経過日数（期間内に利用が無い場合は null）</summary>
        public int? UnusedDays { get; set; }
    }

    /// <summary>
    /// 職員 1 人（または「その他」）の月次利用額系列（Issue #1692）
    /// </summary>
    public class MonthlyUsageSeries
    {
        /// <summary>
        /// 系列名（職員名、または集約系列の「その他（N 名）」）
        /// </summary>
        /// <remarks>
        /// 集約系列の名前は <see cref="Common.Charting.ChartSeriesNameFormatter.BuildOtherSeriesName"/>
        /// が組み立てる。凡例・代替一覧・Excel はこの値をそのまま表示するため、
        /// 消費側で接尾辞を足さないこと（Issue #1858）。
        /// </remarks>
        public string Name { get; set; } = string.Empty;

        /// <summary>上位以外を集約した「その他」系列かどうか</summary>
        public bool IsOther { get; set; }

        /// <summary>
        /// 集約された系列（職員）の数。<see cref="IsOther"/> が false の系列では 0（Issue #1858）
        /// </summary>
        public int AggregatedSeriesCount { get; set; }

        /// <summary>月ごとの払出金額（<see cref="AdminDashboardAnalytics.MonthLabels"/> と同じ並び・長さ）</summary>
        public IReadOnlyList<int> MonthlyExpenses { get; set; } = new int[0];

        /// <summary>期間内の払出金額の合計</summary>
        public int TotalExpense { get; set; }
    }

    /// <summary>
    /// カード 1 枚の月末残高系列（Issue #1692）
    /// </summary>
    public class MonthlyBalanceSeries
    {
        /// <summary>カードIDm</summary>
        public string CardIdm { get; set; } = string.Empty;

        /// <summary>表示用のカード名</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// 月ごとの月末残高。取引の無い月は前月の残高を引き継ぐ。
        /// 取引開始前の月は null（折れ線を描き始めない）。
        /// </summary>
        public IReadOnlyList<double?> MonthlyBalances { get; set; } = new double?[0];
    }
}
