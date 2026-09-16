using System;
using System.IO;
using System.Linq;

namespace ICCardManager.UITests.Infrastructure
{
    /// <summary>
    /// UI テストが開発機の既存 DB（<c>%ProgramData%\ICCardManager\iccard.db</c>）を壊さないよう、
    /// 退避と復元を 1 か所で受け持つ（Issue #2062）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 退避は 1 回の起動手順（<see cref="AppFixture.Launch"/> / <see cref="AppFixture.LaunchWithSeed"/>）につき
    /// <b>入口で 1 回だけ</b>行う。旧実装は起動のたびに「DB があれば退避する」を繰り返したため、
    /// 投入済み DB で 2 度目の起動をした時点で元の DB の退避ファイルを投入データで上書きし、
    /// 終了時にそれを「復元」していた。
    /// </para>
    /// <para>
    /// DB 本体だけでなく付随ファイル（<c>-journal</c> / <c>-wal</c> / <c>-shm</c>）も対で扱う。
    /// テスト中に強制終了したアプリのジャーナルが残ったまま元の DB を書き戻すと、次に開いたときに
    /// SQLite がそのジャーナルで元の DB を「ロールバック」して壊すため。
    /// </para>
    /// </remarks>
    internal sealed class UiTestDatabaseGuard
    {
        internal const string DatabaseFileName = "iccard.db";
        internal const string BackupSuffix = ".uitest-backup";

        /// <summary>元の DB が無かったことを示す印。中断後の回復で、テストが作った DB を消すために使う。</summary>
        internal const string AbsentMarkerFileName = DatabaseFileName + ".uitest-absent";

        /// <summary>本体が DB の保存先を読む設定ファイル（<c>SettingsViewModel.GetDatabaseConfigPath</c> と同じ名前）。</summary>
        internal const string DatabaseConfigFileName = "database_config.txt";

        /// <summary>旧実装（#2062 以前）の <c>LaunchWithSeed</c> が中断時に残し得た退避ファイル。</summary>
        internal static readonly string LegacySeededOriginalFileName = DatabaseFileName + BackupSuffix + ".seeded-original";

        private static readonly string[] CompanionSuffixes = { "-journal", "-wal", "-shm" };

        private readonly string _directory;
        private bool _restored;

        private UiTestDatabaseGuard(string directory)
        {
            _directory = directory;
        }

        internal string DatabasePath => Path.Combine(_directory, DatabaseFileName);

        /// <summary>
        /// 前回の中断を回復したうえで、現在の DB を退避する。
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// <c>database_config.txt</c> が保存先を指定している（アプリが退避対象と別の DB を開く）。
        /// </exception>
        internal static UiTestDatabaseGuard Acquire(string directory)
        {
            if (directory == null) throw new ArgumentNullException(nameof(directory));

            ThrowIfDatabaseLocationIsConfigured(directory);
            Directory.CreateDirectory(directory);

            var guard = new UiTestDatabaseGuard(directory);
            guard.RecoverInterruptedRun();

            if (File.Exists(guard.DatabasePath))
            {
                // 付随ファイルを先に退避し、DB 本体の退避ファイルを最後に置く。
                // DB 本体の退避ファイルが「退避が完了した」ことの印になる（途中で落ちたら印が無いので回復しない）。
                foreach (var name in CompanionFileNames().Where(n => File.Exists(guard.PathOf(n))))
                {
                    CopyWithRetry(guard.PathOf(name), guard.BackupPathOf(name));
                }
                CopyWithRetry(guard.DatabasePath, guard.BackupPathOf(DatabaseFileName));
            }
            else
            {
                File.WriteAllText(guard.PathOf(AbsentMarkerFileName), string.Empty);
            }

            return guard;
        }

        /// <summary>
        /// テストが使った DB と付随ファイルを削除する（マイグレーションを空 DB から走らせるため）。
        /// 削除できなければ例外を投げる — 残ったままアプリを起動すると、退避済みとはいえ元の DB へ投入してしまう。
        /// </summary>
        internal void DeleteWorkingDatabase()
        {
            foreach (var name in AllDatabaseFileNames())
            {
                DeleteWithRetry(PathOf(name));
            }
        }

