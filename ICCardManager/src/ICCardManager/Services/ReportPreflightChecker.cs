using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using System.Globalization;

namespace ICCardManager.Services
{
    /// <summary>
    /// 帳票出力前のプリフライトチェックを行うサービス（Issue #1688）
    /// </summary>
    /// <remarks>
    /// 月次帳票を出力する前に「未返却のまま月をまたぐカード」「負残高」「（貸出中）レコードの混在」
    /// 「繰越額と前月末残高の不一致」「受入 − 払出 = 残額 の不成立」を検出し、
    /// 出力後の目視確認による手戻りを削減する。
    ///
    /// 検証対象は <see cref="IReportDataBuilder.BuildAsync"/> が返す <see cref="MonthlyReportData"/>
    /// （＝帳票が実際に描画するデータ）とする。DB を別クエリで再集計すると
    /// 「帳票には出ているのにチェックは通る」という乖離が生まれるため。
    /// 例外は未返却検出のみで、ReportDataBuilder が「（貸出中）」レコードを除外するため
    /// <see cref="ILedgerRepository.GetAllLentRecordsAsync"/> から別途取得する。
    /// </remarks>
    public class ReportPreflightChecker
    {
        private readonly IReportDataBuilder _reportDataBuilder;
        private readonly ILedgerRepository _ledgerRepository;

        public ReportPreflightChecker(
            IReportDataBuilder reportDataBuilder,
            ILedgerRepository ledgerRepository)
        {
            _reportDataBuilder = reportDataBuilder;
            _ledgerRepository = ledgerRepository;
        }

        /// <summary>
        /// 指定カード群・対象年月についてプリフライトチェックを実行する
        /// </summary>
        /// <param name="cardIdms">対象カードのIDm一覧</param>
        /// <param name="year">対象年</param>
        /// <param name="month">対象月（1-12）</param>
        /// <returns>検出された警告を含むチェック結果</returns>
        public async Task<ReportPreflightResult> CheckAsync(
            IEnumerable<string> cardIdms, int year, int month)
        {
            var result = new ReportPreflightResult();
            if (cardIdms == null) return result;

            var targetIdms = cardIdms.Where(idm => !string.IsNullOrEmpty(idm)).Distinct().ToList();
            if (targetIdms.Count == 0) return result;

            // 貸出中レコードは全カード分を1回で取得する（カード数ぶんのクエリを避ける）
            var lentRecords = await _ledgerRepository.GetAllLentRecordsAsync().ConfigureAwait(false);
            var lentByCard = (lentRecords ?? new List<Ledger>())
                .Where(l => l != null && !string.IsNullOrEmpty(l.CardIdm))
                .GroupBy(l => l.CardIdm)
                .ToDictionary(g => g.Key, g => g.OrderBy(l => l.Date).First());

            foreach (var cardIdm in targetIdms)
            {
                var reportData = await _reportDataBuilder.BuildAsync(cardIdm, year, month).ConfigureAwait(false);
                if (reportData?.Card == null)
                {
                    // カードが見つからない場合は帳票自体が作られないため、ここでは何も報告しない
                    continue;
                }

                lentByCard.TryGetValue(cardIdm, out var lentRecord);
                CheckReportData(reportData, lentRecord, result);
            }

            return result;
        }

        /// <summary>
        /// 1カード分の帳票データを検証する（内部ロジック）
        /// </summary>
        /// <param name="data">帳票データ</param>
        /// <param name="lentRecord">当該カードの貸出中レコード（返却済みならnull）</param>
        /// <param name="result">検出結果の追加先</param>
        internal static void CheckReportData(
            MonthlyReportData data, Ledger lentRecord, ReportPreflightResult result)
        {
            CheckUnreturned(data, lentRecord, result);
            CheckNegativeBalance(data, result);
            CheckCarryoverMismatch(data, result);
            CheckTotalMismatch(data, result);
        }

