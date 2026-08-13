using System;
using ICCardManager.Common.Exceptions;

namespace ICCardManager.Data.Repositories
{
    /// <summary>
    /// カード種別＋管理番号の重複時にスローされる例外
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #1106: 共有フォルダモードで複数PCから同時にカードを登録した場合に、
    /// UNIQUE制約（idx_card_type_number_active）違反を検出するために使用。
    /// 登録（<c>InsertAsync</c>）と更新（<c>UpdateAsync</c>、Issue #1757）の両方が投げる。
    /// </para>
    /// <para>
    /// Issue #1757: <see cref="AppException"/> を継承する。理由は 2 つ。
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <b>文言の単一の真実源</b>: 同じ「管理番号の重複」に対する案内が、カード管理画面
    ///     （登録・編集）と CSV インポートで食い違わないよう <see cref="AppException.UserFriendlyMessage"/>
    ///     に集約する。
    ///   </description></item>
    ///   <item><description>
    ///     <b>取りこぼしたときの安全側への倒れ方</b>: 捕捉漏れがあっても
    ///     <c>App.OnDispatcherUnhandledException</c> と <c>CsvImportService.ToUserFacingErrorMessage</c>
    ///     は <see cref="AppException"/> を特別扱いするため、「SYS999 予期しないエラー」ではなく
    ///     整備済みの案内が出る。復旧可能な入力ミスがクラッシュ相当に見える状態を構造的に防ぐ。
    ///   </description></item>
    /// </list>
    /// </remarks>
    public class DuplicateCardNumberException : AppException
    {
        /// <summary>
        /// 本例外のエラーコード
        /// </summary>
        public const string DuplicateCardNumberErrorCode = "CARD001";

        /// <summary>
        /// 重複したカード種別
        /// </summary>
        public string CardType { get; }

        /// <summary>
        /// 重複した管理番号
        /// </summary>
        public string CardNumber { get; }

        public DuplicateCardNumberException(string cardType, string cardNumber, Exception innerException)
            : base(
                $"同一種別（{cardType}）で同一管理番号（{cardNumber}）のカードが既に登録されています。",
                BuildUserFriendlyMessage(cardNumber),
                DuplicateCardNumberErrorCode,
                innerException)
        {
            CardType = cardType;
            CardNumber = cardNumber;
        }

        /// <summary>
        /// ユーザー向けの案内文言を組み立てる
        /// </summary>
        /// <remarks>
        /// `.claude/rules/error-messages.md` の3要素構成:
        /// 何が＝どの管理番号か／なぜ＝同じ種別で既に使用されている／
        /// どうすれば＝別の番号を指定する（行動指示で終わる）。
        /// カード種別を文言に含めないのは、ユーザーが今まさに編集している種別であり
        /// 画面上で自明なため（文言が長くなるとトースト・ステータス欄で切れる）。
        /// </remarks>
        private static string BuildUserFriendlyMessage(string cardNumber) =>
            $"管理番号 {cardNumber} は同じ種別で既に使用されています。別の番号を指定してください。";
    }
}