        /// <summary>
        /// 退避した状態へ戻す。2 回目以降の呼び出しは何もしない。
        /// </summary>
        /// <remarks>
        /// 書き戻しに失敗したら退避ファイルを消さずに例外を投げる。次回の <see cref="Acquire"/> が回復する。
        /// </remarks>
        internal void Restore()
        {
            if (_restored) return;
            RestoreFromBackup();
            _restored = true;
        }

        private void RecoverInterruptedRun()
        {
            // 旧実装の中断痕。旧実装では .uitest-backup が投入済み DB で上書きされている場合があるため、こちらを優先する
            var legacy = PathOf(LegacySeededOriginalFileName);
            if (File.Exists(legacy))
            {
                CopyWithRetry(legacy, BackupPathOf(DatabaseFileName));
                DeleteWithRetry(legacy);
            }

            if (File.Exists(BackupPathOf(DatabaseFileName)) || File.Exists(PathOf(AbsentMarkerFileName)))
            {
                RestoreFromBackup();
            }
        }

        private void RestoreFromBackup()
        {
            var dbBackup = BackupPathOf(DatabaseFileName);
            var absentMarker = PathOf(AbsentMarkerFileName);

            if (File.Exists(dbBackup))
            {
                // テストのジャーナル等を残したまま書き戻すと、元の DB がそれでロールバックされる
                foreach (var name in AllDatabaseFileNames())
                {
                    DeleteWithRetry(PathOf(name));
                }

                foreach (var name in CompanionFileNames().Where(n => File.Exists(BackupPathOf(n))))
                {
                    CopyWithRetry(BackupPathOf(name), PathOf(name));
                }
                CopyWithRetry(dbBackup, DatabasePath);

                // 書き戻しがすべて成功してから退避ファイルを消す。DB 本体の退避ファイル（回復の印）は最後
                foreach (var name in CompanionFileNames())
                {
                    DeleteWithRetry(BackupPathOf(name));
                }
                DeleteWithRetry(dbBackup);
                DeleteWithRetry(absentMarker);
            }
            else if (File.Exists(absentMarker))
            {
                // 元の DB が無かった。テストが作った DB を「自分の DB」として残さない
                foreach (var name in AllDatabaseFileNames())
                {
                    DeleteWithRetry(PathOf(name));
                }
                DeleteWithRetry(absentMarker);
            }
        }

        private static void ThrowIfDatabaseLocationIsConfigured(string directory)
        {
            var configPath = Path.Combine(directory, DatabaseConfigFileName);
            if (!File.Exists(configPath)) return;

            var configured = File.ReadAllText(configPath).Trim();
            if (configured.Length == 0) return;

            throw new InvalidOperationException(
                $"{configPath} がデータベースの保存先「{configured}」を指定しているため、UI テストを中止しました。" +
                "UI テストが退避・復元するのは既定の保存先の iccard.db だけで、このままでは指定先の DB へ書き込みます。" +
                "設定画面（F5）の「デフォルトに戻す」で保存先を既定に戻してから実行してください。");
        }

        private static string[] CompanionFileNames() =>
            CompanionSuffixes.Select(s => DatabaseFileName + s).ToArray();

        private static string[] AllDatabaseFileNames() =>
            new[] { DatabaseFileName }.Concat(CompanionFileNames()).ToArray();

        private string PathOf(string fileName) => Path.Combine(_directory, fileName);

        private string BackupPathOf(string fileName) => Path.Combine(_directory, fileName + BackupSuffix);

        private static void CopyWithRetry(string source, string destination)
        {
            RetryOnIOException(() => File.Copy(source, destination, overwrite: true));
        }

        private static void DeleteWithRetry(string path)
        {
            // File.Delete は存在しないパスでは何もしない
            RetryOnIOException(() => File.Delete(path));
        }

        /// <summary>
        /// 終了直後のアプリがファイルを掴んでいる間だけ待つ。最後の試行の例外はそのまま投げる（握りつぶさない）。
        /// </summary>
        private static void RetryOnIOException(Action action)
        {
            const int maxAttempts = 10;
            const int delayMs = 500;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    action();
                    return;
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    System.Threading.Thread.Sleep(delayMs);
                }
            }
        }
    }
}
