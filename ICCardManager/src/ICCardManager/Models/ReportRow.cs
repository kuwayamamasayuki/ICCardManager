using System.Collections.Generic;

namespace ICCardManager.Models
{
    /// <summary>
    /// 帳票行の種別
    /// </summary>
    public enum ReportRowType
    {
        /// <summary>繰越行（前年度繰越・前月繰越）</summary>
        Carryover,

        /// <summary>通常データ行</summary>
        Data,

        /// <summary>月計行</summary>
        MonthlyTotal,

        /// <summary>累計行</summary>
        Cumulative,

        /// <summary>次年度繰越行</summary>
        CarryoverToNextYear
    }

    /// <summary>
    /// 帳票の1行分のデータ（プレビュー・Excel出力共用）
    /// </summary>
    /// <remarks>
    /// Issue #1023: PrintService の ReportPrintRow と ReportService の行出力ロジックの
    /// 重複を解消するために導入。MonthlyReportData → ReportRow への変換は ReportRowBuilder が担当。
    /// </remarks>
    public class ReportRow
    {
        /// <summary>日付表示（和暦）</summary>
        public string DateDisplay { get; set; } = string.Empty;

        /// <summary>摘要</summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>受入金額（null: 空欄として表示）</summary>
        public int? Income { get; set; }

        /// <summary>払出金額（null: 空欄として表示）</summary>
        public int? Expense { get; set; }

        /// <summary>残額（null: 空欄として表示）</summary>
        public int? Balance { get; set; }

        /// <summary>利用者</summary>
        public string StaffName { get; set; }

        /// <summary>備考</summary>
        public string Note { get; set; }

        /// <summary>太字表示するか</summary>
        public bool IsBold { get; set; }

        /// <summary>行の種別</summary>
        public ReportRowType RowType { get; set; }
    }

    /// <summary>
    /// 帳票の合計行データ（月計・累計共用、プレビュー・Excel出力共用）
    /// </summary>
    public class ReportTotal
    {
        /// <summary>ラベル（「X月計」「累計」）</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>受入合計</summary>
        public int Income { get; set; }

        /// <summary>払出合計</summary>
        public int Expense { get; set; }

        /// <summary>残高（null: 空欄として表示）</summary>
        public int? Balance { get; set; }
    }

    /// <summary>
    /// 帳票の行データ一式（ReportRowBuilder の出力）
    /// </summary>
    public class ReportRowSet
    {
        /// <summary>データ行（繰越行 + 明細行）</summary>
        public List<ReportRow> DataRows { get; set; } = new();

        /// <summary>月計</summary>
        public ReportTotal MonthlyTotal { get; set; } = new();

        /// <summary>累計（4月はnull）</summary>
        public ReportTotal CumulativeTotal { get; set; }

        /// <summary>次年度繰越額（3月のみ）</summary>
        public int? CarryoverToNextYear { get; set; }
    }
}
