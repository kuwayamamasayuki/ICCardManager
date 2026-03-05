using System;
using System.Collections.Generic;
using System.Linq;
using ICCardManager.Models;

namespace ICCardManager.Common
{
    /// <summary>
    /// LedgerDetailを残高チェーンに基づいて時系列順（古い→新しい）にソートするユーティリティ。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ICカード利用履歴の表示順は、SQLiteのrowidではなく残高チェーンで決定する。
    /// これにより、挿入順序に依存しない安定した時系列表示が可能になる。
    /// </para>
    /// <para>
    /// アルゴリズム:
    /// 各明細の「処理前残高 (balance_before)」を逆算し、
    /// 前の明細のBalance == 次の明細のbalance_before となるチェーンを辿る。
    /// </para>
    /// <list type="bullet">
    /// <item>利用: balance_before = Balance + Amount（利用前は残高が多い）</item>
    /// <item>チャージ: balance_before = Balance - Amount（チャージ前は残高が少ない）</item>
    /// </list>
    /// </remarks>
    internal static class LedgerDetailChronologicalSorter
    {
        /// <summary>
        /// LedgerDetailを時系列順（古い→新しい）にソートする。
        /// </summary>
        /// <param name="details">ソート対象の明細リスト</param>
        /// <param name="preserveOrderOnFailure">
        /// チェーン構築失敗時の動作。
        /// true: 入力順序を維持（DB読み取り時向け）。
        /// false: リストを逆順にする（FeliCa入力向け、入力が新しい→古いの場合）。
        /// </param>
        /// <returns>時系列順にソートされた新しいリスト</returns>
        internal static List<LedgerDetail> Sort(
            IEnumerable<LedgerDetail> details, bool preserveOrderOnFailure = true)
        {
            var detailList = details.ToList();

            if (detailList.Count <= 1)
                return new List<LedgerDetail>(detailList);

            // balance_before を計算:
            // 利用（expense）: balance_before = Balance + Amount
            // チャージ（income）: balance_before = Balance - Amount
            var items = detailList
                .Where(d => d.Balance.HasValue && d.Amount.HasValue)
                .Select(d =>
                {
                    var balanceBefore = d.IsCharge
                        ? d.Balance!.Value - d.Amount!.Value
                        : d.Balance!.Value + d.Amount!.Value;
                    return (Detail: d, BalanceBefore: balanceBefore);
                })
                .ToList();

            // Balance/Amount情報が不十分な場合はフォールバック
            if (items.Count < detailList.Count)
            {
                return Fallback(detailList, preserveOrderOnFailure);
            }

            // チェーン構築: balance_before が他のどのdetailの Balance にも一致しないものが先頭
            var balanceSet = new HashSet<int>(items.Select(i => i.Detail.Balance!.Value));
            var remaining = new List<(LedgerDetail Detail, int BalanceBefore)>(items);

            var start = remaining.FirstOrDefault(r => !balanceSet.Contains(r.BalanceBefore));
            if (start.Detail == null)
            {
                // チェーン構築失敗: フォールバック
                return Fallback(detailList, preserveOrderOnFailure);
            }

            var ordered = new List<LedgerDetail> { start.Detail };
            remaining.Remove(start);
            var currentBalance = start.Detail.Balance!.Value;

            while (remaining.Count > 0)
            {
                var next = remaining.FirstOrDefault(r => r.BalanceBefore == currentBalance);
                if (next.Detail == null)
                {
                    // チェーン途切れ: 残りをBalance降順で追加
                    ordered.AddRange(remaining.OrderByDescending(r => r.BalanceBefore).Select(r => r.Detail));
                    break;
                }

                ordered.Add(next.Detail);
                currentBalance = next.Detail.Balance!.Value;
                remaining.Remove(next);
            }

            return ordered;
        }

        /// <summary>
        /// チェーン構築失敗時のフォールバック処理。
        /// </summary>
        private static List<LedgerDetail> Fallback(
            List<LedgerDetail> detailList, bool preserveOrderOnFailure)
        {
            if (preserveOrderOnFailure)
            {
                // DB読み取り時: 既存のSQL ORDER BY結果を維持
                return new List<LedgerDetail>(detailList);
            }

            // FeliCa入力時: 新しい→古い順を逆転して古い→新しい順に
            var fallback = new List<LedgerDetail>(detailList);
            fallback.Reverse();
            return fallback;
        }
    }
}
