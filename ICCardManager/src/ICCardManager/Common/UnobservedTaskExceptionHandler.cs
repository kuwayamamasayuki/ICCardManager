using System;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Common
{
    /// <summary>
    /// TaskScheduler.UnobservedTaskException のハンドラ本体（Issue #1742）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 未観測 Task 例外は GC のファイナライズ時（＝ファイナライザスレッド）に発火する。
    /// このスレッドで同期ディスパッチ（Dispatcher.Invoke）やモーダル表示を行うと、
    /// ユーザーがダイアログを閉じるまでプロセス全体のファイナライザが停止する。
    /// また、ハンドラから例外が漏れるとファイナライザ経由でプロセスが異常終了する。
    /// このため本クラスは「UI スレッドへの非同期ポスト＋非モーダル通知」だけを行い、
    /// いかなる内部エラーも外へ伝播させない。
    /// </para>
    /// <para>
    /// WPF（Dispatcher / Application）へ直接依存せずデリゲート注入とするのは、
    /// 単体テストから駆動できるようにするため（WpfDispatcherService が
    /// Application.Current 依存の本体から観測ロジックだけを切り出したのと同じ判断）。
    /// App.xaml.cs 側の配線（シャットダウンガード・BeginInvoke・トースト通知）は
    /// AppUnobservedTaskExceptionConventionTests が静的検証で固定する。
    /// </para>
    /// </remarks>
    public class UnobservedTaskExceptionHandler
    {
        private readonly Func<ILogger> _getLogger;
        private readonly Func<bool> _isUiAvailable;
        private readonly Action<Action> _postToUi;
        private readonly Action<Exception> _notifyError;

        /// <summary>
        /// <see cref="UnobservedTaskExceptionHandler"/> を生成します。
        /// </summary>
        /// <param name="getLogger">
        /// ロガーの遅延取得。ハンドラ登録（SetupGlobalExceptionHandlers）は DI コンテナ構築前に
        /// 行われるため、生成時ではなく発火時に解決する。null を返してもよい。
        /// </param>
        /// <param name="isUiAvailable">
        /// UI へ通知できる状態かの判定（Dispatcher がシャットダウンを開始していないか等）。
        /// </param>
        /// <param name="postToUi">
        /// UI スレッドへの非同期ポスト（Dispatcher.BeginInvoke 相当）。
        /// 同期ディスパッチを渡してはいけない（ファイナライザスレッドがブロックされる）。
        /// </param>
        /// <param name="notifyError">
        /// ユーザーへの非モーダル通知（トースト等）。<paramref name="postToUi"/> でポストした
        /// アクションの中から呼ばれる。モーダルダイアログを渡してはいけない。
        /// </param>
        public UnobservedTaskExceptionHandler(
            Func<ILogger> getLogger,
            Func<bool> isUiAvailable,
            Action<Action> postToUi,
            Action<Exception> notifyError)
        {
            _getLogger = getLogger ?? throw new ArgumentNullException(nameof(getLogger));
            _isUiAvailable = isUiAvailable ?? throw new ArgumentNullException(nameof(isUiAvailable));
            _postToUi = postToUi ?? throw new ArgumentNullException(nameof(postToUi));
            _notifyError = notifyError ?? throw new ArgumentNullException(nameof(notifyError));
        }

        /// <summary>
        /// 未観測 Task 例外を処理します（ログ記録＋UI スレッドへの非同期通知）。
        /// </summary>
        /// <param name="exception">未観測の集約例外。null の場合は何もしません。</param>
        /// <remarks>
        /// 呼び出し元（App.OnUnobservedTaskException）は本メソッドを呼ぶ前に
        /// UnobservedTaskExceptionEventArgs.SetObserved() を済ませておくこと。
        /// 本メソッドはいかなる例外も外へ伝播させない。
        /// </remarks>
        public void Handle(AggregateException exception)
        {
            if (exception == null)
            {
                return;
            }

            TryLog(logger =>
            {
                logger.LogError(exception, "Task未観測例外 (InnerCount={InnerCount})", exception.InnerExceptions.Count);

                foreach (var innerException in exception.InnerExceptions)
                {
                    logger.LogError(innerException, "Task未観測例外の内部例外");
                }
            });

            try
            {
                if (!_isUiAvailable())
                {
                    // Dispatcher シャットダウン後のポストは実行されないため、ログ記録のみで終える
                    return;
                }

                // 複数の例外がある場合は最初のものを通知対象とする（従来実装の踏襲）
                var displayException = exception.InnerExceptions.FirstOrDefault() ?? (Exception)exception;

                _postToUi(() =>
                {
                    try
                    {
                        _notifyError(displayException);
                    }
                    catch (Exception notifyException)
                    {
                        // Dispatcher 上で実行されるアクションから漏れた例外は
                        // DispatcherUnhandledException へ波及し二次エラーダイアログを生むため、ここで止める
                        TryLog(logger => logger.LogError(notifyException, "Task未観測例外のユーザー通知に失敗"));
                    }
                });
            }
            catch (Exception dispatchException)
            {
                // ファイナライザスレッドへ例外が漏れるとプロセスが異常終了するため、ここで止める
                TryLog(logger => logger.LogError(dispatchException, "Task未観測例外のUIスレッドへのディスパッチに失敗"));
            }
        }

        /// <summary>
        /// ログ記録を試みます。ロガーの取得・記録自体の失敗は握りつぶします。
        /// </summary>
        /// <remarks>
        /// ログ経路の障害（DI コンテナ構築前でロガー未初期化等）が
        /// 通知経路やファイナライザスレッドを道連れにしないための隔離。
        /// </remarks>
        private void TryLog(Action<ILogger> log)
        {
            try
            {
                var logger = _getLogger();
                if (logger != null)
                {
                    log(logger);
                }
            }
            catch
            {
                // ロガー自体の障害はここでは救えない。例外を漏らさないことを優先する
            }
        }
    }
}
