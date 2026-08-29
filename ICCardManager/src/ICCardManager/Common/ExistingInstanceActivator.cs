using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ICCardManager.Common
{
    /// <summary>
    /// 起動済みインスタンスのウィンドウを前面へ出す（Issue #1910）
    /// </summary>
    /// <remarks>
    /// 二重起動を弾くだけでは、職員が起動アイコンを押した理由（＝画面を見たい）が満たされない。
    /// 起動済みの画面を前面へ出せたなら、それ自体が「新しく起動しなかった」ことの可視の応答になる。
    /// 出せなかったときだけ <see cref="SingleInstanceNotice"/> の案内を表示する。
    /// </remarks>
    public static class ExistingInstanceActivator
    {
        /// <summary>
        /// 同一セッションで起動している同名プロセスのメインウィンドウを前面へ出す
        /// </summary>
        /// <returns>前面へ出せたら <c>true</c>。対象が見つからない・前面化に失敗したら <c>false</c>。</returns>
        public static bool TryActivateExistingInstance()
        {
            try
            {
                using (var current = Process.GetCurrentProcess())
                {
                    var target = ExistingInstanceLocator.SelectActivationTarget(
                        CollectCandidates(current.ProcessName),
                        current.Id,
                        current.SessionId);

                    return target.HasValue && BringToFront(target.Value.MainWindowHandle);
                }
            }
            catch (Exception ex)
            {
                // 前面化はあくまで利便性のための処理。失敗しても案内表示へ退避できるので
                // 起動中止という本来の目的は達成される（痕跡だけ残す）。
                ErrorDialogHelper.LogException(ex, "起動済みインスタンスの活性化");
                return false;
            }
        }

        /// <summary>
        /// 同名プロセスから候補を組み立てる。個々のプロセスの情報取得は失敗し得る
        /// （別セッション・権限不足・列挙直後の終了）ため、1 件の失敗で全体を諦めない。
        /// </summary>
        private static List<InstanceWindowCandidate> CollectCandidates(string processName)
        {
            var candidates = new List<InstanceWindowCandidate>();

            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        candidates.Add(new InstanceWindowCandidate(
                            process.Id, process.SessionId, process.MainWindowHandle));
                    }
                    catch (Exception)
                    {
                        // この 1 件は候補にできないだけ。他の候補の評価は続ける。
                    }
                }
            }

            return candidates;
        }

        /// <summary>
        /// 最小化されていれば元に戻したうえで前面へ出す
        /// </summary>
        private static bool BringToFront(IntPtr hWnd)
        {
            if (IsIconic(hWnd))
            {
                ShowWindow(hWnd, SW_RESTORE);
            }

            // 前面化は Windows のフォアグラウンドロックにより拒否されることがある
            // （拒否されると何も起きずに false が返る）。戻り値をそのまま返し、
            // 呼び出し元が案内表示へ退避できるようにする。
            return SetForegroundWindow(hWnd);
        }

        /// <summary>最小化されたウィンドウを元のサイズ・位置へ戻す</summary>
        private const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);
    }
}
