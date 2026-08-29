using System;
using System.Threading;

namespace ICCardManager.Common
{
    /// <summary>
    /// 二重起動の判定結果（Issue #1910）
    /// </summary>
    public enum SingleInstanceStatus
    {
        /// <summary>このプロセスが唯一のインスタンス（起動を継続してよい）</summary>
        Primary,

        /// <summary>同じ端末で既に起動している（活性化して自分は終了する）</summary>
        AlreadyRunning,

        /// <summary>
        /// 同じ端末の<b>別のユーザーセッション</b>で既に起動している。
        /// 名前付きミューテックスの既定 DACL は作成者以外へアクセスを許可しないため、
        /// 別ユーザーが保持していると <see cref="UnauthorizedAccessException"/> になる。
        /// </summary>
        AlreadyRunningInOtherSession,

        /// <summary>
        /// 判定そのものに失敗した。<b>起動は継続する</b>（<see cref="SingleInstanceGuard.IsPrimaryInstance"/> は true）。
        /// </summary>
        GuardUnavailable
    }

    /// <summary>
    /// 名前付きミューテックスで「1 台につき 1 つ」のインスタンスを保証する（Issue #1910）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 二重起動すると <c>FelicaCardReader</c> が 2 つ動き、1 回のタッチが 2 回読み取られて
    /// 「貸出直後に返却」として台帳へ記録される。6 年保存される <c>ledger</c> に
    /// 実際には起きていない返却が残るため、起動そのものを 1 つに絞る。
    /// </para>
    /// <para>
    /// <b>判定は <c>CreateMutex</c> の <c>createdNew</c> だけで行い、ミューテックスを所有しない</b>
    /// （<c>initiallyOwned: false</c>）。カーネルオブジェクトは最後のハンドルが閉じたときに消えるため、
    /// プロセスが強制終了しても次回起動では必ず <c>createdNew == true</c> になる。
    /// 所有する形（<c>initiallyOwned: true</c>）にすると放棄ミューテックス
    /// （<c>AbandonedMutexException</c>）の面倒を見る必要が生じるが、得るものは無い。
    /// </para>
    /// <para>
    /// <b>判定に失敗したときは起動を止めない</b>（<see cref="SingleInstanceStatus.GuardUnavailable"/>）。
    /// この機構は運用の安定のための予防であって、業務そのものではない。
    /// 予防機構の不調で「ピッすいが起動しない」状態を作るほうが害が大きい。
    /// 失敗理由は <see cref="AcquisitionError"/> に保持して呼び出し元がログへ残す
    /// （握りつぶすと、なぜ二重起動できたのかを後から追えない）。
    /// </para>
    /// </remarks>
    public sealed class SingleInstanceGuard : IDisposable
    {
        private Mutex _mutex;

        private SingleInstanceGuard(SingleInstanceStatus status, Mutex mutex, Exception acquisitionError)
        {
            Status = status;
            _mutex = mutex;
            AcquisitionError = acquisitionError;
        }

        /// <summary>判定結果</summary>
        public SingleInstanceStatus Status { get; }

        /// <summary>
        /// 判定の過程で捕捉した例外（<see cref="SingleInstanceStatus.GuardUnavailable"/> と
        /// <see cref="SingleInstanceStatus.AlreadyRunningInOtherSession"/> のときのみ非 null）。
        /// 呼び出し元がログへ残すために公開している。
        /// </summary>
        public Exception AcquisitionError { get; }

        /// <summary>
        /// 起動を継続してよいか。判定不能（<see cref="SingleInstanceStatus.GuardUnavailable"/>）も
        /// 継続扱いにする（上記 remarks の fail-open）。
        /// </summary>
        public bool IsPrimaryInstance
            => Status == SingleInstanceStatus.Primary || Status == SingleInstanceStatus.GuardUnavailable;

        /// <summary>
        /// 名前付きミューテックスを確保して二重起動を判定する
        /// </summary>
        /// <param name="mutexName">
        /// ミューテックス名。端末全体で一意にするため <c>Global\</c> 接頭辞を付けた
        /// <see cref="AppConstants.SingleInstanceMutexName"/> を渡す。
        /// </param>
        /// <returns>判定結果を保持するガード。<b>結果にかかわらず必ず <see cref="Dispose"/> すること</b>。</returns>
        public static SingleInstanceGuard Acquire(string mutexName)
        {
            if (string.IsNullOrEmpty(mutexName))
            {
                throw new ArgumentException("ミューテックス名を指定してください。", nameof(mutexName));
            }

            try
            {
                var mutex = new Mutex(initiallyOwned: false, name: mutexName, createdNew: out var createdNew);
                return new SingleInstanceGuard(
                    createdNew ? SingleInstanceStatus.Primary : SingleInstanceStatus.AlreadyRunning,
                    mutex,
                    acquisitionError: null);
            }
            catch (UnauthorizedAccessException ex)
            {
                // 既存のミューテックスが別ユーザーの既定 DACL で保護されている
                // ＝ 同じ端末の別セッションで起動している。
                return new SingleInstanceGuard(
                    SingleInstanceStatus.AlreadyRunningInOtherSession, mutex: null, acquisitionError: ex);
            }
            catch (Exception ex)
            {
                // 想定外の失敗（名前が長すぎる・カーネルオブジェクトを作れない等）。
                // 起動は止めず、理由だけを持ち帰る。
                return new SingleInstanceGuard(
                    SingleInstanceStatus.GuardUnavailable, mutex: null, acquisitionError: ex);
            }
        }

        /// <summary>
        /// ミューテックスのハンドルを閉じる。冪等。
        /// </summary>
        public void Dispose()
        {
            var mutex = Interlocked.Exchange(ref _mutex, null);
            mutex?.Dispose();
        }
    }
}