        /// <summary>
        /// 未返却の貸出中レコードを検出する
        /// </summary>
        /// <remarks>
        /// 対象月より前に貸し出されたままなら <see cref="ReportPreflightIssueType.UnreturnedAcrossMonth"/>、
        /// 対象月内なら <see cref="ReportPreflightIssueType.LendingRecordInMonth"/> を報告する。
        /// 対象月より後の貸出は当月帳票に影響しないため報告しない。
        /// </remarks>
        internal static void CheckUnreturned(
            MonthlyReportData data, Ledger lentRecord, ReportPreflightResult result)
        {
            if (lentRecord == null) return;

            var monthStart = new DateTime(data.Year, data.Month, 1);
            var monthEnd = monthStart.AddMonths(1).AddDays(-1);
            var lentDate = lentRecord.Date.Date;

            if (lentDate < monthStart)
            {
                result.Warnings.Add(new ReportPreflightWarning
                {
                    CardIdm = data.Card.CardIdm,
                    CardDisplayName = data.Card.DisplayName,
                    IssueType = ReportPreflightIssueType.UnreturnedAcrossMonth,
                    Date = lentDate,
                    LedgerId = lentRecord.Id,
                    RowSummary = lentRecord.Summary,
                    DisplayText =
                        $"⚠️ {data.Card.DisplayName}: {lentDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} から未返却のまま{data.Month}月をまたいでいます",
                    DetailText =
                        $"未返却のカードは払出が帳票に計上されないため、{data.Month}月の残額が実際のカード残高と一致しません。" +
                        "カードを返却してから帳票を作成してください。"
                });
            }
            else if (lentDate <= monthEnd)
            {
                result.Warnings.Add(new ReportPreflightWarning
                {
                    CardIdm = data.Card.CardIdm,
                    CardDisplayName = data.Card.DisplayName,
                    IssueType = ReportPreflightIssueType.LendingRecordInMonth,
                    Date = lentDate,
                    LedgerId = lentRecord.Id,
                    RowSummary = lentRecord.Summary,
                    DisplayText =
                        $"⚠️ {data.Card.DisplayName}: {lentDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} の貸出が未返却です",
                    DetailText =
                        $"「{SummaryGenerator.GetLendingSummary()}」の履歴は帳票に出力されないため、" +
                        $"この利用分が{data.Month}月の帳票から欠落します。カードを返却してから帳票を作成してください。"
                });
            }
        }

        /// <summary>
        /// 残額がマイナスの行を検出する
        /// </summary>
        /// <remarks>
        /// 交通系ICカードの残高は物理的にマイナスにならないため、検出時点でデータ誤登録が確定している。
        /// 明細行に加えて月計・累計の残額も対象とする。
        /// </remarks>
        internal static void CheckNegativeBalance(
            MonthlyReportData data, ReportPreflightResult result)
        {
            foreach (var ledger in data.Ledgers.Where(l => l.Balance < 0))
            {
                result.Warnings.Add(new ReportPreflightWarning
                {
                    CardIdm = data.Card.CardIdm,
                    CardDisplayName = data.Card.DisplayName,
                    IssueType = ReportPreflightIssueType.NegativeBalance,
                    Date = ledger.Date,
                    LedgerId = ledger.Id,
                    RowSummary = ledger.Summary,
                    DisplayText =
                        $"⚠️ {data.Card.DisplayName}: {ledger.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}「{ledger.Summary}」の残額が " +
                        $"{ledger.Balance:N0}円（マイナス）です",
                    DetailText =
                        "交通系ICカードの残額はマイナスになりません。" +
                        "履歴画面で該当行の受入金額・払出金額を修正してください。"
                });
            }

            AddNegativeTotalWarning(data, data.MonthlyTotal, result);
            AddNegativeTotalWarning(data, data.CumulativeTotal, result);
        }

        /// <summary>
        /// 合計行（月計・累計）の残額がマイナスなら警告を追加する
        /// </summary>
        private static void AddNegativeTotalWarning(
            MonthlyReportData data, ReportTotalData total, ReportPreflightResult result)
        {
            if (total?.Balance == null || total.Balance.Value >= 0) return;

            result.Warnings.Add(new ReportPreflightWarning
            {
                CardIdm = data.Card.CardIdm,
                CardDisplayName = data.Card.DisplayName,
                IssueType = ReportPreflightIssueType.NegativeBalance,
                RowSummary = total.Label,
                DisplayText =
                    $"⚠️ {data.Card.DisplayName}:「{total.Label}」の残額が {total.Balance.Value:N0}円（マイナス）です",
                DetailText =
                    "交通系ICカードの残額はマイナスになりません。" +
                    "履歴画面で該当月の受入金額・払出金額を修正してください。"
            });
        }

