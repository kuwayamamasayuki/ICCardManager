using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Common;

namespace ICCardManager.Services
{
/// <summary>
    /// IDmの発行者コードからカード種別を自動判別するサービス
    /// </summary>
    public class CardTypeDetector
    {
        /// <summary>
        /// 発行者コードとカード種別のマッピング
        /// </summary>
        private static readonly Dictionary<string, CardType> IssuerCodeMap = new()
        {
            { "01", CardType.Suica },
            { "02", CardType.PASMO },
            { "03", CardType.ICOCA },
            { "04", CardType.PiTaPa },
            { "05", CardType.Nimoca },
            { "06", CardType.SUGOCA },
            { "07", CardType.Hayakaken },
            { "08", CardType.Kitaca },
            { "09", CardType.TOICA },
            { "0A", CardType.Manaca },
            { "0a", CardType.Manaca }
        };

        /// <summary>
        /// IDmからカード種別を判別します
        /// </summary>
        /// <param name="idm">IDm (16桁の16進数文字列)</param>
        /// <returns>判別されたカード種別</returns>
        public CardType Detect(string idm)
        {
            return DetectFromIdm(idm);
        }

        /// <summary>
        /// IDmからカード種別を判別します（静的メソッド）
        /// </summary>
        /// <param name="idm">IDm (16桁の16進数文字列)</param>
        /// <returns>判別されたカード種別</returns>
        public static CardType DetectFromIdm(string idm)
        {
            if (string.IsNullOrEmpty(idm) || idm.Length < 2)
            {
                return CardType.Unknown;
            }

            var issuerCode = idm.Substring(0, 2).ToUpperInvariant();

            // 大文字に正規化してから検索
            if (IssuerCodeMap.TryGetValue(issuerCode, out var cardType))
            {
                return cardType;
            }

            // 小文字でも検索（0A/0a対応）
            if (IssuerCodeMap.TryGetValue(issuerCode.ToLowerInvariant(), out cardType))
            {
                return cardType;
            }

            return CardType.Unknown;
        }

        /// <summary>
        /// カード種別を日本語名に変換します
        /// </summary>
        /// <param name="cardType">カード種別</param>
        /// <returns>日本語名</returns>
        public static string GetDisplayName(CardType cardType)
        {
            return cardType switch
            {
                CardType.Suica => "Suica",
                CardType.PASMO => "PASMO",
                CardType.ICOCA => "ICOCA",
                CardType.PiTaPa => "PiTaPa",
                CardType.Nimoca => "nimoca",
                CardType.SUGOCA => "SUGOCA",
                CardType.Hayakaken => "はやかけん",
                CardType.Kitaca => "Kitaca",
                CardType.TOICA => "TOICA",
                CardType.Manaca => "manaca",
                CardType.Unknown => "その他",
                _ => "その他"
            };
        }
    }
}
