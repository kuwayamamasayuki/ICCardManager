using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
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

        /// <summary>
        /// Issue #1173: ログキューのキャパシティ。1000→5000に増加し、高負荷時のドロップを軽減。
        /// </summary>
        internal const int LogQueueCapacity = 5000;

        private readonly BlockingCollection<string> _logQueue = new(LogQueueCapacity);
        private readonly Task _outputTask;
        private readonly CancellationTokenSource _cancellationTokenSource = new();

        private readonly string _logDirectory;
        private string _currentLogFilePath = string.Empty;
        private DateTime _currentLogDate;
        private readonly object _fileLock = new();

        /// <summary>
        /// Issue #1173: キュー溢れによりドロップされたログメッセージの累積件数
        /// </summary>
        private long _droppedLogCount;

        /// <summary>
        /// Issue #1173: 最後にドロップ件数を自己ログ出力した時点での累積件数。
        /// 増分の検出と「直近Nミリ秒のドロップ件数」のレポートに使用。
        /// </summary>
        private long _lastReportedDroppedCount;

        /// <summary>
        /// Issue #1173: ドロップ発生レポートの最小間隔（ミリ秒）。
        /// この間隔より頻繁にレポートしないことで、自己ログ自体がキューを溢れさせる事態を防止。
        /// </summary>
        internal const int DropReportMinIntervalMs = 5000;

        private DateTime _lastDropReportTime = DateTime.MinValue;

        /// <summary>
        /// Issue #1173: キュー溢れによりドロップされたログメッセージの累積件数（テスト・診断用）
        /// </summary>
        public long DroppedLogCount => Interlocked.Read(ref _droppedLogCount);

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

            // キューがいっぱいの場合はメッセージを破棄してドロップカウンタをインクリメント
            try
            {
                if (!_logQueue.TryAdd(message))
                {
                    // Issue #1173: ドロップカウンタを原子的にインクリメント
                    Interlocked.Increment(ref _droppedLogCount);
#if DEBUG
                    System.Diagnostics.Debug.WriteLine("[FileLogger] Log queue full, message dropped");
#endif
                }
            }
            catch (ObjectDisposedException)
            {
                // Issue #1173: Dispose後の呼び出しもドロップとしてカウント
                // (ObjectDisposedExceptionはInvalidOperationExceptionの派生型なので先に捕捉)
                Interlocked.Increment(ref _droppedLogCount);
            }
            catch (InvalidOperationException)
            {
                // Issue #1173: CompleteAddingが呼ばれた後（シャットダウン中）はTryAddがスローする
                // この場合もドロップとしてカウントし、例外を呑み込む
                Interlocked.Increment(ref _droppedLogCount);
            }
        }

        private void ProcessLogQueue()
        {
            try
            {
                foreach (var message in _logQueue.GetConsumingEnumerable(_cancellationTokenSource.Token))
                {
                    WriteToFile(message);

                    // Issue #1173: 各書き込み後にドロップ発生をチェックし、必要なら自己ログを書き込む
                    ReportDroppedLogsIfNeeded();
                }
            }
            catch (OperationCanceledException)
            {
                // 正常終了
            }
        }

        /// <summary>
        /// Issue #1173: 前回レポート以降にドロップが発生していれば、ログファイルに自己レポートを書き込む。
        /// 最小間隔（DropReportMinIntervalMs）より頻繁にはレポートしない。
        /// </summary>
        private void ReportDroppedLogsIfNeeded()
        {
            var currentDropped = Interlocked.Read(ref _droppedLogCount);
            var increment = currentDropped - _lastReportedDroppedCount;
            if (increment <= 0)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if ((now - _lastDropReportTime).TotalMilliseconds < DropReportMinIntervalMs)
            {
                return;
            }

            _lastReportedDroppedCount = currentDropped;
            _lastDropReportTime = now;

            // 自己レポートを書き込む（このメッセージはキューを通さず直接ファイルへ）
            var report = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [WARN] [FileLogger] " +
                         $"Issue #1173: ログキュー溢れにより直近 {increment} 件のログメッセージが破棄されました " +
                         $"(累計: {currentDropped} 件、キャパシティ: {LogQueueCapacity})";
            WriteToFile(report);
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
                _ = ex; // 警告抑制（DEBUGビルドでのみ使用）
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[FileLogger] Failed to write log: {ex.Message}");
#endif
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
                try
                {
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
                catch (Exception ex)
                {
                    _ = ex; // 警告抑制（DEBUGビルドでのみ使用）
                    // ローテーション失敗時は既存ファイルに追記を継続
#if DEBUG
                    System.Diagnostics.Debug.WriteLine($"[FileLogger] Log rotation failed: {ex.Message}");
#endif
                }
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
#if DEBUG
                        System.Diagnostics.Debug.WriteLine($"[FileLogger] Deleted old log: {fileInfo.Name}");
#endif
                    }
                }
            }
            catch (Exception ex)
            {
                _ = ex; // 警告抑制（DEBUGビルドでのみ使用）
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[FileLogger] Failed to cleanup old logs: {ex.Message}");
#endif
            }
        }

        /// <summary>
        /// ログディレクトリを作成する
        /// </summary>
        /// <remarks>
        /// Issue #1455: ランタイムで <c>BUILTIN\Users : FullControl</c> を付与する処理を撤廃した。
        /// インストーラーが <c>{commonappdata}\ICCardManager\Logs</c> に
        /// <c>Permissions: users-full</c> を設定済みのため、ランタイムでの再付与は不要。
        /// 詳細は <see cref="ICCardManager.Data.DbContext.EnsureDirectoryWithPermissions"/> 参照。
        /// </remarks>
        private static void EnsureDirectoryWithPermissions(string directoryPath)
        {
            // Directory.CreateDirectoryは既存ディレクトリに対しても安全（冪等）
            Directory.CreateDirectory(directoryPath);
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

            // Issue #1173: 終了時に未レポートのドロップ件数があれば最終レポートを書き込む
            try
            {
                var totalDropped = Interlocked.Read(ref _droppedLogCount);
                var unreported = totalDropped - _lastReportedDroppedCount;
                if (unreported > 0 && Options.Enabled)
                {
                    var report = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [WARN] [FileLogger] " +
                                 $"Issue #1173: シャットダウン時、未レポートだった {unreported} 件のログドロップがありました " +
                                 $"(累計: {totalDropped} 件)";
                    WriteToFile(report);
                }
            }
            catch
            {
                // シャットダウン時の自己レポート失敗は無視
            }

            _cancellationTokenSource.Dispose();
            _logQueue.Dispose();
        }
    }
}
