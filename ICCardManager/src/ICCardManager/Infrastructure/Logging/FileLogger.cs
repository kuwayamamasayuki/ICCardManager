using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Infrastructure.Logging
{
/// <summary>
    /// ファイル出力ロガー
    /// </summary>
    public class FileLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly FileLoggerProvider _provider;

        public FileLogger(string categoryName, FileLoggerProvider provider)
        {
            _categoryName = categoryName;
            _provider = provider;
        }

        public IDisposable? BeginScope<TState>(TState state)
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
            Exception exception,
            Func<TState, Exception, string> formatter)
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

            var logEntry = FormatLogEntry(logLevel, message, exception);
            _provider.WriteLog(logEntry);
        }

        private string FormatLogEntry(LogLevel logLevel, string message, Exception exception)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var level = GetLogLevelString(logLevel);
            var category = GetShortCategoryName(_categoryName);

            var logLine = $"[{timestamp}] [{level}] [{category}] {message}";

            if (exception != null)
            {
                logLine += Environment.NewLine + $"  Exception: {exception.GetType().Name}: {exception.Message}";
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
