#nullable enable
using System;
using System.Globalization;
using ICCardManager.Models;

namespace ICCardManager.Services.Import
{
    /// <summary>
    /// 利用履歴 CSV の行が貸出中レコード（「（貸出中）」のプレースホルダ）を写したものかを判定する（Issue #2307）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 貸出中レコードは返却時に物理削除される一時的な行で、利用の記録ではない。以前は利用履歴 CSV の出力が
    /// 貸出中レコードを含めていたため、貸出中に出力した CSV を返却後に取り込むと、ID の行が DB に無いので新規として
    /// 追加され、<c>is_lent_record = 0</c> の通常の行（摘要「（貸出中）」・受入 0・払出 0）として 6 年保存の台帳へ残った。
    /// 物品出納簿は貸出中レコードをフラグで除外する（#2044）ので、この行は「0円／0円」の行として印字される。
    /// 出力は <see cref="CsvExportService"/> が貸出中レコードをフラグで除く。本クラスは、修正前に出力した CSV や
    /// 手で作った CSV に残る行を取り込みで拒否する二重化の側を担う。
    /// </para>
    /// <para>
    /// <b>摘要で判定する例外（#2044 の規約の例外）</b>: 台帳の行は貸出中かどうかを行自身のフラグ（<c>is_lent_record</c>）で
    /// 判定する（摘要は組織設定から生成した文字列で、設定を変えると旧文言の行がすり抜けるため）。CSV の行はこのフラグの
    /// 列を持たず、手掛かりは摘要しか無い。そのため判定をこのクラス 1 か所に置き、規約テスト
    /// （<c>LentRecordSummaryComparisonConventionTests</c>）はこのファイルだけを理由付きで対象外にしている。
    /// 設定を変えた後でも以前の CSV を拒否できるよう、現在の設定の文言に加えて既定の文言（「（貸出中）」）とも照合する。
    /// </para>
    /// <para>
    /// ID が指す既存の行が貸出中レコード（まだ返却していない）なら、その行の更新・スキップとして扱い、拒否しない
    /// （取り込みはフラグを既存の行から引き継ぐので、通常の行にはならない）。
    /// </para>
    /// </remarks>
    internal static class LedgerCsvLentRowPolicy
    {
        /// <summary>
        /// 組織設定を変えていないときの貸出中の摘要
        /// </summary>
        private static readonly string DefaultLendingSummaryText = new SummaryTextOptions().LendingSummary;

        /// <summary>
        /// CSV の行の摘要が貸出中レコードの摘要か
        /// </summary>
        /// <remarks>
        /// private にしている。台帳の行（フラグを持つ）の判定に流用すると #2044 の欠陥が再発するため、
        /// 外へは CSV の行エラー（<see cref="BuildError"/>）としてしか出さない（コードレビューで検出）。
        /// </remarks>
        private static bool IsLentPlaceholderSummary(string? summary)
        {
            if (summary is null)
            {
                return false;
            }

            var text = summary.Trim();
            // 設定値も CSV 側と同じく前後の空白を除いてから比べる（レビュー 2 回目で検出）
            return string.Equals(text, SummaryGenerator.GetLendingSummary()?.Trim(), StringComparison.Ordinal)
                || string.Equals(text, DefaultLendingSummaryText, StringComparison.Ordinal);
        }

        /// <summary>
        /// 貸出中レコードを写した行を、通常の行として取り込もうとしているときのエラー
        /// </summary>
        /// <param name="lineNumber">CSV の行番号</param>
        /// <param name="summary">行の摘要</param>
        /// <param name="existingLedger">利用履歴 ID が指す既存の行（ID が空欄・見つからないときは <c>null</c>）</param>
        /// <param name="leavesLedgerUnchanged">
        /// 行が台帳を変えないか（既存の行と全項目一致してスキップ・無変更の更新になる、または ID の行が無く重複キーが
        /// 既存の履歴と一致してスキップになる）。判定は取り込み・プレビューが共通の関数（<c>CsvImportService.LeavesLedgerUnchanged</c>）で行う
        /// </param>
        /// <returns>取り込んでよければ <c>null</c></returns>
        /// <remarks>
        /// <para>
        /// 台帳を変えない行は拒否しない（レビュー 3・4 回目で検出）。以前のバージョンの取り込みで通常の行として
        /// 入ってしまった「（貸出中）」の行（<c>is_lent_record = 0</c>）は今の出力でも CSV に出るため、拒否するとその CSV の往復
        /// （以前は全項目一致・重複キーの一致でスキップ）が取り込み全体の失敗に変わる。台帳は何も変わらないので、止める理由が無い。
        /// ID の無い行・ID の行が見つからない行も、重複キーが一致してスキップになるなら同じ扱いにする。
        /// </para>
        /// <para>
        /// 拒否する行は、原因ごとに直し方の違う文言にする: ID の行が DB に無い（返却で削除された貸出中レコードを写した行）／
        /// ID が既に混入した貸出中の行を指し内容が違う（混入した行は返却しても消えないので、履歴画面から削除してもらう）／
        /// ID が通常の履歴を指す（摘要を貸出中の摘要へ書き換える行）。
        /// </para>
        /// </remarks>
        internal static CsvImportError? BuildError(int lineNumber, string? summary, Ledger? existingLedger, bool leavesLedgerUnchanged)
        {
            if (!IsLentPlaceholderSummary(summary))
            {
                return null;
            }

            // まだ返却していない貸出中レコードそのもの（フラグは既存の行から引き継がれる）
            if (existingLedger?.IsLentRecord == true)
            {
                return null;
            }

            // 台帳を変えない行（スキップ・無変更の更新）。往復を止めない
            if (leavesLedgerUnchanged)
            {
                return null;
            }

            var text = (summary ?? string.Empty).Trim();
            string message;
            if (existingLedger == null)
            {
                message = $"摘要が「{text}」の行は、貸出中を示す一時的な行（返却すると削除されます）で利用の記録ではないため、" +
                          "取り込めません。この行をCSVから削除してから、もう一度取り込んでください。";
            }
            else if (IsLentPlaceholderSummary(existingLedger.Summary))
            {
                message = $"利用履歴ID {existingLedger.Id.ToString(CultureInfo.InvariantCulture)} は、以前の取り込みで通常の行として登録された" +
                          $"「{text}」の行で、返却しても削除されず物品出納簿に0円の行として出力されるため、内容を変えて取り込めません。" +
                          "履歴画面からその行を削除し、CSVからもこの行を削除してから、もう一度取り込んでください。";
            }
            else
            {
                message = $"利用履歴ID {existingLedger.Id.ToString(CultureInfo.InvariantCulture)} の履歴の摘要を、貸出中を示す「{text}」に" +
                          "書き換える行は、利用の記録ではない行を台帳に作るため取り込めません。" +
                          "CSVのこの行の摘要を元の摘要に戻すか、この行をCSVから削除してから、もう一度取り込んでください。";
            }

            return new CsvImportError
            {
                LineNumber = lineNumber,
                Message = message,
                Data = text
            };
        }
    }
}
