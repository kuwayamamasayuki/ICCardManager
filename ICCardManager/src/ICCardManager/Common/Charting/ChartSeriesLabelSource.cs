namespace ICCardManager.Common.Charting
{
    /// <summary>
    /// 系列ラベルを組み立てるための入力（Issue #1886）。
    /// </summary>
    /// <remarks>
    /// 表示名の一意化は「基底の表示名」と「同名だったときに添える識別情報」の 2 つが揃って
    /// 初めて決まる。呼び出し元が 2 本の並行リストで渡す形にすると、長さのずれを型で防げない。
    /// </remarks>
    internal sealed class ChartSeriesLabelSource
    {
        /// <summary>
        /// 修飾しない状態の表示名（職員名、または「（職員名なし）」）。
        /// </summary>
        public string BaseName { get; set; } = string.Empty;

        /// <summary>
        /// 同名の系列が複数あるときに添える識別情報（職員番号）。無ければ null / 空。
        /// </summary>
        /// <remarks>
        /// IDm を使わないのは、職員証の IDm が本システム唯一の認証要素であり、
        /// 画面・Excel へ部分的にでも露出させたくないため（ログに関する Issue #1852 と同じ判断）。
        /// 職員番号は業務上の識別子であり、同姓同名を見分ける手掛かりとしても自然。
        /// </remarks>
        public string Qualifier { get; set; }
    }
}
