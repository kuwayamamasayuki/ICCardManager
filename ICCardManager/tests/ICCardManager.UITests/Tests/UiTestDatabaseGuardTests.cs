using System;
using System.IO;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// UI テストが開発機の既存 DB を壊さないことの検証（Issue #2062）。
    /// アプリは起動せず、一時フォルダー上でファイルの退避・復元だけを検証する。
    /// GUI 不要のため Category=UI を付けず、CI でも実行される。
    /// UITests はソリューション単位の実行から外れているため、CI は ci.yml の
    /// 「Run GUI-free tests in UITests project」ステップで csproj を直接指定して実行する（Issue #2099）。
    /// </summary>
    /// <remarks>
    /// DB の中身は SQLite である必要がないため、ファイルごとに異なる文字列で「どの DB か」を識別する。
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
        private FakeFixture RunSeededLaunch(Action<string>? seedDatabase = null)
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
                launchOwningGuard: guard => new FakeFixture(guard));
        }

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
