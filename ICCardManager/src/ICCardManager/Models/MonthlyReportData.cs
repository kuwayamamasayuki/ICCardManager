namespace ICCardManager.Models
{
    /// <summary>
    /// 月次帳票の共通データモデル（ReportService/PrintService共用）
    /// </summary>
    /// <remarks>
    /// Issue #841: ReportService（Excel出力）とPrintService（印刷プレビュー）の
    /// データ準備ロジックを統合するために導入。
    /// </remarks>
    public class MonthlyReportData
    {
        /// <summary>カード情報</summary>
        public IcCard Card { get; set; }

        /// <summary>年</summary>
        public int Year { get; set; }

        /// <summary>月</summary>
        public int Month { get; set; }

        /// <summary>前月末残高（null: 新規購入カードで過去データなし）</summary>
        public int? PrecedingBalance { get; set; }

        /// <summary>繰越行データ（null: 繰越行なし）</summary>
        public CarryoverRowData Carryover { get; set; }

        /// <summary>フィルタ・並替済みの台帳データ</summary>
        public System.Collections.Generic.List<Ledger> Ledgers { get; set; } = new();

        /// <summary>月計</summary>
        public ReportTotalData MonthlyTotal { get; set; } = new();

        /// <summary>累計（4月はnull: 月計と同額のため省略）</summary>
        public ReportTotalData CumulativeTotal { get; set; }

        /// <summary>次年度繰越額（3月のみ）</summary>
        public int? CarryoverToNextYear { get; set; }
    }

    /// <summary>
    /// 繰越行データ
    /// </summary>
    public class CarryoverRowData
    {
        /// <summary>繰越日付</summary>
        public System.DateTime Date { get; set; }

        /// <summary>摘要（「前年度より繰越」or「X月より繰越」）</summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>受入金額（4月の前年度繰越のみ値あり、それ以外はnull）</summary>
        public int? Income { get; set; }

        /// <summary>残額</summary>
        public int Balance { get; set; }
    }

    /// <summary>
    /// 帳票合計データ（月計・累計共用）
    /// </summary>
    public class ReportTotalData
    {
        /// <summary>ラベル（「X月計」or「累計」）</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>受入合計</summary>
        public int Income { get; set; }

        /// <summary>払出合計</summary>
        public int Expense { get; set; }

        /// <summary>残額（4月の月計とすべての累計で値あり、それ以外はnull）</summary>
        public int? Balance { get; set; }
    }
}
