#nullable enable
using System;
using System.IO;
using ICCardManager.Common;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Data
{
    /// <summary>
    /// appsettings.json の <c>DatabaseOptions:Path</c> を database_config.txt へ移した結果（Issue #2389）
    /// </summary>
    public enum LegacyDatabasePathMigrationOutcome
    {
        /// <summary>appsettings.json に値が無い（通常の状態）。何もしない</summary>
        NoLegacyValue,

        /// <summary>database_config.txt が空（無い）だったので、appsettings.json の値を書き移した</summary>
        Migrated,

        /// <summary>database_config.txt に値があるので移さず、appsettings.json の値は使わない</summary>
        KeptConfigFile,

        /// <summary>以前の起動で移行の判断を済ませている（印がある）。appsettings.json の値は使わない</summary>
        AlreadyDecided,

        /// <summary>database_config.txt を読めなかったので移さず、この起動だけ appsettings.json の値で開く</summary>
        ConfigFileUnreadable,

        /// <summary>database_config.txt へ書けなかったので、この起動だけ appsettings.json の値で開く（次の起動で再試行）</summary>
        WriteFailed,
    }

    /// <summary>
    /// 移行の結果と、データベースパスの解決（<see cref="DatabaseLocationResolver.Resolve"/>）へ渡す値（Issue #2389）
    /// </summary>
    /// <param name="Outcome">何をしたか</param>
    /// <param name="EffectiveConfigFileValue">
    /// database_config.txt の値として解決へ渡す値。移行に失敗したときは appsettings.json の値（旧版と同じ保存先で開く）
    /// </param>
    public sealed record LegacyDatabasePathMigrationResult(
        LegacyDatabasePathMigrationOutcome Outcome,
        string EffectiveConfigFileValue);

    /// <summary>
    /// 廃止した appsettings.json の <c>DatabaseOptions:Path</c> を、起動時に一度だけ database_config.txt へ移す（Issue #2389）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// データベースの保存先は database_config.txt（設定画面・インストーラーが読み書きする）だけで指定する。
    /// 以前は appsettings.json の値も database_config.txt が空のときに使われたが、設定画面の欄には表示されず、
    /// 「デフォルトに戻す」（database_config.txt の削除）を押すと既定ではなく appsettings.json の値で開くなど、
    /// どの DB を開いているかの表示と実際が食い違った。
    /// </para>
    /// <para>
    /// 旧版の構成で appsettings.json に値が書かれていた PC では、黙って既定のローカル DB へ切り替えると台帳が分裂する。
    /// そこで最初に値を見つけた起動で 1 度だけ判断し、database_config.txt が空（無い）ならそこへ書き移す。
    /// 判断を済ませた印（<see cref="AppConfigFiles.LegacyDatabasePathMigratedMarkerPath"/>）を残し、以後は値を見ない —
    /// 印が無いと、移した後に「デフォルトに戻す」を押した次の起動でまた移し、既定に戻せなくなる。
    /// </para>
    /// <para>
    /// 値の形式は検証しない（解決＝<see cref="DatabaseLocationResolver.Resolve"/> の役目。形式不正の値を移した場合は、
    /// 解決が棄却して既定で開き、起動後の警告で設定画面からの再設定を案内する）。
    /// </para>
    /// </remarks>
    public static class LegacyDatabasePathMigration
    {
        /// <summary>廃止した appsettings.json の項目のキー</summary>
        public const string AppSettingsKey = "DatabaseOptions:Path";

        /// <summary>
        /// 移行を行い、database_config.txt の値として解決へ渡す値を返す。
        /// </summary>
        /// <param name="legacyAppSettingsValue">appsettings.json の <c>DatabaseOptions:Path</c>（無ければ null）</param>
        /// <param name="configFilePath">database_config.txt のフルパス</param>
        /// <param name="markerFilePath">移行の判断を済ませた印のフルパス</param>
        /// <param name="logger">移行・無視・失敗を記録するロガー</param>
        /// <remarks>
        /// 書き込みの失敗では起動を止めない。失敗したときはこの起動だけ appsettings.json の値で開き（旧版と同じ保存先。
        /// 既定のローカル DB へ切り替えると台帳が分裂する）、印も残さないので次の起動で再試行する。
        /// database_config.txt の書き込みは一時ファイルから差し替える（<see cref="AppConfigFiles.Write"/>）ので、
        /// 途中で失敗しても書きかけの値が最終名で残ることはない。
        /// </remarks>
        public static LegacyDatabasePathMigrationResult Run(
            string? legacyAppSettingsValue,
            string configFilePath,
            string markerFilePath,
            ILogger logger)
        {
            if (logger is null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            var legacy = legacyAppSettingsValue?.Trim() ?? string.Empty;
            if (legacy.Length == 0)
            {
                return new LegacyDatabasePathMigrationResult(
                    LegacyDatabasePathMigrationOutcome.NoLegacyValue, AppConfigFiles.Read(configFilePath));
            }

            if (File.Exists(markerFilePath))
            {
                // 値が残っている限り起動のたびに出る。値を消せば止まる（設定が使われていないことを IT 担当者が追えるように Warning）
                logger.LogWarning(
                    "appsettings.json の DatabaseOptions:Path は廃止されたため使用しません（移行の判断は済んでいます）。" +
                    "保存先は database_config.txt（設定画面）で指定してください。AppSettingsPath={AppSettingsPath}",
                    legacy);
                return new LegacyDatabasePathMigrationResult(
                    LegacyDatabasePathMigrationOutcome.AlreadyDecided, AppConfigFiles.Read(configFilePath));
            }

            if (!AppConfigFiles.TryRead(configFilePath, out var configValue))
            {
                // 読めないファイルを空とみなして書き込むと、実際の保存先を上書きしてしまう。印も残さず次の起動で判断し直す
                logger.LogWarning(
                    "database_config.txt を読めないため、appsettings.json の DatabaseOptions:Path を移さず、" +
                    "この起動ではその値で開きます。ConfigFile={ConfigFile}, AppSettingsPath={AppSettingsPath}",
                    configFilePath, legacy);
                return new LegacyDatabasePathMigrationResult(
                    LegacyDatabasePathMigrationOutcome.ConfigFileUnreadable, legacy);
            }

            if (configValue.Length > 0)
            {
                logger.LogWarning(
                    "database_config.txt に保存先があるため、appsettings.json の DatabaseOptions:Path は移さず使用しません。" +
                    "ConfigFilePath={ConfigFilePath}, AppSettingsPath={AppSettingsPath}",
                    configValue, legacy);
                TryWriteMarker(markerFilePath, $"kept: {configValue}", logger);
                return new LegacyDatabasePathMigrationResult(
                    LegacyDatabasePathMigrationOutcome.KeptConfigFile, configValue);
            }

            try
            {
                AppConfigFiles.Write(configFilePath, legacy);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                logger.LogWarning(
                    ex,
                    "appsettings.json の DatabaseOptions:Path を database_config.txt へ移せませんでした。" +
                    "この起動ではその値で開き、次の起動で再試行します。ConfigFile={ConfigFile}, AppSettingsPath={AppSettingsPath}",
                    configFilePath, legacy);
                return new LegacyDatabasePathMigrationResult(LegacyDatabasePathMigrationOutcome.WriteFailed, legacy);
            }

            logger.LogInformation(
                "appsettings.json の DatabaseOptions:Path を database_config.txt へ移しました（appsettings.json の項目は廃止）。" +
                "ConfigFile={ConfigFile}, Path={Path}",
                configFilePath, legacy);
            TryWriteMarker(markerFilePath, $"migrated: {legacy}", logger);
            return new LegacyDatabasePathMigrationResult(LegacyDatabasePathMigrationOutcome.Migrated, legacy);
        }

        /// <summary>
        /// 印を書く。失敗しても起動は続ける（次の起動では database_config.txt に値があるので、移し直さずに印だけを書き直す）。
        /// </summary>
        private static void TryWriteMarker(string markerFilePath, string content, ILogger logger)
        {
            try
            {
                AppConfigFiles.Write(markerFilePath, content);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "appsettings.json からの移行の印を書けませんでした。Marker={Marker}", markerFilePath);
            }
        }
    }
}
