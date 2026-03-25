using System;
using System.IO;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// App.DeleteOrClearFile()のテスト
/// ファイル削除とフォールバック（内容クリア）の動作を検証
/// </summary>
/// <remarks>
/// 削除権限がないケース（UnauthorizedAccessException → WriteAllTextフォールバック）は
/// テスト環境でACLを正確にシミュレートすることが困難なため、手動テストで確認する。
/// </remarks>
public class DeleteOrClearFileTests : IDisposable
{
    private readonly string _tempDir;

    public DeleteOrClearFileTests()
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
    public void DeleteOrClearFile_通常のファイルの場合_削除されてtrueを返す()
    {
        // Arrange
        var filePath = Path.Combine(_tempDir, "test.txt");
        File.WriteAllText(filePath, "test content");

        // Act
        var result = App.DeleteOrClearFile(filePath);

        // Assert
        result.Should().BeTrue();
        File.Exists(filePath).Should().BeFalse("ファイルが削除されるべき");
    }

    [Fact]
    public void DeleteOrClearFile_存在しないファイルの場合_trueを返す()
    {
        // Arrange
        var filePath = Path.Combine(_tempDir, "nonexistent.txt");

        // Act
        var result = App.DeleteOrClearFile(filePath);

        // Assert
        result.Should().BeTrue("File.Deleteは存在しないファイルに対してエラーを投げない");
    }

    [Fact]
    public void DeleteOrClearFile_ロガーがnullの場合_例外が発生しない()
    {
        // Arrange
        var filePath = Path.Combine(_tempDir, "test.txt");
        File.WriteAllText(filePath, "test content");

        // Act
        var act = () => App.DeleteOrClearFile(filePath, null);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void DeleteOrClearFile_削除後にファイルが再作成可能()
    {
        // Arrange
        var filePath = Path.Combine(_tempDir, "recreate.txt");
        File.WriteAllText(filePath, "original");
        App.DeleteOrClearFile(filePath);

        // Act: ファイルを再作成
        File.WriteAllText(filePath, "new content");

        // Assert
        File.ReadAllText(filePath).Should().Be("new content");
    }
}
