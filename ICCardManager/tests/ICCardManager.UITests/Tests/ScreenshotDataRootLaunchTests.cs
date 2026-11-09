using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// 撮影用の置き場所でアプリを起動する手順（<see cref="AppFixture.LaunchWithSeedInDataRootCore"/>。Issue #2435）の検証。
    /// アプリは起動せず、一時フォルダー上で「アプリの代わりに DB を作る」起動を差し替えて検証する。
    /// GUI 不要のため Category=UI を付けず、CI でも実行される（<see cref="UiTestDatabaseGuardTests"/> と同じ）。
    /// </summary>
    /// <remarks>
    /// 撮影用の置き場所は UI テストの持ち物なので起動のたびに空にする。開発機の置き場所（DB・バックアップ先）は、
    /// 置き場所の差し替えが効かなかったときの安全網として、従来どおりガードで退避し、バックアップ先を UI テスト専用のフォルダーへ隔離する。
    /// </remarks>
    public class ScreenshotDataRootLaunchTests : IDisposable
    {
        private const string RealStaffName = "開発機の職員（実データ）";
        private const string SeededStaffName = "撮影用の職員";

        private readonly string _root;

        public ScreenshotDataRootLaunchTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ICCardManager.UITests.DataRoot." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DbDirectory);
        }

        public void Dispose()
        {
            SQLiteConnection.ClearAllPools();
            try { Directory.Delete(_root, recursive: true); } catch { /* 後始末の失敗は検証対象外 */ }
        }

        /// <summary>開発機の置き場所（<c>%ProgramData%\ICCardManager</c> の代わり）。</summary>
        private string DbDirectory => Path.Combine(_root, "ICCardManager");

        private string RealDbPath => Path.Combine(DbDirectory, UiTestDatabaseGuard.DatabaseFileName);

        /// <summary>UI テスト専用のバックアップ先（<c>%ProgramData%\ICCardManager.UITests\backup</c> の代わり）。</summary>
        private string IsolatedBackupDirectory => Path.Combine(_root, "ICCardManager.UITests", "backup");

        /// <summary>撮影用の置き場所（<c>%ProgramData%\ICCardManager.UITests\screenshot-data</c> の代わり）。</summary>
        private string DataRoot => Path.Combine(_root, "ICCardManager.UITests", "screenshot-data");

        private string DataRootDbPath => Path.Combine(DataRoot, UiTestDatabaseGuard.DatabaseFileName);

        [Fact]
        public void 差し替えが効いたとき_投入は撮影用の置き場所のDBへ行い_開発機のDBは終了後に元へ戻ること()
        {
            CreateDatabase(RealDbPath, RealStaffName);
            var realBytes = File.ReadAllBytes(RealDbPath);
            string? seededPath = null;

            using (Run(migrateDataRoot: true, seedDatabase: path =>
            {
                seededPath = path;
                InsertStaff(path, SeededStaffName);
            }))
            {
                StaffNames(DataRootDbPath).Should().Equal(SeededStaffName);
            }

            seededPath.Should().Be(DataRootDbPath, "撮影のアプリが開くのは撮影用の置き場所の DB");
            File.ReadAllBytes(RealDbPath).Should().Equal(realBytes, "開発機の DB は終了後に元のまま");
            Directory.GetFiles(DbDirectory).Should().BeEquivalentTo(new[] { RealDbPath }, "退避ファイル・印を残さない");
        }

        [Fact]
        public void 前回の撮影の世代は起動の前に消え_バックアップの一覧が撮影機の履歴に左右されないこと()
        {
            CreateDatabase(RealDbPath, RealStaffName);
            var previousBackup = Path.Combine(DataRoot, UiTestDatabaseGuard.DefaultBackupFolderName, "backup_20261101_075400.db");
            Directory.CreateDirectory(Path.GetDirectoryName(previousBackup)!);
            File.WriteAllText(previousBackup, "前回の撮影の世代");
            File.WriteAllText(DataRootDbPath, "前回の撮影の DB");
            IReadOnlyCollection<string>? filesAtMigration = null;

            using (Run(migrateDataRoot: true, onMigration: () =>
                filesAtMigration = Directory.GetFileSystemEntries(DataRoot, "*", SearchOption.AllDirectories)))
            {
            }

            filesAtMigration.Should().BeEmpty("撮影用の置き場所は空の状態からマイグレーションさせる（前回の DB・世代を残さない）");
        }

        [Fact]
        public void 起動の間_開発機のDBのバックアップ先はUIテスト専用のフォルダーに隔離されていること()
        {
            // 安全網: 差し替えが効かずにアプリが開発機の置き場所を開いても、既定のバックアップ先へ書かせない（#2423）
            CreateDatabase(RealDbPath, RealStaffName);
            string? backupPathAtMigration = null;

            using (Run(migrateDataRoot: true, onMigration: () =>
                backupPathAtMigration = ReadSetting(RealDbPath, UiTestDatabaseGuard.BackupPathSettingKey)))
            {
            }

            backupPathAtMigration.Should().Be(IsolatedBackupDirectory);
            ReadSetting(RealDbPath, UiTestDatabaseGuard.BackupPathSettingKey).Should().BeNull("終了後は元の設定（未設定）へ戻る");
        }

        [Fact]
        public void 差し替えが効かなかったとき_投入せずに止まり_開発機のDBを元へ戻すこと()
        {
            CreateDatabase(RealDbPath, RealStaffName);
            var realBytes = File.ReadAllBytes(RealDbPath);
            var seeded = false;
            var launched = false;

            Action act = () => Run(
                migrateDataRoot: false,
                seedDatabase: _ => seeded = true,
                onLaunched: () => launched = true).Dispose();

            var thrown = act.Should().Throw<InvalidOperationException>().Which;
            thrown.Message.Should().Contain(DataRootDbPath, "何が: 撮影用の置き場所に DB が無いことを示す")
                .And.Contain(AppFixture.ScreenshotDataRootEnvironmentVariable, "なぜ: 置き場所の差し替えを読まなかったことを示す")
                .And.EndWith("撮影してください。", "どうすれば: 行動指示で終わる");
            seeded.Should().BeFalse("撮影用の置き場所を使わないアプリの DB へは投入しない");
            launched.Should().BeFalse("投入データの無い画面を撮らない");
            File.ReadAllBytes(RealDbPath).Should().Equal(realBytes, "開発機の DB は元のまま");
            Directory.GetFiles(DbDirectory).Should().BeEquivalentTo(new[] { RealDbPath }, "退避ファイル・印を残さない");
        }

        /// <param name="overlap">撮影用の置き場所の決め方（開発機の置き場所の配下・同じフォルダー・開発機の置き場所を含む親）。</param>
        [Theory]
        [InlineData("child")]
        [InlineData("same")]
        [InlineData("parent")]
        public void 撮影用の置き場所が開発機の置き場所と重なるとき_何も触らずに止まること(string overlap)
        {
            // 撮影用の置き場所は起動のたびに空にするので、どの重なり方でも開発機の DB を消し得る
            var dataRoot = overlap switch
            {
                "child" => Path.Combine(DbDirectory, "screenshot-data"),
                "same" => DbDirectory + Path.DirectorySeparatorChar,
                _ => _root,
            };
            CreateDatabase(RealDbPath, RealStaffName);
            var realBytes = File.ReadAllBytes(RealDbPath);
            var migrated = false;

            Action act = () => AppFixture.LaunchWithSeedInDataRootCore<FakeFixture>(
                DbDirectory,
                IsolatedBackupDirectory,
                dataRoot: dataRoot,
                launchForMigration: () =>
                {
                    migrated = true;
                    return new FakeFixture(null);
                },
                seedDatabase: _ => { },
                launchOwningGuard: guard => new FakeFixture(guard));

            act.Should().Throw<ArgumentException>().Which.Message.Should().Contain("重なっています");
            migrated.Should().BeFalse();
            File.ReadAllBytes(RealDbPath).Should().Equal(realBytes);
            Directory.GetFiles(DbDirectory).Should().BeEquivalentTo(new[] { RealDbPath }, "空にする対象に開発機の DB を含めない・退避もしない");
        }

        [Fact]
        public void 撮影用の置き場所で起動している間_読み返すDBは撮影用の置き場所のDBであること()
        {
            // 開発機の置き場所の DB はガードの仮の DB で、アプリは開いていない。そちらを読むと、残高チェーンの確認などが投入データを見ない
            AppFixture.ResolveDatabasePath(DbDirectory, DataRoot).Should().Be(DataRootDbPath);
        }

        [Fact]
        public void 撮影用の置き場所で起動していなければ_読み返すDBは開発機の置き場所のDBであること()
        {
            AppFixture.ResolveDatabasePath(DbDirectory, activeScreenshotDataRoot: null).Should().Be(RealDbPath);
        }

        [Theory]
        [InlineData("Release")]
        [InlineData("release")]
        public void Debug起動でなければ止まること(string configuration)
        {
            // Release ビルドのアプリは置き場所の差し替えを持たない
            Action act = () => AppFixture.ThrowUnlessDebugLaunch(configuration);

            act.Should().Throw<InvalidOperationException>().Which.Message
                .Should().Contain(configuration).And.EndWith("走らせてください）。");
        }

        [Fact]
        public void Debug起動なら止まらないこと()
        {
            Action act = () => AppFixture.ThrowUnlessDebugLaunch("Debug");

            act.Should().NotThrow();
        }

        /// <summary>
        /// アプリの代わりに撮影用の置き場所へマイグレーション済みの DB を作る（<paramref name="migrateDataRoot"/> が false なら作らない＝差し替えが効かない）。
        /// </summary>
        private FakeFixture Run(
            bool migrateDataRoot,
            Action<string>? seedDatabase = null,
            Action? onMigration = null,
            Action? onLaunched = null)
        {
            return AppFixture.LaunchWithSeedInDataRootCore(
                DbDirectory,
                IsolatedBackupDirectory,
                DataRoot,
                launchForMigration: () =>
                {
                    onMigration?.Invoke();
                    if (migrateDataRoot)
                    {
                        CreateDatabase(DataRootDbPath, staffName: null);
                    }

                    return new FakeFixture(null);
                },
                seedDatabase: seedDatabase ?? (_ => { }),
                launchOwningGuard: guard =>
                {
                    onLaunched?.Invoke();
                    return new FakeFixture(guard);
                },
                maxAttempts: 1);
        }

        private static void CreateDatabase(string dbPath, string? staffName)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using (var conn = Open(dbPath))
            {
                Execute(conn, "CREATE TABLE IF NOT EXISTS staff (staff_idm TEXT PRIMARY KEY, name TEXT NOT NULL)");
                Execute(conn, "CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT)");
            }

            if (staffName != null)
            {
                InsertStaff(dbPath, staffName);
            }

            SQLiteConnection.ClearAllPools();
        }

        private static void InsertStaff(string dbPath, string name)
        {
            using (var conn = Open(dbPath))
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO staff (staff_idm, name) VALUES (@idm, @name)";
                cmd.Parameters.AddWithValue("@idm", Guid.NewGuid().ToString("N"));
                cmd.Parameters.AddWithValue("@name", name);
                cmd.ExecuteNonQuery();
            }

            SQLiteConnection.ClearAllPools();
        }

        private static IReadOnlyList<string> StaffNames(string dbPath)
        {
            var names = new List<string>();
            using (var conn = Open(dbPath))
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM staff ORDER BY name";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    names.Add(reader.GetString(0));
                }
            }

            SQLiteConnection.ClearAllPools();
            return names;
        }

        private static string? ReadSetting(string dbPath, string key)
        {
            string? value;
            using (var conn = Open(dbPath))
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT value FROM settings WHERE key = @key";
                cmd.Parameters.AddWithValue("@key", key);
                value = cmd.ExecuteScalar() as string;
            }

            SQLiteConnection.ClearAllPools();
            return value;
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

        private sealed class FakeFixture : IDisposable
        {
            private readonly UiTestDatabaseGuard? _ownedGuard;

            public FakeFixture(UiTestDatabaseGuard? ownedGuard)
            {
                _ownedGuard = ownedGuard;
            }

            public void Dispose()
            {
                SQLiteConnection.ClearAllPools();
                _ownedGuard?.Restore();
            }
        }
    }
}
