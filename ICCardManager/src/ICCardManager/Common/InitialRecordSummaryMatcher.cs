#nullable enable
using System;
using System.Linq;
using System.Text.RegularExpressions;
using ICCardManager.Models;
using ICCardManager.Services;

namespace ICCardManager.Common
{
    /// <summary>
    /// 種別（<c>ledger.initial_record_kind</c>）を持たない行について、摘要から導入行の種別を推定する（Issue #2371）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 導入行かどうかは行自身の <see cref="Ledger.InitialRecordKind"/> で判定する。導入行はカード登録でだけ作り、
    /// 手入力の行（履歴の「追加」・利用履歴 CSV の ID の無い新しい行）は摘要が導入行の文言でも導入行にしない。
    /// 本クラスを呼んでよいのは、列を足す前の行を 1 回だけ埋めるマイグレーション 013 と、行編集の「受入と払出が両方0円」の
    /// 注意（入力中の摘要の文字列を見るだけで、保存済みの行の判定ではない）の 2 か所だけ。静的検査
    /// （<c>InitialRecordSummaryComparisonConventionTests</c>）が本クラス以外での照合と、この 2 か所以外からの呼び出しを禁じる。
    /// </para>
    /// <para>
    /// 照合する文言は「現在の組織設定」と「設定の既定値（<see cref="SummaryTextOptions"/> の初期値）」の両方。
    /// 設定を変える前に登録した行は既定の文言、変えた後に登録した行は現在の文言を持つため。2 回以上変えた場合の
    /// 中間の文言は救えない（その行は導入行と認識されない。変更前の判定でも認識されていなかった行）。
    /// 「新規購入」は設定で変えられない固定の文言。
    /// </para>
    /// <para>
    /// <b>誤認の範囲</b>: 照合は摘要の完全一致（○月から繰越は組織設定の正規表現）なので、通常の行が導入行と認識されるのは
    /// 摘要がこれらの文言そのものである行（列を足す前に履歴の「追加」や CSV で同じ摘要を手入力した行）だけ。これは列を足す前の
    /// 判定（摘要と現在の文言の比較）が既に導入行として扱っていた行と同じ集合に、既定の文言の行を加えたものなので、
    /// 埋めた後の判定が埋める前より広がるのは「既定の文言のまま残った手入力の行」に限られる。
    /// 貸出中レコード（<c>is_lent_record = 1</c>）は対象にしない。
    /// </para>
    /// </remarks>
    public static class InitialRecordSummaryMatcher
    {
        /// <summary>「新規購入」の摘要（設定で変えられない固定の文言。<c>BuildInitialLedgerAsync</c> が書く）</summary>
        public const string NewPurchaseSummary = "新規購入";

        private static readonly SummaryTextOptions DefaultSummaryText = new();

        /// <summary>
        /// 摘要から導入行の種別を推定する。導入行の文言でなければ <c>null</c>。
        /// </summary>
        /// <param name="summary">保存済み（または CSV）の摘要</param>
        /// <param name="currentSummaryText">現在の組織設定の摘要テキスト。<c>null</c> なら既定の文言だけと照合する</param>
        public static InitialRecordKind? Infer(string? summary, SummaryTextOptions? currentSummaryText)
        {
            if (summary is null || summary.Length == 0)
            {
                return null;
            }

            if (string.Equals(summary, NewPurchaseSummary, StringComparison.Ordinal))
            {
                return InitialRecordKind.NewPurchase;
            }

            var wordings = currentSummaryText is null
                ? new[] { DefaultSummaryText }
                : new[] { currentSummaryText, DefaultSummaryText };

            if (wordings.Any(w => w.CarryoverFromPreviousYear is { Length: > 0 } text
                && string.Equals(summary, text, StringComparison.Ordinal)))
            {
                return InitialRecordKind.FiscalYearStartCarryover;
            }

            if (wordings.Any(w => MatchesMidYearCarryoverPattern(summary, w.MidYearCarryoverPattern)))
            {
                return InitialRecordKind.MidYearCarryover;
            }

            return null;
        }

        /// <summary>
        /// 現在の組織設定（<see cref="SummaryGenerator.GetCurrentSummaryText"/>）と既定の文言で推定する。
        /// 行編集の「両方0円」の注意（入力中の摘要）が使う。
        /// </summary>
        public static InitialRecordKind? InferWithCurrentSettings(string? summary) =>
            Infer(summary, SummaryGenerator.GetCurrentSummaryText());

        private static bool MatchesMidYearCarryoverPattern(string summary, string? pattern)
        {
            if (pattern is null || pattern.Length == 0)
            {
                return false;
            }

            try
            {
                return Regex.IsMatch(summary, pattern);
            }
            catch (ArgumentException)
            {
                // 不正な正規表現は照合しない（既定のパターンは別に照合する）
                return false;
            }
        }
    }
}
