#nullable enable
using ICCardManager.Common.Exceptions;
using ICCardManager.Infrastructure.Security;

namespace ICCardManager.Services
{
    /// <summary>
    /// 払い戻しを確認したときの残額と、書き込む時点の台帳の残額が食い違った（競合）ことを表す例外（Issue #2322）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 払い戻しの金額は確認ダイアログで示した残額であり、払戻台帳は「払出＝その金額、残額 0」で書く。
    /// 確認ダイアログ・職員証の認証を待つ間に、他のパソコンや別の操作（メイン画面のタッチによる貸出→返却、
    /// 履歴の追加・変更）で台帳が進むと、古い残額のまま払戻台帳が作られ「前の行の残額 − 払出 ≠ 0」となって
    /// 残高チェーンが払戻行で切れる。カードは払戻済になるので、以後その食い違いを直す利用行も来ない。
    /// </para>
    /// <para>
    /// <see cref="LendingService.RefundAsync"/> は払戻台帳を書くのと同じトランザクションの中で残額を読み直し、
    /// 確認時の値と違えば本例外で何も書かずに巻き戻す。読み直した値で黙って払い戻さないのは、
    /// 職員が同意した金額（確認ダイアログ・完了メッセージ）と記録される金額が食い違うため。
    /// </para>
    /// <para>
    /// <see cref="RefundConflictException"/>（払戻済への更新が 0 行）とは原因も「どうすれば」も違うので、
    /// 別の型・別のエラーコードにする（error-messages-implementation.md「エラーコードは 1 つの原因だけを指す」）。
    /// <see cref="AppException"/> 派生であり <c>SQLiteException</c> ではないので、リトライ対象にならない。
    /// </para>
    /// </remarks>
    public sealed class RefundBalanceChangedException : AppException
    {
        private const string UserMessage =
            "払い戻しを記録できませんでした。確認した後に、他のパソコンや別の操作でカードの残額が変わりました。" +
            "カード一覧で状態を確認してから、もう一度払い戻してください。";

        /// <param name="cardIdm">対象カードの IDm（ログ用。ユーザー向け文言には含めない）</param>
        /// <param name="confirmedAmount">確認ダイアログで示した残額（払い戻そうとした金額）</param>
        /// <param name="currentBalance">書き込む時点の台帳の残額</param>
        public RefundBalanceChangedException(string cardIdm, int confirmedAmount, int currentBalance)
            // Issue #1986: 例外の Message はログファイルへ書き出されるため IDm はマスクを通す（#1852）
            : base(
                $"Refund balance changed: {IdmMasker.Mask(cardIdm)} (confirmed {confirmedAmount}, current {currentBalance})",
                UserMessage,
                "BIZ017")
        {
            ConfirmedAmount = confirmedAmount;
            CurrentBalance = currentBalance;
        }

        /// <summary>
        /// 確認ダイアログで示した残額
        /// </summary>
        public int ConfirmedAmount { get; }

        /// <summary>
        /// 書き込む時点の台帳の残額
        /// </summary>
        public int CurrentBalance { get; }
    }
}
