using System;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;

namespace ICCardManager.UITests.Infrastructure
{
    /// <summary>
    /// UI テストが開発機の既存 DB（<c>%ProgramData%\ICCardManager\iccard.db</c>）を壊さないよう、
    /// 退避と復元を 1 か所で受け持つ（Issue #2062）。
    /// DB と同じフォルダーの更新通知ファイル（<c>latest_version.txt</c>）も同じ規約で退避する（Issue #2394）。
    /// アプリが書くバックアップ先は、テスト用の DB の設定でテスト専用のフォルダーへ向けて隔離する（Issue #2423）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 守る対象は「テストのアプリが<b>読み書きする</b>撮影機の状態」から数える。読む側だけを数えると、
    /// アプリが書く場所（バックアップ先とその整理）が漏れる（Issue #2423）。現在の扱い:
    /// </para>
    /// <list type="table">
    /// <listheader><term>撮影機の状態</term><description>扱い</description></listheader>
    /// <item><term>DB（<c>iccard.db</c> と付随ファイル）</term><description>読み書き。退避して終了後に復元する（#2062 / #2290）</description></item>
    /// <item><term>更新通知ファイル（<c>latest_version.txt</c>）</term><description>読み書き。退避して取り除き、終了後に復元する（#2394）</description></item>
    /// <item><term>バックアップ先（既定 <c>%ProgramData%\ICCardManager\backup</c>）</term><description>
    /// 書く（起動時の自動バックアップと古い世代の整理）。退避ではなく<b>隔離</b>する — テスト用の DB の
    /// <c>settings.backup_path</c> を <see cref="UiTestBackupDirectory"/> にして、アプリが既定の保存先に触れないようにする
    /// （<see cref="IsolateBackupFolder"/>）。整理は「各日の最新 1 世代」を残すので、同じ日に作られたテスト用 DB の
    /// バックアップが実 DB のバックアップを押し出していた。退避・復元にしないのは、整理が消した実 DB のバックアップを
    /// 書き戻す手段が無い（消えた後では遅い）ため</description></item>
    /// <item><term>ログ（<c>%ProgramData%\ICCardManager\Logs</c>）</term><description>
    /// 書く（その日のファイルへの追記と、保持期間を過ぎたファイルの削除）。<b>扱わない</b>。追記は既存の行を変えず
    /// （テストの起動の行は時刻で見分けられる）、削除はファイルの日付と保持日数だけで決まる。テストのアプリ（作業ツリーのビルド）と
    /// インストール版の <c>appsettings.json</c> の <c>Logging:File:RetentionDays</c>（<c>FileLoggerOptions.RetentionDays</c>）が同じ
    /// （既定 30 日）なら、本物のアプリが起動しても同じファイルを消すので、テストの起動で失われる記録が無い。インストール版の保持日数を
    /// 既定より長くしている開発機では、テストの起動がそれより古いログを先に消す（この前提が崩れたら扱いを見直す）</description></item>
    /// <item><term>設定ファイル（<c>database_config.txt</c> 等）</term><description>
    /// 読む。保存先を指定していたら中止する（<see cref="ThrowIfDatabaseLocationIsConfigured"/>）。書くのは設定画面の保存の操作だけで、
    /// 起動では書かれない</description></item>
    /// </list>
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

        /// <summary>
        /// 更新通知ファイル（本体の <c>UpdateNotificationService.LatestVersionFileName</c> と同じ名前。DB と同じフォルダーに置かれる）。
        /// </summary>
        internal const string LatestVersionFileName = "latest_version.txt";

        /// <summary>元の更新通知ファイルが無かったことを示す印。中断後の回復で、テスト中のアプリが書いたファイルを消すために使う。</summary>
        internal const string LatestVersionAbsentMarkerFileName = LatestVersionFileName + ".uitest-absent";

        /// <summary>
        /// 更新通知ファイルの退避を書き込む途中の名前。書き切ってから退避の名前へ改名する（途中で落ちた退避を「退避済みの印」にしない）。
        /// 本体の一時ファイルの形（<c>latest_version.txt.&lt;8桁の16進&gt;.tmp</c>）とは一致しないので、本体の回収の対象にならない。
        /// </summary>
        internal const string LatestVersionBackupTempFileName = LatestVersionFileName + BackupSuffix + ".tmp";

        /// <summary>旧実装（#2062 以前）の <c>LaunchWithSeed</c> が中断時に残し得た退避ファイル。</summary>
        internal static readonly string LegacySeededOriginalFileName = DatabaseFileName + BackupSuffix + ".seeded-original";

