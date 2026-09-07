using System;
using System.Drawing;
using System.IO;
using System.Threading;
using FlaUI.Core.AutomationElements;

namespace ICCardManager.UITests.Infrastructure
{
    /// <summary>
    /// マニュアル用スクリーンショット（Issue #2016）の保存先と撮影手順。
    /// </summary>
    /// <remarks>
    /// 出力先は環境変数 <c>ICCARDMANAGER_SCREENSHOT_DIR</c>、未設定なら <c>docs/screenshots/auto/</c>（.gitignore 対象）。
    /// マニュアルが参照する <c>docs/screenshots/</c> へは直接書かず、人が見比べてから差し替える
    /// （DPI スケーリングやステータスバーの表示差で既存画像と見た目が変わり得るため）。
    /// </remarks>
    internal static class ScreenshotHelper
    {
        /// <summary>撮影を有効にする環境変数。<c>1</c> のときだけ撮影テストが走る。</summary>
        public const string EnableEnvironmentVariable = "ICCARDMANAGER_SCREENSHOT";

        /// <summary>出力先を上書きする環境変数。</summary>
        public const string OutputDirEnvironmentVariable = "ICCARDMANAGER_SCREENSHOT_DIR";

        /// <summary>前面化してから描画が落ち着くまでの待ち時間。</summary>
        private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(400);

        /// <summary>
        /// テストプロセス（testhost.exe）を DPI 対応にする。
        /// 既定の DPI 非対応のままだと Windows が座標を仮想化し、UIA の BoundingRectangle と GDI の画面コピーで
        /// 論理／物理座標が食い違って、キャプチャ範囲が画面左上からの矩形へずれることがある（実測: 同じテストで
        /// 1650x700 と 2475x1050 が交互に出た）。物理座標へ統一すると 150% 表示でも矩形とコピーが一致する。
        /// 既に設定済み（false が返る）の場合は何もしない。
        /// </summary>
        public static void EnsureProcessDpiAware()
        {
            try
            {
                // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
                if (!SetProcessDpiAwarenessContext(new IntPtr(-4)))
                {
                    SetProcessDPIAware();
                }
            }
            catch (EntryPointNotFoundException)
            {
                // Windows 10 1703 より前は SetProcessDpiAwarenessContext が無い
                try { SetProcessDPIAware(); } catch { /* ignore */ }
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDPIAware();

        /// <summary>
        /// ウィンドウを画面左上へ移動する。
        /// メイン画面（1650x700 論理）は 150% 表示では 2475x1050 物理になり、起動位置によっては右端・下端が画面外へ出る。
        /// 画面外の要素は FlaUI が <c>NoClickablePointException</c> を投げてクリックできず（実測）、
        /// キャプチャも画面外の部分が欠ける。ダイアログは所有者の中央に開くので、メイン画面を左上へ寄せれば
        /// ダイアログも画面内に収まる。
        /// </summary>
        public static void MoveToTopLeft(Window window)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));
            window.Move(0, 0);
            Thread.Sleep(SettleDelay);
        }

