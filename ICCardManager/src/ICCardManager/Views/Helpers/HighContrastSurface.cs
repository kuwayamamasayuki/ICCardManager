using System.Windows;
using System.Windows.Media;
using ICCardManager.Common;

namespace ICCardManager.Views.Helpers
{
    /// <summary>
    /// 白いカード状の面（<c>SurfaceBrush</c>）を、ハイコントラスト時にシステムのウィンドウ背景色へ差し替える（Issue #2142）
    /// </summary>
    /// <remarks>
    /// <para>
    /// メイン画面の使い方ガイド・履歴表示エリア・月選択ポップアップ・ページ番号、帳票作成のカード一覧は
    /// <c>Background="White"</c> の直書きで、中の文字は色を指定せずシステムの文字色を継承していた。
    /// ハイコントラスト（黒）では文字色が白になるため、<b>白地に白文字</b>で何も読めなかった。
    /// </para>
    /// <para>
    /// 面の色を <c>SurfaceBrush</c>（<c>AccessibilityStyles.xaml</c>、通常時は白）へ寄せ、ハイコントラスト時は
    /// アプリのリソース辞書の最上位で同じキーを <see cref="SystemColors.WindowBrush"/> に上書きする。
    /// 最上位のキーはマージされた辞書より先に引かれるので、<c>DynamicResource</c> で参照している箇所へ届く。
    /// 通常時へ戻ったら上書きを取り除き、<c>AccessibilityStyles.xaml</c> の白へ戻す。
    /// </para>
    /// <para>
    /// アプリの実行中にハイコントラストを切り替えたときも追随する（<see cref="SystemParameters.StaticPropertyChanged"/>）。
    /// 通知の名前では絞らない — ハイコントラストのテーマ間（黒 ⇔ 白）の切り替えでは
    /// <see cref="SystemParameters.HighContrast"/> 自体は変わらないが、ウィンドウ背景色は変わる。
    /// <see cref="Apply"/> は冪等なので、どの通知で呼び直しても害は無い。
    /// </para>
    /// <para>
    /// 判断（<see cref="Apply"/>）は <see cref="ResourceDictionary"/> を受け取る形へ切り出してあり、
    /// <see cref="Application"/> を起動せずに単体テストで固定できる。結線はソーステキストの静的検査で固定する。
    /// </para>
    /// </remarks>
    public static class HighContrastSurface
    {
        /// <summary>
        /// 白いカード状の面のブラシキー（<c>AccessibilityStyles.xaml</c> で定義）
        /// </summary>
        internal const string SurfaceBrushKey = "SurfaceBrush";

        private static bool _registered;

        /// <summary>
        /// 現在の表示設定を適用し、以後の切り替えに追随する（起動時に 1 回呼ぶ）
        /// </summary>
        public static void Register(Application application)
        {
            if (_registered || application == null)
            {
                return;
            }
            _registered = true;

            Apply(application.Resources, SystemParameters.HighContrast, SystemColors.WindowBrush);

            SystemParameters.StaticPropertyChanged += (_, __) => Reapply(application);
        }

        private static void Reapply(Application application)
        {
            // 通知は OS の設定変更のメッセージから届くので通常は UI スレッドだが、リソース辞書は UI スレッドでしか
            // 触れないため、そうでないときは UI スレッドへ回す（例外は観測してログへ残す。Issue #1873）
            if (application.Dispatcher.CheckAccess())
            {
                Apply(application.Resources, SystemParameters.HighContrast, SystemColors.WindowBrush);
            }
            else
            {
                application.Dispatcher.InvokeAsyncObserved(
                    () => Apply(application.Resources, SystemParameters.HighContrast, SystemColors.WindowBrush),
                    "ハイコントラスト表示への追随");
            }
        }

        /// <summary>
        /// ハイコントラスト時は <paramref name="resources"/> の最上位へ <paramref name="systemWindowBrush"/> を置き、
        /// 通常時は取り除いて既定（マージされた辞書の白）へ戻す。
        /// </summary>
        internal static void Apply(ResourceDictionary resources, bool highContrast, Brush systemWindowBrush)
        {
            if (highContrast)
            {
                resources[SurfaceBrushKey] = systemWindowBrush;
            }
            else
            {
                // 最上位の上書きだけを取り除く（Remove はマージされた辞書には触れない）
                resources.Remove(SurfaceBrushKey);
            }
        }
    }
}
