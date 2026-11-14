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
            if (window == null)
            {
                throw new ArgumentNullException(nameof(window));
            }

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
        /// 戻り値を捨てて呼ばない。クリック・キー入力を伴う操作の前には <see cref="RequireForeground"/>、
        /// 撮影の前には <see cref="RequireForegroundForCapture"/> を使う（どちらも前面にできなければ例外。Issue #2478）。
        /// 失敗時の切り分け用の撮影（<see cref="CaptureForDiagnosis"/>）だけは、前面にできなくても撮る。
        /// 呼び出し元がこの 2 つと <see cref="CaptureForDiagnosis"/>（戻り値を記録に使う）に限られることは <c>ScreenshotForegroundConventionTests</c> が固定する。
        /// </remarks>
        /// <returns>前面化できたら true。</returns>
        public static bool BringToForeground(Window window)
        {
            if (window == null)
            {
                throw new ArgumentNullException(nameof(window));
            }

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
                if (handle != IntPtr.Zero && GetForegroundWindow() == handle)
                {
                    return true;
                }
            }
            // ハンドルが取れない場合も失敗として返す。ここで true を返すと、前面化できないときに
            // 止めるための RequireForeground が素通りし（fail-open）、続く Click / Enter が
            // 実際に前面にある別のウィンドウへ注入される（コードレビューで検出）。
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
            var foreground = DescribeWindow(GetForegroundWindow());
            throw new InvalidOperationException(
                "アプリのウィンドウを前面にできません。" +
                (foreground == NoForegroundWindow
                    ? "前面にウィンドウがありません（切り替えの途中など）。他のウィンドウを閉じるか最小化してから、撮影をやり直してください。"
                    : $"前面にあるウィンドウ「{foreground}」が管理者権限で動いている可能性があります。" +
                      "そのウィンドウを閉じるか最小化してから、撮影をやり直してください。"));
        }

        /// <summary>
        /// 撮影の前に、ウィンドウを前面にする。できなければ画像を保存せずに例外にする（Issue #2478）。
        /// </summary>
        /// <remarks>
        /// 前面にできないまま撮ると、タイトルバーが灰色（非アクティブ）の画像が正規の名前で保存され、撮影テストは合格する。
        /// 実測では、タスクバー（<c>Shell_TrayWnd</c>、explorer.exe）が前面を保持したまま、撮影した画像のタイトルバーが
        /// すべて非アクティブになり、目で確かめるまで気付けなかった。撮り直せば直る状況なので、誤った画像を残すより失敗させる。
        /// 失敗したときは出力先にある同名の画像（前回の撮影の残り）も消す。撮影中の確認（<see cref="EnsureStillForeground"/>）の
        /// 失敗と扱いをそろえ、「撮れなかったのに正規の名前の画像がある」状態を残さない（撮影スクリプトは画像の有無で撮れたと数える）。
        /// </remarks>
        /// <returns>前面にしたウィンドウのハンドル（保存の直前にも前面かを確かめるために使う）。</returns>
        internal static IntPtr RequireForegroundForCapture(Window window, string fileName)
        {
            try
            {
                EnsureForegroundForCapture(
                    () => BringToForeground(window), () => DescribeWindow(GetForegroundWindow()), fileName);
            }
            catch (InvalidOperationException)
            {
                DeleteCapturedImage(fileName);
                throw;
            }
            return window.Properties.NativeWindowHandle.ValueOrDefault;
        }

        /// <summary>撮れなかった画像を正規の名前で残さないよう、出力先の同名の画像を消す（消せなくても例外で失敗は伝わる）。</summary>
        private static void DeleteCapturedImage(string fileName)
        {
            try { File.Delete(Path.Combine(OutputDirectory, fileName)); } catch { /* 消せなくても、続く例外で失敗は伝わる */ }
        }

        /// <summary>前面にあるウィンドウの種類（失敗時の案内を変える）。</summary>
        private enum ForegroundKind
        {
            /// <summary>前面のウィンドウが無い（切り替えの途中など）。</summary>
            None,

            /// <summary>タスクバー。閉じる・最小化することはできず、一時的に前面を保持していることが多い。</summary>
            Taskbar,

            /// <summary>その他のウィンドウ（管理者権限なら OS が前面化を拒否する）。</summary>
            Other,
        }

        private static ForegroundKind ClassifyForeground(string description) =>
            description == NoForegroundWindow ? ForegroundKind.None
            : description.Contains("クラス Shell_TrayWnd）") || description.Contains("クラス Shell_SecondaryTrayWnd）") ? ForegroundKind.Taskbar
            : ForegroundKind.Other;

        /// <summary>
        /// <see cref="RequireForegroundForCapture"/> の判定。前面化と前面のウィンドウの説明を差し替えられる継ぎ目
        /// （<c>ScreenshotForegroundTests</c> が実ウィンドウなしで検証する）。
        /// </summary>
        /// <param name="bringToForeground">前面化を試み、できたら true を返す（1 回だけ呼ぶ）。</param>
        /// <param name="describeForeground">前面にあるウィンドウの説明（失敗したときだけ呼ぶ）。</param>
        /// <param name="fileName">撮影する画像のファイル名（文言の「何が」）。</param>
        internal static void EnsureForegroundForCapture(
            Func<bool> bringToForeground, Func<string> describeForeground, string fileName)
        {
            if (bringToForeground())
            {
                return;
            }
            var foreground = describeForeground();
            var (holder, remedy) = ClassifyForeground(foreground) switch
            {
                ForegroundKind.None => ("前面にウィンドウが無い（切り替えの途中など）ため",
                    "一時的な状態なので、そのまま撮影をやり直してください。"),
                ForegroundKind.Taskbar => ($"タスクバー「{foreground}」が前面を保持しているため",
                    "タスクバーが前面を保持するのは一時的なことが多いので、そのまま撮影をやり直してください。"),
                _ => ($"前面にあるウィンドウ「{foreground}」が前面を保持しているため",
                    "一時的なら撮り直せば直りますが、そのウィンドウが管理者権限で動いていると OS が前面化を拒否するため" +
                    "撮り直しても直りません。そのウィンドウを閉じるか最小化してから、撮影をやり直してください。"),
            };
            throw new InvalidOperationException(
                $"撮影するウィンドウを前面にできませんでした（{fileName}）。{holder}、" +
                "このまま撮るとタイトルバーが灰色（非アクティブ）の画像になるので保存しません。" + remedy);
        }

        /// <summary>
        /// 保存の直前に、撮影するウィンドウがまだ前面にあるかを確かめる（Issue #2478 のコードレビューで追加）。
        /// </summary>
        /// <remarks>
        /// 前面化を確かめてから保存するまでに、描画を待つ撮り直し（最長 <see cref="PaintRetryTimeout"/>）が挟まる。
        /// その間に前面を奪われると、撮影前の判定を通ったまま灰色の画像が保存されるため、撮るたびに撮影の前後で確かめる。
        /// 前面が一瞬だけ別のウィンドウ・NULL になる切り替えの途中で失敗させないよう、合わなければ
        /// <see cref="StillForegroundReadInterval"/> おきに <see cref="StillForegroundReads"/> 回まで読み直す
        /// （一瞬の揺れかは推測で、実測した事象ではない。読み直しても合わなければ、灰色の画像を保存するより失敗させる）。
        /// </remarks>
        /// <param name="expected">前面にあるはずのウィンドウ。<see cref="IntPtr.Zero"/> なら確かめない（前面にしない撮影）。</param>
        /// <param name="readForeground">いま前面にあるウィンドウを読む。</param>
        /// <param name="describeForeground">前面にあるウィンドウの説明（失敗したときだけ呼ぶ）。</param>
        /// <param name="fileName">撮影する画像のファイル名。</param>
        /// <param name="pause">読み直しの間の待ち（テストでは待たない）。</param>
        internal static void EnsureStillForeground(
            IntPtr expected, Func<IntPtr> readForeground, Func<IntPtr, string> describeForeground, string fileName,
            Action? pause = null)
        {
            if (expected == IntPtr.Zero)
            {
                return;
            }

            var actual = IntPtr.Zero;
            for (var read = 0; read < StillForegroundReads; read++)
            {
                if (read > 0)
                {
                    (pause ?? (() => Thread.Sleep(StillForegroundReadInterval)))();
                }
                actual = readForeground();
                if (actual == expected)
                {
                    return;
                }
            }

            var foreground = describeForeground(actual);
            var (taker, remedy) = ClassifyForeground(foreground) switch
            {
                ForegroundKind.None => ("前面のウィンドウが無くなったため", "撮影中はマウス・キーボードに触れず、撮影をやり直してください。"),
                ForegroundKind.Taskbar => ($"タスクバー「{foreground}」に前面を奪われたため",
                    "撮影中はマウス・キーボードに触れず、撮影をやり直してください。"),
                _ => ($"前面に移ったウィンドウ「{foreground}」に前面を奪われたため",
                    "撮影中はマウス・キーボードに触れず、前面を奪うウィンドウを閉じてから、撮影をやり直してください。"),
            };
            throw new InvalidOperationException(
                $"撮影中にウィンドウが前面でなくなりました（{fileName}）。{taker}、" +
                "タイトルバーが灰色（非アクティブ）の画像になるので保存しません。" + remedy);
        }

        /// <summary><see cref="EnsureStillForeground"/> が前面を読む回数の上限。</summary>
        internal const int StillForegroundReads = 4;

        /// <summary><see cref="EnsureStillForeground"/> の読み直しの間隔。</summary>
        private static readonly TimeSpan StillForegroundReadInterval = TimeSpan.FromMilliseconds(100);

        /// <summary><see cref="DescribeWindow"/> が前面のウィンドウが無いときに返す説明。</summary>
        internal const string NoForegroundWindow = "前面のウィンドウなし";

        /// <summary>切り分け用の撮影のファイル名の末尾。撮影スクリプトの公開対象から外れる名前。</summary>
        internal const string DiagnosisFileSuffix = "_FAILED.png";

        /// <summary>
        /// 失敗時の画面を、切り分け用に撮る（前面にできなくても撮る）。ファイル名は <see cref="DiagnosisFileSuffix"/> で終わること。
        /// </summary>
        /// <remarks>
        /// 履歴が開かない等の失敗の原因（管理者権限のウィンドウが前面にある等）を残すための撮影で、前面にできないことこそが
        /// 原因であり得る。<see cref="Capture(Window, string, bool)"/> の判定で例外にすると 1 枚も残らない（コードレビューで検出）。
        /// 非アクティブの画像が正規の名前で残らないよう、名前を <c>_FAILED.png</c> に限る（<c>take-screenshots-uitest.ps1</c> は
        /// 対応表の名前だけを公開するので、この名前は公開されない）。呼び出し元の名前は <c>ScreenshotForegroundConventionTests</c> も固定する。
        /// </remarks>
        public static string CaptureForDiagnosis(Window window, string fileName)
        {
            if (fileName == null || !fileName.EndsWith(DiagnosisFileSuffix, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"切り分け用の撮影のファイル名が「{fileName}」です。前面にできないまま撮った画像を正規の名前で残さないため、" +
                    $"「{DiagnosisFileSuffix}」で終わる名前を指定してください。",
                    nameof(fileName));
            }

            if (window == null)
            {
                throw new ArgumentNullException(nameof(window));
            }

            if (!BringToForeground(window))
            {
                Console.WriteLine(
                    $"[screenshot] {fileName}: 前面にできないまま撮影します（切り分け用。前面: {DescribeWindow(GetForegroundWindow())}）");
            }

            return CaptureRectangle(GetVisibleFrameBounds(window), fileName, IntPtr.Zero);
        }

        /// <summary>ウィンドウのタイトル・プロセス名・クラス名（失敗メッセージ用）。</summary>
        /// <remarks>
        /// タスクバー（<c>Shell_TrayWnd</c>）のようにタイトルを持たないウィンドウは、タイトルとプロセス名だけでは
        /// 何が前面にあるのか分からないため、クラス名を併記する（Issue #2478）。
        /// </remarks>
        internal static string DescribeWindow(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                // 前面のウィンドウが無い（切り替えの途中など）。GetWindowThreadProcessId が 0 を返し「Idle.exe」と読めてしまう
                return NoForegroundWindow;
            }

            var title = new System.Text.StringBuilder(256);
            _ = GetWindowText(handle, title, title.Capacity);
            var className = new System.Text.StringBuilder(256);
            _ = GetClassName(handle, className, className.Capacity);
            _ = GetWindowThreadProcessId(handle, out var pid);
            string process;
            try { process = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
            catch { process = "?"; }
            return $"{title}（{process}.exe、クラス {className}）";
        }

        /// <summary>ウィンドウを持つプロセスの ID（<c>GetWindowThreadProcessId</c>）。</summary>
        internal static uint ProcessIdOf(IntPtr handle)
        {
            _ = GetWindowThreadProcessId(handle, out var pid);
            return pid;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder className, int maxCount);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

        /// <summary>
        /// ウィンドウの中の 1 要素だけを PNG に保存する（Issue #2011）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 画面全体では説明したい箇所が小さくなりすぎる画像（カード一覧の状態表示、ステータスバーの
        /// リーダー接続状態）に使う。ウィンドウ単位の <see cref="Capture(Window, string, bool)"/> と違い、
        /// 要素の矩形は影・リサイズ枠を含まないので <c>DWMWA_EXTENDED_FRAME_BOUNDS</c> の補正は要らない。
        /// </para>
        /// <para>
        /// 前面化は所有ウィンドウに対して行う。要素だけを前面化する手段は無く、背面のまま撮ると
        /// 手前のウィンドウが写り込んだ「もっともらしく見えて誤った画像」ができる。前面にできなければ
        /// 撮らずに例外にする（<see cref="RequireForegroundForCapture"/>）。
        /// </para>
        /// </remarks>
        /// <param name="owner">要素を含むウィンドウ（前面化の対象）。</param>
        /// <param name="element">撮影する要素。</param>
        /// <param name="fileName">保存するファイル名。</param>
        public static string CaptureElement(Window owner, AutomationElement element, string fileName) =>
            CaptureElements(owner, fileName, element);

        /// <summary>
        /// 複数の要素をまとめて囲む矩形を PNG に保存する（Issue #2011）。
        /// </summary>
        /// <remarks>
        /// 説明したい UI が複数の要素に分かれている場合に使う。ステータスバーのカードリーダー接続状態は
        /// 文字列（TextBlock）と「再接続」ボタンの 2 要素で、これらを囲む <c>StatusBarItem</c> は
        /// <c>AutomationProperties.Name</c> を付けても UIA ツリーに現れない（実測）ため、
        /// 2 要素の合併矩形で撮る。
        /// </remarks>
        public static string CaptureElements(Window owner, string fileName, params AutomationElement[] elements)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }

            if (elements == null || elements.Length == 0)
            {
                throw new ArgumentException("撮影する要素を指定してください。", nameof(elements));
            }

            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ArgumentException("ファイル名を指定してください。", nameof(fileName));
            }

            var foreground = RequireForegroundForCapture(owner, fileName);  // 内側で SettleDelay ぶん待つ

            // 空の矩形は Union の前に弾く。Rectangle.Union は空の矩形（0,0,0,0）も 1 点として扱うため、
            // 畳んだ後は「画面の左上から対象までを覆う大きな矩形」になり、Width / Height の検査を素通りする。
            // 結果、名前は正しいのに中身が画面の切れ端という画像ができる（コードレビューで検出）。
            // 要素が Collapsed になった・まだ配置されていない場合に実際に空が返る。
            var bounds = Rectangle.Empty;
            for (var i = 0; i < elements.Length; i++)
            {
                var rect = elements[i].BoundingRectangle;
                if (rect.Width <= 0 || rect.Height <= 0)
                {
                    throw new InvalidOperationException(
                        $"撮影対象の要素に大きさがありません（{fileName} の {i + 1} 件目）。" +
                        "要素が画面外にあるか、非表示になったか、まだ描画されていません。" +
                        "メイン画面を左上へ寄せてから、要素の出現を待って撮影してください。");
                }
                bounds = bounds.IsEmpty ? rect : Rectangle.Union(bounds, rect);
            }

            return CaptureRectangle(bounds, fileName, foreground);
        }

        /// <summary>
        /// メイン画面とトースト通知の両方を含む矩形を PNG に保存する（Issue #2019）。
        /// </summary>
        /// <remarks>
        /// トースト通知は画面の隅（既定は右上）に出る別ウィンドウで、メイン画面の矩形の外にある。
        /// メイン画面を最大化して内側に収める案は、トーストが画面の内容と重なって「どこに出るのか」が
        /// 読み取りにくくなるため採らない。手動撮影の既存画像と同じく、2 つのウィンドウを囲む最小の矩形で撮る
        /// （間にデスクトップが写るが、その方が「メイン画面の外に通知が出る」ことが伝わる）。
        /// </remarks>
        public static string CaptureWithToast(Window mainWindow, Window toast, string fileName)
        {
            if (mainWindow == null)
            {
                throw new ArgumentNullException(nameof(mainWindow));
            }

            if (toast == null)
            {
                throw new ArgumentNullException(nameof(toast));
            }

            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ArgumentException("ファイル名を指定してください。", nameof(fileName));
            }

            // トーストは Topmost（ToastNotificationWindow.xaml）なので、メイン画面を前面化しても隠れない
            var foreground = RequireForegroundForCapture(mainWindow, fileName);
            var bounds = Rectangle.Union(GetVisibleFrameBounds(mainWindow), GetVisibleFrameBounds(toast));
            return CaptureRectangle(bounds, fileName, foreground);
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
        /// 前面にできなければ撮らずに例外にする（<see cref="RequireForegroundForCapture"/>）。
        /// </summary>
        /// <param name="window">撮影するウィンドウ。</param>
        /// <param name="fileName">保存するファイル名（例: <c>main.png</c>）。</param>
        /// <param name="bringToFront">
        /// 撮影前に前面化するか。トースト通知（フォーカスを奪わない別ウィンドウ）を撮るときは false にする。
        /// 前面化すると他のウィンドウの Z 順が変わり、メイン画面の上に載っているトーストが隠れることがある。
        /// false のときは前面かどうかを確かめない（トーストはタイトルバーを持たず、前面にしないのが正しい）。
        /// </param>
        public static string Capture(Window window, string fileName, bool bringToFront = true)
        {
            if (window == null)
            {
                throw new ArgumentNullException(nameof(window));
            }

            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ArgumentException("ファイル名を指定してください。", nameof(fileName));
            }

            var foreground = IntPtr.Zero;
            if (bringToFront)
            {
                foreground = RequireForegroundForCapture(window, fileName);  // 内側で SettleDelay ぶん待つ
            }
            else
            {
                // 前面化しない経路にも同じ待ちを入れる。トースト（ToastNotificationWindow）は
                // Opacity のフェードイン アニメーションを持つため、待たずに撮ると途中の状態が写る。
                Thread.Sleep(SettleDelay);
            }

            return CaptureRectangle(GetVisibleFrameBounds(window), fileName, foreground);
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

        /// <summary>
        /// 撮影した矩形が「描画済み」と言える程度に色を持つか（サンプルの最多色がこの割合未満か）。
        /// </summary>
        /// <remarks>
        /// WPF はウィンドウを表示してから最初の描画が終わるまでの間、クライアント領域が白いままになる。
        /// UIA の要素はその前から見えるため、要素の出現を待っても<b>未描画の白い画像</b>が撮れてしまう
        /// （実測: 別のビルドを並行させて負荷を掛けた撮影で、テストは全件成功しながら
        /// <c>main.png</c> / <c>operation_log.png</c> / <c>restore_list.png</c> 等が白紙になった）。
        /// 判定は実測値から決めた ― 白紙は 0.940〜0.952、正常な画像は 0.187〜0.726 で、
        /// 両者の間は広く空いている。要素だけを撮った画像（カード一覧 0.551・ステータスバー 0.436）も
        /// 正常側に収まる。
        /// </remarks>
        private const double PaintedDominantColorLimit = 0.90;

        /// <summary>描画の完了を待つ再試行の間隔と上限。</summary>
        private static readonly TimeSpan PaintRetryInterval = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan PaintRetryTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// 矩形を撮り、描画済みになるまで撮り直してから保存する。
        /// </summary>
        /// <remarks>
        /// 未描画を「失敗」ではなく「待てば直るもの」として扱う。時間内に描画されなければ、
        /// 診断用に <c>_FAILED.png</c> として残したうえで例外にする（公開対象からは
        /// <c>take-screenshots-uitest.ps1</c> の絞り込みが外す）。もっともらしく見えて中身の無い画像を
        /// そのままの名前で残さないことが要点。
        /// 撮るたびに、撮影するウィンドウがまだ前面にあるかも確かめる（<see cref="EnsureStillForeground"/>）。
        /// </remarks>
        /// <param name="requiredForeground">前面にあるはずのウィンドウ。<see cref="IntPtr.Zero"/> なら確かめない。</param>
        private static string CaptureRectangle(Rectangle bounds, string fileName, IntPtr requiredForeground)
        {
            var path = Path.Combine(OutputDirectory, fileName);
            var deadline = DateTime.UtcNow + PaintRetryTimeout;
            double dominant;

            void RequireStillForeground()
            {
                try
                {
                    EnsureStillForeground(requiredForeground, GetForegroundWindow, DescribeWindow, fileName);
                }
                catch (InvalidOperationException)
                {
                    // 撮った画像（前回の撮り直し・前回の撮影の分を含む）を正規の名前で残さない
                    DeleteCapturedImage(fileName);
                    throw;
                }
            }

            while (true)
            {
                RequireStillForeground();  // 撮る前（前回の撮り直しから今までに奪われていないか）
                using (var image = FlaUI.Core.Capturing.Capture.Rectangle(bounds))
                {
                    image.ToFile(path);
                }
                RequireStillForeground();  // 撮った後（撮っている最中に奪われていないか）

                dominant = DominantColorShare(path);
                if (dominant < PaintedDominantColorLimit)
                {
                    ReportSize(path, fileName);
                    return path;
                }
                if (DateTime.UtcNow >= deadline)
                {
                    break;
                }
                Thread.Sleep(PaintRetryInterval);
            }

            // 切り分け用の撮影（既に _FAILED.png）はそのまま残す（_FAILED_FAILED.png にしない）
            var failedPath = fileName.EndsWith(DiagnosisFileSuffix, StringComparison.Ordinal)
                ? path
                : Path.Combine(OutputDirectory, Path.GetFileNameWithoutExtension(fileName) + DiagnosisFileSuffix);
            if (failedPath != path)
            {
                try { File.Copy(path, failedPath, overwrite: true); File.Delete(path); } catch { /* 診断用なので失敗は無視 */ }
            }

            throw new InvalidOperationException(
                $"撮影した画像がほぼ単色です（{fileName}: 最多色 {dominant:P1}）。" +
                $"ウィンドウの描画が {PaintRetryTimeout.TotalSeconds} 秒以内に終わりませんでした。" +
                "撮影中は他のビルドやテストを走らせないでください。" +
                $"撮れた画像は {Path.GetFileName(failedPath)} に残しています。");
        }

        /// <summary>画像のサンプル画素のうち、最も多い色が占める割合。</summary>
        private static double DominantColorShare(string path)
        {
            using var bmp = new Bitmap(path);
            var counts = new System.Collections.Generic.Dictionary<int, int>();
            var samples = 0;
            // 4 画素おきに走査する（全画素を数えると 2457x1041 で 250 万回になる）
            for (var y = 0; y < bmp.Height; y += 4)
            {
                for (var x = 0; x < bmp.Width; x += 4)
                {
                    var argb = bmp.GetPixel(x, y).ToArgb();
                    counts.TryGetValue(argb, out var n);
                    counts[argb] = n + 1;
                    samples++;
                }
            }
            if (samples == 0)
            {
                return 1.0;
            }

            var max = 0;
            foreach (var n in counts.Values)
            {
                if (n > max)
                {
                    max = n;
                }
            }
            return (double)max / samples;
        }

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
