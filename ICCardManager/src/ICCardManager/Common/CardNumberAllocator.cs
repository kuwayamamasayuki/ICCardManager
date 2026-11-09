#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace ICCardManager.Common
{
    /// <summary>
    /// 管理番号の採番（Issue #2419）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 新規登録の採番（<c>CardRepository.GetNextCardNumberAsync</c>）と、移行時の重複の番号ずらし
    /// （<c>Migration_008_AddCardTypeNumberUniqueIndex.ResolveDuplicates</c>。Migration_012 も使う）が共有する（#1763）。
    /// どちらも同じ種別の <c>MAX(CAST(card_number AS INTEGER))</c>（削除済みを含む）から次の番号を決める。
    /// </para>
    /// <para>
    /// 1. <c>CAST(... AS INTEGER)</c> は 64 ビット整数を返す。管理番号は最大 20 文字で 2,147,483,647 を超える
    ///    数字だけの番号もあり得るため、long で受ける（int へ変換すると <see cref="OverflowException"/>）。
    /// 2. SQLite の CAST は long の範囲を超える数字列を <see cref="long.MaxValue"/> で頭打ちにするため、
    ///    その「最大＋1」は表せない（unchecked では long.MinValue へ折り返し、同じ番号が重なる）。
    ///    そのときは、その種別で文字列として使われていない最小の正の整数を返す。
    /// 3. 通常は「最大＋1」のまま（既存の採番の結果を変えない）。使用中の番号は 2. のときだけ読む。
    /// </para>
    /// </remarks>
    public static class CardNumberAllocator
    {
        /// <summary>
        /// <c>MAX(CAST(card_number AS INTEGER))</c> の結果を long へ変換する（カードが無ければ 0）。
        /// </summary>
        public static long ParseMax(object? scalar)
        {
            return scalar is null || scalar == DBNull.Value
                ? 0L
                : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 「最大＋1」が 64 ビット整数で表せるか（表せなければ <see cref="Next"/> は使用中の番号を読む）。
        /// </summary>
        public static bool CanIncrement(long max) => max < long.MaxValue;

        /// <summary>
        /// 次の管理番号を返す。「最大＋1」が表せないときだけ <paramref name="loadUsedNumbers"/> を呼ぶ。
        /// </summary>
        /// <param name="max">同じ種別の <c>MAX(CAST(card_number AS INTEGER))</c>（<see cref="ParseMax"/> の結果）</param>
        /// <param name="loadUsedNumbers">同じ種別のカード（削除済みを含む）の管理番号を返す関数</param>
        public static string Next(long max, Func<IEnumerable<string>> loadUsedNumbers)
        {
            if (loadUsedNumbers is null)
            {
                throw new ArgumentNullException(nameof(loadUsedNumbers));
            }

            return CanIncrement(max)
                ? (max + 1).ToString(CultureInfo.InvariantCulture)
                : SmallestUnusedPositiveNumber(loadUsedNumbers());
        }

        /// <summary>
        /// <paramref name="usedNumbers"/> に文字列として含まれない最小の正の整数を返す。
        /// </summary>
        /// <remarks>
        /// 比較は文字列で行う（UNIQUE インデックスは文字列の一致で重複を判定する）。
        /// 削除済みのカードの番号も渡すのは、そのカードを復元したときに重複にしないため。
        /// 使われている番号は有限なので、必ず未使用の番号が見つかる。
        /// </remarks>
        public static string SmallestUnusedPositiveNumber(IEnumerable<string> usedNumbers)
        {
            var used = new HashSet<string>(usedNumbers, StringComparer.Ordinal);
            for (var candidate = 1L; ; candidate++)
            {
                var text = candidate.ToString(CultureInfo.InvariantCulture);
                if (!used.Contains(text))
                {
                    return text;
                }
            }
        }
    }
}
