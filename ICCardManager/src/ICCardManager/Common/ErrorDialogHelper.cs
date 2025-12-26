using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Windows;
using ICCardManager.Common.Exceptions;

namespace ICCardManager.Common
{
/// <summary>
    /// エラーダイアログ表示のヘルパークラス
    /// 例外の種類に応じて適切なエラーダイアログを表示する
    /// </summary>
    public static class ErrorDialogHelper
    {
        /// <summary>
        /// ログディレクトリ（CommonApplicationDataを使用して全ユーザーで共有）
        /// </summary>
        private static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ICCardManager",
            "Logs");

        /// <summary>
        /// 例外に応じたエラーダイアログを表示
        /// </summary>
        /// <param name="exception">例外</param>
        /// <param name="title">ダイアログタイトル（省略時は「エラー」）</param>
        public static void ShowError(Exception exception, string title = null)
        {
            var (message, errorCode) = GetErrorInfo(exception);
            var dialogTitle = title ?? "エラー";

            // ログ出力
            LogError(exception, errorCode);

            // UIスレッドでダイアログを表示
            if (Application.Current?.Dispatcher != null)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ShowErrorDialog(message, dialogTitle, errorCode, exception);
                });
            }
            else
            {
                ShowErrorDialog(message, dialogTitle, errorCode, exception);
            }
        }

        /// <summary>
        /// 警告ダイアログを表示（エラーではないが注意が必要な場合）
        /// </summary>
        /// <param name="message">メッセージ</param>
        /// <param name="title">タイトル（省略時は「警告」）</param>
        public static void ShowWarning(string message, string title = null)
        {
            var dialogTitle = title ?? "警告";

            if (Application.Current?.Dispatcher != null)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    MessageBox.Show(message, dialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                });
            }
            else
            {
                MessageBox.Show(message, dialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 致命的エラーダイアログを表示（アプリケーション終了を伴うエラー）
        /// </summary>
        /// <param name="exception">例外</param>
        public static void ShowFatalError(Exception exception)
        {
            var (message, errorCode) = GetErrorInfo(exception);

            // ログ出力
            LogError(exception, errorCode, isFatal: true);

            var fullMessage = $"{message}\n\n" +
                              $"エラーコード: {errorCode}\n\n" +
                              $"アプリケーションを終了します。\n" +
                              $"問題が解決しない場合は、管理者に連絡してください。";

            ShowErrorDialogWithClipboard(fullMessage, "致命的なエラー", exception);
        }

        /// <summary>
        /// 例外からエラー情報を取得
        /// </summary>
        private static (string Message, string ErrorCode) GetErrorInfo(Exception exception)
        {
            if (exception is AppException appException)
            {
                return (appException.UserFriendlyMessage, appException.ErrorCode);
            }

            // 一般的な例外の場合は、種類に応じたメッセージを返す
            return exception switch
            {
                UnauthorizedAccessException => ("ファイルへのアクセス権限がありません。", "SYS001"),
                IOException => ("ファイルの読み書き中にエラーが発生しました。", "SYS002"),
                TimeoutException => ("処理がタイムアウトしました。再度お試しください。", "SYS003"),
                InvalidOperationException => ("この操作は現在実行できません。", "SYS004"),
                ArgumentException or ArgumentNullException => ("入力値が正しくありません。", "SYS005"),
                NotSupportedException => ("この機能はサポートされていません。", "SYS006"),
                _ => ("予期しないエラーが発生しました。", "SYS999")
            };
        }

        /// <summary>
        /// エラーダイアログを表示
        /// </summary>
        private static void ShowErrorDialog(string message, string title, string errorCode, Exception exception)
        {
            var displayMessage = string.IsNullOrEmpty(errorCode)
                ? message
                : $"{message}\n\nエラーコード: {errorCode}";

    #if DEBUG
            // デバッグビルドでは詳細情報を表示
            displayMessage += $"\n\n【デバッグ情報】\n{exception.GetType().Name}: {exception.Message}";
            if (exception.InnerException != null)
            {
                displayMessage += $"\nInner: {exception.InnerException.Message}";
            }
    #endif

            MessageBox.Show(displayMessage, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        /// <summary>
        /// クリップボードにコピー可能なエラーダイアログを表示
        /// </summary>
        private static void ShowErrorDialogWithClipboard(string message, string title, Exception exception)
        {
            var technicalDetails = $"例外: {exception.GetType().FullName}\n" +
                                   $"メッセージ: {exception.Message}\n" +
                                   $"スタックトレース:\n{exception.StackTrace}";

            if (exception.InnerException != null)
            {
                technicalDetails += $"\n\n内部例外: {exception.InnerException.GetType().FullName}\n" +
                                   $"メッセージ: {exception.InnerException.Message}";
            }

            var result = MessageBox.Show(
                $"{message}\n\n" +
                $"[はい]をクリックするとエラー詳細をクリップボードにコピーします。",
                title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Error);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    Clipboard.SetText(technicalDetails);
                }
                catch
                {
                    // クリップボードへのコピーに失敗した場合は無視
                }
            }
        }

        /// <summary>
        /// エラーをログファイルに出力
        /// </summary>
        private static void LogError(Exception exception, string errorCode, bool isFatal = false)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);

                var logFileName = $"error_{DateTime.Now:yyyyMMdd}.log";
                var logFilePath = Path.Combine(LogDirectory, logFileName);

                var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] " +
                              $"{(isFatal ? "FATAL" : "ERROR")} [{errorCode}] " +
                              $"{exception.GetType().Name}: {exception.Message}\n" +
                              $"StackTrace: {exception.StackTrace}\n";

                if (exception.InnerException != null)
                {
                    logEntry += $"InnerException: {exception.InnerException.GetType().Name}: {exception.InnerException.Message}\n";
                }

                logEntry += new string('-', 80) + "\n";

                File.AppendAllText(logFilePath, logEntry);

                // デバッグ出力にも出力
                System.Diagnostics.Debug.WriteLine($"[{errorCode}] {exception.GetType().Name}: {exception.Message}");
            }
            catch
            {
                // ログ出力に失敗した場合は無視（ログ出力失敗でアプリがクラッシュしないように）
                System.Diagnostics.Debug.WriteLine($"Failed to write error log: {exception.Message}");
            }
        }

        /// <summary>
        /// 古いログファイルを削除（30日以上前のファイル）
        /// </summary>
        public static void CleanupOldLogs()
        {
            try
            {
                if (!Directory.Exists(LogDirectory))
                {
                    return;
                }

                var cutoffDate = DateTime.Now.AddDays(-30);
                var logFiles = Directory.GetFiles(LogDirectory, "error_*.log");

                foreach (var logFile in logFiles)
                {
                    var fileInfo = new FileInfo(logFile);
                    if (fileInfo.LastWriteTime < cutoffDate)
                    {
                        fileInfo.Delete();
                    }
                }
            }
            catch
            {
                // クリーンアップに失敗しても無視
            }
        }
    }
}
