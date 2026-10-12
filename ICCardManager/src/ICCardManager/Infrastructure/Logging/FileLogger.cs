#nullable enable

using System;
using System.Globalization;
using ICCardManager.Common;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Infrastructure.Logging
{
    /// <summary>
    /// ファイル出力ロガー
    /// </summary>
    public class FileLogger : ILogger
    {
        /// <summary>
        /// メッセージの 2 行目以降の行頭に置く接頭辞（Issue #2270）
        /// </summary>
        internal const string MessageContinuationPrefix = LogContinuationLines.Prefix;

        /// <summary>
        /// 例外メッセージの 2 行目以降の行頭に置く接頭辞（<c>  Exception:</c> 行の下へ字下げする。Issue #2270）
        /// </summary>
        internal const string ExceptionMessageContinuationPrefix = LogContinuationLines.NestedPrefix;

        private readonly string _categoryName;
        private readonly FileLoggerProvider _provider;

        public FileLogger(string categoryName, FileLoggerProvider provider)
        {
            _categoryName = categoryName;
            _provider = provider;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel != LogLevel.None && _provider.Options.Enabled;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception == null)
            {
                return;
            }

            var logEntry = FormatLogEntry(DateTime.Now, logLevel, _categoryName, message, exception);
            _provider.WriteLog(logEntry);
        }

        /// <summary>
        /// 1 件のログレコードを組み立てる。
        /// </summary>
        /// <remarks>
        /// <para>
        /// レコードの先頭行だけが <c>[日時] [レベル] [カテゴリ]</c> で始まる。メッセージと例外メッセージに
        /// 含まれる改行は <see cref="LogContinuationLines"/> で継続行（行頭が <see cref="MessageContinuationPrefix"/> /
        /// <see cref="ExceptionMessageContinuationPrefix"/>）へ置き換え、埋め込まれた値が偽のレコードを
        /// 行頭に差し込めないようにする（Issue #2270）。
        /// </para>
        /// <para>
        /// スタックトレースは従来どおり行ごとに字下げする。スタックトレースはコードの位置情報で、
        /// 外部から与えた値は入らない。
        /// </para>
        /// </remarks>
        internal static string FormatLogEntry(
            DateTime timestamp,
            LogLevel logLevel,
            string categoryName,
            string? message,
            Exception? exception)
        {
            var timestampText = timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var level = GetLogLevelString(logLevel);
            var category = GetShortCategoryName(categoryName);
            var safeMessage = LogContinuationLines.Indent(message, Environment.NewLine, MessageContinuationPrefix);

            var logLine = $"[{timestampText}] [{level}] [{category}] {safeMessage}";

            if (exception != null)
            {
                var safeExceptionMessage = LogContinuationLines.Indent(
                    exception.Message, Environment.NewLine, ExceptionMessageContinuationPrefix);
                logLine += Environment.NewLine + $"  Exception: {exception.GetType().Name}: {safeExceptionMessage}";
                if (exception.StackTrace != null)
                {
                    logLine += Environment.NewLine + "  StackTrace: " + exception.StackTrace.Replace(Environment.NewLine, Environment.NewLine + "    ");
                }
            }

            return logLine;
        }

        private static string GetLogLevelString(LogLevel logLevel)
        {
            return logLevel switch
            {
                LogLevel.Trace => "TRC",
                LogLevel.Debug => "DBG",
                LogLevel.Information => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                LogLevel.Critical => "CRT",
                _ => "???"
            };
        }

        private static string GetShortCategoryName(string categoryName)
        {
            // ICCardManager.Services.LendingService -> LendingService
            var lastDot = categoryName.LastIndexOf('.');
            return lastDot >= 0 ? categoryName.Substring(lastDot + 1) : categoryName;
        }
    }
}
