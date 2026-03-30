using System;

namespace ICCardManager.Data.Repositories
{
    /// <summary>
    /// カード種別＋管理番号の重複時にスローされる例外
    /// </summary>
    /// <remarks>
    /// Issue #1106: 共有フォルダモードで複数PCから同時にカードを登録した場合に、
    /// UNIQUE制約（idx_card_type_number_active）違反を検出するために使用。
    /// </remarks>
    public class DuplicateCardNumberException : Exception
    {
        /// <summary>
        /// 重複したカード種別
        /// </summary>
        public string CardType { get; }

        /// <summary>
        /// 重複した管理番号
        /// </summary>
        public string CardNumber { get; }

        public DuplicateCardNumberException(string cardType, string cardNumber, Exception innerException)
            : base($"同一種別（{cardType}）で同一管理番号（{cardNumber}）のカードが既に登録されています。", innerException)
        {
            CardType = cardType;
            CardNumber = cardNumber;
        }
    }
}
