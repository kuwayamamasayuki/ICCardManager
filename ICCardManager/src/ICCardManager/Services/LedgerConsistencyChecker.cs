using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;

namespace ICCardManager.Services
{
    /// <summary>
    /// 残高チェーンの整合性をチェックするサービス（Issue #635）
    /// </summary>
    /// <remarks>
    /// 各行の残高が「前行の残高 + 受入 - 払出」と一致するかを検証します。
    /// 行の追加・削除・修正後に呼び出し、不整合があれば警告を表示します。
    /// </remarks>
    internal class LedgerConsistencyChecker
    {
        private readonly ILedgerRepository _ledgerRepository;

        public LedgerConsistencyChecker(ILedgerRepository ledgerRepository)
        {
            _ledgerRepository = ledgerRepository;
        }

        /// <summary>
        /// 指定期間の残高チェーンの整合性をチェック
        /// </summary>
        /// <param name="cardIdm">カードIDm</param>
        /// <param name="fromDate">開始日</param>
        /// <param name="toDate">終了日</param>
        /// <returns>整合性チェック結果</returns>
        public async Task<ConsistencyResult> CheckBalanceConsistencyAsync(
            string cardIdm, DateTime fromDate, DateTime toDate)
        {
            var ledgers = (await _ledgerRepository.GetByDateRangeAsync(cardIdm, fromDate, toDate))
                .OrderBy(l => l.Date)
                .ThenBy(l => l.Id)
                .ToList();

            return CheckConsistency(ledgers, cardIdm, fromDate);
        }

        /// <summary>
        /// 残高チェーンの整合性をチェック（内部ロジック）
        /// </summary>
        internal ConsistencyResult CheckConsistency(
            List<Ledger> ledgers, string cardIdm, DateTime fromDate)
        {
            var result = new ConsistencyResult { IsConsistent = true };

            if (ledgers.Count == 0) return result;

            // 期間の直前のレコードから前残高を取得する処理は非同期なので、
            // 最初の行は前行がないためスキップし、2行目以降をチェック
            for (int i = 1; i < ledgers.Count; i++)
            {
                var previousBalance = ledgers[i - 1].Balance;
                var current = ledgers[i];
                var expectedBalance = previousBalance + current.Income - current.Expense;

                if (current.Balance != expectedBalance)
                {
                    result.IsConsistent = false;
                    result.Inconsistencies.Add((current.Id, expectedBalance, current.Balance));
                }
            }

            return result;
        }

        /// <summary>
        /// 前残高を含めた完全な整合性チェック（非同期版）
        /// </summary>
        internal async Task<ConsistencyResult> CheckConsistencyWithPreviousAsync(
            List<Ledger> ledgers, string cardIdm, DateTime fromDate)
        {
            var result = new ConsistencyResult { IsConsistent = true };

            if (ledgers.Count == 0) return result;

            // 期間の直前のレコードから前残高を取得
            var previousLedger = await _ledgerRepository.GetLatestBeforeDateAsync(cardIdm, fromDate);
            var previousBalance = previousLedger?.Balance ?? 0;

            foreach (var ledger in ledgers)
            {
                var expectedBalance = previousBalance + ledger.Income - ledger.Expense;

                if (ledger.Balance != expectedBalance)
                {
                    result.IsConsistent = false;
                    result.Inconsistencies.Add((ledger.Id, expectedBalance, ledger.Balance));
                }

                previousBalance = ledger.Balance;
            }

            return result;
        }
    }

    /// <summary>
    /// 残高整合性チェック結果
    /// </summary>
    internal class ConsistencyResult
    {
        /// <summary>
        /// 整合性があるかどうか
        /// </summary>
        public bool IsConsistent { get; set; }

        /// <summary>
        /// 不整合箇所リスト（LedgerId, ExpectedBalance, ActualBalance）
        /// </summary>
        public List<(int LedgerId, int ExpectedBalance, int ActualBalance)> Inconsistencies { get; set; } = new();
    }
}
