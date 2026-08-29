using System;
using System.Collections.Generic;
using System.Linq;

namespace ICCardManager.Common
{
    /// <summary>
    /// 起動中のインスタンスの候補（Issue #1910）
    /// </summary>
    /// <remarks>
    /// <see cref="System.Diagnostics.Process"/> から取り出した値だけを持つ。
    /// 選択規則を <see cref="ExistingInstanceLocator"/> の純関数として単体テストするための型。
    /// </remarks>
    public readonly struct InstanceWindowCandidate
    {
        public InstanceWindowCandidate(int processId, int sessionId, IntPtr mainWindowHandle)
        {
            ProcessId = processId;
            SessionId = sessionId;
            MainWindowHandle = mainWindowHandle;
        }

        /// <summary>プロセス ID</summary>
        public int ProcessId { get; }

        /// <summary>ターミナルサービスのセッション ID</summary>
        public int SessionId { get; }

        /// <summary>メインウィンドウのハンドル（未生成なら <see cref="IntPtr.Zero"/>）</summary>
        public IntPtr MainWindowHandle { get; }
    }

    /// <summary>
    /// 「前面に出すべき起動済みインスタンス」を選ぶ規則（Issue #1910）
    /// </summary>
    /// <remarks>
    /// 状態の取得（<c>Process.GetProcessesByName</c>）と判断を分けている。
    /// 判断だけを純関数にすれば、実プロセスを起動せずに規則を網羅できる
    /// （<see cref="DialogOwnerResolver"/> がオーナー選択で採ったのと同じ形）。
    /// </remarks>
    public static class ExistingInstanceLocator
    {
        /// <summary>
        /// 活性化の対象を選ぶ
        /// </summary>
        /// <param name="candidates">同名プロセスの一覧（自分自身を含んでよい）</param>
        /// <param name="currentProcessId">自プロセスの ID</param>
        /// <param name="currentSessionId">自プロセスのセッション ID</param>
        /// <returns>活性化すべき候補。該当が無ければ <c>null</c>。</returns>
        /// <remarks>
        /// <para>除外する候補は次の 3 つ。</para>
        /// <list type="bullet">
        /// <item>自分自身（活性化しても意味がない）</item>
        /// <item>
        /// 別セッションのプロセス。<c>SetForegroundWindow</c> はセッションをまたげないため、
        /// 選んでも「前面に出したつもりで何も起きない」＝無言の失敗になる。
        /// </item>
        /// <item>
        /// メインウィンドウが未生成（ハンドルが <see cref="IntPtr.Zero"/>）の候補。
        /// 先行インスタンスが起動直後で画面をまだ出していない場合がこれにあたる。
        /// </item>
        /// </list>
        /// <para>
        /// 残りが複数あるときはプロセス ID の小さいもの（＝先に起動したもの）を選ぶ。
        /// 二重起動を塞いだ後は 1 つしか残らないが、この機能の導入前から起動していた
        /// 複数インスタンスが残っている端末では実際に複数一致し得るため、選択を決定的にしておく。
        /// </para>
        /// </remarks>
        public static InstanceWindowCandidate? SelectActivationTarget(
            IReadOnlyList<InstanceWindowCandidate> candidates,
            int currentProcessId,
            int currentSessionId)
        {
            if (candidates == null)
            {
                return null;
            }

            var matches = candidates
                .Where(c => c.ProcessId != currentProcessId)
                .Where(c => c.SessionId == currentSessionId)
                .Where(c => c.MainWindowHandle != IntPtr.Zero)
                .OrderBy(c => c.ProcessId)
                .ToList();

            return matches.Count == 0 ? (InstanceWindowCandidate?)null : matches[0];
        }
    }
}
