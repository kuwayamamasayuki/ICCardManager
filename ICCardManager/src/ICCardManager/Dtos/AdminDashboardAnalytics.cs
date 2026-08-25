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
        private string _name = string.Empty;

        /// <summary>
        /// 系列名（職員名、または集約系列の「その他（N 名）」）
        /// </summary>
        /// <remarks>
        /// 集約系列（<see cref="IsOther"/> が true）では
        /// <see cref="Common.Charting.ChartSeriesNameFormatter.BuildOtherSeriesName"/> が
        /// <see cref="AggregatedSeriesCount"/> から<b>導出</b>する。凡例・代替一覧・Excel は
        /// この値をそのまま表示するため、消費側で接尾辞を足さないこと（Issue #1858）。
        /// <para>
        /// Issue #1883: 以前は本プロパティも <see cref="AggregatedSeriesCount"/> も独立した
        /// setter を持つ自動プロパティで、「件数は 4 なのに名前は『その他（3 名）』」という
        /// <b>食い違った状態を誰でも作れた</b>。件数を読む本番の消費側は導出している 1 行だけなので、
        /// ずれは「凡例のラベルだけが間違っている」という静かな形でしか現れない。
        /// 導出にすることで、その状態を型として表現できなくした。
        /// </para>
        /// </remarks>
        /// <exception cref="System.InvalidOperationException">
        /// 集約系列に対して setter を呼んだとき（表示名は件数から導出されるため設定できない）。
        /// </exception>
        public string Name
        {
            get => IsOther
                ? Common.Charting.ChartSeriesNameFormatter.BuildOtherSeriesName(AggregatedSeriesCount)
                : _name;
            set
            {
                if (IsOther)
                {
                    // 無言で捨てると「設定したのに反映されない」形になる。
                    // 集約系列の表示名を変えたいなら件数を変える（＝MarkAsAggregated）のが正しい。
                    throw new System.InvalidOperationException(
                        "集約系列の表示名は AggregatedSeriesCount から導出されるため設定できません。"
                            + "件数を変える場合は MarkAsAggregated(int) を呼んでください。");
                }

                _name = value;
            }
        }

        /// <summary>
        /// 上位以外を集約した「その他」系列かどうか。
        /// <see cref="AggregatedSeriesCount"/> が 1 以上であることと同値（Issue #1883）。
        /// </summary>
        /// <remarks>
        /// 集約の有無を独立した設定可能フラグとして持つと、
        /// 「<c>IsOther = true</c> だが件数 0」という同じ事実の食い違いをもう 1 通り作れてしまう。
        /// 集約系列になれる唯一の経路は <see cref="MarkAsAggregated(int)"/> で、
        /// そこが件数の定義域（1 以上）を検証する。
        /// </remarks>
        public bool IsOther => AggregatedSeriesCount > 0;

        /// <summary>
        /// 集約された系列の数。集約系列でなければ 0（Issue #1858）
        /// </summary>
        /// <remarks>
        /// 数えているのは<b>系列</b>であり、職員の同一性の解決結果に乗る。
        /// <c>lender_idm</c> を持つ行と持たない行が同じ職員に混在する過去のインポートデータでは
        /// 1 人が 2 系列に分かれ、実人数より大きくなり得る（同じ状況では上位系列の凡例にも
        /// 同名の行が 2 つ並ぶ。本グラフ全体の identity モデルの近似）。
        /// <para>
        /// Issue #1883: 本プロパティが集約に関する唯一の情報源で、
        /// <see cref="Name"/> と <see cref="IsOther"/> はここから導出される。
        /// </para>
        /// </remarks>
        public int AggregatedSeriesCount { get; private set; }

        /// <summary>
        /// この系列を、指定した数の系列を集約した「その他」系列にする（Issue #1883）。
        /// </summary>
        /// <param name="aggregatedSeriesCount">集約された系列の数。1 以上であること</param>
        /// <exception cref="System.ArgumentOutOfRangeException">
        /// <paramref name="aggregatedSeriesCount"/> が 0 以下のとき。
        /// </exception>
        /// <remarks>
        /// 件数・集約フラグ・表示名を 1 回の呼び出しで確定させる唯一の経路。
        /// 呼び出し元が 3 つを別々の文で組み立てる形にすると、片方だけ変わる日が来る。
        /// 0 以下は集約が起きていない状態であり、
        /// <see cref="Common.Charting.ChartSeriesNameFormatter.BuildOtherSeriesName"/> と
        /// 同じ定義域でここでも弾く（呼び出し側の誤りを、表示に届く前に露見させる）。
        /// <para>
        /// 既に <see cref="Name"/> を設定済みの系列に対しても弾く。集約すると表示名は
        /// 件数からの導出に切り替わり、設定済みの名前は<b>二度と読まれない</b>。
        /// 「集約してから名前を設定する」順は例外にしておきながら、
        /// 逆順（名前を設定してから集約する）を無言で捨てると、
        /// <b>片方の順序だけが大声で、鏡像の順序は無言</b>という非対称が残る。
        /// </para>
        /// </remarks>
        /// <exception cref="System.InvalidOperationException">
        /// 既に <see cref="Name"/> が設定されているとき。
        /// </exception>
        public void MarkAsAggregated(int aggregatedSeriesCount)
        {
            if (aggregatedSeriesCount <= 0)
            {
                throw new System.ArgumentOutOfRangeException(
                    nameof(aggregatedSeriesCount),
                    aggregatedSeriesCount,
                    "集約系列には 1 以上の件数を指定してください。0 以下では集約が起きておらず、"
                        + "「その他」系列として扱えません。");
            }

            if (!string.IsNullOrEmpty(_name))
            {
                throw new System.InvalidOperationException(
                    "表示名を設定済みの系列は集約系列にできません。集約系列の表示名は "
                        + "AggregatedSeriesCount から導出されるため、設定済みの名前は読まれなくなります。"
                        + "集約系列は表示名を設定していない系列に対して作成してください。");
            }

            AggregatedSeriesCount = aggregatedSeriesCount;
        }

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
