using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ICCardManager.Common
{
    /// <summary>
    /// 起動済みインスタンスの前面化を試みた結果（Issue #1910）
    /// </summary>
    /// <remarks>
    /// 案内文言の「どうすれば」はこの結果で選ぶ。ミューテックスの判定結果
    /// （<see cref="SingleInstanceStatus"/>）は DACL の所有者、つまり<b>別ユーザーかどうか</b>しか
    /// 表さないため、同じユーザーが 2 セッション（コンソール＋リモートデスクトップ、
    /// ユーザーの簡易切り替え）で起動している場合に「タスクバーで切り替えてください」という
    /// <b>このセッションでは実行できない指示</b>を出してしまう。
    /// 「自分のセッションに切り替え先の画面があるか」を知っているのは前面化の側だけ。
    /// </remarks>
    public enum ActivationOutcome
    {
        /// <summary>前面へ出せた（案内は表示しない）</summary>
        Activated,

        /// <summary>
        /// 同一セッションに対象の画面があったが、前面化が拒否された
        /// （Windows のフォアグラウンドロック）。タスクバーからの切り替えは有効。
        /// </summary>
        ActivationRefused,

        /// <summary>
        /// 同一セッションに対象の画面が無かった。別のセッションで起動しているか、
        /// 先行インスタンスがまだ画面を表示していない。
        /// </summary>
        NoWindowInThisSession
    }

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
        /// <returns>
        /// 前面化の結果。呼び出し元はこれで案内文言の「どうすれば」を選ぶ
        /// （<see cref="ActivationOutcome"/> の remarks 参照）。
        /// </returns>
        public static ActivationOutcome TryActivateExistingInstance()
        {
            try
            {
                using (var current = Process.GetCurrentProcess())
                {
                    var target = ExistingInstanceLocator.SelectActivationTarget(
                        CollectCandidates(current.ProcessName),
                        current.Id,
                        current.SessionId);

                    if (!target.HasValue)
                    {
                        return ActivationOutcome.NoWindowInThisSession;
                    }

                    return BringToFront(target.Value.MainWindowHandle)
                        ? ActivationOutcome.Activated
                        : ActivationOutcome.ActivationRefused;
                }
            }
            catch (Exception ex)
            {
                // 前面化はあくまで利便性のための処理。失敗しても案内表示へ退避できるので
                // 起動中止という本来の目的は達成される（痕跡だけ残す）。
                // 「対象が見つからなかった」とは区別できないため、案内の広い側
                // （NoWindowInThisSession）へ倒す。
                ErrorDialogHelper.LogException(ex, "起動済みインスタンスの活性化");
                return ActivationOutcome.NoWindowInThisSession;
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
