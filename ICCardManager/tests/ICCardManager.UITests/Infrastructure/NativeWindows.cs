using System;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;

namespace ICCardManager.UITests.Infrastructure
{
    /// <summary>
    /// ウィンドウの Win32 レベルの状態（有効・無効、オーナー）を問い合わせる（Issue #2190）。
    /// </summary>
    /// <remarks>
    /// <c>ShowDialog()</c> も Win32 の <c>MessageBox</c> も、下位のウィンドウを <c>EnableWindow(FALSE)</c> で無効化する。
    /// WPF の <c>Window.IsEnabled</c>（UIA の IsEnabled）はこれを反映しないことがあるため（UT-089 のコードレビュー指摘）、
    /// 「押せない」ことは Win32 に直接問い合わせる。
    /// </remarks>
    internal static class NativeWindows
    {
        private const uint GwOwner = 4;

        /// <summary>UIA 要素のウィンドウ ハンドル。取れなければ例外にする（0 のまま比較すると偽の一致になる）。</summary>
        public static IntPtr HandleOf(AutomationElement window)
        {
            var handle = window.Properties.NativeWindowHandle.ValueOrDefault;
            if (handle == IntPtr.Zero)
            {
                throw new InvalidOperationException($"ウィンドウ「{window.Name}」のハンドルを取得できませんでした。");
            }

            return handle;
        }

        /// <summary>Win32 レベルで入力を受け付けるか（<c>IsWindowEnabled</c>）。</summary>
        public static bool IsEnabled(IntPtr handle) => IsWindowEnabled(handle);

        /// <summary>オーナー ウィンドウのハンドル（<c>GetWindow(GW_OWNER)</c>）。オーナーが無ければ <see cref="IntPtr.Zero"/>。</summary>
        public static IntPtr OwnerOf(IntPtr handle) => GetWindow(handle, GwOwner);

        /// <summary>前面のウィンドウのハンドル。</summary>
        public static IntPtr Foreground => GetForegroundWindow();

        /// <summary>ウィンドウを前面にする（<c>SetForegroundWindow</c>）。フォアグラウンド ロックで拒否され得るので、結果は呼び出し側で確かめる。</summary>
        public static void SetForeground(IntPtr handle) => _ = SetForegroundWindow(handle);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>
        /// ウィンドウの大きさを変える（<c>SetWindowPos</c>。位置と Z 順は変えない）。
        /// </summary>
        /// <remarks>
        /// UIA の Transform パターンの Resize は WPF のダイアログで効かなかった（実測: 幅が変わらない）。Win32 で直接変えると、
        /// WPF は <c>WM_GETMINMAXINFO</c> で <c>MinWidth</c> / <c>MinHeight</c> に止める。
        /// </remarks>
        public static void Resize(IntPtr handle, int width, int height)
        {
            const uint noMove = 0x0002;
            const uint noZOrder = 0x0004;
            const uint noActivate = 0x0010;
            _ = SetWindowPos(handle, IntPtr.Zero, 0, 0, width, height, noMove | noZOrder | noActivate);
        }

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        /// <summary>ウィンドウを所有するプロセスの ID。</summary>
        public static int ProcessIdOf(IntPtr handle)
        {
            _ = GetWindowThreadProcessId(handle, out var pid);
            return (int)pid;
        }

        /// <summary>ウィンドウのタイトルとプロセス名（失敗メッセージ・診断用）。</summary>
        public static string Describe(IntPtr handle)
        {
            var title = new System.Text.StringBuilder(256);
            _ = GetWindowText(handle, title, title.Capacity);
            _ = GetWindowThreadProcessId(handle, out var pid);
            string process;
            try { process = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
            catch { process = "?"; }
            return $"「{title}」（{process}）";
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
    }
}
