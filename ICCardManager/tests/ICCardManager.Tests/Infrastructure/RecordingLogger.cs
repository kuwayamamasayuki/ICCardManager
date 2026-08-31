using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Tests.Infrastructure;

/// <summary>
/// ログ出力を記録するテスト用 <see cref="ILogger{TCategoryName}"/>（Issue #1961）
/// </summary>
/// <remarks>
/// <para>
/// 失敗を例外ではなく戻り値（<c>null</c> / <c>false</c>）で表すメソッド
/// （<c>BackupService.ExecuteAutoBackupAsync</c> 等。
/// <c>development-conventions.md</c>「失敗を『例外』で表さないメソッドに try/catch を足しても、
/// 失敗は扱えていない」／Issue #1737）を検証するテストは、
/// <see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger{T}"/> を渡すと
/// <b>サービスが記録した失敗理由まで一緒に捨ててしまう</b>。
/// その結果、CI が赤くなっても「なぜ null なのか」がログから判別できない。
/// </para>
/// <para>
/// 本クラスは記録したエントリを <see cref="FormatEntries"/> で 1 つの文字列に畳めるため、
/// アサーションの <c>because</c> 引数へそのまま載せられる。
/// </para>
/// <para>
/// スレッドセーフ。<c>Task.Run</c> でオフロードされた経路からの記録も取りこぼさない。
/// </para>
/// </remarks>
public sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<LogEntry> _entries = new List<LogEntry>();
    private readonly object _sync = new object();

    /// <summary>記録済みのログエントリ（スナップショット）</summary>
    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_sync)
            {
                return _entries.ToList();
            }
        }
    }

    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull
        => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (formatter == null)
        {
            throw new ArgumentNullException(nameof(formatter));
        }

        var entry = new LogEntry(logLevel, formatter(state, exception), exception);
        lock (_sync)
        {
            _entries.Add(entry);
        }
    }

    /// <summary>
    /// 記録済みエントリをアサーションメッセージ用の 1 文字列へ整形する。
    /// </summary>
    /// <remarks>
    /// 例外は型名とメッセージまで載せる。UI スレッドガード（Issue #1281）の
    /// <see cref="InvalidOperationException"/> と、I/O 失敗・権限失敗とを
    /// CI のログだけで切り分けられるようにするため。
    /// </remarks>
    public string FormatEntries()
    {
        var entries = Entries;
        if (entries.Count == 0)
        {
            return "（ログ出力なし）";
        }

        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            builder.Append("[").Append(entry.Level).Append("] ").AppendLine(entry.Message);
            if (entry.Exception != null)
            {
                builder.Append("    -> ")
                    .Append(entry.Exception.GetType().FullName)
                    .Append(": ")
                    .AppendLine(entry.Exception.Message);
            }
        }

        return builder.ToString();
    }

    /// <summary>記録された 1 件のログ</summary>
    public sealed class LogEntry
    {
        public LogEntry(LogLevel level, string message, Exception? exception)
        {
            Level = level;
            Message = message;
            Exception = exception;
        }

        public LogLevel Level { get; }

        public string Message { get; }

        public Exception? Exception { get; }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new NullScope();

        private NullScope()
        {
        }

        public void Dispose()
        {
        }
    }
}
