#nullable enable

namespace ICCardManager.Models
{
    /// <summary>
    /// 導入行（カード登録時に書く台帳の先頭の行）の種別（Issue #2371）。<c>ledger.initial_record_kind</c> に整数で保存する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 導入行かどうか・どの種別かは、摘要ではなく行自身が持つこの値で判定する。摘要は組織設定
    /// （<c>SummaryText.CarryoverFromPreviousYear</c> / <c>MidYearCarryoverFormat</c>）から生成した文字列で、
    /// 保存済みの行を<b>現在の</b>設定値と比べると、文言を変える前に登録した導入行を認識できなくなる
    /// （貸出中レコードを <c>is_lent_record</c> で判定する #2044 と同じ形）。
    /// </para>
    /// <para>
    /// 値は保存形式なので変えない（列挙子を足すときは新しい値を割り当てる）。導入行でない行は <c>NULL</c>。
    /// </para>
    /// </remarks>
    public enum InitialRecordKind
    {
        /// <summary>「新規購入」。受入＝残額</summary>
        NewPurchase = 1,

        /// <summary>「○月から繰越」（年度途中の導入、Issue #510）。受入は空欄（0）</summary>
        MidYearCarryover = 2,

        /// <summary>「前年度より繰越」（3 月を繰越月にした登録、Issue #2326）。新年度の 4/1 付け・受入＝残額</summary>
        FiscalYearStartCarryover = 3,
    }
}
