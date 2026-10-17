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

        /// <summary>本体が DB の保存先を読む設定ファイル（<c>AppConfigFiles.DatabaseConfigPath</c> と同じ名前）。</summary>
        internal const string DatabaseConfigFileName = "database_config.txt";

        /// <summary>旧実装（#2062 以前）の <c>LaunchWithSeed</c> が中断時に残し得た退避ファイル。</summary>
        internal static readonly string LegacySeededOriginalFileName = DatabaseFileName + BackupSuffix + ".seeded-original";

        private static readonly string[] CompanionSuffixes = { "-journal", "-wal", "-shm" };

        /// <summary>終了直後のアプリがファイルを掴んでいる間の再試行の回数（500ms 間隔。既定で約 5 秒待つ）。</summary>
        internal const int DefaultMaxAttempts = 10;

        private const int RetryDelayMs = 500;

        private readonly string _directory;
        private readonly int _maxAttempts;
        private bool _restored;

        private UiTestDatabaseGuard(string directory, int maxAttempts)
        {
            _directory = directory;
            _maxAttempts = maxAttempts;
        }

        internal string DatabasePath => Path.Combine(_directory, DatabaseFileName);

        /// <summary>
        /// 前回の中断を回復したうえで、現在の DB を退避する。
        /// </summary>
        /// <param name="directory">DB のあるフォルダー。</param>
        /// <param name="maxAttempts">ファイルが使用中のときの試行回数（テストで待ち時間を縮めるためのもの）。</param>
        /// <exception cref="InvalidOperationException">
        /// <c>database_config.txt</c> が保存先を指定している（アプリが退避対象と別の DB を開く）。
        /// または、DB を別のプロセス（起動中のピッすい）が開いている。どちらの場合も何も触らない。
        /// </exception>
        internal static UiTestDatabaseGuard Acquire(string directory, int maxAttempts = DefaultMaxAttempts)
        {
            if (directory == null)
            {
                throw new ArgumentNullException(nameof(directory));
            }

            if (maxAttempts < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "1 以上を指定する");
            }

            ThrowIfDatabaseLocationIsConfigured(directory);
            Directory.CreateDirectory(directory);

            var guard = new UiTestDatabaseGuard(directory, maxAttempts);

            // Issue #2290: 回復・退避より前に確かめる。退避のコピーは開いているファイルでも成功するため、確かめずに退避すると
            // 続く削除だけが失敗して、退避ファイル（＝「中断した実行の痕」の印）が残る。次回の Acquire はそれを書き戻し、
            // その間にピッすいで記録した貸出・返却を巻き戻す。回復（書き戻し）も DB を消すので、開かれている間は同じく失敗する
            guard.ThrowIfDatabaseInUse();
            guard.RecoverInterruptedRun();
            guard.DeleteOrphanedCompanionBackups();

            if (File.Exists(guard.DatabasePath))
            {
                // 付随ファイルを先に退避し、DB 本体の退避ファイルを最後に置く。
                // DB 本体の退避ファイルが「退避が完了した」ことの印になる（途中で落ちたら印が無いので回復しない）。
                foreach (var name in CompanionFileNames().Where(n => File.Exists(guard.PathOf(n))))
                {
                    guard.CopyWithRetry(guard.PathOf(name), guard.BackupPathOf(name));
                }
                guard.CopyWithRetry(guard.DatabasePath, guard.BackupPathOf(DatabaseFileName));
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
        /// <remarks>
        /// <see cref="Acquire"/> の直後に呼ぶこと（DB はまだ退避した時点のまま、という前提で次の判断をしている）。
        /// <para>
        /// Issue #2290: DB 本体を消せなかったときは、退避ファイルを捨ててから例外を投げ、以後の <see cref="Restore"/> を何もしないようにする。
        /// DB は一度もテストの DB に置き換わっておらず、退避ファイルは元の DB と同じ内容なので、書き戻す必要が無い。
        /// 残すと次回の <see cref="Acquire"/> が「中断した実行の痕」と見なして書き戻し、その間に加えた変更を巻き戻す。
        /// <see cref="Acquire"/> の確認の後に DB が開かれた場合（確認と削除の間の競合）に効く。
        /// </para>
        /// <para>
        /// DB 本体を消した後に付随ファイルで失敗したときは、退避ファイルを残す（DB はもう元のままではないので、書き戻しが要る）。
        /// DB 本体を最初に消すので、本体の削除の失敗は「何も変えていない」ことを意味する。
        /// </para>
        /// </remarks>
        internal void DeleteWorkingDatabase()
        {
            try
            {
                DeleteWithRetry(DatabasePath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                try
                {
                    DiscardBackup();
                }
                catch (Exception discardFailure) when (discardFailure is IOException || discardFailure is UnauthorizedAccessException)
                {
                    // 削除の失敗（本来の原因）を失わない。net48 の AggregateException.Message は内側の例外の文言を含まないので、
                    // 残り得るファイルと、残したまま実行したときの結果は文言そのものに書く
                    throw new AggregateException(
                        $"{DatabasePath} を別のプロセスが開いていたため UI テストの DB を消せず、" +
                        $"退避ファイル {BackupPathOf(DatabaseFileName)}（元の DB が無かったときは {PathOf(AbsentMarkerFileName)}）も捨て切れませんでした。" +
                        "このファイルを残したまま UI テストを実行すると、次の実行がそれを書き戻し、その間に加えた変更が巻き戻ります。" +
                        "ピッすいを終了し、これらのファイルが残っていれば削除してから実行してください。",
                        ex, discardFailure);
                }

                throw;
            }

            foreach (var name in CompanionFileNames())
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
            if (_restored)
            {
                return;
            }

            RestoreFromBackup();
            _restored = true;
        }

        /// <summary>
        /// 退避ファイルと印を消し、以後の <see cref="Restore"/> を何もしないようにする（DB が退避した時点のままのときだけ使う）。
        /// </summary>
        private void DiscardBackup()
        {
            // 以後の Restore は書き戻さない（DB は退避した時点のままなので、書き戻す必要が無い）
            _restored = true;

            // 回復の印（DB 本体の退避ファイル・元の DB が無かった印）を先に消す。印が残ったまま途中で落ちると、次回の回復が
            // それを書き戻す（または DB を消す）。失敗の後もピッすいで業務を続ければ DB は変わっていくので、その記録を失う。
            // 印を消した後に残る付随ファイルの退避は、次回の Acquire が掃除する（DeleteOrphanedCompanionBackups）
            DeleteWithRetry(BackupPathOf(DatabaseFileName));
            DeleteWithRetry(PathOf(AbsentMarkerFileName));
            foreach (var name in CompanionFileNames())
            {
                DeleteWithRetry(BackupPathOf(name));
            }
        }

        /// <summary>
        /// 回復の印（DB 本体の退避ファイル）を伴わない付随ファイルの退避を消す（<see cref="RecoverInterruptedRun"/> の後に呼ぶ）。
        /// </summary>
        /// <remarks>
        /// 退避は付随ファイルを先に作り、DB 本体の退避ファイルを最後に置く。退避の途中で落ちたとき・退避を捨てる途中で落ちたときに、
        /// 印の無い付随ファイルの退避が残り得る。残したまま新しく退避すると、今回は存在しない付随ファイル（古い <c>-wal</c> 等）を
        /// 復元のときに書き戻し、元の DB をそれでロールバックさせる。
        /// </remarks>
        private void DeleteOrphanedCompanionBackups()
        {
            foreach (var name in CompanionFileNames())
            {
                DeleteWithRetry(BackupPathOf(name));
            }
        }

        /// <summary>
        /// Issue #2290: DB を別のプロセスが開いていたら、何も触らずに中止する。
        /// </summary>
        /// <remarks>
        /// SQLite は DB を共有読み書き（削除の共有なし）で開くので、排他で開けなければ「使用中」とみなす。
        /// 終了直後のアプリが掴んでいる間は待つ（<see cref="RetryOnIOException"/>）。
        /// </remarks>
        private void ThrowIfDatabaseInUse()
        {
            if (!File.Exists(DatabasePath))
            {
                return;
            }

            try
            {
                RetryOnIOException(() =>
                {
                    using (new FileStream(DatabasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                    }
                });
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    $"{DatabasePath} を別のプロセスが使用しているため、UI テストを中止しました（DB には何も触れていません）。" +
                    "UI テストは DB を退避してテスト用の DB に入れ替えるため、開かれている DB は入れ替えられません。" +
                    "ピッすい（インストール版・デバッグ起動）を終了してから実行してください。",
                    ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                // 読み取り専用の属性・アクセス権。ピッすいを終了しても解決しないので、別の行動を案内する
                throw new InvalidOperationException(
                    $"{DatabasePath} が読み取り専用か、書き込む権限が無いため、UI テストを中止しました（DB には何も触れていません）。" +
                    "UI テストは DB を退避してテスト用の DB に入れ替えるため、書き換えられない DB は入れ替えられません。" +
                    "ファイルの読み取り専用の属性とアクセス権を確認してから実行してください。",
                    ex);
            }
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
            if (!File.Exists(configPath))
            {
                return;
            }

            var configured = File.ReadAllText(configPath).Trim();
            if (configured.Length == 0)
            {
                return;
            }

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

        private void CopyWithRetry(string source, string destination)
        {
            RetryOnIOException(() => File.Copy(source, destination, overwrite: true));
        }

        private void DeleteWithRetry(string path)
        {
            // File.Delete は存在しないパスでは何もしない
            RetryOnIOException(() => File.Delete(path));
        }

        /// <summary>
        /// 終了直後のアプリがファイルを掴んでいる間だけ待つ。最後の試行の例外はそのまま投げる（握りつぶさない）。
        /// </summary>
        private void RetryOnIOException(Action action)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    action();
                    return;
                }
                catch (IOException) when (attempt < _maxAttempts)
                {
                    System.Threading.Thread.Sleep(RetryDelayMs);
                }
            }
        }
    }
}
