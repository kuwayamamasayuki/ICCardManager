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
    public void SetDatabaseFilePermissions_ファイルが存在する場合_Usersグループにアクセス権が付与される()
    {
        // Arrange
        var dbPath = Path.Combine(_tempDir, "test.db");
        File.WriteAllText(dbPath, "test");

        // Act
        DbContext.SetDatabaseFilePermissions(dbPath);

        // Assert
        var fileInfo = new FileInfo(dbPath);
        var fileSecurity = fileInfo.GetAccessControl();
        var rules = fileSecurity.GetAccessRules(true, false, typeof(SecurityIdentifier));

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

        hasUsersAccess.Should().BeTrue("Usersグループにフルコントロール権限が付与されるべき");
    }

    [Fact]
    public void SetDatabaseFilePermissions_ファイルが存在する場合_SYSTEMにアクセス権が付与される()
    {
        // Arrange
        var dbPath = Path.Combine(_tempDir, "test.db");
        File.WriteAllText(dbPath, "test");

        // Act
        DbContext.SetDatabaseFilePermissions(dbPath);

        // Assert
        var fileInfo = new FileInfo(dbPath);
        var fileSecurity = fileInfo.GetAccessControl();
        var rules = fileSecurity.GetAccessRules(true, false, typeof(SecurityIdentifier));

        var systemIdentity = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var hasSystemAccess = false;
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.IdentityReference.Equals(systemIdentity)
                && rule.AccessControlType == AccessControlType.Allow
                && rule.FileSystemRights.HasFlag(FileSystemRights.FullControl))
            {
                hasSystemAccess = true;
                break;
            }
        }

        hasSystemAccess.Should().BeTrue("SYSTEMにフルコントロール権限が付与されるべき");
    }

    [Fact]
    public void SetDatabaseFilePermissions_ファイルが存在する場合_Administratorsにアクセス権が付与される()
    {
        // Arrange
        var dbPath = Path.Combine(_tempDir, "test.db");
        File.WriteAllText(dbPath, "test");

        // Act
        DbContext.SetDatabaseFilePermissions(dbPath);

        // Assert
        var fileInfo = new FileInfo(dbPath);
        var fileSecurity = fileInfo.GetAccessControl();
        var rules = fileSecurity.GetAccessRules(true, false, typeof(SecurityIdentifier));

        var adminsIdentity = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var hasAdminsAccess = false;
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.IdentityReference.Equals(adminsIdentity)
                && rule.AccessControlType == AccessControlType.Allow
                && rule.FileSystemRights.HasFlag(FileSystemRights.FullControl))
            {
                hasAdminsAccess = true;
                break;
            }
        }

        hasAdminsAccess.Should().BeTrue("Administratorsにフルコントロール権限が付与されるべき");
    }

    [Fact]
    public void SetDatabaseFilePermissions_継承が無効化される()
    {
        // Arrange
        var dbPath = Path.Combine(_tempDir, "test.db");
        File.WriteAllText(dbPath, "test");

        // Act
        DbContext.SetDatabaseFilePermissions(dbPath);

        // Assert
        var fileInfo = new FileInfo(dbPath);
        var fileSecurity = fileInfo.GetAccessControl();
        var isProtected = fileSecurity.AreAccessRulesProtected;

        isProtected.Should().BeTrue("継承は無効化され、明示的なルールのみが適用されるべき");
    }

    [Fact]
    public void SetDatabaseFilePermissions_明示的に付与された3つのルールのみが存在する()
    {
        // Arrange
        var dbPath = Path.Combine(_tempDir, "test.db");
        File.WriteAllText(dbPath, "test");

        // Act
        DbContext.SetDatabaseFilePermissions(dbPath);

        // Assert
        var fileInfo = new FileInfo(dbPath);
        var fileSecurity = fileInfo.GetAccessControl();
        // 継承ルールを除外し、明示的ルールのみを取得
        var rules = fileSecurity.GetAccessRules(true, false, typeof(SecurityIdentifier));

        // Users, SYSTEM, Administrators の3つのAllowルールのみ
        var allowRules = 0;
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType == AccessControlType.Allow)
            {
                allowRules++;
            }
        }

        allowRules.Should().Be(3, "Users, SYSTEM, Administratorsの3つのAllowルールのみが存在すべき");
    }
}
