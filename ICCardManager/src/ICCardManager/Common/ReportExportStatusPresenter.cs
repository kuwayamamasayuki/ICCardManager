using System;
using ICCardManager.Dtos;

namespace ICCardManager.Common
{
    /// <summary>
    /// Issue #1691: 帳票の出力済み / 未出力状態の表示要素を
    /// 「アイコン＋短いテキスト＋スクリーンリーダー用詳細テキスト＋色リソースキー」に正規化する純粋関数クラス。
    /// </summary>
    /// <remarks>
    /// <see cref="LendingStatusPresenter"/> と同じ方針。CLAUDE.md の
    /// 「色・アイコン・テキストで状態を伝達（色のみに依存しない）」原則を満たすため、
    /// 一覧のチェックリストではアイコンとテキストを必ず併記する。
    /// 色はリテラルではなくリソースキー名を返し、XAML 側の
    /// <c>ResourceKeyToBrushConverter</c> でブラシへ解決する（Issue #1392 / #1461）。
    /// </remarks>
    public static class ReportExportStatusPresenter
    {
        /// <summary>出力済みのアイコン</summary>
        public const string ExportedIcon = "✅";

        /// <summary>未出力のアイコン</summary>
        public const string NotExportedIcon = "⬜";

        /// <summary>判定不能のアイコン</summary>
        public const string UnknownIcon = "❓";

        /// <summary>プリフライト警告のアイコン</summary>
        public const string WarningIcon = "⚠";

        /// <summary>
        /// 出力状況から表示要素一式を決定する。
        /// </summary>
        /// <param name="state">出力状況</param>
        /// <param name="lastWriteTime">年度ファイルの最終更新日時（出力済みの場合のみ意味を持つ）</param>
        /// <returns>アイコン・ラベル・説明文・色リソースキーを含む結果</returns>
        public static ReportExportStatusPresentation Resolve(
            ReportExportState state, DateTime? lastWriteTime = null)
        {
            switch (state)
            {
                case ReportExportState.Exported:
                    var timeText = DisplayFormatters.FormatDateTime(lastWriteTime, null);
                    return new ReportExportStatusPresentation(
                        state,
                        icon: ExportedIcon,
                        shortText: string.IsNullOrEmpty(timeText) ? "出力済み" : $"出力済み（{timeText}）",
                        accessibilityText: string.IsNullOrEmpty(timeText)
                            ? "この月の帳票は出力済みです"
                            : $"この月の帳票は出力済みです。年度ファイルの最終更新は {timeText} です",
                        brushKey: "SuccessForegroundBrush");

                case ReportExportState.NotExported:
                    return new ReportExportStatusPresentation(
                        state,
                        icon: NotExportedIcon,
                        shortText: "未出力",
                        accessibilityText: "この月の帳票はまだ出力されていません",
                        brushKey: "SecondaryTextBrush");

                default:
                    return new ReportExportStatusPresentation(
                        state,
                        icon: UnknownIcon,
                        shortText: "確認できません",
                        // 「なぜ」「どうすれば」を含める（.claude/rules/error-messages.md）
                        accessibilityText:
                            "出力状況を確認できません。出力先フォルダが存在しないか、" +
                            "年度ファイルを開けませんでした。出力先フォルダを指定し直してください",
                        brushKey: "MutedTextBrush");
            }
        }

        /// <summary>
        /// プリフライト警告件数から一覧行に付けるマーカー文字列を返す。
        /// </summary>
        /// <param name="warningCount">警告件数（0以下なら空文字）</param>
        public static string FormatWarningMarker(int warningCount)
        {
            return warningCount > 0 ? $"{WarningIcon} 警告{warningCount}件" : string.Empty;
        }

        /// <summary>
        /// プリフライト警告件数からスクリーンリーダー用の説明文を返す。
        /// </summary>
        /// <param name="warningCount">警告件数（0以下なら空文字）</param>
        public static string FormatWarningAccessibilityText(int warningCount)
        {
            return warningCount > 0
                ? $"事前チェックで{warningCount}件の警告が見つかりました。「事前チェック」ボタンで内容を確認してください"
                : string.Empty;
        }
    }

    /// <summary>
    /// Issue #1691: 帳票出力状況の表示要素セット
    /// </summary>
    public class ReportExportStatusPresentation
    {
        public ReportExportStatusPresentation(
            ReportExportState state,
            string icon,
            string shortText,
            string accessibilityText,
            string brushKey)
        {
            State = state;
            Icon = icon;
            ShortText = shortText;
            AccessibilityText = accessibilityText;
            BrushKey = brushKey;
        }

        /// <summary>出力状況</summary>
        public ReportExportState State { get; }

        /// <summary>アイコン（絵文字）</summary>
        public string Icon { get; }

        /// <summary>一覧に表示する短いテキスト</summary>
        public string ShortText { get; }

        /// <summary>スクリーンリーダー向けの完全な説明文</summary>
        public string AccessibilityText { get; }

        /// <summary>文字色として使うリソースキー名（色値リテラルは返さない）</summary>
        public string BrushKey { get; }
    }
}
