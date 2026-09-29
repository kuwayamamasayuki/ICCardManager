using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Helpers
{
    /// <summary>
    /// 処理中（<see cref="IBusyState.IsBusy"/>）のダイアログを、利用者の操作で閉じさせない（Issue #2141）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 処理中オーバーレイが塞ぐのはマウスのヒットテストだけで（#1761）、Esc（<c>IsCancel</c>）と
    /// タイトルバーの ✕・Alt+F4 は効いていた。閉じる処理に処理中の判定を持っていたのは
    /// <c>LedgerDetailDialog</c>（#1743）だけで、保存・取込・リストアの最中に画面を閉じると
    /// 処理は裏で続き、結果の表示先（ステータス欄・完了後の <c>DialogResult</c>）が失われた。
    /// </para>
    /// <para>
    /// <b><see cref="Window.Closing"/> では判定しない。</b>アプリ自身の「保存して閉じる」も処理中に起きる —
    /// 行編集は <c>IsBusy = true</c> のまま <c>IsSaved = true</c> を立て、その <c>PropertyChanged</c> で
    /// <c>Close()</c> する。<c>Closing</c> で処理中を拒むと、保存が終わってもダイアログが閉じなくなる。
    /// 止めたいのは利用者起点の閉じるだけなので、その入口で止める。
    /// </para>
    /// <list type="bullet">
    /// <item><description>✕・Alt+F4・システムメニューの「閉じる」は <c>WM_SYSCOMMAND(SC_CLOSE)</c> を経由する。
    /// <see cref="Window.Close"/> は <c>WM_CLOSE</c> を直接送るのでここを通らない</description></item>
    /// <item><description>Esc は <c>IsCancel</c> のボタン・<c>KeyBinding</c>・<c>KeyDown</c> のいずれで閉じる画面でも
    /// ウィンドウの <c>PreviewKeyDown</c> が最初に受け取る。処理中は握り潰す（<c>EditFormKeyPolicy</c> #2080 と同じ判断。
    /// 処理中でないときは触らないので、ドロップダウンを閉じる Esc 等の既存の消費者の順序は変わらない）</description></item>
    /// </list>
    /// <para>
    /// 全ウィンドウへクラスハンドラーで 1 度に掛ける（<see cref="Register"/>）。画面ごとに結線すると、
    /// 画面が増えたときに掛け忘れる（#1786「走査対象は性質から導出する」）。メイン画面は対象外 —
    /// 閉じることがアプリの終了を意味し、その扱いは <c>MainWindow</c> 自身の <c>SC_CLOSE</c> フック
    /// （終了確認、Issue #2143）が持つ。
    /// </para>
    /// <para>
    /// 判断は純関数（<see cref="ShouldBlockUserClose"/> / <see cref="IsUserCloseCommand"/> /
    /// <see cref="ShouldSwallowKey"/>）へ切り出してある。<see cref="Window"/> は STA 依存で xUnit から
    /// 実行できないため、判断は単体テストで、結線はソーステキストの静的検査で固定する。
    /// </para>
    /// </remarks>
    public static class BusyCloseGuard
    {
        internal const int WmSysCommand = 0x0112;
        internal const int ScClose = 0xF060;

        /// <summary>
        /// <c>WM_SYSCOMMAND</c> の wParam の下位 4 ビットはシステムが内部で使う（Win32 の仕様）
        /// </summary>
        private const long SysCommandMask = 0xFFF0;

        private static bool _registered;

        private static readonly DependencyProperty IsHookedProperty =
            DependencyProperty.RegisterAttached("IsHooked", typeof(bool), typeof(BusyCloseGuard), new PropertyMetadata(false));

        /// <summary>
        /// アプリ内のすべてのウィンドウへガードを掛ける（起動時に 1 回呼ぶ）
        /// </summary>
        public static void Register()
        {
            if (_registered)
            {
                return;
            }
            _registered = true;

            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
            EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnWindowPreviewKeyDown));
        }

        /// <summary>
        /// 利用者の操作による閉じるを止めるべきか
        /// </summary>
        /// <param name="dataContext">ウィンドウの DataContext</param>
        /// <param name="isMainWindow">メイン画面か</param>
        internal static bool ShouldBlockUserClose(object dataContext, bool isMainWindow)
            => !isMainWindow && dataContext is IBusyState busy && busy.IsBusy;

        /// <summary>
        /// ウィンドウメッセージが利用者の「閉じる」操作（✕・Alt+F4・システムメニュー）か
        /// </summary>
        internal static bool IsUserCloseCommand(int msg, IntPtr wParam)
            => msg == WmSysCommand && (wParam.ToInt64() & SysCommandMask) == ScClose;

        /// <summary>
        /// キー入力を握り潰すべきか（処理中の Esc）
        /// </summary>
        internal static bool ShouldSwallowKey(Key key, object dataContext, bool isMainWindow)
            => key == Key.Escape && ShouldBlockUserClose(dataContext, isMainWindow);

        private static bool IsMainWindow(Window window)
            => ReferenceEquals(Application.Current?.MainWindow, window);

        private static void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (!(sender is Window window) || (bool)window.GetValue(IsHookedProperty))
            {
                return;
            }

            var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            if (source == null)
            {
                return;
            }

            window.SetValue(IsHookedProperty, true);
            source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (IsUserCloseCommand(msg, wParam) && ShouldBlockUserClose(window.DataContext, IsMainWindow(window)))
                {
                    handled = true;
                }
                return IntPtr.Zero;
            });
        }

        private static void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is Window window && ShouldSwallowKey(e.Key, window.DataContext, IsMainWindow(window)))
            {
                e.Handled = true;
            }
        }
    }
}
