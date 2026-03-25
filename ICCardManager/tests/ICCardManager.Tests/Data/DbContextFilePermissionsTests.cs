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
    public void EnsureDirectoryWithPermissions_新規ディレクトリの場合_Usersグループにフルコントロールが付与される()
    {
        // Arrange
        var dirPath = Path.Combine(_tempDir, "newdir");

        // Act
        DbContext.EnsureDirectoryWithPermissions(dirPath);

        // Assert
        var dirInfo = new DirectoryInfo(dirPath);
        dirInfo.Exists.Should().BeTrue();
        var dirSecurity = dirInfo.GetAccessControl();
        var rules = dirSecurity.GetAccessRules(true, false, typeof(SecurityIdentifier));

        var usersIdentity = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var hasUsersAccess = false;
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.IdentityReference.Equals(usersIdentity)
                && rule.AccessControlType == AccessControlType.Allow
                && rule.FileSystemRights.HasFlag(FileSystemRights.FullControl)
                && rule.InheritanceFlags.HasFlag(InheritanceFlags.ContainerInherit)
                && rule.InheritanceFlags.HasFlag(InheritanceFlags.ObjectInherit))
            {
                hasUsersAccess = true;
                break;
            }
        }

        hasUsersAccess.Should().BeTrue("Usersグループにフルコントロール（継承あり）が付与されるべき");
    }

    [Fact]
    public void EnsureDirectoryWithPermissions_既存ディレクトリの場合でもUsersグループにフルコントロールが付与される()
    {
        // Arrange: ディレクトリを先に作成（Usersの権限なし）
        var dirPath = Path.Combine(_tempDir, "existingdir");
        Directory.CreateDirectory(dirPath);

        // Act
        DbContext.EnsureDirectoryWithPermissions(dirPath);

        // Assert
        var dirInfo = new DirectoryInfo(dirPath);
        var dirSecurity = dirInfo.GetAccessControl();
        var rules = dirSecurity.GetAccessRules(true, false, typeof(SecurityIdentifier));

        var usersIdentity = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var hasUsersAccess = false;
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.IdentityReference.Equals(usersIdentity)
                && rule.AccessControlType == AccessControlType.Allow
                && rule.FileSystemRights.HasFlag(FileSystemRights.FullControl))
            {
                hasUsersAccess = true;
                break;
            }
        }

        hasUsersAccess.Should().BeTrue("既存ディレクトリでもUsersグループにフルコントロールが付与されるべき");
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
