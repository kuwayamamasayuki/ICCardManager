using System;
using System.IO;
using ICCardManager.Common;

namespace DebugDataViewer
{
    /// <summary>データベースパスをどこから決めたか</summary>
    public enum DatabasePathSource
    {
        /// <summary>コマンドライン引数で明示された</summary>
        CommandLine,

        /// <summary><c>database_config.txt</c>（本体と同じ設定ファイル）</summary>
        ConfigFile,

        /// <summary>実行ファイルと同じディレクトリの <c>iccard.db</c></summary>
        ExecutableDirectory,

        /// <summary>既定パス（<c>C:\ProgramData\ICCardManager\iccard.db</c>）</summary>
        Default
    }

    /// <summary>データベースパスの解決結果</summary>
    public sealed class DatabasePathResolution
    {
        internal DatabasePathResolution(string path, DatabasePathSource source, string rejectedConfiguredPath)
        {
            Path = path;
            Source = source;
            RejectedConfiguredPath = rejectedConfiguredPath;
        }

        /// <summary>解決したデータベースパス</summary>
        public string Path { get; }

        /// <summary>どこから決めたか</summary>
        public DatabasePathSource Source { get; }

        /// <summary>
        /// <c>database_config.txt</c> に値はあったが形式が不正で採用しなかった場合の元の値。
        /// 採用した場合・未設定の場合は <c>null</c>。
        /// </summary>
        public string RejectedConfiguredPath { get; }

        /// <summary>ステータス表示用の短いラベル</summary>
        public string SourceLabel
        {
            get
            {
                switch (Source)
                {
                    case DatabasePathSource.CommandLine: return "コマンドライン引数";
                    case DatabasePathSource.ConfigFile: return "database_config.txt";
                    case DatabasePathSource.ExecutableDirectory: return "実行ファイルと同じフォルダー";
                    default: return "既定の保存先";
                }
            }
        }
    }

    /// <summary>
    /// DebugDataViewer が開くデータベースのパスを決める（Issue #2012）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 以前は「引数 → 実行ファイルと同じフォルダー → 既定パス」だけを見ており、
    /// <c>C:\ProgramData\ICCardManager\database_config.txt</c> を読んでいなかった。
    /// 共有フォルダモード（#1559）で運用している PC では、本体は UNC 上の DB を
    /// 開いているのに本ツールは既定パスのローカル DB を開くため、
    /// <b>共有へ移行する前の古いコピー</b>を読んで誤った結論を出せた。
    /// </para>
    /// <para>
    /// 設定ファイルの読み取りは本体の
    /// <c>SettingsViewModel.LoadDatabasePathFromConfigFile()</c> をそのまま呼ぶ
    /// （<c>App.xaml.cs</c> で注入する）。ツール側へ書き写すと、次に設定ファイルの
    /// 扱いを変えた人が片方を取りこぼす（#1744）。
    /// </para>
    /// <para>
    /// 形式検証は本体と同じ <see cref="PathValidator.ValidatePathFormat"/> だけを行い、
    /// <b>到達性は確認しない</b>。ネットワークが一時的に切れているだけの正当な共有 DB パスを
    /// 無効と判定して黙ってローカルへ切り替えると、本体と違う DB を見ているのに
    /// そうと分からない状態に戻ってしまうため（#1599 と同じ判断）。
    /// </para>
    /// </remarks>
    public static class DatabasePathResolver
    {
        /// <summary>既定のデータベースパス（本体と同じ場所）</summary>
        public static string GetDefaultDatabasePath()
        {
            return System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ICCardManager",
                "iccard.db");
        }

        /// <summary>
        /// データベースパスを解決する。
        /// </summary>
        /// <param name="commandLineArgs"><see cref="Environment.GetCommandLineArgs"/> の戻り値（先頭は実行ファイル）</param>
        /// <param name="baseDirectory">実行ファイルのディレクトリ</param>
        /// <param name="configuredPathReader"><c>database_config.txt</c> の値を読む関数</param>
        /// <param name="fileExists">ファイルの存在確認（テスト用。既定は <see cref="File.Exists"/>）</param>
        public static DatabasePathResolution Resolve(
            string[] commandLineArgs,
            string baseDirectory,
            Func<string> configuredPathReader,
            Func<string, bool> fileExists = null)
        {
            var exists = fileExists ?? File.Exists;

            // 1. コマンドライン引数（開発者が明示的に指定した DB を最優先する）
            if (commandLineArgs != null && commandLineArgs.Length > 1 && exists(commandLineArgs[1]))
            {
                return new DatabasePathResolution(commandLineArgs[1], DatabasePathSource.CommandLine, null);
            }

            // 2. database_config.txt（本体が実際に開いている DB。共有フォルダモードではこれが正）
            string rejectedConfiguredPath = null;
            var configured = SafeReadConfiguredPath(configuredPathReader);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var validation = PathValidator.ValidatePathFormat(configured);
                if (validation.IsValid)
                {
                    // 到達性は確認しない（上の remarks 参照）
                    return new DatabasePathResolution(configured, DatabasePathSource.ConfigFile, null);
                }

                rejectedConfiguredPath = configured;
            }

            // 3. 実行ファイルと同じディレクトリ（持ち出した DB を横に置いて見る運用）
            if (!string.IsNullOrEmpty(baseDirectory))
            {
                var localDb = System.IO.Path.Combine(baseDirectory, "iccard.db");
                if (exists(localDb))
                {
                    return new DatabasePathResolution(
                        localDb, DatabasePathSource.ExecutableDirectory, rejectedConfiguredPath);
                }
            }

            // 4. 既定パス（見つからなくてもここを返す。本体と同じ場所）
            return new DatabasePathResolution(
                GetDefaultDatabasePath(), DatabasePathSource.Default, rejectedConfiguredPath);
        }

        /// <summary>
        /// 設定ファイルの読み取りで例外が出ても解決を止めない
        /// （設定ファイルが壊れていてもツールは起動できるべき）。
        /// </summary>
        private static string SafeReadConfiguredPath(Func<string> configuredPathReader)
        {
            if (configuredPathReader == null)
            {
                return null;
            }

            try
            {
                return configuredPathReader();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
