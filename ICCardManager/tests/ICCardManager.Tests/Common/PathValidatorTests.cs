using System.IO;
using System.Runtime.InteropServices;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// PathValidatorの単体テスト
/// </summary>
public class PathValidatorTests : IDisposable
{
    private readonly string _testDirectory;

    public PathValidatorTests()
    {
        // テスト用の一時ディレクトリを作成
        _testDirectory = Path.Combine(Path.GetTempPath(), $"PathValidatorTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        // テスト用ディレクトリを削除
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
            // クリーンアップ失敗は無視
        }

        GC.SuppressFinalize(this);
    }

    #region ValidateBackupPath - Null/Empty テスト

    /// <summary>
    /// nullパスが拒否されることを確認
    /// </summary>
    [Fact]
    public void ValidateBackupPath_NullPath_ReturnsInvalid()
    {
        // Act
        var result = PathValidator.ValidateBackupPath(null);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("指定されていません");
    }

    /// <summary>
    /// 空文字列が拒否されることを確認
    /// </summary>
    [Fact]
    public void ValidateBackupPath_EmptyPath_ReturnsInvalid()
    {
        // Act
        var result = PathValidator.ValidateBackupPath(string.Empty);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("指定されていません");
    }

    /// <summary>
    /// 空白のみのパスが拒否されることを確認
    /// </summary>
    [Fact]
    public void ValidateBackupPath_WhitespacePath_ReturnsInvalid()
    {
        // Act
        var result = PathValidator.ValidateBackupPath("   ");

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("指定されていません");
    }

    #endregion

    #region ValidateBackupPath - パス長テスト

    /// <summary>
    /// 長すぎるパスが拒否されることを確認
    /// </summary>
    [Fact]
    public void ValidateBackupPath_PathTooLong_ReturnsInvalid()
    {
        // Arrange
        var longPath = @"C:\" + new string('a', 300);

        // Act
        var result = PathValidator.ValidateBackupPath(longPath);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("長すぎます");
    }

    /// <summary>
    /// 260文字ちょうどのパスは許容されることを確認
    /// </summary>
    [Fact]
    public void ValidateBackupPath_PathAt260Chars_IsValid()
    {
        // Arrange - 260文字ちょうどのパスを作成
        // C:\ = 3文字、残り257文字
        var path = @"C:\" + new string('a', 257);

        // Act
        var result = PathValidator.ValidateBackupPath(path);

        // Assert - パストラバーサルやUNCでなければ長さは OK
        // ドライブが存在しない可能性があるのでそのエラーは無視
        if (!result.IsValid)
        {
            result.ErrorMessage.Should().NotContain("長すぎます");
        }
    }

    #endregion

    #region ValidateBackupPath - UNCパステスト

    /// <summary>
    /// UNCパス（\\server\share形式）が拒否されることを確認
    /// </summary>
    [Fact]
    public void ValidateBackupPath_UncPath_ReturnsInvalid()
    {
        // Act
        var result = PathValidator.ValidateBackupPath(@"\\server\share\backup");

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("ネットワークパス");
    }

    /// <summary>
    /// UNCパス（//server/share形式）が拒否されることを確認
    /// </summary>
    [Fact]
    public void ValidateBackupPath_UncPathWithForwardSlash_ReturnsInvalid()
    {
        // Act
        var result = PathValidator.ValidateBackupPath("//server/share/backup");

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("ネットワークパス");
    }

    #endregion

    #region ValidateBackupPath - 相対パステスト

    /// <summary>
    /// 相対パスが拒否されることを確認
    /// </summary>
    [Fact]
    public void ValidateBackupPath_RelativePath_ReturnsInvalid()
    {
        // Act
        var result = PathValidator.ValidateBackupPath("backup/folder");

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("絶対パス");
    }

    /// <summary>
    /// ドット開始の相対パスが拒否されることを確認
    /// </summary>
    [Fact]
    public void ValidateBackupPath_DotRelativePath_ReturnsInvalid()
    {
        // Act
        var result = PathValidator.ValidateBackupPath("./backup");

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("絶対パス");
    }

    #endregion

    #region ValidateBackupPath - パストラバーサルテスト

    /// <summary>
    /// パストラバーサル（..）を含むパスが拒否されることを確認
    /// </summary>
    [Fact]
    public void ValidateBackupPath_PathTraversal_ReturnsInvalid()
    {
        // Act
        var result = PathValidator.ValidateBackupPath(@"C:\backup\..\Windows\System32");

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("..");
    }

    /// <summary>
    /// 中間に..を含むパスが拒否されることを確認
    /// </summary>
    [Fact]
    public void ValidateBackupPath_PathTraversalInMiddle_ReturnsInvalid()
    {
        // Act
        var result = PathValidator.ValidateBackupPath(@"C:\Users\test\..\admin\backup");

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("..");
    }

    /// <summary>
    /// 末尾に..を含むパスが拒否されることを確認
    /// </summary>
    [Fact]
    public void ValidateBackupPath_PathTraversalAtEnd_ReturnsInvalid()
    {
        // Act
        var result = PathValidator.ValidateBackupPath(@"C:\Users\test\..");

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("..");
    }

    #endregion

    #region ValidateBackupPath - 有効なパステスト

