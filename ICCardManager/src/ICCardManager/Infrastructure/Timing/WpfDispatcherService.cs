using System;
using System.Threading.Tasks;
using ICCardManager.Common;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Infrastructure.Timing
{
    /// <summary>
    /// WPFのDispatcherを使用するIDispatcherService実装。
    /// 本番環境で使用されます。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #1725: ディスパッチした処理の例外を必ず観測してログへ残す。
    /// </para>
    /// <para>
    /// <see cref="System.Windows.Threading.Dispatcher.InvokeAsync(Action)"/> は例外を
    /// 戻り値の <c>DispatcherOperation</c> の Task に格納するため、
    /// <c>Application.DispatcherUnhandledException</c> は発火しない。
    /// さらに <c>InvokeAsync(Func&lt;Task&gt;)</c> は <c>Task&lt;Task&gt;</c> を返すため、
    /// 外側だけ観測しても async lambda 本体の例外は取りこぼす。
    /// 誰も観測しないと <c>TaskScheduler.UnobservedTaskException</c> が GC ファイナライズ時に
    /// 遅れて発火するだけで、障害調査に使えるログが残らない。
    /// </para>
    /// </remarks>
    public class WpfDispatcherService : IDispatcherService
    {
        /// <summary>ログに載せる操作名（Issue #1730: 障害調査を先に進める値を載せる）</summary>
        private const string DispatchOperationName = "UIスレッドへディスパッチした処理";

        private readonly ILogger<WpfDispatcherService> _logger;

        /// <summary>
        /// <see cref="WpfDispatcherService"/> を生成します。
        /// </summary>
        /// <param name="logger">
        /// ディスパッチした処理の例外を記録するロガー。DI 未登録の環境でも動作するよう省略可能。
        /// </param>
        public WpfDispatcherService(ILogger<WpfDispatcherService> logger = null)
        {
            _logger = logger;
        }

        /// <inheritdoc/>
        public void InvokeAsync(Action action)
        {
            var app = System.Windows.Application.Current;
            if (app == null)
            {
                return;
            }

            ObserveTask(app.Dispatcher.InvokeAsync(action).Task);
        }

        /// <inheritdoc/>
        public void InvokeAsync(Func<Task> asyncAction)
        {
            var app = System.Windows.Application.Current;
            if (app == null)
            {
                return;
            }

            // DispatcherOperation<Task>.Task は Task<Task>。Unwrap() で内側の Task まで
            // 含めた 1 本の Task にしないと、async lambda 本体の例外を観測できない。
            ObserveTask(app.Dispatcher.InvokeAsync(asyncAction).Task.Unwrap());
        }

        /// <summary>
        /// ディスパッチした処理の完了を監視し、失敗していれば例外をログへ記録します。
        /// </summary>
        /// <param name="task">監視対象のタスク。<c>null</c> の場合は何もしません。</param>
        /// <remarks>
        /// <para>
        /// テストから直接検証できるよう <c>internal</c> 公開する
        /// （<c>InvokeAsync</c> 本体は <c>Application.Current</c> を必要とし単体テストから駆動できない）。
        /// </para>
        /// <para>
        /// ログレベルは <c>LogError</c>。<c>LogDebug</c> では本番の
        /// <c>Logging:LogLevel:Default = Information</c> によりファイルへ一切出力されず、
        /// 「無言で握りつぶさない」という本メソッドの目的を果たせない（Issue #1716 の教訓）。
        /// 失敗時のみ出力するためログ肥大化は起きない。
        /// </para>
        /// </remarks>
        internal void ObserveTask(Task task)
        {
            if (task == null)
            {
                return;
            }

            // 観測の実体は Common/DispatcherObservation に 1 つだけ置く（Issue #1873）。
            // View コードビハインドは IDispatcherService を注入できないため同じ観測を
            // 拡張メソッドとして使う。継続の登録方法を 2 通り持つと、片方だけ変わる日が来る
            // （development-conventions.md Issue #1831「手段を 1 つに寄せる」）。
            // 記録先だけがこの層の関心事（ILogger 注入済み）なので、そこを差し替える。
            DispatcherObservation.Observe(
                task,
                DispatchOperationName,
                (exception, operationName) => _logger?.LogError(
                    exception,
                    "{Operation}が例外で終了しました",
                    operationName));
        }
    }
}
