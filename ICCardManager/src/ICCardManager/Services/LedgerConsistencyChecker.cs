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

            // Issue #2007: 導入時残高の誤りの形状なら、直すべき行と値を名指しする
            result.InitialBalanceCorrection = DetectInitialBalanceCorrection(ledgers, result);

            return result;
        }

        /// <summary>
        /// Issue #2007: 残高チェーンの不整合が「導入時（カード登録時）の残高の誤り」の形状かを判定し、
        /// 該当すれば後続の行から逆算した正しい導入時残高を返す。該当しなければ null。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 導入行（<see cref="Ledger.IsInitialRecord"/>）は手入力の繰越額がカード実残高より優先されて
        /// 書かれる（<c>CardManageViewModel.BuildInitialLedgerAsync</c>）。以後の利用行の残額はカードの
        /// 実残高に追随するため、初期残高の誤りは導入行 1 行に閉じ、チェーンは<b>導入行の直後の
        /// 1 か所だけ</b>で切れる。この形状を満たす条件:
        /// </para>
        /// <list type="bullet">
        /// <item>先頭行が導入行で、2 行目が存在する</item>
        /// <item>親レコードの不整合がちょうど 1 件で、それが 2 行目</item>
        /// <item>詳細レベルの不整合は無いか、2 行目の 1 件だけで差が親と一致する
        /// （導入行の残高を起点に検査される先頭明細の写像）</item>
        /// <item>逆算した残高（2 行目の残額 − 受入 ＋ 払出）が 0 以上</item>
        /// </list>
        /// <para>
        /// 従来はチェーンが切れた側（2 行目＝正しい行）だけがハイライトされ、利用者が正しい行を
        /// 誤った導入行に合わせて書き換える誘導になっていた。ここで名指しする「直すべき行」は導入行。
        /// 不整合が 2 か所以上ある・先頭が導入行でない・逆算が負になる形は、1 行の訂正では直らない
        /// （または導入時の誤りと決めつけられない）ので提案しない。
        /// </para>
        /// </remarks>
        internal static InitialBalanceCorrection DetectInitialBalanceCorrection(
            List<Ledger> ledgers, ConsistencyResult result)
        {
            if (ledgers.Count < 2) return null;

            var initial = ledgers[0];
            var next = ledgers[1];
            if (!initial.IsInitialRecord) return null;

            if (result.Inconsistencies.Count != 1 || result.Inconsistencies[0].LedgerId != next.Id) return null;

            var suggested = next.Balance - next.Income + next.Expense;
            if (suggested < 0) return null;

            var delta = initial.Balance - suggested;
            if (result.DetailInconsistencies.Count > 1) return null;
            if (result.DetailInconsistencies.Count == 1)
            {
                var detail = result.DetailInconsistencies[0];
                if (detail.LedgerId != next.Id) return null;
                if (detail.ExpectedBalance - detail.ActualBalance != delta) return null;
            }

            return new InitialBalanceCorrection(
                ledgerId: initial.Id,
                date: initial.Date,
                recordedBalance: initial.Balance,
                suggestedBalance: suggested,
                appliesToIncome: Ledger.InitialRecordCarriesIncome(initial.Summary));
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

        /// <summary>
        /// Issue #2007: 不整合が「導入時残高の誤り」の形状であるときの訂正案。該当しなければ null。
        /// </summary>
        /// <remarks>
        /// <see cref="LedgerConsistencyChecker.DetectInitialBalanceCorrection"/> が
        /// <see cref="Inconsistencies"/> / <see cref="DetailInconsistencies"/> から導出する。
        /// 導出値なので、それらを書き換えた後は再判定が要る。
        /// </remarks>
        public InitialBalanceCorrection InitialBalanceCorrection { get; set; }
    }

    /// <summary>
    /// Issue #2007: 導入時（カード登録時）の残高の誤りに対する訂正案
    /// </summary>
    /// <remarks>
    /// 不変オブジェクト。「直すべき行」「記録されている残高」「後続の行から逆算した残高」
    /// 「受入欄も一緒に直すか」を 1 つにまとめ、消費側（警告文言・ハイライト・行編集ダイアログ）が
    /// 別々に逆算し直さないようにする（#1763「同じ判断を配らない」）。
    /// </remarks>
    public sealed class InitialBalanceCorrection
    {
        public InitialBalanceCorrection(int ledgerId, DateTime date, int recordedBalance, int suggestedBalance, bool appliesToIncome)
        {
            if (suggestedBalance < 0) throw new ArgumentOutOfRangeException(nameof(suggestedBalance), suggestedBalance, "逆算した残高は 0 以上でなければならない");
            LedgerId = ledgerId;
            Date = date;
            RecordedBalance = recordedBalance;
            SuggestedBalance = suggestedBalance;
            AppliesToIncome = appliesToIncome;
        }

        /// <summary>直すべき導入行の ID</summary>
        public int LedgerId { get; }

        /// <summary>導入行の日付（履歴表示の期間をここから始めるために使う）</summary>
        public DateTime Date { get; }

        /// <summary>導入行に記録されている残高（誤っている疑いのある値）</summary>
        public int RecordedBalance { get; }

        /// <summary>直後の行から逆算した残高（直後の残額 − 受入 ＋ 払出）</summary>
        public int SuggestedBalance { get; }

        /// <summary>
        /// 受入欄も <see cref="SuggestedBalance"/> へ直すか。
        /// 新規購入・前年度より繰越は真（受入欄に残高を書く）、○月から繰越は偽（受入欄は空欄）。
        /// <see cref="Ledger.InitialRecordCarriesIncome"/> と同じ判断。
        /// </summary>
        public bool AppliesToIncome { get; }
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
        /// 詳細のシーケンス番号（id）
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