    /// <summary>
    /// 有効な絶対パスが受け入れられることを確認
    /// </summary>
    [Fact]
    public void ValidateBackupPath_ValidAbsolutePath_ReturnsValid()
    {
        // Arrange - 実在するディレクトリを使用
        var validPath = _testDirectory;

        // Act
        var result = PathValidator.ValidateBackupPath(validPath);

        // Assert
        result.IsValid.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    /// <summary>
    /// 存在しないが有効な形式のパスが受け入れられることを確認
    /// </summary>
    [Fact]
    public void ValidateBackupPath_ValidPathNotExists_ReturnsValid()
    {
        // Arrange - 存在しないが有効なパス
        var validPath = Path.Combine(_testDirectory, "new_folder");

        // Act
        var result = PathValidator.ValidateBackupPath(validPath);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// ネストしたパスが受け入れられることを確認
    /// </summary>
    [Fact]
    public void ValidateBackupPath_NestedPath_ReturnsValid()
    {
        // Arrange
        var nestedPath = Path.Combine(_testDirectory, "level1", "level2", "level3");

        // Act
        var result = PathValidator.ValidateBackupPath(nestedPath);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    #endregion

    #region NormalizePath テスト

    /// <summary>
    /// nullが返されることを確認
    /// </summary>
    [Fact]
    public void NormalizePath_NullInput_ReturnsNull()
    {
        // Act
        var result = PathValidator.NormalizePath(null);

        // Assert
        result.Should().BeNull();
    }

    /// <summary>
    /// 空文字列が返されることを確認
    /// </summary>
    [Fact]
    public void NormalizePath_EmptyInput_ReturnsNull()
    {
        // Act
        var result = PathValidator.NormalizePath(string.Empty);

        // Assert
        result.Should().BeNull();
    }

    /// <summary>
    /// 末尾のスペースが除去されることを確認
    /// </summary>
    [Fact]
    public void NormalizePath_TrailingSpaces_TrimsSpaces()
    {
        // Arrange
        var pathWithSpaces = _testDirectory + "   ";

        // Act
        var result = PathValidator.NormalizePath(pathWithSpaces);

        // Assert
        result.Should().NotBeNull();
        result.Should().NotEndWith(" ");
    }

    /// <summary>
    /// パスが正規化されることを確認
    /// </summary>
    [Fact]
    public void NormalizePath_ValidPath_ReturnsFullPath()
    {
        // Arrange
        var inputPath = _testDirectory;

        // Act
        var result = PathValidator.NormalizePath(inputPath);

        // Assert
        result.Should().NotBeNull();
        result.Should().Be(Path.GetFullPath(inputPath));
    }

    #endregion

    #region GetDefaultBackupPath テスト

    /// <summary>
    /// デフォルトパスがLocalApplicationData内であることを確認
    /// </summary>
    [Fact]
    public void GetDefaultBackupPath_ReturnsLocalAppDataPath()
    {
        // Act
        var result = PathValidator.GetDefaultBackupPath();

        // Assert
        result.Should().Contain("ICCardManager");
        result.Should().Contain("backup");
        result.Should().StartWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    /// <summary>
    /// デフォルトパスが絶対パスであることを確認
    /// </summary>
    [Fact]
    public void GetDefaultBackupPath_ReturnsAbsolutePath()
    {
        // Act
        var result = PathValidator.GetDefaultBackupPath();

        // Assert
        Path.IsPathRooted(result).Should().BeTrue();
    }

    #endregion

    #region 境界値テスト

    /// <summary>
    /// 不正な文字を含むパスが拒否されることを確認（プラットフォーム依存）
    /// </summary>
    [Theory]
    [InlineData("C:\\backup<test")]
    [InlineData("C:\\backup>test")]
    [InlineData("C:\\backup|test")]
    [InlineData("C:\\backup\"test")]
    public void ValidateBackupPath_InvalidCharacters_ReturnsInvalid(string invalidPath)
    {
        // Act
        var result = PathValidator.ValidateBackupPath(invalidPath);

        // Assert
        result.IsValid.Should().BeFalse();
    }

    #endregion

    #region エッジケーステスト

    /// <summary>
    /// ドライブルートパスの検証を確認
    /// Note: ドライブルートは書き込み権限がない可能性があるため、
    /// パス形式として有効かどうかをチェック
    /// </summary>
    [Fact]
    public void ValidateBackupPath_DriveRoot_ChecksPathFormat()
    {
        // Arrange - 存在するドライブのルートを使用
        var driveRoot = Path.GetPathRoot(_testDirectory);

        // Act
        var result = PathValidator.ValidateBackupPath(driveRoot);

        // Assert
        // ドライブルートは有効なパス形式だが、書き込み権限がない場合は失敗する可能性がある
        // パストラバーサルやUNCパスなどの致命的なエラーではないことを確認
        if (!result.IsValid)
        {
            // 書き込み権限関連のエラーは許容
            result.ErrorMessage.Should().NotContain("ネットワークパス");
            result.ErrorMessage.Should().NotContain("..");
            result.ErrorMessage.Should().NotContain("絶対パス");
        }
    }

    /// <summary>
    /// 日本語を含むパスが有効であることを確認
    /// </summary>
    [Fact]
    public void ValidateBackupPath_JapanesePath_IsValid()
    {
        // Arrange
        var japanesePath = Path.Combine(_testDirectory, "バックアップ", "フォルダ");

        // Act
        var result = PathValidator.ValidateBackupPath(japanesePath);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// スペースを含むパスが有効であることを確認
    /// </summary>
    [Fact]
    public void ValidateBackupPath_PathWithSpaces_IsValid()
    {
        // Arrange
        var pathWithSpaces = Path.Combine(_testDirectory, "backup folder", "sub folder");

        // Act
        var result = PathValidator.ValidateBackupPath(pathWithSpaces);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    #endregion
}
