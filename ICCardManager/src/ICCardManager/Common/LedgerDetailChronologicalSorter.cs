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
    /// ICカード利用履歴の表示順は、ledger_detail.idではなく残高チェーンで決定する。
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

            return BuildChain(detailList, strict: false)
                ?? Fallback(detailList, preserveOrderOnFailure);
        }

        /// <summary>
        /// 残高チェーンだけで時系列順を確定できるときにその並びを返し、
        /// 曖昧・不完全なときは <c>null</c> を返す（Issue #1932）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="Sort"/> との違いは「解けなかったこと」を呼び出し元へ伝えるかどうか。
        /// <see cref="Sort"/> は必ず並びを返すためチェーンが途中で切れても残りを
        /// 残高降順で継ぎ足すが、本メソッドは以下のいずれかに当たれば <c>null</c> を返す。
        /// </para>
        /// <list type="bullet">
        /// <item><description>Balance を持たない明細がある</description></item>
        /// <item><description>チェーンの先頭候補が 0 個または 2 個以上（開始点が定まらない）</description></item>
        /// <item><description>途中の後続候補が 0 個または 2 個以上（次が定まらない）</description></item>
        /// </list>
        /// <para>
        /// 呼び出し元は <c>null</c> を受けたら別の定義（SequenceNumber の規約等）へ倒すこと。
        /// 「解けたことにして半端な並びを返す」と、その並びから採った残額が 6 年保存の台帳へ入る。
        /// </para>
        /// </remarks>
        /// <param name="details">対象の明細リスト</param>
        /// <returns>時系列順（古い→新しい）の新しいリスト。確定できないときは <c>null</c></returns>
        internal static List<LedgerDetail>? TrySortByBalanceChain(IEnumerable<LedgerDetail> details)
        {
            var detailList = details.ToList();

            if (detailList.Count <= 1)
                return new List<LedgerDetail>(detailList);

            return BuildChain(detailList, strict: true);
        }

        /// <summary>
        /// 残高チェーンを構築する。構築できないときは <c>null</c> を返す。
        /// </summary>
        /// <param name="detailList">対象の明細リスト（2 件以上）</param>
        /// <param name="strict">
        /// true: 開始点・後続が一意に定まらない、またはチェーンが途中で切れたら <c>null</c>。
        /// false: 従来挙動（開始点は最初の候補、途中で切れたら残りを残高降順で継ぎ足す）。
        /// </param>
        private static List<LedgerDetail>? BuildChain(List<LedgerDetail> detailList, bool strict)
        {
            // balance_before を計算:
            // 残高増加（チャージ・ポイント還元）: balance_before = Balance - Amount
            // 残高減少（利用）: balance_before = Balance + Amount
            // Issue #964: Amount が null の場合は 0 として扱う（FeliCa最古レコード等で発生）
            // Issue #1004: IsPointRedemption もチャージと同様に残高が増加するため、
            //   Balance - Amount で計算する（FelicaCardReader.ParseHistoryData と同じ判定）
            var items = detailList
                .Where(d => d.Balance.HasValue)
                .Select(d =>
                {
                    var amount = d.Amount ?? 0;
                    var isIncomeTransaction = d.IsCharge || d.IsPointRedemption;
                    var balanceBefore = isIncomeTransaction
                        ? d.Balance!.Value - amount
                        : d.Balance!.Value + amount;
                    return (Detail: d, BalanceBefore: balanceBefore);
                })
                .ToList();

            // Balance情報が不十分な場合はフォールバック
            if (items.Count < detailList.Count)
            {
                return null;
            }

            // チェーン構築: balance_before が他のどのdetailの Balance にも一致しないものが先頭
            // Issue #964: Amount=null/0の場合 balance_before == Balance となるため、
            // 自分自身のBalanceではなく他のエントリのBalanceとのみ比較する
            var remaining = new List<(LedgerDetail Detail, int BalanceBefore)>(items);

            bool IsStartCandidate((LedgerDetail Detail, int BalanceBefore) r) =>
                !remaining.Any(other =>
                    !ReferenceEquals(other.Detail, r.Detail) &&
                    other.Detail.Balance!.Value == r.BalanceBefore);

            // Issue #1932: strict では開始点が 2 つ以上あるときも「確定できなかった」とする。
            // FirstOrDefault は候補が複数でも黙って 1 つ目を選ぶため、
            // 「解けた」と「たまたま最初の候補を選んだ」を呼び出し元が区別できない。
            if (strict && remaining.Count(IsStartCandidate) != 1)
            {
                return null;
            }

            var start = remaining.FirstOrDefault(r => IsStartCandidate(r));
            if (start.Detail == null)
            {
                // チェーン構築失敗
                return null;
            }

            var ordered = new List<LedgerDetail> { start.Detail };
            remaining.Remove(start);
            var currentBalance = start.Detail.Balance!.Value;

            while (remaining.Count > 0)
            {
                // Issue #1932: strict では後続候補が 2 つ以上あるときも確定できないとみなす。
                if (strict && remaining.Count(r => r.BalanceBefore == currentBalance) != 1)
                {
                    return null;
                }

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