        /// <summary>
        /// バックアップ先の設定キー（本体の <c>SettingsRepository.KeyBackupPath</c> と同じ。Issue #2423）。
        /// </summary>
        internal const string BackupPathSettingKey = "backup_path";

        /// <summary>
        /// 既定のバックアップ先のフォルダー名（本体の <c>PathValidator.GetDefaultBackupPath</c> は DB と同じフォルダーの配下のこの名前）。
        /// </summary>
        internal const string DefaultBackupFolderName = "backup";

        /// <summary>
        /// UI テストのアプリが自動バックアップを作るフォルダー（Issue #2423）。
        /// </summary>
        /// <remarks>
        /// <c>%TEMP%</c> ではなく <c>%ProgramData%</c> の配下に置く。システム管理画面（F6）は保存先のパスを表示し、撮影
        /// （<c>restore_list.png</c>）に写る。<c>%TEMP%</c> だと撮影機の利用者名がマニュアルの画像へ入る。
        /// 実行のたびに変えないのは、世代が溜まり続けないようにするため（古い世代はアプリ自身の整理が消す）。
        /// 本体の既定の保存先（<c>%ProgramData%\ICCardManager\backup</c>）の外にあり、本体の整理はこのフォルダーにしか効かない。
        /// </remarks>
        internal static string UiTestBackupDirectory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            UiTestBackupRootFolderName,
            DefaultBackupFolderName);

        /// <summary>
        /// <see cref="UiTestBackupDirectory"/> の親フォルダーの名前（<c>%ProgramData%</c> の直下）。本体がこの場所をバックアップ先として
        /// 受け付けることは単体テスト（<c>UiTestBackupIsolationConstantsTests</c>）が本体の検証で確かめる。
        /// </summary>
        internal const string UiTestBackupRootFolderName = "ICCardManager.UITests";

        private static readonly string[] CompanionSuffixes = { "-journal", "-wal", "-shm" };

        /// <summary>終了直後のアプリがファイルを掴んでいる間の再試行の回数（500ms 間隔。既定で約 5 秒待つ）。</summary>
        internal const int DefaultMaxAttempts = 10;

        private const int RetryDelayMs = 500;

        private readonly string _directory;
        private readonly int _maxAttempts;
        private bool _restored;

        /// <summary>
        /// <see cref="Acquire"/> の時点の既定のバックアップ先のファイル名（読み取りのみ。Issue #2423）。
        /// 起動の終わりに <see cref="ThrowIfDefaultBackupFolderChanged"/> が比べる。
        /// </summary>
        private string[] _defaultBackupFilesAtAcquire = new string[0];

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
            guard._defaultBackupFilesAtAcquire = guard.ListDefaultBackupFiles();

            // Issue #2290: 回復・退避より前に確かめる。退避のコピーは開いているファイルでも成功するため、確かめずに退避すると
            // 続く削除だけが失敗して、退避ファイル（＝「中断した実行の痕」の印）が残る。次回の Acquire はそれを書き戻し、
            // その間にピッすいで記録した貸出・返却を巻き戻す。回復（書き戻し）も DB を消すので、開かれている間は同じく失敗する
            guard.ThrowIfDatabaseInUse();
            // Issue #2394: 更新通知ファイルも、取り除けるかを何も触る前に確かめる（読み取り専用・別のプロセスが開いている）
            guard.ThrowIfLatestVersionFileNotRemovable();
            guard.RecoverInterruptedRun();
            guard.DeleteOrphanedCompanionBackups();

            // Issue #2394: 更新通知ファイルは DB より先に退避する。DB の回復の印を置いた後に失敗すると、ガードが返らないので
            // Restore も DiscardBackup も走らず、印だけが残る。次回の Acquire がそれを書き戻し、その間の業務の記録を巻き戻す（#2290）
            guard.SetAsideLatestVersionFile();

            try
            {
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
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // DB の退避が完了しなかった（印が無いので、DB は次回も回復しない）。取り除いた更新通知ファイルはここで戻す。
                // 戻せなくても退避ファイルは残るので次回の Acquire が戻す。本来の原因（DB の退避の失敗）を失わないよう、こちらは握る
                try
                {
                    guard.RestoreLatestVersionFile();
                }
                catch (Exception restoreFailure) when (restoreFailure is IOException || restoreFailure is UnauthorizedAccessException)
                {
                }

                throw;
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
                // Issue #2394: 更新通知ファイルは DB と違い、退避した時点で取り除いている（テストのアプリに読ませないため）。
                // 退避を捨てたガードの Restore は何もしないので、ここで戻す。DB の退避の破棄の成否とは独立に試み、
                // 失敗の文言も分ける（更新通知ファイルの退避は次の実行が戻すだけで、業務の記録を巻き戻さない）
                Exception? discardFailure = null;
                try
                {
                    DiscardBackup();
                }
                catch (Exception failure) when (failure is IOException || failure is UnauthorizedAccessException)
                {
                    discardFailure = failure;
                }

                Exception? latestVersionFailure = null;
                try
                {
                    RestoreLatestVersionFile();
                }
                catch (Exception failure) when (failure is IOException || failure is UnauthorizedAccessException)
                {
                    latestVersionFailure = failure;
                }

                if (discardFailure != null)
                {
                    // 削除の失敗（本来の原因）を失わない。net48 の AggregateException.Message は内側の例外の文言を含まないので、
                    // 残り得るファイルと、残したまま実行したときの結果は文言そのものに書く
                    throw new AggregateException(
                        $"{DatabasePath} を別のプロセスが開いていたため UI テストの DB を消せず、" +
                        $"退避ファイル {BackupPathOf(DatabaseFileName)}（元の DB が無かったときは {PathOf(AbsentMarkerFileName)}）も捨て切れませんでした。" +
                        "このファイルを残したまま UI テストを実行すると、次の実行がそれを書き戻し、その間に加えた変更が巻き戻ります。" +
                        (latestVersionFailure == null
                            ? string.Empty
                            : $"また、一時的に退避した更新通知ファイルも元へ戻せませんでした。その退避 {BackupPathOf(LatestVersionFileName)}" +
                              $"（元が無かったときは {PathOf(LatestVersionAbsentMarkerFileName)}）は次の実行が元へ戻すので、削除しないでください。") +
                        "ピッすいを終了し、DB の退避ファイルが残っていれば削除してから実行してください。",
                        new[] { ex, discardFailure }.Concat(latestVersionFailure == null ? new Exception[0] : new[] { latestVersionFailure }));
                }

                if (latestVersionFailure != null)
                {
                    throw new AggregateException(
                        $"{DatabasePath} を別のプロセスが開いていたため UI テストの DB を消せず（DB には触れていません）、" +
                        $"一時的に退避した更新通知ファイル {PathOf(LatestVersionFileName)} も元へ戻せませんでした。" +
                        $"退避 {BackupPathOf(LatestVersionFileName)}（元が無かったときは {PathOf(LatestVersionAbsentMarkerFileName)}）は次の実行が元へ戻すので、削除しないでください。" +
                        "ピッすいを終了してから実行してください。",
                        ex, latestVersionFailure);
                }

                throw;
            }

            foreach (var name in CompanionFileNames())
            {
                DeleteWithRetry(PathOf(name));
            }
        }

        /// <summary>
        /// アプリの既定のバックアップ先（DB のフォルダーの <see cref="DefaultBackupFolderName"/>）。
        /// アプリの <c>PathValidator.GetDefaultBackupPath</c> と同じ場所で、テストのアプリにはここへ書かせない（Issue #2423）。
        /// </summary>
        internal string DefaultBackupDirectory => PathOf(DefaultBackupFolderName);

        /// <summary>
        /// Issue #2423: <see cref="Acquire"/> の後に既定のバックアップ先のファイルが増えた・消えたら例外を投げる（ファイルには触れない）。
        /// テストのアプリの終了と DB の復元の後に呼ぶ。
        /// </summary>
        /// <remarks>
        /// 隔離はアプリが設定先を受け付けることに依っている。本体は <c>backup_path</c> がパスの検証に通らないと、警告のログを出すだけで
        /// 既定の保存先へ戻る（<c>BackupService.ResolveBackupFolderDetailAsync</c>）。そのときテストは緑のまま、同じ日の実 DB の
        /// バックアップが整理で押し出される。黙って戻ったことを、起動のたびに結果（既定の保存先の中身）で見張る。
        /// 消えたファイルも数える — 整理が消すのは既にあった世代で、守りたいのはそちらである。
        /// インストール版のピッすいは原因にならない: 先に動いていれば <see cref="Acquire"/> の排他の確認（#2290）で止まり、
        /// テストの最中に起動すればテスト用の DB（バックアップ先は UI テスト専用のフォルダー）を開くので、既定の保存先は変わらない
        /// （テストの最中に起動すること自体は別の誤りで、終了時の復元の失敗として現れる）。したがってこの例外は隔離が外れたことを示す。
        /// </remarks>
        internal void ThrowIfDefaultBackupFolderChanged()
        {
            var now = ListDefaultBackupFiles();
            var added = now.Except(_defaultBackupFilesAtAcquire, StringComparer.OrdinalIgnoreCase).ToArray();
            var removed = _defaultBackupFilesAtAcquire.Except(now, StringComparer.OrdinalIgnoreCase).ToArray();
            if (added.Length == 0 && removed.Length == 0)
            {
                return;
            }

            throw new InvalidOperationException(
                $"UI テストの間に既定のバックアップ先 {DefaultBackupDirectory} のファイルが変わりました" +
                $"（増えた: {(added.Length == 0 ? "なし" : string.Join(", ", added))}、消えた: {(removed.Length == 0 ? "なし" : string.Join(", ", removed))}）。" +
                "テストのアプリが UI テスト専用のバックアップ先を使わず、既定の保存先へバックアップを作って古い世代を整理しました。" +
                $"アプリのログ（{Path.Combine(_directory, "Logs")}）で「バックアップパスが無効です」を探し、" +
                $"UI テスト専用のバックアップ先 {UiTestBackupDirectory} をアプリが受け付けるか確かめてください。");
        }

        private string[] ListDefaultBackupFiles() =>
            Directory.Exists(DefaultBackupDirectory)
                ? Directory.GetFiles(DefaultBackupDirectory).Select(Path.GetFileName).ToArray()
                : new string[0];

        /// <summary>
        /// Issue #2423: テストのアプリが開く DB の <c>settings.backup_path</c> を <paramref name="backupDirectory"/> にして、
        /// 起動時の自動バックアップと古い世代の整理を既定の保存先から切り離す。アプリを起動する<b>前</b>に呼ぶこと。
        /// </summary>
        /// <param name="backupDirectory">テスト専用のバックアップ先（無ければ作る）。</param>
        /// <remarks>
        /// <para>
        /// 本体では <c>backup_path</c> を書くのはバックアップ先の変更（<c>SettingsRepository.ChangeBackupPathAsync</c>。職員証の認証と
        /// 変更日時・PC の記録を伴う）だけで、一括保存にも載らない。ここで直接書くのは、テストのアプリに既定の保存先を一度も
        /// 使わせないため — 起動時の自動バックアップはメイン画面の表示より前に走るので、起動してから画面で変える手段では間に合わない。
        /// 変更日時・PC は書かない。システム管理画面の「バックアップ先の最終変更」は、新規 DB（投入して起動する <see cref="AppFixture.LaunchWithSeed(System.Action{System.Data.SQLite.SQLiteConnection})"/>）では
        /// 「記録なし」、開発機の DB をそのまま開く <see cref="AppFixture.Launch"/> ではその DB に残っている記録（UI テストのフォルダーへ変えた記録ではない）を表示する。
        /// </para>
        /// <para>
        /// DB が無ければ、<c>settings</c> テーブル（本体の初期マイグレーションと同じ定義）だけを持つ DB を作る。<c>staff</c> テーブルが
        /// 無いので、アプリはこれを新規 DB とみなして初期マイグレーションから適用する（<c>CREATE TABLE IF NOT EXISTS</c> と
        /// <c>INSERT OR IGNORE</c> なので、書いた行は残る）。DB があれば（<see cref="AppFixture.Launch"/> は開発機の DB をそのまま開く）
        /// その行だけを上書きする。DB は退避済みなので、元の設定は <see cref="Restore"/> で戻る。
        /// </para>
        /// <para>
        /// 設定したフォルダーをアプリが使えない（パスの検証に通らない）と、アプリは既定の保存先へ退避する。
        /// それを 2 か所で見張る: 本体の検証がこのフォルダーを受け付けること（単体テスト <c>UiTestBackupIsolationConstantsTests</c>。CI で走る）と、
        /// 起動の終わりに既定の保存先の中身が変わっていないこと（<see cref="ThrowIfDefaultBackupFolderChanged"/>。<see cref="AppFixture"/> の Dispose）。
        /// </para>
        /// </remarks>
        internal void IsolateBackupFolder(string backupDirectory)
        {
            if (string.IsNullOrWhiteSpace(backupDirectory))
            {
                throw new ArgumentException("テスト専用のバックアップ先を指定する", nameof(backupDirectory));
            }

            var normalized = Path.GetFullPath(backupDirectory).TrimEnd(Path.DirectorySeparatorChar);
            var defaultDirectory = Path.GetFullPath(DefaultBackupDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(normalized, defaultDirectory, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(defaultDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                // 既定の保存先とその配下は、本体の整理（各日の最新 1 世代）が実 DB のバックアップと同じ扱いで間引く
                throw new ArgumentException(
                    $"テスト専用のバックアップ先 {backupDirectory} が既定のバックアップ先 {DefaultBackupDirectory} と重なっています。" +
                    "既定のバックアップ先の外のフォルダーを指定してください。",
                    nameof(backupDirectory));
            }

            Directory.CreateDirectory(normalized);

            using var connection = new SQLiteConnection($"Data Source={DatabasePath};Version=3");
            connection.Open();
            using (var create = connection.CreateCommand())
            {
                create.CommandText = "CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT)";
                create.ExecuteNonQuery();
            }

            using var upsert = connection.CreateCommand();
            upsert.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES (@key, @value)";
            upsert.Parameters.AddWithValue("@key", BackupPathSettingKey);
            upsert.Parameters.AddWithValue("@value", normalized);
            upsert.ExecuteNonQuery();
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

            // 更新通知ファイルは DB の書き戻しの成否と独立に戻す（DB の書き戻しの失敗で、開発機の更新通知ファイルを消えたままにしない）。
            // DB の書き戻しが失敗したときはその例外を優先して伝える（更新通知ファイルの退避は残り、次回の Acquire が戻す）
            Exception? latestVersionFailure = null;
            try
            {
                RestoreLatestVersionFile();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                latestVersionFailure = ex;
            }

            RestoreFromBackup();

            if (latestVersionFailure != null)
            {
                ExceptionDispatchInfo.Capture(latestVersionFailure).Throw();
            }

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

            // 更新通知ファイルはここでは戻さない。呼び出し元（DeleteWorkingDatabase）が、この破棄の成否と独立に戻す
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

            // DB の印とは独立に回復する（DB の有無と更新通知ファイルの有無は独立）
            RestoreLatestVersionFile();
        }

        /// <summary>
        /// Issue #2394: 更新通知ファイルを退避し、テストのアプリから見えないよう取り除く。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 本体は DB と同じフォルダーの <c>latest_version.txt</c> に自バージョンより新しい版が書かれていると、
        /// システム警告エリアに「新しいバージョン … が公開されています」を出す。開発機に残ったこのファイル
        /// （実例は 99.0.0）が、マニュアル用のスクリーンショットに写り込んでいた。撮影の結果を撮影機の状態に左右させない。
        /// </para>
        /// <para>
        /// 退避の形は DB と同じ規約に揃える。元があれば <see cref="BackupSuffix"/> 付きの退避ファイル、
        /// 無ければ印（<see cref="LatestVersionAbsentMarkerFileName"/>）を置き、どちらかが「退避が完了した」ことの印になる。
        /// 本体の起動時タスク（Issue #2149）はファイルが無ければ自バージョンを書くので、復元ではテスト中に書かれたファイルを消してから戻す。
        /// </para>
        /// <para>
        /// 中断からの回復は、中断から次の実行までの間に本物のアプリが書いたファイルも、退避した時点の古い内容で上書きする。これは許容する —
        /// 起動時タスクは自分の方が新しいときだけ自バージョンを書く（単調増加）ので、次にアプリを起動したときに直り、害は更新の案内の遅れだけ。
        /// </para>
        /// </remarks>
        private void SetAsideLatestVersionFile()
        {
            var path = PathOf(LatestVersionFileName);
            var temp = PathOf(LatestVersionBackupTempFileName);

            // 前回、退避の書き込みの途中で落ちた残り。印ではない（退避の名前に改名する前に落ちた）ので捨てる
            DeleteWithRetry(temp);

            if (File.Exists(path))
            {
                // 退避ファイルを置き切ってから元を消す（途中で落ちても、印があれば次回の回復が書き戻す）。
                // 一時名へ書き切ってから退避の名前へ改名する。退避の名前へ直接コピーすると、途中で落ちた中途半端な内容が
                // 「退避済みの印」になり、次回の回復がそれで元を上書きする
                var backup = BackupPathOf(LatestVersionFileName);
                try
                {
                    CopyWithRetry(path, temp);
                    RetryOnIOException(() => File.Move(temp, backup));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // 退避は完了していない（元は残っている）。一時名の残りを捨ててから伝える。捨て切れなくても次回の Acquire が捨てる
                    try
                    {
                        File.Delete(temp);
                    }
                    catch (Exception cleanupFailure) when (cleanupFailure is IOException || cleanupFailure is UnauthorizedAccessException)
                    {
                    }

                    throw;
                }
                try
                {
                    DeleteWithRetry(path);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // 確認（ThrowIfLatestVersionFileNotRemovable）と削除の間に開かれた。元は残っているので退避を捨てる
                    // （残すと次回の回復が、その間に書き換わった内容を退避した時点の内容で上書きする）。
                    // 退避ファイルは元の属性ごとコピーされるので、読み取り専用を外してから消す。
                    // 確認と削除の間の競合でしか通らないため、単体テストでは再現していない（確認の側の文言は UiTestDatabaseGuardTests が固定）
                    var leftover = string.Empty;
                    try
                    {
                        File.SetAttributes(backup, FileAttributes.Normal);
                        DeleteWithRetry(backup);
                    }
                    catch (Exception cleanupFailure) when (cleanupFailure is IOException || cleanupFailure is UnauthorizedAccessException)
                    {
                        // 後始末の失敗で、本来の原因（取り除けなかったこと）と案内を失わない。残った退避は名指しする
                        // （元は残っているので、この退避は不要。残したままにすると次回の回復が元を退避した時点の内容で上書きする）
                        leftover = $"退避ファイル {backup} も消せませんでした。元のファイルは残っているので、この退避ファイルは削除してください。";
                    }

                    throw new InvalidOperationException(
                        $"更新通知ファイル {path} を取り除けなかったため、UI テストを中止しました（DB にも更新通知ファイルにも触れていません）。" +
                        "確認の直後に、別のプロセスがこのファイルを開いたか、属性・アクセス権が変わりました。" +
                        leftover +
                        "このファイルを開いているアプリ（ピッすい・エディター等）を終了し、読み取り専用の属性とアクセス権を確認してから実行してください。",
                        ex);
                }
            }
            else
            {
                File.WriteAllText(PathOf(LatestVersionAbsentMarkerFileName), string.Empty);
            }
        }

        /// <summary>
        /// Issue #2394: 更新通知ファイルを取り除けない（読み取り専用・権限不足・別のプロセスが開いている）なら、何も触らずに中止する。
        /// </summary>
        /// <remarks>
        /// 取り除けないまま撮影すると、開発機の更新の案内が画像へ写る。退避の途中で失敗すると、読み取り専用の属性ごとコピーされた
        /// 退避ファイルが残り、次回以降の回復が毎回それを書き戻そうとして失敗する。
        /// </remarks>
        private void ThrowIfLatestVersionFileNotRemovable()
        {
            var path = PathOf(LatestVersionFileName);
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                RetryOnIOException(() =>
                {
                    using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                    }
                });
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    $"更新通知ファイル {path} を別のプロセスが使用しているため、UI テストを中止しました（DB にも更新通知ファイルにも触れていません）。" +
                    "UI テストは撮影画像に開発機の更新の案内が写らないよう、このファイルを一時的に退避します。" +
                    "このファイルを開いているアプリ（ピッすい・エディター等）を終了してから実行してください。",
                    ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException(
                    $"更新通知ファイル {path} が読み取り専用か、書き込む権限が無いため、UI テストを中止しました（DB にも更新通知ファイルにも触れていません）。" +
                    "UI テストは撮影画像に開発機の更新の案内が写らないよう、このファイルを一時的に退避します。" +
                    "ファイルの読み取り専用の属性とアクセス権を確認してから実行してください。",
                    ex);
            }
        }

        /// <summary>
        /// 退避した更新通知ファイルを書き戻す（元が無かったなら、テスト中に書かれたファイルを消す）。退避が無ければ何もしない。
        /// </summary>
        /// <remarks>
        /// 書き戻しに失敗したら退避ファイル（印）を消さずに例外を投げる。次回の <see cref="Acquire"/> が回復する。
        /// </remarks>
        private void RestoreLatestVersionFile()
        {
            var path = PathOf(LatestVersionFileName);
            var backup = BackupPathOf(LatestVersionFileName);
            var absentMarker = PathOf(LatestVersionAbsentMarkerFileName);

            if (File.Exists(backup))
            {
                CopyWithRetry(backup, path);
                DeleteWithRetry(backup);
                DeleteWithRetry(absentMarker);
            }
            else if (File.Exists(absentMarker))
            {
                DeleteWithRetry(path);
                DeleteWithRetry(absentMarker);
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
