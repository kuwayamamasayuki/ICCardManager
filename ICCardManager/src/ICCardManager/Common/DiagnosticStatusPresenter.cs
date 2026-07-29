using ICCardManager.Dtos;

namespace ICCardManager.Common
{
    /// <summary>
    /// <see cref="DiagnosticStatus"/> を画面・コピー結果の表示要素へ写像する（Issue #1690）
    /// </summary>
    /// <remarks>
    /// アイコン・ラベル・色を 1 か所に集約することで、ダイアログの表示と
    /// クリップボードへコピーしたテキストで判定の呼び方がずれるのを防ぐ。
    /// 色・アイコン・テキストの 3 要素で状態を伝える UI/UX 原則にも対応する
    /// （色のみに依存しない）。
    /// </remarks>
    public static class DiagnosticStatusPresenter
    {
        /// <summary>
        /// 判定を表すアイコン
        /// </summary>
        public static string GetIcon(DiagnosticStatus status)
        {
            switch (status)
            {
                case DiagnosticStatus.Ok:
                    return "✔";
                case DiagnosticStatus.Warning:
                    return "⚠";
                case DiagnosticStatus.Error:
                    return "✖";
                default:
                    return "－";
            }
        }

        /// <summary>
        /// 判定を表す日本語ラベル
        /// </summary>
        public static string GetLabel(DiagnosticStatus status)
        {
            switch (status)
            {
                case DiagnosticStatus.Ok:
                    return "正常";
                case DiagnosticStatus.Warning:
                    return "警告";
                case DiagnosticStatus.Error:
                    return "異常";
                default:
                    return "対象外";
            }
        }

        /// <summary>
        /// 判定に対応する文字色のリソースキー名
        /// </summary>
        /// <remarks>
        /// 色値リテラルを直接返さず、<c>AccessibilityStyles.xaml</c> のブラシキー名を返す。
        /// XAML 側は <c>ResourceKeyToBrushConverter</c> で解決する（Issue #1392、#1461）。
        /// </remarks>
        public static string GetForegroundResourceKey(DiagnosticStatus status)
        {
            switch (status)
            {
                case DiagnosticStatus.Ok:
                    return "SuccessForegroundBrush";
                case DiagnosticStatus.Warning:
                    return "WarningForegroundBrush";
                case DiagnosticStatus.Error:
                    return "ErrorForegroundBrush";
                default:
                    return "SecondaryTextBrush";
            }
        }

        /// <summary>
        /// 総合判定を利用者向けに言い換えた文言（ダイアログ上部の見出し用）
        /// </summary>
        public static string GetOverallSummary(DiagnosticStatus status)
        {
            switch (status)
            {
                case DiagnosticStatus.Ok:
                    return "すべて正常です";
                case DiagnosticStatus.Warning:
                    return "注意が必要な項目があります";
                case DiagnosticStatus.Error:
                    return "異常が見つかりました";
                default:
                    return "診断結果がありません";
            }
        }
    }
}
