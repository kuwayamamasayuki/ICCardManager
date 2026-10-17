using System;
using System.IO;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// UI テストが開発機の既存 DB を壊さないことの検証（Issue #2062 / #2290）。
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

        /// <summary>
        /// アプリの代わりに DB ファイルを書き換える <see cref="AppFixture.LaunchWithSeedCore"/> を実行する。
        /// </summary>
        private FakeFixture RunSeededLaunch(Action<string>? seedDatabase = null, int maxAttempts = UiTestDatabaseGuard.DefaultMaxAttempts)
        {
            return AppFixture.LaunchWithSeedCore(
                _directory,
                launchForMigration: () =>
                {
                    File.Exists(DbPath).Should().BeFalse("マイグレーションは空の状態から走らせる");
                    File.WriteAllText(DbPath, MigratedContent);
                    return new FakeFixture(ownedGuard: null);
                },
                seedDatabase: seedDatabase ?? (path => File.WriteAllText(path, SeededContent)),
                launchOwningGuard: guard => new FakeFixture(guard),
                maxAttempts: maxAttempts);
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
