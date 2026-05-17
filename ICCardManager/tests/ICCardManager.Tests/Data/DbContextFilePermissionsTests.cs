using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using ICCardManager.Data;
using Xunit;

namespace ICCardManager.Tests.Data;

/// <summary>
/// DbContext.SetDatabaseFilePermissions()のテスト
/// データベースファイルのアクセス権限設定を検証
/// </summary>
public class DbContextFilePermissionsTests : IDisposable
{
    private readonly string _tempDir;

    public DbContextFilePermissionsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ICCardManagerTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // テスト後のクリーンアップ失敗は無視
        }
    }

    [Fact]
    public void SetDatabaseFilePermissions_存在しないファイルの場合_例外が発生しない()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_tempDir, "nonexistent.db");

        // Act
        var act = () => DbContext.SetDatabaseFilePermissions(nonExistentPath);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void SetDatabaseFilePermissions_継承が無効化されたファイルの場合_継承を再有効化する()
    {
        // Arrange: 旧バージョンの動作を再現（継承無効化＋現在のユーザーのみ）
        var dbPath = Path.Combine(_tempDir, "test.db");
        File.WriteAllText(dbPath, "test");
        SimulateOldVersionPermissions(dbPath);

        // Act
        DbContext.SetDatabaseFilePermissions(dbPath);

        // Assert
        var fileInfo = new FileInfo(dbPath);
        var fileSecurity = fileInfo.GetAccessControl();
        fileSecurity.AreAccessRulesProtected.Should().BeFalse("継承が有効化されるべき");
    }

    [Fact]
    public void SetDatabaseFilePermissions_継承が無効化されたファイルの場合_明示的ACLが削除される()
    {
        // Arrange: 旧バージョンの動作を再現
        var dbPath = Path.Combine(_tempDir, "test.db");
        File.WriteAllText(dbPath, "test");
        SimulateOldVersionPermissions(dbPath);

        // Act
        DbContext.SetDatabaseFilePermissions(dbPath);

        // Assert
        var fileInfo = new FileInfo(dbPath);
        var fileSecurity = fileInfo.GetAccessControl();
        var explicitRules = fileSecurity.GetAccessRules(true, false, typeof(SecurityIdentifier));
        explicitRules.Count.Should().Be(0, "明示的ACLは削除され、継承ルールのみになるべき");
    }

    [Fact]
    public void SetDatabaseFilePermissions_既に継承が有効なファイルの場合_何もしない()
    {
        // Arrange: 継承が有効な通常のファイル
        var dbPath = Path.Combine(_tempDir, "test.db");
        File.WriteAllText(dbPath, "test");

        var fileInfoBefore = new FileInfo(dbPath);
        var securityBefore = fileInfoBefore.GetAccessControl();
        var rulesBefore = securityBefore.GetAccessRules(true, true, typeof(SecurityIdentifier));
        var rulesCountBefore = rulesBefore.Count;

        // Act
        DbContext.SetDatabaseFilePermissions(dbPath);

        // Assert: 変更なし
        var fileInfoAfter = new FileInfo(dbPath);
        var securityAfter = fileInfoAfter.GetAccessControl();
        securityAfter.AreAccessRulesProtected.Should().BeFalse("継承は有効のまま");
    }

    [Fact]
    public void SetDatabaseFilePermissions_WALファイルも処理される()
    {
        // Arrange: DBファイルとWALファイルの両方の継承を無効化
        var dbPath = Path.Combine(_tempDir, "test.db");
        var walPath = dbPath + "-wal";
        File.WriteAllText(dbPath, "test");
        File.WriteAllText(walPath, "wal");
        SimulateOldVersionPermissions(dbPath);
        SimulateOldVersionPermissions(walPath);

        // Act
        DbContext.SetDatabaseFilePermissions(dbPath);

        // Assert: 両方のファイルで継承が有効化される
        var dbSecurity = new FileInfo(dbPath).GetAccessControl();
        dbSecurity.AreAccessRulesProtected.Should().BeFalse("DBファイルの継承が有効化されるべき");

        var walSecurity = new FileInfo(walPath).GetAccessControl();
        walSecurity.AreAccessRulesProtected.Should().BeFalse("WALファイルの継承が有効化されるべき");
    }

    [Fact]
    public void SetDatabaseFilePermissions_SHMファイルも処理される()
    {
        // Arrange
        var dbPath = Path.Combine(_tempDir, "test.db");
        var shmPath = dbPath + "-shm";
        File.WriteAllText(dbPath, "test");
        File.WriteAllText(shmPath, "shm");
        SimulateOldVersionPermissions(shmPath);

        // Act
        DbContext.SetDatabaseFilePermissions(dbPath);

        // Assert
        var shmSecurity = new FileInfo(shmPath).GetAccessControl();
        shmSecurity.AreAccessRulesProtected.Should().BeFalse("SHMファイルの継承が有効化されるべき");
    }

    [Fact]
    public void SetDatabaseFilePermissions_journalファイルも処理される()
    {
        // Arrange
        var dbPath = Path.Combine(_tempDir, "test.db");
        var journalPath = dbPath + "-journal";
        File.WriteAllText(dbPath, "test");
        File.WriteAllText(journalPath, "journal");
        SimulateOldVersionPermissions(journalPath);

        // Act
        DbContext.SetDatabaseFilePermissions(dbPath);

        // Assert
        var journalSecurity = new FileInfo(journalPath).GetAccessControl();
        journalSecurity.AreAccessRulesProtected.Should().BeFalse("journalファイルの継承が有効化されるべき");
    }

    [Fact]
    public void EnsureDirectoryExists_新規ディレクトリの場合_ディレクトリが作成される()
    {
        // Arrange
        var dirPath = Path.Combine(_tempDir, "newdir");

        // Act
        DbContext.EnsureDirectoryExists(dirPath);

        // Assert
        Directory.Exists(dirPath).Should().BeTrue("ディレクトリが作成されるべき");
    }

    [Fact]
    public void EnsureDirectoryExists_既存ディレクトリの場合_例外が発生しない()
    {
        // Arrange: ディレクトリを先に作成
        var dirPath = Path.Combine(_tempDir, "existingdir");
        Directory.CreateDirectory(dirPath);

        // Act
        var act = () => DbContext.EnsureDirectoryExists(dirPath);

        // Assert
        act.Should().NotThrow("既存ディレクトリに対しても冪等に動作すべき");
        Directory.Exists(dirPath).Should().BeTrue();
    }

    /// <summary>
    /// Issue #1455: ランタイムでの ACL 拡張は撤廃された。
    /// インストーラーが親ディレクトリに <c>users-full</c> を設定済みであり、
    /// ランタイムで <c>BUILTIN\Users : FullControl</c> を <c>AddAccessRule</c> すると
    /// (1) 削除権限を含む過剰権限となり、
    /// (2) 起動の度に新規 ACE が追加され ACL が累積する。
    /// 本テストは過剰権限の付与が再発しないことを保証する regression detector。
    /// </summary>
    [Fact]
    public void EnsureDirectoryExists_明示的なUsersFullControlACEを追加しない()
    {
        // Arrange
        var dirPath = Path.Combine(_tempDir, "noexplicit_acl");

        // Act
        DbContext.EnsureDirectoryExists(dirPath);

        // Assert
        var dirInfo = new DirectoryInfo(dirPath);
        var dirSecurity = dirInfo.GetAccessControl();
        // includeExplicit=true, includeInherited=false で、明示的に追加された ACE のみ取得
        var explicitRules = dirSecurity.GetAccessRules(true, false, typeof(SecurityIdentifier));

        var usersIdentity = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        foreach (FileSystemAccessRule rule in explicitRules)
        {
            if (rule.IdentityReference.Equals(usersIdentity)
                && rule.AccessControlType == AccessControlType.Allow)
            {
                rule.FileSystemRights.HasFlag(FileSystemRights.FullControl)
                    .Should().BeFalse(
                        "Issue #1455: BUILTIN\\Users への明示的 FullControl ACE は付与しない");
            }
        }
    }

    /// <summary>
    /// Issue #1455: 旧実装の <c>AddAccessRule</c> は冪等ではなく、繰り返し呼び出すと
    /// ACE が累積していた。本テストは複数回呼び出しても明示的 ACE 数が増加しないことを検証する。
    /// </summary>
    /// <remarks>
    /// 絶対値（0 や 1 等）でアサートするとテスト実行環境（<c>Path.GetTempPath()</c> の ACL 構成、
    /// VirtualStore 設定、ローミングプロファイル等）に依存して false positive / false negative
    /// が発生しうる。本テストは「初回呼び出し後の件数」を基準に「N 回呼び出し後も同件数」を
    /// 検証することで、環境依存を排除して累積発生のみを検出する。
    /// </remarks>
    [Fact]
    public void EnsureDirectoryExists_複数回呼び出してもACEが累積しない()
    {
        // Arrange
        var dirPath = Path.Combine(_tempDir, "no_acl_growth");

        // Act 1: 初回呼び出しで基準件数を取得
        DbContext.EnsureDirectoryExists(dirPath);
        var initialExplicitAceCount = GetExplicitAceCount(dirPath);

        // Act 2: 追加で 4 回（合計 5 回）呼び出す
        for (int i = 0; i < 4; i++)
        {
            DbContext.EnsureDirectoryExists(dirPath);
        }

        // Assert: 明示的 ACE 数は初回と同じ（累積していない）
        var finalExplicitAceCount = GetExplicitAceCount(dirPath);
        finalExplicitAceCount.Should().Be(initialExplicitAceCount,
            "ランタイムでの繰り返し呼び出しで明示的 ACE が増えてはならない（旧 AddAccessRule 非冪等問題の回帰防止）");
    }

    /// <summary>
    /// 明示的（継承ではない）に追加された ACE の件数を取得する。
    /// </summary>
    private static int GetExplicitAceCount(string dirPath)
    {
        var dirInfo = new DirectoryInfo(dirPath);
        var dirSecurity = dirInfo.GetAccessControl();
        // includeExplicit=true, includeInherited=false で、明示的に追加された ACE のみ取得
        return dirSecurity.GetAccessRules(true, false, typeof(SecurityIdentifier)).Count;
    }

    /// <summary>
    /// 旧バージョンの RestrictDatabaseFilePermissions の動作を再現する。
    /// 継承を無効化し、現在のユーザーのみにフルコントロールを設定。
    /// </summary>
    private static void SimulateOldVersionPermissions(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        var fileSecurity = fileInfo.GetAccessControl();

        // 継承を無効化
        fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        // 既存ルールを削除
        var rules = fileSecurity.GetAccessRules(true, true, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            fileSecurity.RemoveAccessRule(rule);
        }

        // 現在のユーザーのみフルコントロール
        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser != null)
        {
            fileSecurity.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }

        fileInfo.SetAccessControl(fileSecurity);
    }
}