        /// <summary>
        /// ウィンドウを確実に前面（アクティブ）にする。
        /// </summary>
        /// <remarks>
        /// Windows は「最後に入力を受けたプロセス」以外からの <c>SetForegroundWindow</c> を拒否する（フォアグラウンド ロック）。
        /// PowerShell スクリプト経由で起動した testhost はこれに該当し、<see cref="Window.SetForeground"/> が効かないまま
        /// キー入力（Enter）が別のウィンドウへ流れて履歴が開かなかった（実測。WSL から直接起動したときは成功していた）。
        /// ALT キーを押して離すと自プロセスが最後の入力元になり、直後の <c>SetForegroundWindow</c> が通る（定石の回避策）。
        /// 前面化できたかは <c>GetForegroundWindow</c> で確かめ、失敗したら数回やり直す。
        /// 撮影だけなら前面化できなくても矩形は撮れるので例外にはしない。クリック・キー入力を伴う操作の前には
        /// <see cref="RequireForeground"/> を使う。
        /// </remarks>
        /// <returns>前面化できたら true。</returns>
        public static bool BringToForeground(Window window)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));
            var handle = window.Properties.NativeWindowHandle.ValueOrDefault;

            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0 || GetForegroundWindow() != handle)
                {
                    FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ALT);
                    FlaUI.Core.Input.Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.ALT);
                }
                window.SetForeground();
                if (handle != IntPtr.Zero && GetForegroundWindow() != handle)
                {
                    // SetForegroundWindow が拒否されたとき（前面が別プロセスのウィンドウで入力注入も届かない）の予備手段。
                    // Alt+Tab 相当の切替として扱われ、フォアグラウンド ロックの対象外になる
                    SwitchToThisWindow(handle, true);
                }
                Thread.Sleep(SettleDelay);
                if (handle == IntPtr.Zero || GetForegroundWindow() == handle)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// クリック・キー入力を伴う操作の前に、ウィンドウを前面にする。できなければ原因を名指しして例外にする。
        /// </summary>
        /// <remarks>
        /// 前面化できないのは、前面のウィンドウが管理者権限で動いている等、OS が前面化も入力注入も拒否している状況。
        /// 黙って続けるとクリック・キー入力が届かず「履歴が開かない」だけの分かりにくい失敗になるため、
        /// 何が前面にいるかを名指しして案内する（実測: 管理者権限の「Switch USB」が前面にあると必ず起きた）。
        /// </remarks>
        public static void RequireForeground(Window window)
        {
            if (BringToForeground(window))
            {
                return;
            }
            var foreground = GetForegroundWindow();
            throw new InvalidOperationException(
                $"アプリのウィンドウを前面にできません。前面にあるウィンドウ「{DescribeWindow(foreground)}」が" +
                "管理者権限で動いている可能性があります。そのウィンドウを閉じるか最小化してから、撮影をやり直してください。");
        }

        private static string DescribeWindow(IntPtr handle)
        {
            var title = new System.Text.StringBuilder(256);
            _ = GetWindowText(handle, title, title.Capacity);
            _ = GetWindowThreadProcessId(handle, out var pid);
            string process;
            try { process = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
            catch { process = "?"; }
            return $"{title}（{process}.exe）";
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

        /// <summary>
        /// ウィンドウを最大化する（Issue #2019）。
        /// トースト通知は画面の隅（既定は右上）に出る別ウィンドウなので、メイン画面を最大化しておくと
        /// トーストがメイン画面の矩形の内側に収まり、背景を含めずに 1 枚で撮れる。
        /// </summary>
        public static void Maximize(Window window)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));
            var pattern = window.Patterns.Window.PatternOrDefault;
            if (pattern != null && pattern.CanMaximize.ValueOrDefault)
            {
                pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized);
            }
            Thread.Sleep(SettleDelay);
        }

        /// <summary>撮影テストをスキップすべきか（環境変数 <see cref="EnableEnvironmentVariable"/> が <c>1</c> でない）。</summary>
        public static bool ShouldSkip =>
            !string.Equals(Environment.GetEnvironmentVariable(EnableEnvironmentVariable), "1", StringComparison.Ordinal);

        /// <summary>出力先ディレクトリ（存在しなければ作成する）。</summary>
        public static string OutputDirectory
        {
            get
            {
                var dir = Environment.GetEnvironmentVariable(OutputDirEnvironmentVariable);
                if (string.IsNullOrWhiteSpace(dir))
                {
                    dir = Path.Combine(ResolveProjectRoot(), "docs", "screenshots", "auto");
                }
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>マニュアルが参照している既存画像のディレクトリ（サイズ比較の参考用）。</summary>
        public static string PublishedDirectory =>
            Path.Combine(ResolveProjectRoot(), "docs", "screenshots");

        /// <summary>
        /// ウィンドウを前面化して PNG に保存し、保存先のパスを返す。
        /// 既存の同名画像が <see cref="PublishedDirectory"/> にあればサイズを併せて出力する。
        /// </summary>
        /// <param name="window">撮影するウィンドウ。</param>
        /// <param name="fileName">保存するファイル名（例: <c>main.png</c>）。</param>
        /// <param name="bringToFront">
        /// 撮影前に前面化するか。トースト通知（フォーカスを奪わない別ウィンドウ）を撮るときは false にする。
        /// 前面化すると他のウィンドウの Z 順が変わり、メイン画面の上に載っているトーストが隠れることがある。
        /// </param>
        public static string Capture(Window window, string fileName, bool bringToFront = true)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));
            if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("ファイル名を指定してください。", nameof(fileName));

            if (bringToFront)
            {
                BringToForeground(window);
            }

            var path = Path.Combine(OutputDirectory, fileName);
            var bounds = GetVisibleFrameBounds(window);
            using (var image = FlaUI.Core.Capturing.Capture.Rectangle(bounds))
            {
                image.ToFile(path);
            }

            ReportSize(path, fileName);
            return path;
        }

        /// <summary>
        /// ウィンドウの「実際に見えている」矩形（物理ピクセル）を返す。
        /// </summary>
        /// <remarks>
        /// Windows 10/11 のウィンドウは、見えないリサイズ用の枠と影の領域を <c>GetWindowRect</c>
        /// （FlaUI の <see cref="AutomationElement.BoundingRectangle"/> の元）に含むため、その矩形で撮ると
        /// 周囲に背景が数ピクセル写り込む。Snipping Tool の「ウィンドウ」モードと同じく DWM の
        /// <c>DWMWA_EXTENDED_FRAME_BOUNDS</c> で見えている枠の矩形を取る。取得できないときは従来の矩形へ戻す。
        /// </remarks>
        internal static Rectangle GetVisibleFrameBounds(Window window)
        {
            var fallback = window.BoundingRectangle;
            var handle = window.Properties.NativeWindowHandle.ValueOrDefault;
            if (handle == IntPtr.Zero)
            {
                return fallback;
            }

            // DWMWA_EXTENDED_FRAME_BOUNDS = 9
            var hr = DwmGetWindowAttribute(handle, 9, out var rect, System.Runtime.InteropServices.Marshal.SizeOf<RECT>());
            if (hr != 0 || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
            {
                return fallback;
            }

            return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

        private static void ReportSize(string capturedPath, string fileName)
        {
            var publishedPath = Path.Combine(PublishedDirectory, fileName);
            var captured = ReadImageSize(capturedPath);
            var published = File.Exists(publishedPath) ? ReadImageSize(publishedPath) : (Size?)null;

            Console.WriteLine(published.HasValue
                ? $"[screenshot] {fileName}: {captured.Width}x{captured.Height} （既存 {published.Value.Width}x{published.Value.Height}） → {capturedPath}"
                : $"[screenshot] {fileName}: {captured.Width}x{captured.Height} （既存なし） → {capturedPath}");
        }

        private static Size ReadImageSize(string path)
        {
            using var bmp = new Bitmap(path);
            return bmp.Size;
        }

        /// <summary>テスト DLL のディレクトリからプロジェクトルート（ICCardManager/）を解決する。</summary>
        private static string ResolveProjectRoot()
        {
            // tests/ICCardManager.UITests/bin/{Config}/net48/ → ICCardManager/
            return Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
        }
    }
}
