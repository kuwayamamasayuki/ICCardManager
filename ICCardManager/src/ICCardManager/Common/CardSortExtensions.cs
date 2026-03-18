using System;
using System.Collections.Generic;
using System.Linq;

namespace ICCardManager.Common
{
    /// <summary>
    /// カードソート順の拡張メソッド
    /// </summary>
    /// <remarks>
    /// Issue #1024: 4箇所以上に散在していた .OrderBy(CardType).ThenBy(CardNumber) パターンを集約。
    /// </remarks>
    public static class CardSortExtensions
    {
        /// <summary>
        /// カード種別→管理番号の既定順でソート
        /// </summary>
        /// <typeparam name="T">要素の型</typeparam>
        /// <param name="source">ソース</param>
        /// <param name="cardTypeSelector">カード種別セレクター</param>
        /// <param name="cardNumberSelector">管理番号セレクター</param>
        /// <returns>ソート済みシーケンス</returns>
        public static IOrderedEnumerable<T> OrderByCardDefault<T>(
            this IEnumerable<T> source,
            Func<T, string> cardTypeSelector,
            Func<T, string> cardNumberSelector)
        {
            return source.OrderBy(cardTypeSelector).ThenBy(cardNumberSelector);
        }

        /// <summary>
        /// 既存のソート済みシーケンスにカード種別→管理番号のセカンダリソートを追加
        /// </summary>
        /// <typeparam name="T">要素の型</typeparam>
        /// <param name="source">ソート済みソース</param>
        /// <param name="cardTypeSelector">カード種別セレクター</param>
        /// <param name="cardNumberSelector">管理番号セレクター</param>
        /// <returns>ソート済みシーケンス</returns>
        public static IOrderedEnumerable<T> ThenByCardDefault<T>(
            this IOrderedEnumerable<T> source,
            Func<T, string> cardTypeSelector,
            Func<T, string> cardNumberSelector)
        {
            return source.ThenBy(cardTypeSelector).ThenBy(cardNumberSelector);
        }
    }
}