        /// <summary>
        /// 繰越行の残額と先頭行の残高チェーンの接続を検証する
        /// </summary>
        /// <remarks>
        /// <see cref="LedgerConsistencyChecker"/> は「最初の行は前行がないためスキップ」するため、
        /// 繰越行と月の先頭行の接続だけが検証の空白地帯になっている。帳票では繰越行が描画されるので
        /// ここを埋める。
        ///
        /// 紙出納簿から年度途中で移行したカード（Issue #510）では先頭行が「○月から繰越」となり
        /// <c>Income = 残高</c> で保存されるため、通常の残高チェーン式が成立しない。スキップする。
        /// </remarks>
        internal static void CheckCarryoverMismatch(
            MonthlyReportData data, ReportPreflightResult result)
        {
            if (data.Carryover == null || data.Ledgers.Count == 0) return;

            var first = data.Ledgers[0];
            if (SummaryGenerator.IsMidYearCarryoverSummary(first.Summary)) return;

            var expected = data.Carryover.Balance + first.Income - first.Expense;
            if (first.Balance == expected) return;

            result.Warnings.Add(new ReportPreflightWarning
            {
                CardIdm = data.Card.CardIdm,
                CardDisplayName = data.Card.DisplayName,
                IssueType = ReportPreflightIssueType.CarryoverMismatch,
                Date = first.Date,
                LedgerId = first.Id,
                RowSummary = first.Summary,
                DisplayText =
                    $"⚠️ {data.Card.DisplayName}: 繰越額 {data.Carryover.Balance:N0}円 と先頭行の残額が一致しません" +
                    $"（期待 {expected:N0}円 / 実際 {first.Balance:N0}円）",
                DetailText =
                    $"「{data.Carryover.Summary}」の残額に先頭行の受入・払出を加減した額が、先頭行の残額と一致しません。" +
                    "前月の帳票と突き合わせて該当行を修正してください。"
            });
        }

        /// <summary>
        /// 月計・累計で「受入 − 払出 = 残額」が成立するかを検証する
        /// </summary>
        /// <remarks>
        /// <c>.claude/rules/business-logic.md</c>（Issue #1494）が定める不変条件の機械検証。
        /// 5月以降の月計は残額が null（帳票に出さない）ため、
        /// 「前月末残高 + 受入 − 払出 = 月末残高」の形で検算する。
        /// </remarks>
        internal static void CheckTotalMismatch(
            MonthlyReportData data, ReportPreflightResult result)
        {
            // 累計行（5月以降）／4月の月計行: 残額が帳票に出るのでそのまま検算する
            AddTotalMismatchWarning(data, data.CumulativeTotal, result);
            if (data.MonthlyTotal?.Balance != null)
            {
                AddTotalMismatchWarning(data, data.MonthlyTotal, result);
                return;
            }

            // 5月以降の月計: 残額が null のため残高チェーンで検算する
            if (data.MonthlyTotal == null) return;
            if (!data.PrecedingBalance.HasValue) return;       // 新規購入カードで前月末残高なし
            if (data.Ledgers.Count == 0) return;               // 月末残高が確定しない
            // 紙出納簿移行月は「○月から繰越」の受入が集計から除外される一方で残高チェーンには寄与するため
            // 「受入 − 払出 = 残額」が成立しない（Issue #510 / #1494）
            if (data.Ledgers.Any(l => SummaryGenerator.IsMidYearCarryoverSummary(l.Summary))) return;

            var monthEndBalance = data.Ledgers[data.Ledgers.Count - 1].Balance;
            var expected = data.PrecedingBalance.Value + data.MonthlyTotal.Income - data.MonthlyTotal.Expense;
            if (expected == monthEndBalance) return;

            result.Warnings.Add(new ReportPreflightWarning
            {
                CardIdm = data.Card.CardIdm,
                CardDisplayName = data.Card.DisplayName,
                IssueType = ReportPreflightIssueType.TotalMismatch,
                RowSummary = data.MonthlyTotal.Label,
                DisplayText =
                    $"⚠️ {data.Card.DisplayName}:「{data.MonthlyTotal.Label}」で 受入 − 払出 = 残額 が成立しません" +
                    $"（前月末残高 {data.PrecedingBalance.Value:N0}円 + 受入 {data.MonthlyTotal.Income:N0}円 − " +
                    $"払出 {data.MonthlyTotal.Expense:N0}円 ≠ 月末残額 {monthEndBalance:N0}円）",
                DetailText =
                    "帳票の検収では「受入 − 払出 = 残額」の成立が確認されます。" +
                    "履歴画面で残高の不整合を修正してください。"
            });
        }

