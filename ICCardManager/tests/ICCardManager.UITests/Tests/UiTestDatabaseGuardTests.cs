using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// UI テストが開発機の既存 DB と更新通知ファイルを壊さないことの検証（Issue #2062 / #2290 / #2394）。
    /// アプリは起動せず、一時フォルダー上でファイルの退避・復元だけを検証する。
    /// GUI 不要のため Category=UI を付けず、CI でも実行される。
    /// UITests はソリューション単位の実行から外れているため、CI は ci.yml の
    /// 「Run GUI-free tests in UITests project」ステップで csproj を直接指定して実行する（Issue #2099）。
    /// </summary>
    /// <remarks>
    /// DB の中身は SQLite である必要がないため、ファイルごとに異なる文字列で「どの DB か」を識別する。
    /// 起動中のピッすいが DB を開いている状態は、SQLite と同じ共有モード（読み書きの共有あり・削除の共有なし）で
    /// ファイルを開いて作る（<see cref="OpenLikeSqlite"/>）。その状態のテストは試行回数を 1 にして待ち時間を省く。
    /// </remarks>
    public class UiTestDatabaseGuardTests : IDisposable
    {
        private const string OriginalContent = "元の DB（職員の実データ）";
        private const string MigratedContent = "マイグレーション直後の空 DB";
        private const string SeededContent = "撮影用の投入データ";

        private readonly string _directory;

        public UiTestDatabaseGuardTests()
        {
            _directory = Path.Combine(Path.GetTempPath(), "ICCardManager.UITests.DbGuard." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); } catch { /* 後始末の失敗は検証対象外 */ }
        }

        private string DbPath => Path.Combine(_directory, UiTestDatabaseGuard.DatabaseFileName);

        private string PathOf(string fileName) => Path.Combine(_directory, fileName);

        private string LatestVersionPath => Path.Combine(_directory, UiTestDatabaseGuard.LatestVersionFileName);

        /// <summary>テストのアプリにバックアップを作らせるフォルダー（Issue #2423）。既定のバックアップ先（<c>backup</c>）とは別の名前にする。</summary>
        private string IsolatedBackupDirectory => Path.Combine(_directory, "uitest-backup-folder");

        /// <summary>このフォルダーを DB の置き場所とみなしたときの、アプリの既定のバックアップ先。</summary>
        private string DefaultBackupDirectory => Path.Combine(_directory, UiTestDatabaseGuard.DefaultBackupFolderName);

        /// <summary>開発機に残っていた更新通知ファイルの内容（Issue #2394 の実例）。</summary>
        private const string OriginalLatestVersion = "99.0.0";

        /// <summary>起動時タスク（Issue #2149）がテスト中に書く自バージョン。</summary>
        private const string AppWrittenLatestVersion = "2.20.0";

        #region LaunchWithSeed の手順

        [Fact]
        public void LaunchWithSeed_既存DBがあるとき_終了後に元のDBへ戻ること()
        {
            File.WriteAllText(DbPath, OriginalContent);

            using (RunSeededLaunch())
            {
                File.ReadAllText(DbPath).Should().Be(SeededContent, "起動中のアプリは投入済み DB を使う");
            }

            File.ReadAllText(DbPath).Should().Be(OriginalContent,
                "旧実装は本起動で投入済み DB を退避ファイルへ上書きし、終了時にそれを「復元」していた");
            Directory.GetFiles(_directory).Should().BeEquivalentTo(new[] { DbPath }, "退避ファイル・印を残さない");
        }

        [Fact]
        public void LaunchWithSeed_既存DBがあるとき_投入は元のDBではなくマイグレーション済みDBへ行うこと()
        {
            File.WriteAllText(DbPath, OriginalContent);
            string? contentBeforeSeed = null;

            using (RunSeededLaunch(seedDatabase: path =>
                   {
                       contentBeforeSeed = File.ReadAllText(path);
                       File.WriteAllText(path, SeededContent);
                   }))
            {
            }

            contentBeforeSeed.Should().Be(MigratedContent);
        }

        [Fact]
        public void LaunchWithSeed_既存DBが無いとき_テストが作ったDBを残さないこと()
        {
            using (RunSeededLaunch())
            {
                File.Exists(DbPath).Should().BeTrue();
            }

            Directory.GetFiles(_directory).Should().BeEmpty(
                "撮影用 DB が残ると、次に本体を起動したとき撮影用データが自分の DB として見える");
        }

        [Fact]
        public void LaunchWithSeed_マイグレーション用の起動が失敗したとき_元のDBへ戻して例外を伝えること()
        {
            File.WriteAllText(DbPath, OriginalContent);

            Action act = () => AppFixture.LaunchWithSeedCore<FakeFixture>(
                _directory,
                IsolatedBackupDirectory,
                launchForMigration: () =>
                {
                    File.WriteAllText(DbPath, MigratedContent);
                    throw new TimeoutException("メインウィンドウが表示されませんでした");
                },
                seedDatabase: _ => throw new InvalidOperationException("到達しないはず"),
                launchOwningGuard: guard => new FakeFixture(guard));

            act.Should().Throw<TimeoutException>();
            File.ReadAllText(DbPath).Should().Be(OriginalContent);
            Directory.GetFiles(_directory).Should().BeEquivalentTo(new[] { DbPath });
        }

        [Fact]
        public void LaunchWithSeed_投入が失敗したとき_元のDBへ戻して例外を伝えること()
        {
            File.WriteAllText(DbPath, OriginalContent);

            Action act = () => RunSeededLaunch(seedDatabase: path =>
            {
                File.WriteAllText(path, SeededContent);
                throw new InvalidOperationException("投入に失敗");
            });

            act.Should().Throw<InvalidOperationException>();
            File.ReadAllText(DbPath).Should().Be(OriginalContent);
        }

        [Fact]
        public void LaunchWithSeed_続けて2回実行しても_元のDBへ戻ること()
        {
            File.WriteAllText(DbPath, OriginalContent);

            using (RunSeededLaunch()) { }
            using (RunSeededLaunch()) { }

            File.ReadAllText(DbPath).Should().Be(OriginalContent);
        }

        #endregion

        #region 中断からの回復

        [Fact]
        public void Acquire_前回の実行が復元前に中断していたとき_退避ファイルを上書きせず元のDBへ戻すこと()
        {
            // 前回：退避後、復元する前にテストプロセスが落ちた
            File.WriteAllText(DbPath, OriginalContent);
            UiTestDatabaseGuard.Acquire(_directory);
            File.WriteAllText(DbPath, SeededContent);

            var guard = UiTestDatabaseGuard.Acquire(_directory);
            File.ReadAllText(DbPath).Should().Be(OriginalContent, "新たに退避する前に前回の退避を書き戻す");

            File.WriteAllText(DbPath, SeededContent);
            guard.Restore();

            File.ReadAllText(DbPath).Should().Be(OriginalContent,
                "旧実装は投入済み DB で退避ファイルを上書きしていた");
        }

        [Fact]
        public void Acquire_元のDBが無いまま中断していたとき_テストが作ったDBを消すこと()
        {
            UiTestDatabaseGuard.Acquire(_directory);
            File.WriteAllText(DbPath, SeededContent);

            UiTestDatabaseGuard.Acquire(_directory).Restore();

            Directory.GetFiles(_directory).Should().BeEmpty();
        }

        [Fact]
        public void Acquire_旧実装の中断痕が残っているとき_そちらを元のDBとして戻すこと()
        {
            // 旧実装では .uitest-backup が投入済み DB で上書きされ得たため、.seeded-original を優先する
            File.WriteAllText(DbPath, SeededContent);
            File.WriteAllText(PathOf(UiTestDatabaseGuard.DatabaseFileName + UiTestDatabaseGuard.BackupSuffix), SeededContent);
            File.WriteAllText(PathOf(UiTestDatabaseGuard.LegacySeededOriginalFileName), OriginalContent);

            UiTestDatabaseGuard.Acquire(_directory).Restore();

            File.ReadAllText(DbPath).Should().Be(OriginalContent);
            Directory.GetFiles(_directory).Should().BeEquivalentTo(new[] { DbPath });
        }

        #endregion

        #region 別のプロセスが DB を開いているとき（Issue #2290）

        [Fact]
        public void Acquire_DBを別のプロセスが開いているとき_退避ファイルを作らずに中止すること()
        {
            File.WriteAllText(DbPath, OriginalContent);

            using (OpenLikeSqlite(DbPath))
            {
                Action act = () => UiTestDatabaseGuard.Acquire(_directory, maxAttempts: 1);

                var thrown = act.Should().Throw<InvalidOperationException>().Which;
                thrown.Message.Should().Contain(DbPath, "何が: どのファイルかを示す")
                    .And.Contain("別のプロセスが使用している", "なぜ: 中止した理由を示す")
                    .And.EndWith("終了してから実行してください。", "どうすれば: 行動指示で終わる");
                thrown.InnerException.Should().BeOfType<IOException>("元の例外を失わない");
            }

            Directory.GetFiles(_directory).Should().BeEquivalentTo(new[] { DbPath },
                "退避ファイルを残すと、次回の Acquire が「中断した実行の痕」と見なして書き戻す");
            File.ReadAllText(DbPath).Should().Be(OriginalContent);
        }

        [Fact]
        public void LaunchWithSeed_DBを開いているアプリがあって失敗した後_次の実行がその間の変更を巻き戻さないこと()
        {
            // Issue #2290 の再現手順: インストール版を起動したまま UI テストを流す → 業務を続ける → 終了して UI テストを流す
            const string RecordedWhileTestFailed = "インストール版で記録した貸出・返却";
            File.WriteAllText(DbPath, OriginalContent);

            using (OpenLikeSqlite(DbPath))
            {
                Action act = () => RunSeededLaunch(maxAttempts: 1);
                act.Should().Throw<Exception>("DB を入れ替えられないので起動手順は失敗する");
            }

            File.WriteAllText(DbPath, RecordedWhileTestFailed);

            using (RunSeededLaunch())
            {
            }

            File.ReadAllText(DbPath).Should().Be(RecordedWhileTestFailed,
                "修正前は 1 回目が残した退避ファイル（記録より前の DB）を 2 回目が書き戻し、記録が消えていた");
            Directory.GetFiles(_directory).Should().BeEquivalentTo(new[] { DbPath });
        }

        [Fact]
        public void Acquire_前回の中断痕があってもDBが開かれていれば_中断痕を残したまま中止し閉じた後に回復すること()
        {
            // 前回：退避後、復元する前にテストプロセスが落ちた（DB はテストの DB に置き換わっている）
            File.WriteAllText(DbPath, OriginalContent);
            UiTestDatabaseGuard.Acquire(_directory);
            File.WriteAllText(DbPath, SeededContent);

            using (OpenLikeSqlite(DbPath))
            {
                Action act = () => UiTestDatabaseGuard.Acquire(_directory, maxAttempts: 1);
                act.Should().Throw<InvalidOperationException>();
            }

            File.Exists(PathOf(UiTestDatabaseGuard.DatabaseFileName + UiTestDatabaseGuard.BackupSuffix)).Should().BeTrue(
                "本当に中断した実行の退避ファイルは、開かれていたからといって捨てない（元の DB はそこにしか無い）");

            UiTestDatabaseGuard.Acquire(_directory).Restore();

            File.ReadAllText(DbPath).Should().Be(OriginalContent, "閉じた後の実行が従来どおり回復する");
        }

        [Fact]
        public void DeleteWorkingDatabase_退避の後にDBが開かれたとき_退避ファイルを捨てて元のDBに触れないこと()
        {
            // Acquire の確認と削除の間に DB が開かれる競合
            const string ChangedAfterFailure = "失敗の後にピッすいで加えた変更";
            File.WriteAllText(DbPath, OriginalContent);
            var guard = UiTestDatabaseGuard.Acquire(_directory, maxAttempts: 1);

            using (OpenLikeSqlite(DbPath))
            {
                Action act = () => guard.DeleteWorkingDatabase();
                act.Should().Throw<IOException>();
            }

            Directory.GetFiles(_directory).Should().BeEquivalentTo(new[] { DbPath },
                "DB はテストの DB に置き換わっていないので、退避ファイルは要らず、残すと害になる");
            File.WriteAllText(DbPath, ChangedAfterFailure);
            guard.Restore();
            File.ReadAllText(DbPath).Should().Be(ChangedAfterFailure, "退避を捨てたガードの Restore は何もしない");

            UiTestDatabaseGuard.Acquire(_directory).Restore();
            File.ReadAllText(DbPath).Should().Be(ChangedAfterFailure, "次回の Acquire も巻き戻さない");
        }

        [Fact]
        public void DeleteWorkingDatabase_DB本体を消した後に付随ファイルで失敗したとき_退避ファイルを残し次回に元へ戻すこと()
        {
            // 対: DB がもう元のままではない失敗では、従来どおり退避ファイルで回復する（捨てると元の DB を失う）
            var journal = PathOf(UiTestDatabaseGuard.DatabaseFileName + "-journal");
            File.WriteAllText(DbPath, OriginalContent);
            File.WriteAllText(journal, "元のジャーナル");
            var guard = UiTestDatabaseGuard.Acquire(_directory, maxAttempts: 1);

            using (OpenLikeSqlite(journal))
            {
                Action delete = () => guard.DeleteWorkingDatabase();
                delete.Should().Throw<IOException>();
                File.Exists(DbPath).Should().BeFalse("前提: DB 本体は消えた");

                Action restore = () => guard.Restore();
                restore.Should().Throw<IOException>("前提: ジャーナルが開かれている間は書き戻せない");
            }

            File.Exists(PathOf(UiTestDatabaseGuard.DatabaseFileName + UiTestDatabaseGuard.BackupSuffix)).Should().BeTrue(
                "書き戻せなかった退避ファイルは残す");

            UiTestDatabaseGuard.Acquire(_directory).Restore();

            File.ReadAllText(DbPath).Should().Be(OriginalContent);
            File.ReadAllText(journal).Should().Be("元のジャーナル");
        }

        [Fact]
        public void Acquire_DBが読み取り専用のとき_ピッすいの終了ではなく属性と権限の確認を案内して中止すること()
        {
            File.WriteAllText(DbPath, OriginalContent);
            File.SetAttributes(DbPath, FileAttributes.ReadOnly);
            try
            {
                Action act = () => UiTestDatabaseGuard.Acquire(_directory, maxAttempts: 1);

                var thrown = act.Should().Throw<InvalidOperationException>().Which;
                thrown.Message.Should().Contain(DbPath)
                    .And.Contain("読み取り専用")
                    .And.NotContain("終了してから", "ピッすいを終了しても解決しない")
                    .And.EndWith("確認してから実行してください。");
                Directory.GetFiles(_directory).Should().BeEquivalentTo(new[] { DbPath }, "退避ファイル・印を作らない");
            }
            finally
            {
                // 退行して退避が作られると、退避ファイルも読み取り専用の属性ごとコピーされる。後始末の削除が黙って失敗しないよう、すべて戻す
                foreach (var file in Directory.GetFiles(_directory))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
            }
        }

        [Fact]
        public void DeleteWorkingDatabase_退避ファイルも捨て切れなかったとき_両方の原因と残ったファイルを伝えること()
        {
            // 確認と削除の間に DB が開かれ、退避ファイルまで開かれている（印が残る、データを失い得る最後の経路）
            var dbBackup = PathOf(UiTestDatabaseGuard.DatabaseFileName + UiTestDatabaseGuard.BackupSuffix);
            File.WriteAllText(DbPath, OriginalContent);
            var guard = UiTestDatabaseGuard.Acquire(_directory, maxAttempts: 1);

            using (OpenLikeSqlite(DbPath))
            using (OpenLikeSqlite(dbBackup))
            {
                Action act = () => guard.DeleteWorkingDatabase();

                var thrown = act.Should().Throw<AggregateException>().Which;
                thrown.InnerExceptions.Should().HaveCount(2, "DB を消せなかった本来の原因と、退避を捨て切れなかった原因の両方を伝える");
                thrown.Message.Should().Contain(dbBackup, "何が: 残ったファイルを名指しする（net48 の Message は内側の例外を含まない）")
                    .And.Contain("巻き戻ります", "なぜ: 残したまま実行したときの結果を示す")
                    .And.EndWith("削除してから実行してください。", "どうすれば: 行動指示で終わる");
            }

            File.ReadAllText(DbPath).Should().Be(OriginalContent, "DB には触れていない");
        }

        [Fact]
        public void Acquire_印の無い付随ファイルの退避が残っているとき_それを書き戻さないこと()
        {
            // 退避・退避の破棄が途中で落ちると、DB 本体の退避ファイル（回復の印）を伴わない付随ファイルの退避が残り得る
            var wal = PathOf(UiTestDatabaseGuard.DatabaseFileName + "-wal");
            File.WriteAllText(DbPath, OriginalContent);
            File.WriteAllText(wal + UiTestDatabaseGuard.BackupSuffix, "どの実行のものか分からない古い WAL");

            var guard = UiTestDatabaseGuard.Acquire(_directory);
            File.WriteAllText(DbPath, SeededContent);
            guard.Restore();

            File.ReadAllText(DbPath).Should().Be(OriginalContent);
            File.Exists(wal).Should().BeFalse("今回の元の DB に無かった付随ファイルを書き戻すと、元の DB がそれでロールバックされる");
            Directory.GetFiles(_directory).Should().BeEquivalentTo(new[] { DbPath });
        }

        #endregion

        #region 更新案内ファイル（latest_version.txt、Issue #2394）

        [Fact]
        public void LaunchWithSeed_更新案内ファイルがあるとき_起動中は無く終了後に元の内容へ戻ること()
        {
            // Issue #2394: 開発機に残った latest_version.txt（99.0.0）が、撮影画像に「新しいバージョン 99.0.0 が公開されています」を写していた
            File.WriteAllText(DbPath, OriginalContent);
            File.WriteAllText(LatestVersionPath, OriginalLatestVersion);
            bool? existsWhileRunning = null;

            using (RunSeededLaunch(onLaunched: () =>
                   {
                       existsWhileRunning = File.Exists(LatestVersionPath);
                       // 起動時タスク（Issue #2149）は、ファイルが無ければ自バージョンを書く
                       File.WriteAllText(LatestVersionPath, AppWrittenLatestVersion);
                   }))
            {
            }

            existsWhileRunning.Should().BeFalse("撮影中のアプリに開発機の更新案内を読ませない");
            File.ReadAllText(LatestVersionPath).Should().Be(OriginalLatestVersion,
                "テスト中にアプリが書いた自バージョンを残さず、元の内容へ戻す");
            Directory.GetFiles(_directory).Should().BeEquivalentTo(new[] { DbPath, LatestVersionPath }, "退避ファイル・印を残さない");
        }

        [Fact]
        public void LaunchWithSeed_更新案内ファイルが無いとき_テストのアプリが書いたファイルを残さないこと()
        {
            File.WriteAllText(DbPath, OriginalContent);

            using (RunSeededLaunch(onLaunched: () => File.WriteAllText(LatestVersionPath, AppWrittenLatestVersion)))
            {
            }

            Directory.GetFiles(_directory).Should().BeEquivalentTo(new[] { DbPath },
                "残すと、開発機に無かった更新通知ファイルが以後の起動の判定に使われる");
        }

        [Fact]
        public void Launch_DBが無くても_更新案内ファイルは退避して元へ戻すこと()
        {
            // AppFixture.Launch の手順（Acquire → 起動 → Restore）。DB の有無と更新案内ファイルの有無は独立
            File.WriteAllText(LatestVersionPath, OriginalLatestVersion);

            var guard = UiTestDatabaseGuard.Acquire(_directory);
            File.Exists(LatestVersionPath).Should().BeFalse();
            File.WriteAllText(DbPath, SeededContent);
            File.WriteAllText(LatestVersionPath, AppWrittenLatestVersion);
            guard.Restore();

            Directory.GetFiles(_directory).Should().BeEquivalentTo(new[] { LatestVersionPath });
            File.ReadAllText(LatestVersionPath).Should().Be(OriginalLatestVersion);
        }

        [Fact]
        public void Acquire_更新案内ファイルを退避したまま中断していたとき_元の内容へ戻すこと()
        {
            File.WriteAllText(DbPath, OriginalContent);
            File.WriteAllText(LatestVersionPath, OriginalLatestVersion);
            UiTestDatabaseGuard.Acquire(_directory);
            File.WriteAllText(LatestVersionPath, AppWrittenLatestVersion);

            var guard = UiTestDatabaseGuard.Acquire(_directory);
            File.Exists(LatestVersionPath).Should().BeFalse("前回の退避を書き戻したうえで、今回も退避する");
            guard.Restore();

            File.ReadAllText(LatestVersionPath).Should().Be(OriginalLatestVersion,
                "退避ファイルをテストのアプリが書いた内容で上書きしない");
            Directory.GetFiles(_directory).Should().BeEquivalentTo(new[] { DbPath, LatestVersionPath });
        }

        [Fact]
        public void Acquire_更新案内ファイルが無いまま中断していたとき_テストのアプリが書いたファイルを消すこと()
        {
            File.WriteAllText(DbPath, OriginalContent);
            UiTestDatabaseGuard.Acquire(_directory);
            File.WriteAllText(LatestVersionPath, AppWrittenLatestVersion);

            UiTestDatabaseGuard.Acquire(_directory).Restore();

            Directory.GetFiles(_directory).Should().BeEquivalentTo(new[] { DbPath });
        }

        [Fact]
        public void DeleteWorkingDatabase_退避の後にDBが開かれたとき_更新案内ファイルも元へ戻すこと()
        {
            // DB の退避を捨てる経路（Issue #2290）でも、退避した更新案内ファイルは捨てずに戻す
            File.WriteAllText(DbPath, OriginalContent);
            File.WriteAllText(LatestVersionPath, OriginalLatestVersion);
            var guard = UiTestDatabaseGuard.Acquire(_directory, maxAttempts: 1);

            using (OpenLikeSqlite(DbPath))
            {
                Action act = () => guard.DeleteWorkingDatabase();
                act.Should().Throw<IOException>();
            }

            File.ReadAllText(LatestVersionPath).Should().Be(OriginalLatestVersion,
                "退避を捨てたガードの Restore は何もしないので、ここで戻さないと開発機の更新通知ファイルが消えたままになる");
            Directory.GetFiles(_directory).Should().BeEquivalentTo(new[] { DbPath, LatestVersionPath });
        }

        [Fact]
        public void Acquire_DBを別のプロセスが開いているとき_更新案内ファイルにも触れないこと()
        {
            File.WriteAllText(DbPath, OriginalContent);
            File.WriteAllText(LatestVersionPath, OriginalLatestVersion);

            using (OpenLikeSqlite(DbPath))
            {
                Action act = () => UiTestDatabaseGuard.Acquire(_directory, maxAttempts: 1);
                act.Should().Throw<InvalidOperationException>();
            }

            Directory.GetFiles(_directory).Should().BeEquivalentTo(new[] { DbPath, LatestVersionPath });
            File.ReadAllText(LatestVersionPath).Should().Be(OriginalLatestVersion);
        }

        [Fact]
        public void Acquire_更新案内ファイルが読み取り専用のとき_DBの退避も残さずに属性の確認を案内して中止すること()
        {
            // 取り除けない更新通知ファイルで Acquire が途中で抜けると、ガードが返らないので Restore が走らない。
            // DB の回復の印が残れば、次回の Acquire がそれを書き戻し、その間の業務の記録を巻き戻す（#2290）
            File.WriteAllText(DbPath, OriginalContent);
            File.WriteAllText(LatestVersionPath, OriginalLatestVersion);
            File.SetAttributes(LatestVersionPath, FileAttributes.ReadOnly);
            try
            {
                Action act = () => UiTestDatabaseGuard.Acquire(_directory, maxAttempts: 1);

                var thrown = act.Should().Throw<InvalidOperationException>().Which;
                thrown.Message.Should().Contain(LatestVersionPath, "何が: どのファイルかを示す")
                    .And.Contain("読み取り専用", "なぜ: 中止した理由を示す")
                    .And.EndWith("確認してから実行してください。", "どうすれば: 行動指示で終わる");
                Directory.GetFiles(_directory).Should().BeEquivalentTo(new[] { DbPath, LatestVersionPath },
                    "DB の退避ファイル・印、更新通知ファイルの退避を残さない");
                File.ReadAllText(LatestVersionPath).Should().Be(OriginalLatestVersion);
            }
            finally
            {
                foreach (var file in Directory.GetFiles(_directory))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
            }
        }

        [Fact]
        public void Acquire_DBの退避に失敗したとき_更新案内ファイルを元へ戻して例外を伝えること()
        {
            // DB 本体の退避先に同名のフォルダーがあると、コピーが失敗する（DB の退避が完了しない）
            File.WriteAllText(DbPath, OriginalContent);
            File.WriteAllText(LatestVersionPath, OriginalLatestVersion);
            Directory.CreateDirectory(PathOf(UiTestDatabaseGuard.DatabaseFileName + UiTestDatabaseGuard.BackupSuffix));

            Action act = () => UiTestDatabaseGuard.Acquire(_directory, maxAttempts: 1);

            act.Should().Throw<Exception>().Which.Should().Match<Exception>(e => e is IOException || e is UnauthorizedAccessException);
            File.ReadAllText(LatestVersionPath).Should().Be(OriginalLatestVersion,
                "ガードが返らないので Restore は走らない。ここで戻さないと開発機の更新通知ファイルが消えたままになる");
            Directory.GetFiles(_directory).Should().BeEquivalentTo(new[] { DbPath, LatestVersionPath });
        }

        [Fact]
        public void Acquire_更新案内ファイルの退避に失敗したとき_DBの退避も印も残さないこと()
        {
            // 順序の固定: 更新通知ファイルは DB より先に退避する。後にすると、ここで抜けたときに DB の回復の印だけが残り、
            // ガードが返らないので Restore も退避の破棄も走らない。次回の Acquire がそれを書き戻し、その間の業務の記録を巻き戻す（#2290）。
            // 退避先に同名のフォルダーがあると、取り除けるかの確認は通るがコピーが失敗する
            var latestBackupFolder = PathOf(UiTestDatabaseGuard.LatestVersionFileName + UiTestDatabaseGuard.BackupSuffix);
            File.WriteAllText(DbPath, OriginalContent);
            File.WriteAllText(LatestVersionPath, OriginalLatestVersion);
            Directory.CreateDirectory(latestBackupFolder);

            Action act = () => UiTestDatabaseGuard.Acquire(_directory, maxAttempts: 1);

            act.Should().Throw<Exception>().Which.Should().Match<Exception>(e => e is IOException || e is UnauthorizedAccessException);
            Directory.GetFiles(_directory).Should().BeEquivalentTo(new[] { DbPath, LatestVersionPath },
                "DB の退避ファイル・印を残さない（残すと次回の Acquire が書き戻す）");
            File.ReadAllText(LatestVersionPath).Should().Be(OriginalLatestVersion);
        }

        [Fact]
        public void DeleteWorkingDatabase_DBの退避ファイルを捨て切れなかったときも_更新案内ファイルは元へ戻すこと()
        {
            // #2290 の最後の経路（DB も退避ファイルも開かれている）。更新通知ファイルの戻しは DB の退避の破棄に従属させない
            var dbBackup = PathOf(UiTestDatabaseGuard.DatabaseFileName + UiTestDatabaseGuard.BackupSuffix);
            File.WriteAllText(DbPath, OriginalContent);
            File.WriteAllText(LatestVersionPath, OriginalLatestVersion);
            var guard = UiTestDatabaseGuard.Acquire(_directory, maxAttempts: 1);

            using (OpenLikeSqlite(DbPath))
            using (OpenLikeSqlite(dbBackup))
            {
                Action act = () => guard.DeleteWorkingDatabase();
                act.Should().Throw<AggregateException>();
            }

            File.ReadAllText(LatestVersionPath).Should().Be(OriginalLatestVersion,
                "退避を捨てたガードの Restore は何もしないので、ここで戻さないと開発機の更新通知ファイルが消えたままになる");
            File.Exists(PathOf(UiTestDatabaseGuard.LatestVersionFileName + UiTestDatabaseGuard.BackupSuffix)).Should().BeFalse();
        }

        [Fact]
        public void DeleteWorkingDatabase_更新案内ファイルだけ戻せなかったとき_DBの巻き戻りではなく次の実行が戻すことを伝えること()
        {
            File.WriteAllText(DbPath, OriginalContent);
            File.WriteAllText(LatestVersionPath, OriginalLatestVersion);
            var guard = UiTestDatabaseGuard.Acquire(_directory, maxAttempts: 1);
            // 書き戻し先に同名のフォルダーがあると、更新通知ファイルを戻せない
            Directory.CreateDirectory(LatestVersionPath);

            using (OpenLikeSqlite(DbPath))
            {
                Action act = () => guard.DeleteWorkingDatabase();

                var thrown = act.Should().Throw<AggregateException>().Which;
                thrown.InnerExceptions.Should().HaveCount(2, "DB を消せなかった本来の原因と、更新通知ファイルを戻せなかった原因の両方を伝える");
                thrown.Message.Should().Contain(PathOf(UiTestDatabaseGuard.LatestVersionFileName + UiTestDatabaseGuard.BackupSuffix), "何が: 残った退避を名指しする")
                    .And.NotContain("巻き戻ります", "DB の退避は捨てられており、業務の記録は巻き戻らない")
                    .And.EndWith("実行してください。", "どうすれば: 行動指示で終わる");
            }

            Directory.GetFiles(_directory).Should().BeEquivalentTo(
                new[] { DbPath, PathOf(UiTestDatabaseGuard.LatestVersionFileName + UiTestDatabaseGuard.BackupSuffix) },
                "DB の退避ファイル・印は捨て、更新通知ファイルの退避は次の実行のために残す");

            Directory.Delete(LatestVersionPath);
            UiTestDatabaseGuard.Acquire(_directory).Restore();
            File.ReadAllText(LatestVersionPath).Should().Be(OriginalLatestVersion, "次の実行が戻す");
            File.ReadAllText(DbPath).Should().Be(OriginalContent);
        }

        [Fact]
        public void DeleteWorkingDatabase_DBの退避も更新案内ファイルの戻しも失敗したとき_更新案内ファイルの退避は消さないよう伝えること()
        {
            // DB の退避ファイルは「削除してから実行」と案内する。同じ文言の中で、更新通知ファイルの退避まで消されないようにする
            // （消すと開発機の更新通知ファイルを失う）
            var dbBackup = PathOf(UiTestDatabaseGuard.DatabaseFileName + UiTestDatabaseGuard.BackupSuffix);
            var latestBackup = PathOf(UiTestDatabaseGuard.LatestVersionFileName + UiTestDatabaseGuard.BackupSuffix);
            File.WriteAllText(DbPath, OriginalContent);
            File.WriteAllText(LatestVersionPath, OriginalLatestVersion);
            var guard = UiTestDatabaseGuard.Acquire(_directory, maxAttempts: 1);
            // 書き戻し先に同名のフォルダーがあると、更新通知ファイルを戻せない
            Directory.CreateDirectory(LatestVersionPath);

            using (OpenLikeSqlite(DbPath))
            using (OpenLikeSqlite(dbBackup))
            {
                Action act = () => guard.DeleteWorkingDatabase();

                var thrown = act.Should().Throw<AggregateException>().Which;
                thrown.InnerExceptions.Should().HaveCount(3, "DB を消せなかった原因・退避を捨て切れなかった原因・更新通知ファイルを戻せなかった原因");
                thrown.Message.Should().Contain(dbBackup)
                    .And.Contain("巻き戻ります", "DB の退避ファイルの危険は従来どおり伝える")
                    .And.Contain(latestBackup, "何が: 更新通知ファイルの退避を名指しする")
                    .And.Contain("削除しないでください", "どうすれば: 更新通知ファイルの退避は消さない")
                    .And.EndWith("削除してから実行してください。");
            }

            File.Exists(latestBackup).Should().BeTrue("更新通知ファイルの退避は次の実行のために残す");
        }

        [Fact]
        public void Restore_DBは戻せて更新案内ファイルだけ戻せなかったとき_例外を伝え退避を残して次回に戻すこと()
        {
            File.WriteAllText(DbPath, OriginalContent);
            File.WriteAllText(LatestVersionPath, OriginalLatestVersion);
            var guard = UiTestDatabaseGuard.Acquire(_directory, maxAttempts: 1);
            File.WriteAllText(DbPath, SeededContent);
            // 書き戻し先に同名のフォルダーがあると、更新通知ファイルを戻せない
            Directory.CreateDirectory(LatestVersionPath);

            Action restore = () => guard.Restore();

            restore.Should().Throw<Exception>("黙って成功にすると、開発機の更新通知ファイルが消えたままになったことに誰も気付かない")
                .Which.Should().Match<Exception>(e => e is IOException || e is UnauthorizedAccessException);
            File.ReadAllText(DbPath).Should().Be(OriginalContent, "DB の書き戻しは更新通知ファイルの失敗に巻き込まれない");
            File.Exists(PathOf(UiTestDatabaseGuard.LatestVersionFileName + UiTestDatabaseGuard.BackupSuffix)).Should().BeTrue(
                "戻せなかった退避は残し、次回の Acquire に任せる");

            Directory.Delete(LatestVersionPath);
            UiTestDatabaseGuard.Acquire(_directory).Restore();
            File.ReadAllText(LatestVersionPath).Should().Be(OriginalLatestVersion);
            File.ReadAllText(DbPath).Should().Be(OriginalContent);
        }

        [Fact]
        public void Acquire_更新案内ファイルの退避の書き込み途中の残りがあるとき_それを退避として使わず捨てること()
        {
            // 前回、退避の一時ファイルへ書き込む途中で落ちた（退避の名前へ改名する前なので、印ではない）。
            // 今回は元の更新通知ファイルが無い（元があれば、今回の退避の一時ファイルとして上書きされて消える）
            var temp = PathOf(UiTestDatabaseGuard.LatestVersionBackupTempFileName);
            File.WriteAllText(DbPath, OriginalContent);
            File.WriteAllText(temp, "99.");

            var guard = UiTestDatabaseGuard.Acquire(_directory);
            File.Exists(temp).Should().BeFalse("書き込み途中の残りを残さない");
            File.WriteAllText(LatestVersionPath, AppWrittenLatestVersion);
            guard.Restore();

            Directory.GetFiles(_directory).Should().BeEquivalentTo(new[] { DbPath },
                "書き込み途中の残りを退避として書き戻さず、テスト中のアプリが書いたファイルも消す");
        }

        [Fact]
        public void Restore_DBの書き戻しに失敗しても_更新案内ファイルは元へ戻すこと()
        {
            var journal = PathOf(UiTestDatabaseGuard.DatabaseFileName + "-journal");
            File.WriteAllText(DbPath, OriginalContent);
            File.WriteAllText(LatestVersionPath, OriginalLatestVersion);
            var guard = UiTestDatabaseGuard.Acquire(_directory, maxAttempts: 1);
            File.WriteAllText(DbPath, SeededContent);
            File.WriteAllText(journal, "テストのジャーナル");
            File.WriteAllText(LatestVersionPath, AppWrittenLatestVersion);

            using (OpenLikeSqlite(journal))
            {
                Action restore = () => guard.Restore();
                restore.Should().Throw<IOException>("前提: ジャーナルが開かれている間は DB を書き戻せない（DB の失敗を優先して伝える）");
            }

            File.ReadAllText(LatestVersionPath).Should().Be(OriginalLatestVersion,
                "更新通知ファイルは DB の書き戻しの成否と独立に戻す");

            UiTestDatabaseGuard.Acquire(_directory).Restore();
            File.ReadAllText(DbPath).Should().Be(OriginalContent, "DB は従来どおり次回の Acquire が回復する");
            File.ReadAllText(LatestVersionPath).Should().Be(OriginalLatestVersion);
        }

        #endregion

        #region 付随ファイル・設定

        [Fact]
        public void Restore_テストが残したジャーナルを消し_元のジャーナルだけを戻すこと()
        {
            var journal = PathOf(UiTestDatabaseGuard.DatabaseFileName + "-journal");
            var wal = PathOf(UiTestDatabaseGuard.DatabaseFileName + "-wal");
            File.WriteAllText(DbPath, OriginalContent);
            File.WriteAllText(journal, "元のジャーナル");

            var guard = UiTestDatabaseGuard.Acquire(_directory);
            File.WriteAllText(DbPath, SeededContent);
            File.WriteAllText(journal, "テスト中に強制終了したアプリのジャーナル");
            File.WriteAllText(wal, "テストの WAL");
            guard.Restore();

            File.ReadAllText(DbPath).Should().Be(OriginalContent);
            File.ReadAllText(journal).Should().Be("元のジャーナル",
                "テストのジャーナルが残ると、SQLite が元の DB をそれでロールバックして壊す");
            File.Exists(wal).Should().BeFalse();
        }

        [Fact]
        public void Restore_2回呼んでも_2回目は何もしないこと()
        {
            File.WriteAllText(DbPath, OriginalContent);
            var guard = UiTestDatabaseGuard.Acquire(_directory);
            guard.Restore();

            File.WriteAllText(DbPath, "復元後に本体を起動して加えた変更");
            guard.Restore();

            File.ReadAllText(DbPath).Should().Be("復元後に本体を起動して加えた変更");
        }

        [Fact]
        public void Acquire_database_configが保存先を指定しているとき_何も触らずに中止すること()
        {
            File.WriteAllText(DbPath, OriginalContent);
            File.WriteAllText(PathOf(UiTestDatabaseGuard.DatabaseConfigFileName), @"\\server\share\iccard.db");

            Action act = () => UiTestDatabaseGuard.Acquire(_directory);

            act.Should().Throw<InvalidOperationException>().WithMessage(@"*\\server\share\iccard.db*");
            Directory.GetFiles(_directory).Should().HaveCount(2, "退避ファイル・印を作らない");
        }

        [Theory]
        [InlineData("")]
        [InlineData("  \r\n")]
        public void Acquire_database_configが空のとき_既定の保存先として退避すること(string content)
        {
            // 本体は空欄を「既定の保存先」と解釈する（Issue #1559）
            File.WriteAllText(DbPath, OriginalContent);
            File.WriteAllText(PathOf(UiTestDatabaseGuard.DatabaseConfigFileName), content);

            var guard = UiTestDatabaseGuard.Acquire(_directory);
            File.WriteAllText(DbPath, SeededContent);
            guard.Restore();

            File.ReadAllText(DbPath).Should().Be(OriginalContent);
        }

        #endregion

        #region バックアップ先の隔離（Issue #2423）

        // テストのアプリは起動時に自動バックアップを作り、保存先の古い世代を「各日の最新 1 世代」へ間引く。保存先が既定
        // （%ProgramData%\ICCardManager\backup）のままだと、同じ日にインストール版が作った実 DB のバックアップをテスト用 DB の
        // バックアップが押し出す。起動の手順はすべて、アプリを起動する前に DB の backup_path をテスト専用のフォルダーへ向ける

        [Fact]
        public void LaunchWithSeed_マイグレーションの起動も本起動も_既定ではなくUIテスト専用のバックアップ先を使うこと()
        {
            File.WriteAllText(DbPath, OriginalContent);
            string? atMigration = null;
            string? atLaunch = null;

            using (AppFixture.LaunchWithSeedCore(
                       _directory,
                       IsolatedBackupDirectory,
                       launchForMigration: () =>
                       {
                           // 1 回目の起動も自動バックアップを作る。投入で書くのでは間に合わない
                           atMigration = ReadSetting(DbPath, UiTestDatabaseGuard.BackupPathSettingKey);
                           MigrateLikeApp(DbPath);
                           return new FakeFixture(ownedGuard: null);
                       },
                       seedDatabase: SeedStaff,
                       launchOwningGuard: guard =>
                       {
                           atLaunch = ReadSetting(DbPath, UiTestDatabaseGuard.BackupPathSettingKey);
                           return new FakeFixture(guard);
                       }))
            {
            }

            atMigration.Should().Be(IsolatedBackupDirectory);
            atLaunch.Should().Be(IsolatedBackupDirectory, "マイグレーションと投入で書いた行が消えないこと");
            atLaunch.Should().NotBe(DefaultBackupDirectory);
            Directory.Exists(IsolatedBackupDirectory).Should().BeTrue("保存先が無いとアプリの検証で既定の保存先へ退避され得る");
            File.ReadAllText(DbPath).Should().Be(OriginalContent, "開発機の DB は書き換えずに戻る");
        }

        [Fact]
        public void Launch_開発機のDBがあるとき_起動中はUIテスト専用のバックアップ先を指し_終了後は元の設定へ戻ること()
        {
            // AppFixture.Launch は投入せず開発機の DB をそのまま開くので、その DB の設定を書き換え、終了後に戻す
            const string configuredByStaff = @"D:\職員が設定したバックアップ先";
            CreateMigratedDatabase(DbPath, backupPath: configuredByStaff);
            string? atLaunch = null;

            using (AppFixture.LaunchCore(
                       _directory,
                       IsolatedBackupDirectory,
                       launchOwningGuard: guard =>
                       {
                           atLaunch = ReadSetting(DbPath, UiTestDatabaseGuard.BackupPathSettingKey);
                           TableNames(DbPath).Should().Contain("staff", "開発機の DB の他のテーブルはそのまま");
                           return new FakeFixture(guard);
                       }))
            {
            }

            atLaunch.Should().Be(IsolatedBackupDirectory);
            ReadSetting(DbPath, UiTestDatabaseGuard.BackupPathSettingKey).Should().Be(configuredByStaff,
                "職員が設定したバックアップ先を UI テストの値のまま残さない");
            Directory.GetFiles(_directory).Should().BeEquivalentTo(new[] { DbPath }, "退避ファイル・印を残さない");
        }

        [Fact]
        public void Launch_開発機のDBが無いとき_バックアップ先だけを書いた新規のDBで起動し_終了後に残さないこと()
        {
            string? atLaunch = null;
            IReadOnlyCollection<string>? tablesAtLaunch = null;

            using (AppFixture.LaunchCore(
                       _directory,
                       IsolatedBackupDirectory,
                       launchOwningGuard: guard =>
                       {
                           atLaunch = ReadSetting(DbPath, UiTestDatabaseGuard.BackupPathSettingKey);
                           tablesAtLaunch = TableNames(DbPath);
                           return new FakeFixture(guard);
                       }))
            {
            }

            atLaunch.Should().Be(IsolatedBackupDirectory);
            // staff テーブルが無いので、本体（DbContext.HandleLegacyDatabase）は新規 DB とみなして初期マイグレーションから適用する
            tablesAtLaunch.Should().BeEquivalentTo(new[] { "settings" });
            Directory.GetFiles(_directory).Should().BeEmpty("テストが作った DB を開発機の DB として残さない");
        }

        [Fact]
        public void Launch_起動が失敗したとき_元の設定へ戻して例外を伝えること()
        {
            const string configuredByStaff = @"D:\職員が設定したバックアップ先";
            CreateMigratedDatabase(DbPath, backupPath: configuredByStaff);

            Action act = () => AppFixture.LaunchCore<FakeFixture>(
                _directory,
                IsolatedBackupDirectory,
                launchOwningGuard: _ => throw new InvalidOperationException("起動の失敗"));

            act.Should().Throw<InvalidOperationException>().WithMessage("起動の失敗");
            ReadSetting(DbPath, UiTestDatabaseGuard.BackupPathSettingKey).Should().Be(configuredByStaff);
        }

        [Fact]
        public void IsolateBackupFolder_既定のバックアップ先かその配下を指定したとき_DBに書かずに拒むこと()
        {
            // 既定の保存先とその配下は、本体の整理が実 DB のバックアップと同じ扱いで間引く
            var guard = UiTestDatabaseGuard.Acquire(_directory);
            try
            {
                foreach (var target in new[] { DefaultBackupDirectory, DefaultBackupDirectory + Path.DirectorySeparatorChar, Path.Combine(DefaultBackupDirectory, "uitest") })
                {
                    Action act = () => guard.IsolateBackupFolder(target);
                    act.Should().Throw<ArgumentException>().WithMessage("*既定のバックアップ先*");
                }

                File.Exists(DbPath).Should().BeFalse("拒んだときは DB を作らない");
                Directory.Exists(DefaultBackupDirectory).Should().BeFalse();
            }
            finally
            {
                guard.Restore();
            }
        }

        [Fact]
        public void IsolateBackupFolder_既定のバックアップ先と名前が前方一致するだけのフォルダーは_受け付けること()
        {
            // 対: 「配下か」の判定を前方一致で書くと backup2 のような別のフォルダーまで拒む
            var sibling = DefaultBackupDirectory + "2";
            var guard = UiTestDatabaseGuard.Acquire(_directory);
            try
            {
                guard.IsolateBackupFolder(sibling);
                ReadSetting(DbPath, UiTestDatabaseGuard.BackupPathSettingKey).Should().Be(sibling);
            }
            finally
            {
                guard.Restore();
            }
        }

        [Fact]
        public void UIテスト専用のバックアップ先は_実際の起動で使うDBの既定のバックアップ先の外にあること()
        {
            // AppFixture.Launch / LaunchWithSeed が渡す値。既定の保存先かその配下なら、隔離しても本体の整理が実 DB のバックアップを間引く
            var appDataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ICCardManager");
            var productionDefault = Path.Combine(appDataRoot, UiTestDatabaseGuard.DefaultBackupFolderName);
            var uiTestBackup = Path.GetFullPath(UiTestDatabaseGuard.UiTestBackupDirectory).TrimEnd(Path.DirectorySeparatorChar);

            AppFixture.DatabasePath.Should().Be(Path.Combine(appDataRoot, UiTestDatabaseGuard.DatabaseFileName),
                "既定のバックアップ先は DB と同じフォルダーの配下");
            uiTestBackup.Should().NotBeEquivalentTo(productionDefault);
            uiTestBackup.StartsWith(productionDefault + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                .Should().BeFalse();
            uiTestBackup.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase)
                .Should().BeFalse("保存先のパスはシステム管理画面に表示され、マニュアルの撮影に写る（利用者のフォルダーだと利用者名が写る）");
        }

        [Fact]
        public void Launch_起動中に中断したとき_次のAcquireの回復で職員が設定したバックアップ先へ戻ること()
        {
            // テストのプロセスが kill されると Dispose（Restore）が走らず、開発機の DB に UI テスト用の backup_path が残る。
            // 残ったまま本物のピッすいを起動すると、その自動バックアップは UI テスト用のフォルダーへ作られ続ける
            const string configuredByStaff = @"D:\職員が設定したバックアップ先";
            CreateMigratedDatabase(DbPath, backupPath: configuredByStaff);

            AppFixture.LaunchCore(
                _directory,
                IsolatedBackupDirectory,
                launchOwningGuard: guard => new FakeFixture(guard)); // Dispose しない（中断）
            ReadSetting(DbPath, UiTestDatabaseGuard.BackupPathSettingKey).Should().Be(IsolatedBackupDirectory, "中断した直後は残っている");

            UiTestDatabaseGuard.Acquire(_directory).Restore();

            ReadSetting(DbPath, UiTestDatabaseGuard.BackupPathSettingKey).Should().Be(configuredByStaff);
        }

        [Fact]
        public void ThrowIfDefaultBackupFolderChanged_既定のバックアップ先にファイルが増えたとき_名指しして例外を投げること()
        {
            // 本体は設定先がパスの検証に通らないと、警告のログだけで既定の保存先へ戻る。そのときの結果をここで捕まえる
            Directory.CreateDirectory(DefaultBackupDirectory);
            File.WriteAllText(Path.Combine(DefaultBackupDirectory, "backup_20261109_041043.db"), OriginalContent);
            var guard = UiTestDatabaseGuard.Acquire(_directory);
            File.WriteAllText(Path.Combine(DefaultBackupDirectory, "backup_20261109_060348.db"), SeededContent);
            guard.Restore();

            Action act = () => guard.ThrowIfDefaultBackupFolderChanged();

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*増えた: backup_20261109_060348.db*消えた: なし*");
        }

        [Fact]
        public void ThrowIfDefaultBackupFolderChanged_既存の世代が消えたとき_名指しして例外を投げること()
        {
            // 整理（各日の最新 1 世代）が消すのは既にあった世代で、守りたいのはそちら
            Directory.CreateDirectory(DefaultBackupDirectory);
            var realBackup = Path.Combine(DefaultBackupDirectory, "backup_20261109_041043.db");
            File.WriteAllText(realBackup, OriginalContent);
            var guard = UiTestDatabaseGuard.Acquire(_directory);
            File.Delete(realBackup);
            guard.Restore();

            Action act = () => guard.ThrowIfDefaultBackupFolderChanged();

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*消えた: backup_20261109_041043.db*");
        }

        [Fact]
        public void FinishOwnedGuard_既定のバックアップ先が変わっていたとき_DBを復元したうえで例外を投げること()
        {
            // AppFixture.Dispose の後始末。復元を先に済ませる（確認の失敗で開発機の DB をテストの DB のまま残さない）
            File.WriteAllText(DbPath, OriginalContent);
            var guard = UiTestDatabaseGuard.Acquire(_directory);
            File.WriteAllText(DbPath, SeededContent);
            Directory.CreateDirectory(DefaultBackupDirectory);
            File.WriteAllText(Path.Combine(DefaultBackupDirectory, "backup_20261109_060348.db"), SeededContent);

            Action act = () => AppFixture.FinishOwnedGuard(guard);

            act.Should().Throw<InvalidOperationException>().WithMessage("*増えた: backup_20261109_060348.db*");
            File.ReadAllText(DbPath).Should().Be(OriginalContent);
        }

        [Fact]
        public void FinishOwnedGuard_DBの復元に失敗しても_既定のバックアップ先の確認を走らせ両方の失敗を伝えること()
        {
            // 次回の Acquire はその時点の一覧を記録し直すので、復元の失敗で確認を飛ばすと、この回の書き込みは二度と検出できない
            File.WriteAllText(DbPath, OriginalContent);
            var guard = UiTestDatabaseGuard.Acquire(_directory, maxAttempts: 1);
            File.WriteAllText(DbPath, SeededContent);
            Directory.CreateDirectory(DefaultBackupDirectory);
            File.WriteAllText(Path.Combine(DefaultBackupDirectory, "backup_20261109_060348.db"), SeededContent);

            using (OpenLikeSqlite(DbPath))
            {
                Action act = () => AppFixture.FinishOwnedGuard(guard);

                var thrown = act.Should().Throw<AggregateException>().Which;
                thrown.InnerExceptions.Should().HaveCount(2);
                thrown.InnerExceptions[0].Should().BeOfType<IOException>("先に DB の復元の失敗");
                thrown.InnerExceptions[1].Should().BeOfType<InvalidOperationException>();
                thrown.Message.Should().Contain("backup_20261109_060348.db", "net48 の AggregateException.Message は内側の文言を含まないので自分で並べる");
            }
        }

        [Fact]
        public void FinishOwnedGuard_DBの復元だけが失敗したとき_その例外をそのまま伝えること()
        {
            // 対: 既定のバックアップ先が変わっていなければ、確認は失敗を足さない
            File.WriteAllText(DbPath, OriginalContent);
            var guard = UiTestDatabaseGuard.Acquire(_directory, maxAttempts: 1);
            File.WriteAllText(DbPath, SeededContent);

            using (OpenLikeSqlite(DbPath))
            {
                Action act = () => AppFixture.FinishOwnedGuard(guard);

                act.Should().Throw<IOException>();
            }
        }

        [Fact]
        public void FinishOwnedGuard_既定のバックアップ先が変わっていないとき_DBを復元するだけであること()
        {
            File.WriteAllText(DbPath, OriginalContent);
            var guard = UiTestDatabaseGuard.Acquire(_directory);
            File.WriteAllText(DbPath, SeededContent);

            AppFixture.FinishOwnedGuard(guard);
            AppFixture.FinishOwnedGuard(null); // 入れ子の起動はガードを持たない

            File.ReadAllText(DbPath).Should().Be(OriginalContent);
        }

        [Fact]
        public void ThrowIfDefaultBackupFolderChanged_既定のバックアップ先が変わっていないとき_何もしないこと()
        {
            // 対: 既にある世代（実 DB のバックアップ）や、フォルダーが無いことを「変わった」と誤らない
            Directory.CreateDirectory(DefaultBackupDirectory);
            File.WriteAllText(Path.Combine(DefaultBackupDirectory, "backup_20261109_041043.db"), OriginalContent);
            var guard = UiTestDatabaseGuard.Acquire(_directory);
            // テストのアプリが書くべき場所（UI テスト専用のフォルダー）に増えるのは正常
            Directory.CreateDirectory(IsolatedBackupDirectory);
            File.WriteAllText(Path.Combine(IsolatedBackupDirectory, "backup_20261109_060348.db"), SeededContent);
            guard.Restore();

            Action act = () => guard.ThrowIfDefaultBackupFolderChanged();

            act.Should().NotThrow();
        }

        #endregion

        /// <summary>
        /// アプリの代わりに DB ファイルを書き換える <see cref="AppFixture.LaunchWithSeedCore"/> を実行する。
        /// </summary>
        /// <param name="seedDatabase">投入。null なら <see cref="SeededContent"/> を書く。</param>
        /// <param name="maxAttempts">ファイルが使用中のときの試行回数。</param>
        /// <param name="onLaunched">本起動の直後（アプリが起動している間）に行うこと。</param>
        private FakeFixture RunSeededLaunch(
            Action<string>? seedDatabase = null,
            int maxAttempts = UiTestDatabaseGuard.DefaultMaxAttempts,
            Action? onLaunched = null)
        {
            return AppFixture.LaunchWithSeedCore(
                _directory,
                IsolatedBackupDirectory,
                launchForMigration: () =>
                {
                    // Issue #2423: マイグレーションは空の状態から走らせる。DB にあるのはバックアップ先の設定だけ
                    TableNames(DbPath).Should().BeEquivalentTo(new[] { "settings" }, "マイグレーションは空の状態から走らせる");
                    File.WriteAllText(DbPath, MigratedContent);
                    return new FakeFixture(ownedGuard: null);
                },
                seedDatabase: seedDatabase ?? (path => File.WriteAllText(path, SeededContent)),
                launchOwningGuard: guard =>
                {
                    onLaunched?.Invoke();
                    return new FakeFixture(guard);
                },
                maxAttempts: maxAttempts);
        }

        /// <summary>本体の初期マイグレーションのうち、この検証に関わる部分（settings の作成と既定値、staff の作成）を再現する。</summary>
        private static void MigrateLikeApp(string dbPath)
        {
            using var conn = Open(dbPath);
            Execute(conn, "CREATE TABLE IF NOT EXISTS staff (staff_idm TEXT PRIMARY KEY, name TEXT NOT NULL, is_deleted INTEGER DEFAULT 0)");
            Execute(conn, "CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT)");
            Execute(conn, "INSERT OR IGNORE INTO settings (key, value) VALUES ('warning_balance', '10000')");
            Execute(conn, "INSERT OR IGNORE INTO settings (key, value) VALUES ('font_size', 'medium')");
        }

        private static void SeedStaff(string dbPath)
        {
            using var conn = Open(dbPath);
            Execute(conn, "INSERT INTO staff (staff_idm, name) VALUES ('FFFF000000000001', 'テスト職員')");
        }

        /// <summary>マイグレーション済みの開発機の DB（<paramref name="backupPath"/> をバックアップ先に設定済み）を作る。</summary>
        private static void CreateMigratedDatabase(string dbPath, string backupPath)
        {
            MigrateLikeApp(dbPath);
            using var conn = Open(dbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES (@key, @value)";
            cmd.Parameters.AddWithValue("@key", UiTestDatabaseGuard.BackupPathSettingKey);
            cmd.Parameters.AddWithValue("@value", backupPath);
            cmd.ExecuteNonQuery();
        }

        private static string? ReadSetting(string dbPath, string key)
        {
            using var conn = Open(dbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM settings WHERE key = @key";
            cmd.Parameters.AddWithValue("@key", key);
            return cmd.ExecuteScalar() as string;
        }

        private static IReadOnlyCollection<string> TableNames(string dbPath)
        {
            using var conn = Open(dbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
            using var reader = cmd.ExecuteReader();
            var names = new List<string>();
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }
            return names;
        }

        private static SQLiteConnection Open(string dbPath)
        {
            var conn = new SQLiteConnection($"Data Source={dbPath};Version=3");
            conn.Open();
            return conn;
        }

        private static void Execute(SQLiteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// 起動中のピッすいと同じ形でファイルを開く。SQLite（Windows）は読み書きを共有し削除を共有しないので、
        /// 他のプロセスはこのファイルを読めるが、消せず、排他でも開けない。
        /// </summary>
        private static FileStream OpenLikeSqlite(string path) =>
            new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        /// <summary>本物の <see cref="AppFixture"/> と同じく、Dispose で所有するガードを復元する。</summary>
        private sealed class FakeFixture : IDisposable
        {
            private readonly UiTestDatabaseGuard? _ownedGuard;

            public FakeFixture(UiTestDatabaseGuard? ownedGuard)
            {
                _ownedGuard = ownedGuard;
            }

            public void Dispose() => _ownedGuard?.Restore();
        }
    }
}
