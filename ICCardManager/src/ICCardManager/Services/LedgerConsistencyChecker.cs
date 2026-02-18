using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;

namespace ICCardManager.Services
{
    /// <summary>
    /// 残高チェーンの整合性をチェック・修正するサービス（Issue #635）
    /// </summary>
    /// <remarks>
    /// 各行の残高が「前行の残高 + 受入 - 払出」と一致するかを検証します。
    /// 行の追加・削除・修正後に呼び出し、不整合があれば修正を提案します。
    /// Issue #785: 修正内容の詳細表示と元に戻す機能を追加。
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
        /// 指定期間の残高チェーンを再計算して修正
        /// </summary>
        /// <param name="cardIdm">カードIDm</param>
        /// <param name="fromDate">開始日</param>
        /// <param name="toDate">終了日</param>
        /// <returns>修正した項目のリスト（修正前後の残高を含む、undo用）</returns>
        public async Task<List<BalanceCorrection>> RecalculateBalancesAsync(
            string cardIdm, DateTime fromDate, DateTime toDate)
        {
            var ledgers = (await _ledgerRepository.GetByDateRangeAsync(cardIdm, fromDate, toDate))
                .OrderBy(l => l.Date)
                .ThenBy(l => l.Id)
                .ToList();

            if (ledgers.Count == 0) return new List<BalanceCorrection>();

            // 期間の直前のレコードから前残高を取得
            var previousLedger = await _ledgerRepository.GetLatestBeforeDateAsync(cardIdm, fromDate);
            var previousBalance = previousLedger?.Balance ?? 0;

            var corrections = new List<BalanceCorrection>();
            foreach (var ledger in ledgers)
            {
                var expectedBalance = previousBalance + ledger.Income - ledger.Expense;

                if (ledger.Balance != expectedBalance)
                {
                    corrections.Add(new BalanceCorrection
                    {
                        LedgerId = ledger.Id,
                        Date = ledger.Date,
                        Summary = ledger.Summary,
                        ActualBalance = ledger.Balance,
                        ExpectedBalance = expectedBalance
                    });

                    ledger.Balance = expectedBalance;
                    await _ledgerRepository.UpdateAsync(ledger);
                }

                previousBalance = ledger.Balance;
            }

            return corrections;
        }

        /// <summary>
        /// 残高再計算を元に戻す（Issue #785）
        /// </summary>
        /// <param name="corrections">RecalculateBalancesAsyncが返した修正リスト</param>
        /// <returns>元に戻した件数</returns>
        public async Task<int> UndoRecalculationAsync(List<BalanceCorrection> corrections)
        {
            if (corrections == null || corrections.Count == 0) return 0;

            var undoCount = 0;
            foreach (var correction in corrections)
            {
                var ledger = await _ledgerRepository.GetByIdAsync(correction.LedgerId);
                if (ledger != null)
                {
                    ledger.Balance = correction.ActualBalance;
                    await _ledgerRepository.UpdateAsync(ledger);
                    undoCount++;
                }
            }

            return undoCount;
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
                    result.Inconsistencies.Add(new BalanceCorrection
                    {
                        LedgerId = current.Id,
                        Date = current.Date,
                        Summary = current.Summary,
                        ActualBalance = current.Balance,
                        ExpectedBalance = expectedBalance
                    });
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
                    result.Inconsistencies.Add(new BalanceCorrection
                    {
                        LedgerId = ledger.Id,
                        Date = ledger.Date,
                        Summary = ledger.Summary,
                        ActualBalance = ledger.Balance,
                        ExpectedBalance = expectedBalance
                    });
                }

                previousBalance = ledger.Balance;
            }

            return result;
        }
    }

    /// <summary>
    /// 残高修正情報（表示用＋undo用）
    /// </summary>
    /// <remarks>
    /// Issue #785: 修正内容の詳細表示と元に戻す機能のためのデータ構造。
    /// </remarks>
    internal class BalanceCorrection
    {
        /// <summary>利用履歴ID</summary>
        public int LedgerId { get; set; }

        /// <summary>日付</summary>
        public DateTime Date { get; set; }

        /// <summary>摘要</summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>修正前の残高</summary>
        public int ActualBalance { get; set; }

        /// <summary>修正後の残高（期待される残高）</summary>
        public int ExpectedBalance { get; set; }
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
        /// 不整合箇所リスト
        /// </summary>
        public List<BalanceCorrection> Inconsistencies { get; set; } = new();
    }
}
