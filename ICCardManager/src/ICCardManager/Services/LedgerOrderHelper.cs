using System.Collections.Generic;
using System.Linq;
using ICCardManager.Models;

namespace ICCardManager.Services
{
    /// <summary>
    /// 残高チェーンに基づいてLedgerの同一日内順序を復元するヘルパー。
    /// Issue #784: チャージと利用の表示順をハードコードせず、残高の連鎖から時系列順を決定する。
    /// </summary>
    internal static class LedgerOrderHelper
    {
        /// <summary>
        /// 残高チェーンに基づいて同一日のLedgerを時系列順に並び替える。
        /// 新規購入・繰越レコードは常に先頭に配置される。
        /// </summary>
        /// <param name="ledgers">並び替え対象のLedger一覧</param>
        /// <param name="precedingBalance">前日（または前月）の最終残高。nullの場合は自動推定。</param>
        /// <returns>時系列順に並び替えられたLedgerリスト</returns>
        internal static List<Ledger> ReorderByBalanceChain(
            IEnumerable<Ledger> ledgers, int? precedingBalance = null)
        {
            if (ledgers == null)
            {
                return new List<Ledger>();
            }

            var sortedByDate = ledgers.OrderBy(l => l.Date).ThenBy(l => l.Id).ToList();

            if (sortedByDate.Count <= 1)
            {
                return sortedByDate;
            }

            var result = new List<Ledger>();
            var currentBalance = precedingBalance;

            foreach (var dayGroup in sortedByDate.GroupBy(l => l.Date.Date))
            {
                var dayRecords = dayGroup.ToList();

                // 特殊レコード（新規購入・繰越）は常に先頭
                var special = dayRecords
                    .Where(l => IsSpecialRecord(l))
                    .OrderBy(l => l.Id)
                    .ToList();
                var normal = dayRecords.Except(special).ToList();

                result.AddRange(special);

                if (normal.Count <= 1)
                {
                    result.AddRange(normal);
                    // 当日の最終残高を更新
                    var lastRecord = normal.LastOrDefault() ?? special.LastOrDefault();
                    if (lastRecord != null)
                    {
                        currentBalance = lastRecord.Balance;
                    }
                    continue;
                }

                // 残高チェーンを構築
                var startBalance = special.LastOrDefault()?.Balance ?? currentBalance;
                var ordered = ReconstructChain(normal, startBalance);
                result.AddRange(ordered);

                // 当日の最終残高を更新
                currentBalance = ordered.Last().Balance;
            }

            return result;
        }

        /// <summary>
        /// 残高チェーンを構築して同一日内のレコードを時系列順に並べる。
        /// balance_before = Balance + Expense - Income で処理前残高を逆算し、
        /// 前レコードのBalance = 次レコードのbalance_before となるチェーンを辿る。
        /// </summary>
        private static List<Ledger> ReconstructChain(List<Ledger> records, int? startBalance)
        {
            var items = records
                .Select(l => new ChainItem(l, l.Balance + l.Expense - l.Income))
                .ToList();

            var ordered = new List<Ledger>();
            var remaining = new List<ChainItem>(items);
            var current = startBalance;

            while (remaining.Count > 0)
            {
                ChainItem next = null;

                // startBalanceと一致するbalance_beforeを持つレコードを探す
                if (current.HasValue)
                {
                    next = remaining.FirstOrDefault(r => r.BalanceBefore == current.Value);
                }

                // 見つからない場合: balance_beforeが他レコードのBalanceに存在しないものを探す
                // （チェーンの開始点 = 前日の残高から始まるレコード）
                if (next == null && ordered.Count == 0)
                {
                    var balanceSet = new HashSet<int>(items.Select(i => i.Ledger.Balance));
                    next = remaining.FirstOrDefault(r => !balanceSet.Contains(r.BalanceBefore));
                }

                if (next == null)
                {
                    // チェーン構築失敗: ID順にフォールバック
                    ordered.AddRange(remaining.OrderBy(r => r.Ledger.Id).Select(r => r.Ledger));
                    break;
                }

                ordered.Add(next.Ledger);
                current = next.Ledger.Balance;
                remaining.Remove(next);
            }

            return ordered;
        }

        /// <summary>
        /// 新規購入または繰越レコードかどうかを判定する。
        /// </summary>
        private static bool IsSpecialRecord(Ledger l) =>
            l.Summary == "新規購入" || l.Summary.EndsWith("月から繰越");

        /// <summary>
        /// チェーン構築用の内部データ構造。
        /// </summary>
        private sealed class ChainItem
        {
            public Ledger Ledger { get; }
            public int BalanceBefore { get; }

            public ChainItem(Ledger ledger, int balanceBefore)
            {
                Ledger = ledger;
                BalanceBefore = balanceBefore;
            }
        }
    }
}
