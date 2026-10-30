#nullable enable

namespace ICCardManager.Common
{
    /// <summary>
    /// 貸出中の行（貸出中レコード）を削除するときの確認文言の、復旧方法を述べる部分（Issue #1574 / #2322）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 通常のカードでは「メイン画面でタッチして返却するのが正しい復旧方法」と案内し、削除を思いとどまらせる。
    /// ところが払戻済のカードはタッチで返却できない（貸出の前に払戻済のトーストで止まる）ため、払戻済のカードに
    /// 残った貸出中の行の警告から削除へ進んだ職員に同じ文言を出すと、実行できない手順を勧めて迷わせる。
    /// 払戻済のカードでは「この削除で解消する」と案内する。
    /// </para>
    /// <para>
    /// 確認は履歴一覧の削除（<c>HistoryPanelViewModel.DeleteLedgerRow</c>）と行編集ダイアログの削除要求
    /// （<c>LedgerRowEditViewModel.RequestDelete</c>）の 2 か所にあり、出し分けを 1 か所に置く（#1763）。
    /// </para>
    /// </remarks>
    public static class LentRecordDeletionGuidance
    {
        /// <summary>
        /// 復旧方法を述べる部分（確認の問いを含む）を返す
        /// </summary>
        /// <param name="isRefundedCard">対象のカードが払戻済か</param>
        public static string Build(bool isRefundedCard)
        {
            return isRefundedCard
                ? "払戻済のカードはタッチで返却できないため、\n" +
                  "この削除で貸出中の状態を解消します。削除しますか？"
                : "通常は、メイン画面で交通系ICカードをタッチして返却操作を\n" +
                  "行うのが正しい復旧方法です。それでも削除しますか？";
        }
    }
}
