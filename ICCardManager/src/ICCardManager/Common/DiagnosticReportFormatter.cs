using System;
using System.Globalization;
using System.Text;
using ICCardManager.Dtos;

namespace ICCardManager.Common
{
    /// <summary>
    /// 接続診断結果をクリップボード共有用のプレーンテキストへ整形する（Issue #1690）
    /// </summary>
    /// <remarks>
    /// <para>
    /// クリップボード API に触れない純粋な変換として切り出している。
    /// 出力文言は IT 担当への障害報告そのものになるため、単体テストで書式を固定する。
    /// </para>
    /// <para>
    /// 貼り付け先はメモ帳・メール本文を想定し、書式なしテキストとする。
    /// 罫線や表組みは等幅フォント以外で崩れるため使わない。
    /// </para>
    /// </remarks>
    public static class DiagnosticReportFormatter
    {
        /// <summary>
        /// 詳細行のインデント（項目行の <c>[異常] </c> 部分と視覚的に段差を付ける）
        /// </summary>
        private const string DetailIndent = "    ";

        /// <summary>
        /// 診断結果を共有用テキストへ整形する
        /// </summary>
        /// <param name="report">診断結果。null の場合は説明文のみを返す</param>
        /// <returns>クリップボードへ設定するテキスト</returns>
        public static string Format(DiagnosticReport report)
        {
            if (report == null)
                return "診断結果がありません。「再診断」を実行してから、もう一度コピーしてください。";

            var builder = new StringBuilder();

            AppendHeader(builder, report);
            builder.AppendLine();
            AppendItems(builder, report);

            return builder.ToString();
        }

        private static void AppendHeader(StringBuilder builder, DiagnosticReport report)
        {
            builder.AppendLine("■ " + AppConstants.SystemName + " 接続診断結果");
            builder.AppendLine("診断日時: " +
                report.DiagnosedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

            var overall = DiagnosticStatusPresenter.GetOverallSummary(report.OverallStatus);
            builder.AppendLine(report.ProblemCount > 0
                ? "総合判定: " + overall + "（対処が必要な項目 " + report.ProblemCount + "件）"
                : "総合判定: " + overall);

            builder.AppendLine("アプリバージョン: " + Fallback(report.AppVersion));
            builder.AppendLine("PC名: " + Fallback(report.MachineName));
            builder.AppendLine("OS: " + Fallback(report.OsDescription));
            builder.AppendLine("データベース: " + Fallback(report.DatabasePath) +
                "（" + (report.IsSharedMode ? "共有モード" : "ローカルモード") + "）");
        }

        private static void AppendItems(StringBuilder builder, DiagnosticReport report)
        {
            if (report.Items == null || report.Items.Count == 0)
            {
                builder.AppendLine("診断項目がありません。");
                return;
            }

            foreach (var item in report.Items)
            {
                if (item == null)
                    continue;

                builder.AppendLine(
                    "[" + DiagnosticStatusPresenter.GetLabel(item.Status) + "] " +
                    item.Title + ": " + Fallback(item.SummaryText));

                // 正常項目の詳細まで載せると、対処すべき箇所が埋もれて報告の価値が下がる。
                // 対処が必要な項目（警告・異常）に限って「なぜ・どうすれば」を続ける。
                if (item.IsProblem && !string.IsNullOrWhiteSpace(item.DetailText))
                    AppendIndented(builder, item.DetailText);
            }
        }

        /// <summary>
        /// 複数行になり得る詳細文をインデント付きで出力する
        /// </summary>
        private static void AppendIndented(StringBuilder builder, string text)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            foreach (var line in lines)
                builder.AppendLine(DetailIndent + line);
        }

        /// <summary>
        /// 未設定値をそのまま空欄にすると報告先が「欠落」と「未設定」を区別できないため補う
        /// </summary>
        private static string Fallback(string value) =>
            string.IsNullOrWhiteSpace(value) ? "不明" : value;
    }
}
