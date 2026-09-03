using ICCardManager.Services;

namespace ICCardManager.Common
{
    /// <summary>
    /// Issue #2007: 導入時（カード登録時）の残高の誤りを利用者へ案内する文言を組み立てる。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 文言は「何が（導入時の残額）／なぜ（直後の記録と合わない）／どうすれば（受入と残額を逆算値へ直す）」
    /// の 3 要素で組み立てる（<c>.claude/rules/error-messages.md</c>）。
    /// 履歴一覧のハイライト（ToolTip）・行編集ダイアログの提案エリア・警告エリアの 3 か所が同じ事実を
    /// 述べるため、組み立てを 1 か所へ寄せる（#1763「同じ判断を配らない」）。
    /// </para>
    /// <para>
    /// 「受入と残額」か「残額」かは <see cref="InitialBalanceCorrection.AppliesToIncome"/> で決まる。
    /// 新規購入・前年度より繰越は受入欄にも残高を書くため両方を直す必要があり、片方だけ直すと
    /// 月次帳票の「受入 − 払出 = 残額」が崩れたまま残る。○月から繰越は受入欄が空欄なので残額だけ直す。
    /// </para>
    /// <para>
    /// 汎用/固有の別: 汎用（物品出納簿の「導入時の在庫量」の訂正であり、交通系ICカードに固有ではない）。
    /// </para>
    /// </remarks>
    public static class InitialBalanceCorrectionMessage
    {
        /// <summary>直す対象の欄名（「受入と残額」または「残額」）</summary>
        public static string TargetFields(bool appliesToIncome) => appliesToIncome ? "受入と残額" : "残額";

        /// <summary>
        /// 履歴一覧のハイライト行（導入行）に添える ToolTip 文言。
        /// </summary>
        /// <param name="recordedBalance">導入行に記録されている残高</param>
        /// <param name="suggestedBalance">直後の記録から逆算した残高</param>
        /// <param name="appliesToIncome">受入欄も直すか</param>
        public static string ForHistoryRow(int recordedBalance, int suggestedBalance, bool appliesToIncome) =>
            $"導入時の残額 {recordedBalance:N0}円 が直後の記録と合いません。" +
            $"直後の記録から逆算すると {suggestedBalance:N0}円 です。" +
            $"修正ボタンから{TargetFields(appliesToIncome)}を {suggestedBalance:N0}円 に直してください。";

        /// <summary>
        /// 行編集ダイアログの提案エリアに表示する文言。
        /// </summary>
        public static string ForEditDialog(InitialBalanceCorrection correction) =>
            $"導入時の残額 {correction.RecordedBalance:N0}円 が、直後の記録から逆算した {correction.SuggestedBalance:N0}円 と合いません。" +
            "以後の残額はカードから読み取った値のため、誤っているのは導入時に入力した残高と考えられます。" +
            $"「逆算した金額を適用」で{TargetFields(correction.AppliesToIncome)}を {correction.SuggestedBalance:N0}円 に直してから保存してください。";

        /// <summary>
        /// 警告エリア（メイン画面右下）の文言。クリックで履歴を開くため、対処はハイライト側に委ねる。
        /// </summary>
        public static string ForWarningArea(string cardType, string cardNumber) =>
            $"⚠️ 導入時の残額が直後の記録と合いません（{cardType} {cardNumber}）。クリックして履歴の導入行を修正してください";

        /// <summary>
        /// 行編集ダイアログで、受入欄に残高を書く導入行の受入と残額が食い違っているときの警告。
        /// </summary>
        public static string ForIncomeBalanceMismatch(int income, int balance) =>
            $"導入時の行は受入と残額が同じ金額になります。受入 {income:N0}円 と残額 {balance:N0}円 が異なると" +
            "月次帳票の受入合計が残額と合わなくなるため、両方を同じ金額にしてください。";
    }
}