        /// <summary>
        /// 残額が帳票に出る合計行について「受入 − 払出 = 残額」を検算する
        /// </summary>
        private static void AddTotalMismatchWarning(
            MonthlyReportData data, ReportTotalData total, ReportPreflightResult result)
        {
            if (total?.Balance == null) return;

            var expected = total.Income - total.Expense;
            if (expected == total.Balance.Value) return;

            result.Warnings.Add(new ReportPreflightWarning
            {
                CardIdm = data.Card.CardIdm,
                CardDisplayName = data.Card.DisplayName,
                IssueType = ReportPreflightIssueType.TotalMismatch,
                RowSummary = total.Label,
                DisplayText =
                    $"⚠️ {data.Card.DisplayName}:「{total.Label}」で 受入 − 払出 = 残額 が成立しません" +
                    $"（受入 {total.Income:N0}円 − 払出 {total.Expense:N0}円 ≠ 残額 {total.Balance.Value:N0}円）",
                DetailText =
                    "帳票の検収では「受入 − 払出 = 残額」の成立が確認されます。" +
                    "履歴画面で残高の不整合を修正してください。"
            });
        }
    }

    /// <summary>
    /// プリフライトチェックで検出する問題の種別（Issue #1688）
    /// </summary>
    public enum ReportPreflightIssueType
    {
        /// <summary>未返却のまま対象月をまたいでいる</summary>
        UnreturnedAcrossMonth,

        /// <summary>対象月内に未返却の貸出があり、帳票から除外される</summary>
        LendingRecordInMonth,

        /// <summary>残額がマイナス</summary>
        NegativeBalance,

        /// <summary>繰越額と先頭行の残高チェーンが不一致</summary>
        CarryoverMismatch,

        /// <summary>月計・累計で「受入 − 払出 = 残額」が不成立</summary>
        TotalMismatch
    }

    /// <summary>
    /// プリフライトチェックで検出した警告1件（Issue #1688）
    /// </summary>
    public class ReportPreflightWarning
    {
        /// <summary>対象カードIDm</summary>
        public string CardIdm { get; set; } = string.Empty;

        /// <summary>対象カードの表示名（例: "はやかけん 001"）</summary>
        public string CardDisplayName { get; set; } = string.Empty;

        /// <summary>問題の種別</summary>
        public ReportPreflightIssueType IssueType { get; set; }

        /// <summary>該当行の利用日（合計行など行に紐づかない場合はnull）</summary>
        public DateTime? Date { get; set; }

        /// <summary>該当行のLedgerId（合成行・合計行の場合はnull）</summary>
        public int? LedgerId { get; set; }

        /// <summary>該当行の摘要、または合計行のラベル</summary>
        public string RowSummary { get; set; } = string.Empty;

        /// <summary>一覧に表示する1行サマリ</summary>
        public string DisplayText { get; set; } = string.Empty;

        /// <summary>「なぜ」「どうすれば」を含む詳細説明</summary>
        public string DetailText { get; set; } = string.Empty;
    }

    /// <summary>
    /// プリフライトチェックの結果（Issue #1688）
    /// </summary>
    public class ReportPreflightResult
    {
        /// <summary>検出された警告一覧</summary>
        public List<ReportPreflightWarning> Warnings { get; } = new();

        /// <summary>警告が1件以上あるか</summary>
        public bool HasWarnings => Warnings.Count > 0;
    }
}
