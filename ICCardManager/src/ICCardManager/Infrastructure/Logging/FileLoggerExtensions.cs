using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Configuration;

namespace ICCardManager.Infrastructure.Logging
{
/// <summary>
    /// ファイルロガーの拡張メソッド
    /// </summary>
    public static class FileLoggerExtensions
    {
        /// <summary>
        /// ファイルロガーをロギングビルダーに追加
        /// </summary>
        public static ILoggingBuilder AddFile(this ILoggingBuilder builder)
        {
            builder.AddConfiguration();
            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<ILoggerProvider, FileLoggerProvider>());
            LoggerProviderOptions.RegisterProviderOptions<FileLoggerOptions, FileLoggerProvider>(builder.Services);
            return builder;
        }

        /// <summary>
        /// ファイルロガーをロギングビルダーに追加（オプション指定）
        /// </summary>
        public static ILoggingBuilder AddFile(this ILoggingBuilder builder, Action<FileLoggerOptions> configure)
        {
            builder.AddFile();
            builder.Services.Configure(configure);
            return builder;
        }
    }
}
