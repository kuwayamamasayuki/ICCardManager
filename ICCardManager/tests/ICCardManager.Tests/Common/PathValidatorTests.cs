using System.IO;
using System.Runtime.InteropServices;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


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
    /// 有効なUNCパス（\\server\share形式）が受け入れられることを確認
    /// </summary>
    [Fact]
    public void ValidateBackupPath_ValidUncPath_ReturnsValid()
    {
        // Act
        var result = PathValidator.ValidateBackupPath(@"\\server\share\backup");

        // Assert
        // UNCパスの形式としては有効（実際のネットワーク到達性は書き込みチェック時に判定）
        // 書き込み権限チェックで失敗する場合があるが、UNCパス形式としての拒否はされない
        if (!result.IsValid)
        {
            result.ErrorMessage.Should().NotContain("サーバー名と共有名が必要");
        }
    }

    /// <summary>
    /// 有効なUNCパス（//server/share形式）が受け入れられることを確認
    /// </summary>
    [Fact]
    public void ValidateBackupPath_ValidUncPathWithForwardSlash_ReturnsValid()
    {
        // Act
        var result = PathValidator.ValidateBackupPath("//server/share/backup");

        // Assert
        if (!result.IsValid)
        {
            result.ErrorMessage.Should().NotContain("サーバー名と共有名が必要");
        }
    }

    /// <summary>
    /// サーバー名のみのUNCパス（共有名なし）が拒否されることを確認
    /// </summary>
    [Fact]
    public void ValidateBackupPath_UncPathServerOnly_ReturnsInvalid()
    {
        // Act
        var result = PathValidator.ValidateBackupPath(@"\\server");

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("サーバー名と共有名が必要");
    }

    /// <summary>
    /// サーバー名の後にセパレータのみのUNCパスが拒否されることを確認
    /// </summary>
    [Fact]
    public void ValidateBackupPath_UncPathServerWithTrailingSeparator_ReturnsInvalid()
    {
        // Act
        var result = PathValidator.ValidateBackupPath(@"\\server\");

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("サーバー名と共有名が必要");
    }

    /// <summary>
    /// IsUncPathがUNCパスを正しく検出することを確認
    /// </summary>
    [Theory]
    [InlineData(@"\\server\share", true)]
    [InlineData("//server/share", true)]
    [InlineData(@"C:\backup", false)]
    [InlineData(@"D:\data\backup", false)]
    public void IsUncPath_DetectsCorrectly(string path, bool expected)
    {
        // Act
        var result = PathValidator.IsUncPath(path);

        // Assert
        result.Should().Be(expected);
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

    #region Issue #1268: 強化されたパストラバーサル検出

    /// <summary>
    /// Issue #1268: UNC パスに埋め込まれたトラバーサルを検出する。
    /// <c>\\server\share\..\admin</c> は <c>\\server\admin</c> に正規化され、
    /// 意図した共有境界を逸脱する。
    /// </summary>
    [Fact]
    public void ValidateBackupPath_UncPathWithTraversal_ReturnsInvalid()
    {
        // Act
        var result = PathValidator.ValidateBackupPath(@"\\server\share\..\admin\iccard.db");

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("..");
    }

    /// <summary>
    /// Issue #1268: UNC パスで共有境界を逸脱する深いトラバーサル。
    /// </summary>
    [Fact]
    public void ValidateBackupPath_UncPathDeepTraversal_ReturnsInvalid()
    {
        // Act: \\server\share\..\..\admin\iccard.db
        var result = PathValidator.ValidateBackupPath(@"\\server\share\..\..\admin\iccard.db");

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("..");
    }

    /// <summary>
    /// Issue #1268: URL エンコードされたトラバーサル (<c>%2E%2E</c>) を検出する。
    /// </summary>
    [Fact]
    public void ValidateBackupPath_UrlEncodedTraversal_ReturnsInvalid()
    {
        // Act: C:\backup\%2E%2E\Windows （%2E%2E は .. の URL エンコード）
        var result = PathValidator.ValidateBackupPath(@"C:\backup\%2E%2E\Windows\System32");

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("..");
    }

    /// <summary>
    /// Issue #1268: 混合区切り文字 (<c>/</c> と <c>\</c>) のパストラバーサル検出。
    /// </summary>
    [Theory]
    [InlineData(@"C:\backup/../Windows")]
    [InlineData(@"C:/backup\..\Windows")]
    [InlineData(@"C:\backup/..\..\Windows")]
    public void ValidateBackupPath_MixedSeparatorTraversal_ReturnsInvalid(string path)
    {
        var result = PathValidator.ValidateBackupPath(path);
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("..");
    }

    /// <summary>
    /// Issue #1268: 末尾空白パターン。
    /// Windows は末尾の空白を無視するため <c>.. </c> は <c>..</c> として解釈されうる。
    /// </summary>
    [Theory]
    [InlineData(@"C:\backup\.. \Windows")]     // ".. " （末尾空白1つ）
    [InlineData(@"C:\backup\..  \Windows")]    // "..  "（末尾空白2つ）
    public void ValidateBackupPath_DotSpaceTraversal_ReturnsInvalid(string path)
    {
        var result = PathValidator.ValidateBackupPath(path);
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("..");
    }

    /// <summary>
    /// Issue #1268: 通常の有効なパスにはトラバーサル検出が誤反応しないこと（false positive 防止）。
    /// traversal エラーメッセージの特徴的キーワード「URL エンコード」の非出現を確認する
    /// （一般的な書き込み権限エラーと区別するため）。
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users\test\backup")]
    [InlineData(@"C:\ProgramData\ICCardManager")]
    [InlineData(@"C:\Users\test\.config")]          // 先頭ドットのファイル名
    [InlineData(@"C:\Users\test\file.bak")]          // ドットは中間に
    [InlineData(@"C:\backup\...\abc")]               // 3つドットは通常のディレクトリ名
    public void ValidateBackupPath_SafeLookalikePaths_NotFlaggedAsTraversal(string path)
    {
        var result = PathValidator.ValidateBackupPath(path);

        // IsValid は書き込み権限次第だが、traversal 固有のエラーが含まれないことを検証
        if (!result.IsValid)
        {
            // 強化版エラーメッセージ特有の語彙
            result.ErrorMessage.Should().NotContain("URL エンコード",
                $"「{path}」は通常のパスで traversal と誤判定されるべきでない");
            result.ErrorMessage.Should().NotContain("不正な文字列（..）",
                $"「{path}」は旧エラーメッセージでも traversal と判定されるべきでない");
        }
    }

    /// <summary>
    /// Issue #1268: ContainsTraversalSegment の個別パターン検証。
    /// </summary>
    [Theory]
    [InlineData(@"C:\..\Windows", true)]
    [InlineData(@"C:\backup\..\Windows", true)]
    [InlineData(@"C:\backup\.\current", false)]          // . 単独は traversal ではない
    [InlineData(@"C:\backup\...\Windows", false)]         // ... は通常のディレクトリ名
    [InlineData(@"\\server\share\..\admin", true)]
    [InlineData(@"C:/backup/../Windows", true)]
    [InlineData(@"C:\backup\subdir\file.txt", false)]
    [InlineData("", false)]
    public void ContainsTraversalSegment_DetectsCorrectly(string path, bool expected)
    {
        PathValidator.ContainsTraversalSegment(path).Should().Be(expected);
    }

    /// <summary>
    /// Issue #1268: ExtractUncRoot の動作検証。
    /// </summary>
    [Theory]
    [InlineData(@"\\server\share", @"\\server\share")]
    [InlineData(@"\\server\share\subdir", @"\\server\share")]
    [InlineData(@"\\server\share\..\other", @"\\server\share")]     // 元の入力のルートを抽出
    [InlineData(@"//server/share/subdir", @"\\server\share")]
    [InlineData(@"C:\backup", null)]                                 // 非UNC
    [InlineData(@"\\server", null)]                                  // 共有名なし
    [InlineData(@"\\", null)]                                        // 空
    public void ExtractUncRoot_ReturnsNormalizedUncRoot(string input, string expected)
    {
        PathValidator.ExtractUncRoot(input).Should().Be(expected);
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
    /// デフォルトパスがCommonApplicationData内であることを確認
    /// </summary>
    /// <remarks>
    /// バックアップは共有フォルダ（C:\ProgramData）に保存される。
    /// これにより、管理者が全ユーザーのバックアップを一元管理できる。
    /// </remarks>
    [Fact]
    public void GetDefaultBackupPath_ReturnsCommonAppDataPath()
    {
        // Act
        var result = PathValidator.GetDefaultBackupPath();

        // Assert
        result.Should().Contain("ICCardManager");
        result.Should().Contain("backup");
        result.Should().StartWith(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
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
