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
    /// 登録（<c>InsertAsync</c>）・更新（<c>UpdateAsync</c>）・復元（<c>RestoreAsync</c>）の
    /// **この制約に触れる 3 経路すべて**が投げる（Issue #1757）。
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
    ///     整備済みの案内が出る。復旧可能な入力ミスが原因不明のエラーに見える状態を構造的に防ぐ。
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

        /// <summary>
        /// 登録・更新（管理番号を指定する操作）で重複したときの例外を生成する
        /// </summary>
        public DuplicateCardNumberException(string cardType, string cardNumber, Exception innerException)
            : this(cardType, cardNumber, BuildUserFriendlyMessage(cardNumber), innerException)
        {
        }

        private DuplicateCardNumberException(
            string cardType, string cardNumber, string userFriendlyMessage, Exception innerException)
            : base(
                $"同一種別（{cardType}）で同一管理番号（{cardNumber}）のカードが既に登録されています。",
                userFriendlyMessage,
                DuplicateCardNumberErrorCode,
                innerException)
        {
            CardType = cardType;
            CardNumber = cardNumber;
        }

        /// <summary>
        /// 削除済みカードの復元で重複したときの例外を生成する（Issue #1757）
        /// </summary>
        /// <remarks>
        /// 復元操作には管理番号の入力欄が無く、番号は削除時のまま復元される。
        /// したがって「別の番号を指定してください」は**この経路では実行できない指示**になるため、
        /// 「どうすれば」を実際に取れる行動（先に使用中カードの番号を変更する）へ差し替える。
        /// `.claude/rules/error-messages.md`「サービス層は復旧手段を知らない」ではなく、
        /// **同じ原因でも経路によって取れる行動が違う**ケースの手当て。
        /// </remarks>
        public static DuplicateCardNumberException ForRestore(
            string cardType, string cardNumber, Exception innerException) =>
            new DuplicateCardNumberException(
                cardType, cardNumber, BuildRestoreUserFriendlyMessage(cardNumber), innerException);

        /// <summary>
        /// ユーザー向けの案内文言を組み立てる
        /// </summary>
        /// <remarks>
        /// `.claude/rules/error-messages.md` の3要素構成:
        /// 何が＝どの管理番号か／なぜ＝同じ種別で既に使用されている／
        /// どうすれば＝別の番号を指定する（行動指示で終わる）。
        /// カード種別を文言に含めないのは、カード管理画面ではユーザーが今まさに編集している
        /// 種別であり画面上で自明なため（文言が長くなるとトースト・ステータス欄で切れる）。
        /// CSVインポートでは種別が自明ではないが、報告が行番号付きで行を特定できるため
        /// 「何が」は成立する。
        /// </remarks>
        private static string BuildUserFriendlyMessage(string cardNumber) =>
            $"{DescribeCardNumber(cardNumber)}同じ種別で既に使用されています。別の番号を指定してください。";

        /// <summary>
        /// 復元経路のユーザー向け案内文言を組み立てる
        /// </summary>
        private static string BuildRestoreUserFriendlyMessage(string cardNumber) =>
            $"{DescribeCardNumber(cardNumber)}同じ種別の別のカードが使用中のため、" +
            "このカードを復元できません。使用中のカードの管理番号を変更してから、" +
            "もう一度復元してください。";

        /// <summary>
        /// 文言の「何が」部分（助詞まで含む）
        /// </summary>
        /// <remarks>
        /// 復元経路は IDm しか持たないため、例外を組み立てる時点で管理番号を読み直す。
        /// その読み取りにも失敗した場合に「管理番号  は…」という壊れた文にならないよう、
        /// 主語を丸ごと差し替える。
        /// </remarks>
        private static string DescribeCardNumber(string cardNumber) =>
            string.IsNullOrWhiteSpace(cardNumber) ? "このカードの管理番号は" : $"管理番号 {cardNumber} は";
    }
}
