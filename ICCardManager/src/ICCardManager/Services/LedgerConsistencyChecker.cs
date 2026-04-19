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
    /// Issue #1059: 詳細（LedgerDetail）レベルの残高チェーン検証も行います。
    /// </remarks>
    public class LedgerConsistencyChecker
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
            // Issue #1004: 同一日内の順序を残高チェーンで決定する
            // ID順だとポイント還元と利用の順序が残高推移と一致せず、
            // 偽の不整合が報告される場合がある
            var ledgers = LedgerOrderHelper.ReorderByBalanceChain(
                await _ledgerRepository.GetByDateRangeAsync(cardIdm, fromDate, toDate).ConfigureAwait(false));

            // Issue #1059: 詳細レベルのチェックのためにDetailsを読み込む
            if (ledgers.Count > 0)
            {
                var ledgerIds = ledgers.Select(l => l.Id).ToList();
                var detailsMap = await _ledgerRepository.GetDetailsByLedgerIdsAsync(ledgerIds).ConfigureAwait(false);
                foreach (var ledger in ledgers)
                {
                    if (detailsMap.TryGetValue(ledger.Id, out var details))
                    {
                        ledger.Details = details;
                    }
                }
            }

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

            // 親レコードレベルのチェック
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

            // Issue #1059: 詳細レベルのチェック
            CheckDetailConsistency(ledgers, result);

            return result;
        }

        /// <summary>
        /// Issue #1059: 詳細（LedgerDetail）レベルの残高チェーン整合性をチェック
        /// </summary>
        /// <remarks>
        /// 各Ledger内のDetail間、および連続するLedger間のDetail残高チェーンを検証します。
        /// 検証式: チャージ/ポイント還元の場合 → 前の残額 + 金額 = 次の残額
        ///         通常利用の場合 → 前の残額 - 金額 = 次の残額
        /// </remarks>
        internal static void CheckDetailConsistency(List<Ledger> ledgers, ConsistencyResult result)
        {
            // 全Ledgerの詳細を時系列順に連結してチェーン検証
            int? previousDetailBalance = null;
            int previousLedgerId = -1;

            foreach (var ledger in ledgers)
            {
                if (ledger.Details == null || ledger.Details.Count == 0)
                {
                    // 詳細がないLedgerの場合、親の残高を前残高として引き継ぐ
                    previousDetailBalance = ledger.Balance;
                    previousLedgerId = ledger.Id;
                    continue;
                }

                foreach (var detail in ledger.Details)
                {
                    if (!detail.Amount.HasValue || !detail.Balance.HasValue)
                    {
                        // 金額/残額がnullの詳細はスキップ
                        continue;
                    }

                    if (previousDetailBalance.HasValue)
                    {
                        var expected = CalculateExpectedDetailBalance(
                            previousDetailBalance.Value, detail);

                        if (detail.Balance.Value != expected)
                        {
                            result.IsConsistent = false;
                            result.DetailInconsistencies.Add(new DetailInconsistency
                            {
                                LedgerId = ledger.Id,
                                SequenceNumber = detail.SequenceNumber,
                                ExpectedBalance = expected,
                                ActualBalance = detail.Balance.Value
                            });
                        }
                    }

                    previousDetailBalance = detail.Balance.Value;
                    previousLedgerId = ledger.Id;
                }
            }
        }

        /// <summary>
        /// 詳細レコードの期待残高を計算
        /// </summary>
        internal static int CalculateExpectedDetailBalance(int previousBalance, LedgerDetail detail)
        {
            if (detail.IsCharge || detail.IsPointRedemption)
            {
                // チャージ・ポイント還元: 残高が増加
                return previousBalance + detail.Amount.Value;
            }
            else
            {
                // 通常利用（鉄道・バス）: 残高が減少
                return previousBalance - detail.Amount.Value;
            }
        }
    }

    /// <summary>
    /// 残高整合性チェック結果
    /// </summary>
    public class ConsistencyResult
    {
        /// <summary>
        /// 整合性があるかどうか
        /// </summary>
        public bool IsConsistent { get; set; }

        /// <summary>
        /// 不整合箇所リスト（LedgerId, ExpectedBalance, ActualBalance）
        /// </summary>
        public List<(int LedgerId, int ExpectedBalance, int ActualBalance)> Inconsistencies { get; set; } = new();

        /// <summary>
        /// Issue #1059: 詳細レベルの不整合箇所リスト
        /// </summary>
        public List<DetailInconsistency> DetailInconsistencies { get; set; } = new();
    }

    /// <summary>
    /// Issue #1059: 詳細レベルの残高不整合情報
    /// </summary>
    public class DetailInconsistency
    {
        /// <summary>
        /// 親LedgerのID
        /// </summary>
        public int LedgerId { get; set; }

        /// <summary>
        /// 詳細のシーケンス番号（rowid）
        /// </summary>
        public int SequenceNumber { get; set; }

        /// <summary>
        /// 期待される残高
        /// </summary>
        public int ExpectedBalance { get; set; }

        /// <summary>
        /// 実際の残高
        /// </summary>
        public int ActualBalance { get; set; }
    }
}
