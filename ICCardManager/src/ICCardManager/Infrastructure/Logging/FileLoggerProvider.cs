using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ICCardManager.Infrastructure.Logging
{
/// <summary>
    /// ファイルロガープロバイダー
    /// </summary>
    [ProviderAlias("File")]
    public class FileLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
        private readonly BlockingCollection<string> _logQueue = new(1000);
        private readonly Task _outputTask;
        private readonly CancellationTokenSource _cancellationTokenSource = new();

        private readonly string _logDirectory;
        private string _currentLogFilePath = string.Empty;
        private DateTime _currentLogDate;
        private readonly object _fileLock = new();

        public FileLoggerOptions Options { get; }

        public FileLoggerProvider(IOptions<FileLoggerOptions> options)
        {
            Options = options.Value;

            // ログディレクトリを決定（DBと同じくCommonApplicationDataに保存）
            // C:\ProgramData\ICCardManager\Logs を使用し、全ユーザーで共有
            var appDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            _logDirectory = Path.Combine(appDataDirectory, "ICCardManager", Options.Path);

            if (Options.Enabled)
            {
                // ログディレクトリを作成（全ユーザーがアクセスできるように権限を設定）
                EnsureDirectoryWithPermissions(_logDirectory);

                // 古いログファイルを削除
                CleanupOldLogs();

                // バックグラウンドでログを書き込むタスクを開始
                _outputTask = Task.Factory.StartNew(
                    ProcessLogQueue,
                    _cancellationTokenSource.Token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }
            else
            {
                _outputTask = Task.CompletedTask;
            }
        }

        public ILogger CreateLogger(string categoryName)
        {
            return _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));
        }

        public void WriteLog(string message)
        {
            if (!Options.Enabled)
            {
                return;
            }

            // キューがいっぱいの場合は古いエントリを破棄
            if (!_logQueue.TryAdd(message))
            {
                // キューがいっぱい - ログを破棄
                System.Diagnostics.Debug.WriteLine("[FileLogger] Log queue full, message dropped");
            }
        }

        private void ProcessLogQueue()
        {
            try
            {
                foreach (var message in _logQueue.GetConsumingEnumerable(_cancellationTokenSource.Token))
                {
                    WriteToFile(message);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常終了
            }
        }

        private void WriteToFile(string message)
        {
            try
            {
                lock (_fileLock)
                {
                    EnsureLogFile();

                    using var writer = new StreamWriter(_currentLogFilePath, append: true, Encoding.UTF8);
                    writer.WriteLine(message);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FileLogger] Failed to write log: {ex.Message}");
            }
        }

        private void EnsureLogFile()
        {
            var today = DateTime.Today;

            // 日付が変わった場合、または初回の場合
            if (_currentLogDate != today || string.IsNullOrEmpty(_currentLogFilePath))
            {
                _currentLogDate = today;
                _currentLogFilePath = Path.Combine(
                    _logDirectory,
                    $"ICCardManager_{today:yyyyMMdd}.log");

                // ファイルサイズチェック（既存ファイルの場合）
                CheckFileSize();
            }
            else
            {
                // ファイルサイズチェック
                CheckFileSize();
            }
        }

        private void CheckFileSize()
        {
            if (!File.Exists(_currentLogFilePath))
            {
                return;
            }

            var fileInfo = new FileInfo(_currentLogFilePath);
            var maxSizeBytes = Options.MaxFileSizeMB * 1024 * 1024;

            if (fileInfo.Length >= maxSizeBytes)
            {
                // ローテーション: ファイル名に番号を付けて新しいファイルを作成
                var baseName = Path.GetFileNameWithoutExtension(_currentLogFilePath);
                var extension = Path.GetExtension(_currentLogFilePath);
                var counter = 1;

                string newPath;
                do
                {
                    newPath = Path.Combine(_logDirectory, $"{baseName}_{counter}{extension}");
                    counter++;
                } while (File.Exists(newPath));

                // 現在のファイルをリネーム
                File.Move(_currentLogFilePath, newPath);

                // 新しいファイルを作成（次のWriteToFile呼び出しで作成される）
            }
        }

        private void CleanupOldLogs()
        {
            try
            {
                var cutoffDate = DateTime.Today.AddDays(-Options.RetentionDays);
                var logFiles = Directory.GetFiles(_logDirectory, "ICCardManager_*.log");

                foreach (var file in logFiles)
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.LastWriteTime < cutoffDate)
                    {
                        fileInfo.Delete();
                        System.Diagnostics.Debug.WriteLine($"[FileLogger] Deleted old log: {fileInfo.Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FileLogger] Failed to cleanup old logs: {ex.Message}");
            }
        }

        /// <summary>
        /// ディレクトリを作成し、全ユーザーがアクセスできるように権限を設定
        /// </summary>
        private static void EnsureDirectoryWithPermissions(string directoryPath)
        {
            try
            {
                if (!Directory.Exists(directoryPath))
                {
                    var directoryInfo = Directory.CreateDirectory(directoryPath);

                    // Usersグループにフルコントロール権限を付与
                    var directorySecurity = directoryInfo.GetAccessControl();
                    var usersIdentity = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
                    var accessRule = new FileSystemAccessRule(
                        usersIdentity,
                        FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None,
                        AccessControlType.Allow);
                    directorySecurity.AddAccessRule(accessRule);
                    directoryInfo.SetAccessControl(directorySecurity);

                    System.Diagnostics.Debug.WriteLine($"[FileLogger] ディレクトリを作成し権限を設定: {directoryPath}");
                }
            }
            catch (Exception ex)
            {
                // 権限設定に失敗してもディレクトリ作成は試みる
                System.Diagnostics.Debug.WriteLine($"[FileLogger] ディレクトリ権限設定エラー: {ex.Message}");
                Directory.CreateDirectory(directoryPath);
            }
        }

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
            _logQueue.CompleteAdding();

            try
            {
                // キューに残っているログを書き込むために少し待機
                _outputTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // タスクがキャンセルされた
            }

            _cancellationTokenSource.Dispose();
            _logQueue.Dispose();
        }
    }
}
