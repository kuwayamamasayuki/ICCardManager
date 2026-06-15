namespace ICCardManager.Views.Helpers
{
    /// <summary>
    /// 職員証認証ダイアログ（<see cref="Dialogs.StaffAuthDialog"/>）のタイムアウト残り秒数表示を
    /// 組み立てる純粋ロジック（Issue #1613）。
    /// </summary>
    /// <remarks>
    /// development-conventions.md の「色・アイコン・テキスト・音の4要素で状態を伝達」原則に基づき、
    /// 残り時間が警告閾値以下になったら ⚠ アイコンを文言へ前置し、赤色（DangerTextBrush）への
    /// 色変化のみに依存しないようにする。コードビハインド（WPF Window）は単体テストが困難なため、
    /// 表示判定・文言生成を WPF 非依存の純粋関数として切り出して単体テスト可能にしている。
    /// </remarks>
    internal static class AuthTimeoutDisplay
    {
        /// <summary>残り時間の警告閾値（秒）。これ以下で視覚（色・アイコン）＋聴覚（警告音）の補助伝達を行う。</summary>
        public const int WarningThresholdSeconds = 10;

        /// <summary>
        /// 残り秒数が警告域（1〜<see cref="WarningThresholdSeconds"/>秒）かどうか。
        /// 0 以下はタイムアウト確定であり警告域には含めない。
        /// </summary>
        public static bool IsWarning(int remainingSeconds)
            => remainingSeconds > 0 && remainingSeconds <= WarningThresholdSeconds;

        /// <summary>
        /// 残り秒数の表示文言。警告域では ⚠ アイコンを前置し、色以外の手段でも警告を伝える。
        /// 負値は 0 に丸める（カウントダウンが 0 を跨いだ瞬間の表示崩れ防止）。
        /// </summary>
        public static string FormatRemaining(int remainingSeconds)
        {
            var seconds = remainingSeconds < 0 ? 0 : remainingSeconds;
            return IsWarning(seconds) ? $"⚠ {seconds}秒" : $"{seconds}秒";
        }
    }
}
