using System;
using System.Diagnostics;
using System.Threading;

namespace ICCardManager.UITests.Infrastructure
{
    /// <summary>
    /// 別のプロセスに小さなウィンドウを開いて前面に出し、アプリを背面に回す（Issue #2190）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// UT-089（Issue #1794）の故障は「呼び出しスレッドがフォアグラウンドでないとき」にだけ起きる。オーナーを渡さない
    /// <c>MessageBox</c> は <c>GetActiveWindow()</c> でオーナーを決め、背面のアプリではそれが NULL になるため、
    /// 下位のダイアログが押せるまま残る。アプリが前面にあると、オーナーを渡さない実装でも偶然オーナーが付いて
    /// テストは緑になる — 故障の条件を作らないテストは何も守らない。
    /// </para>
    /// <para>
    /// <b>「Invoke の前に背面へ回す」だけでは条件が作れない</b>（実測で判明）。UI Automation でボタンを Invoke すると、
    /// 背面にいたアプリがその直後に前面を取る（このウィンドウを testhost 内に作っても、別プロセスに作っても同じだった）。
    /// アプリが前面を取ったのを見届けてから <see cref="BringToFront"/> で前面を奪い返し、その後にアプリが出す画面を
    /// 背面で出させる。ウィンドウを別プロセス（PowerShell）に作るのは、実際の故障（職員が別のアプリへ切り替えた）に
    /// 条件をそろえるためである。
    /// </para>
    /// <para>
    /// 前面化はフォアグラウンド ロックに阻まれ得るので、ALT キーで最後の入力元になってから前面化する
    /// （<see cref="ScreenshotHelper.BringToForeground"/> と同じ定石）。前面化できなかったら例外にする —
    /// 前提が成立しないまま緑になるのを防ぐ。
    /// </para>
    /// </remarks>
    internal sealed class ForegroundDecoy : IDisposable
    {
        /// <summary>ウィンドウのタイトル。テストが失敗して残ったときに、何のウィンドウか分かるようにする。</summary>
        private const string DecoyTitle = "UI テスト用の前面ウィンドウ（Issue #2190）";

        private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

        private readonly Process _process;
        private IntPtr _handle;

        private ForegroundDecoy(Process process)
        {
            _process = process;
        }

        /// <summary>ウィンドウを開いて前面に出す。前面にできなければ例外。</summary>
        public static ForegroundDecoy Activate()
        {
            // PowerShell で WinForms のフォームを開く（Windows に標準で入っており、追加のビルド成果物が要らない）。
            // アプリのウィンドウ（左上へ寄る）と重ならない位置に置く。
            var script =
                "Add-Type -AssemblyName System.Windows.Forms; " +
                "$f = New-Object System.Windows.Forms.Form; " +
                $"$f.Text = '{DecoyTitle}'; " +
                "$f.StartPosition = 'Manual'; $f.Left = 1200; $f.Top = 600; $f.Width = 360; $f.Height = 120; " +
                "[System.Windows.Forms.Application]::Run($f)";
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("前面化用のウィンドウ（powershell.exe）を起動できませんでした。");

            var decoy = new ForegroundDecoy(process);
            try
            {
                var deadline = DateTime.Now + StartTimeout;
                while (decoy._handle == IntPtr.Zero && DateTime.Now < deadline && !process.HasExited)
                {
                    Thread.Sleep(200);
                    process.Refresh();
                    decoy._handle = process.MainWindowHandle;
                }

                if (decoy._handle == IntPtr.Zero)
                {
                    throw new TimeoutException($"前面化用のウィンドウが {StartTimeout.TotalSeconds} 秒以内に開きませんでした。");
                }

                decoy.BringToFront();
                return decoy;
            }
            catch
            {
                decoy.Dispose();
                throw;
            }
        }

        /// <summary>このウィンドウが前面にあるか。</summary>
        public bool IsForeground => _handle != IntPtr.Zero && NativeWindows.Foreground == _handle;

        /// <summary>
        /// もう一度前面に出す（職員が別のアプリへ切り替えた状況を作る）。前面にできなければ例外。
        /// </summary>
        /// <remarks>
        /// UI Automation の Invoke は、呼ばれた側（アプリ）へ前面化の権利を渡す（実測: 認証ダイアログのボタンを
        /// Invoke した直後に、背面にいたはずのアプリが前面を取った。おとりを別プロセスにしても同じ）。
        /// そのため「Invoke の前に背面へ回す」だけでは故障の条件が作れない。Invoke の後、アプリが次の画面を出す前に
        /// これを呼んで背面へ回し直す。
        /// </remarks>
        public void BringToFront()
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ALT);
                FlaUI.Core.Input.Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.ALT);
                NativeWindows.SetForeground(_handle);
                Thread.Sleep(300);
                if (IsForeground)
                {
                    return;
                }
            }

            throw new InvalidOperationException(
                "テスト用のウィンドウを前面にできず、アプリを背面に回せませんでした。" +
                "前面に管理者権限のウィンドウがあると OS が前面化を拒否します。そのウィンドウを閉じるか最小化してから、やり直してください。");
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill();
                    _process.WaitForExit(5000);
                }
            }
            catch
            {
                // 既に終了している
            }

            _process.Dispose();
        }
    }
}
