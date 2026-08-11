using System;
using System.Linq;

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
        /// <summary>
        /// ユーザー通知（トースト）の最小間隔。
        /// </summary>
        /// <remarks>
        /// エラートーストは自動消去されない（クリックで閉じる）ため、共有モードの
        /// ヘルスチェック失敗のような繰り返し発生する障害で発火のたびに通知すると、
        /// 無人の共用端末にトーストが際限なく積み上がる。ログは間引かず毎回記録し、
        /// 通知だけを抑制する。
        /// </remarks>
        internal static readonly TimeSpan NotificationCooldown = TimeSpan.FromMinutes(5);

        private readonly Action<Exception, string> _logError;
        private readonly Func<bool> _isUiAvailable;
        private readonly Action<Action> _postToUi;
        private readonly Action<Exception> _notifyError;
        private readonly Func<DateTime> _utcNow;

        /// <summary>
        /// 最後にユーザー通知をポストした時刻（UTC）。
        /// UnobservedTaskException は単一のファイナライザスレッドから逐次発火するため、
        /// ロックは不要。
        /// </summary>
        private DateTime _lastNotifiedUtc = DateTime.MinValue;

        /// <summary>
        /// <see cref="UnobservedTaskExceptionHandler"/> を生成します。
        /// </summary>
        /// <param name="logError">
        /// 例外とメッセージの記録。ハンドラ登録（SetupGlobalExceptionHandlers）は DI コンテナ
        /// 構築前に行われるため、呼び出し側は ILogger が未初期化でも記録が残る実装
        /// （ErrorDialogHelper.LogException 等の DI 非依存経路へのフォールバック）を渡すこと。
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
        /// <param name="utcNow">
        /// 現在時刻（UTC）の取得。通知クールダウンの判定に使う。
        /// 省略時は DateTime.UtcNow（テストから決定論的に駆動するための注入点）。
        /// </param>
        public UnobservedTaskExceptionHandler(
            Action<Exception, string> logError,
            Func<bool> isUiAvailable,
            Action<Action> postToUi,
            Action<Exception> notifyError,
            Func<DateTime> utcNow = null)
        {
            _logError = logError ?? throw new ArgumentNullException(nameof(logError));
            _isUiAvailable = isUiAvailable ?? throw new ArgumentNullException(nameof(isUiAvailable));
            _postToUi = postToUi ?? throw new ArgumentNullException(nameof(postToUi));
            _notifyError = notifyError ?? throw new ArgumentNullException(nameof(notifyError));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
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

            TryLog(exception, $"Task未観測例外 (InnerCount={exception.InnerExceptions.Count})");

            foreach (var innerException in exception.InnerExceptions)
            {
                TryLog(innerException, "Task未観測例外の内部例外");
            }

            try
            {
                if (!_isUiAvailable())
                {
                    // Dispatcher シャットダウン後のポストは実行されないため、ログ記録のみで終える
                    return;
                }

                var now = _utcNow();
                if (now - _lastNotifiedUtc < NotificationCooldown)
                {
                    // 繰り返し発生する障害での通知の積み上がりを抑止（ログは上で記録済み）
                    return;
                }

                _lastNotifiedUtc = now;

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
                        TryLog(notifyException, "Task未観測例外のユーザー通知に失敗");
                    }
                });
            }
            catch (Exception dispatchException)
            {
                // ファイナライザスレッドへ例外が漏れるとプロセスが異常終了するため、ここで止める
                TryLog(dispatchException, "Task未観測例外のUIスレッドへのディスパッチに失敗");
            }
        }

        /// <summary>
        /// ログ記録を試みます。記録自体の失敗は握りつぶします。
        /// </summary>
        /// <remarks>
        /// ログ経路の障害が通知経路やファイナライザスレッドを道連れにしないための隔離。
        /// </remarks>
        private void TryLog(Exception exception, string message)
        {
            try
            {
                _logError(exception, message);
            }
            catch
            {
                // ログ記録の障害はここでは救えない。例外を漏らさないことを優先する
            }
        }
    }
}
