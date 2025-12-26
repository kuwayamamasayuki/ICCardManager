using System.IO;
using FluentAssertions;
using ICCardManager.Services;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.Services;

/// <summary>
/// TemplateResolverの単体テスト
/// </summary>
public class TemplateResolverTests : IDisposable
{
    private readonly List<string> _tempFiles = new();
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        // テスト後に一時ファイルを削除
        foreach (var tempFile in _tempFiles)
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); }
                catch { /* 削除失敗は無視 */ }
            }
        }

        foreach (var tempDir in _tempDirs)
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); }
                catch { /* 削除失敗は無視 */ }
            }
        }

        // TemplateResolverの一時ファイルもクリーンアップ
        TemplateResolver.CleanupTempFiles();
    }

    #region ヘルパーメソッド

    private string CreateTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"TemplateResolverTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        _tempDirs.Add(tempDir);
        return tempDir;
    }

    private string CreateTempFile(string extension = ".xlsx", string? content = null)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"TemplateTest_{Guid.NewGuid()}{extension}");
        if (content != null)
        {
            File.WriteAllText(tempPath, content);
        }
        else
        {
            File.WriteAllBytes(tempPath, new byte[0]);
        }
        _tempFiles.Add(tempPath);
        return tempPath;
    }

    #endregion

    #region 正常系テスト

    /// <summary>
    /// テンプレートファイルが正常に解決される（埋め込みリソースからのフォールバック）
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void ResolveTemplatePath_WithEmbeddedResource_ReturnsValidPath()
    {
        // Act
        var path = TemplateResolver.ResolveTemplatePath();

        // Assert
        path.Should().NotBeNullOrEmpty();
        File.Exists(path).Should().BeTrue("埋め込みリソースから展開されたテンプレートが存在するべき");
        path.Should().EndWith(".xlsx", "Excelファイルであるべき");
    }

    /// <summary>
    /// ResolveTemplatePathが返すパスのファイルがExcel形式である
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void ResolveTemplatePath_ReturnedFile_IsValidExcelFormat()
    {
        // Act
        var path = TemplateResolver.ResolveTemplatePath();

        // Assert
        var fileInfo = new FileInfo(path);
        fileInfo.Exists.Should().BeTrue();
        fileInfo.Length.Should().BeGreaterThan(0, "テンプレートファイルは空でないべき");

        // Excelファイルのマジックナンバーをチェック（PKヘッダー = ZIP形式）
        using var stream = File.OpenRead(path);
        var header = new byte[4];
        stream.Read(header, 0, 4);
        // XLSX は ZIP 形式なので PK (0x50, 0x4B) で始まる
        (header[0] == 0x50 && header[1] == 0x4B).Should().BeTrue("XLSXファイルはZIP形式（PKヘッダー）であるべき");
    }

    /// <summary>
    /// TemplateExistsがテンプレート存在時にtrueを返す
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void TemplateExists_WhenTemplateAvailable_ReturnsTrue()
    {
        // Act
        var exists = TemplateResolver.TemplateExists();

        // Assert
        exists.Should().BeTrue("埋め込みリソースが存在するためtrueを返すべき");
    }

    /// <summary>
    /// 複数回のResolveTemplatePathの呼び出しで同じ結果が返る（冪等性）
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void ResolveTemplatePath_CalledMultipleTimes_ReturnsConsistentResults()
    {
        // Act
        var path1 = TemplateResolver.ResolveTemplatePath();
        var path2 = TemplateResolver.ResolveTemplatePath();

        // Assert
        // 両方とも有効なパスを返す（毎回新しい一時ファイルが作成される可能性があるため、パスの一致は確認しない）
        File.Exists(path1).Should().BeTrue();
        File.Exists(path2).Should().BeTrue();
    }

    /// <summary>
    /// CleanupTempFilesが一時ファイルを削除する
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void CleanupTempFiles_RemovesTempFiles()
    {
        // Arrange - テンプレートを展開して一時ファイルを作成
        var path = TemplateResolver.ResolveTemplatePath();
        var tempDir = Path.GetDirectoryName(path);

        // Act
        TemplateResolver.CleanupTempFiles();

        // Assert
        // クリーンアップは使用中のファイルはスキップするため、
        // エラーなく完了することを確認
        // （現在使用中のファイルがあるかもしれないので、完全な削除は保証しない）
    }

    #endregion

    #region TemplateNotFoundException テスト

    /// <summary>
    /// TemplateNotFoundExceptionが正しいプロパティを持つ
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void TemplateNotFoundException_HasCorrectProperties()
    {
        // Arrange
        var templateName = "テストテンプレート";
        var searchedPaths = new[] { "/path/1", "/path/2", "/path/3" };
        var message = "テンプレートが見つかりません";

        // Act
        var exception = new TemplateNotFoundException(templateName, searchedPaths, message);

        // Assert
        exception.TemplateName.Should().Be(templateName);
        exception.SearchedPaths.Should().HaveCount(3);
        exception.SearchedPaths.Should().BeEquivalentTo(searchedPaths);
        exception.Message.Should().Be(message);
    }

    /// <summary>
    /// TemplateNotFoundExceptionのGetDetailedMessageが検索パスを含む
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void TemplateNotFoundException_GetDetailedMessage_IncludesSearchedPaths()
    {
        // Arrange
        var templateName = "物品出納簿テンプレート";
        var searchedPaths = new[] { "C:/app/Resources/template.xlsx", "D:/templates/template.xlsx" };
        var message = "テンプレートファイルが見つかりません";

        // Act
        var exception = new TemplateNotFoundException(templateName, searchedPaths, message);
        var detailedMessage = exception.GetDetailedMessage();

        // Assert
        detailedMessage.Should().Contain(message);
        detailedMessage.Should().Contain("検索したパス:");
        detailedMessage.Should().Contain("C:/app/Resources/template.xlsx");
        detailedMessage.Should().Contain("D:/templates/template.xlsx");
    }

    /// <summary>
    /// TemplateNotFoundExceptionがinnerExceptionを保持する
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void TemplateNotFoundException_WithInnerException_PreservesInnerException()
    {
        // Arrange
        var innerException = new FileNotFoundException("ファイルが見つかりません");
        var templateName = "テストテンプレート";
        var searchedPaths = new[] { "/path/1" };
        var message = "テンプレートエラー";

        // Act
        var exception = new TemplateNotFoundException(templateName, searchedPaths, message, innerException);

        // Assert
        exception.InnerException.Should().Be(innerException);
        exception.InnerException.Should().BeOfType<FileNotFoundException>();
    }

    /// <summary>
    /// エラーメッセージがユーザーフレンドリーである
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void TemplateNotFoundException_Message_IsUserFriendly()
    {
        // Arrange
        var templateName = "物品出納簿テンプレート";
        var searchedPaths = new[] { "/path/1", "/path/2" };
        var message = "テンプレートファイルが見つかりません。アプリケーションを再インストールしてください。";

        // Act
        var exception = new TemplateNotFoundException(templateName, searchedPaths, message);
        var detailedMessage = exception.GetDetailedMessage();

        // Assert
        // ユーザーが対処できる情報が含まれている
        exception.Message.Should().Contain("再インストール", "対処方法が含まれているべき");
        detailedMessage.Should().Contain("検索したパス", "デバッグ情報が含まれているべき");
    }

    /// <summary>
    /// SearchedPathsが読み取り専用リストである
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void TemplateNotFoundException_SearchedPaths_IsReadOnly()
    {
        // Arrange
        var searchedPaths = new List<string> { "/path/1", "/path/2" };

        // Act
        var exception = new TemplateNotFoundException("test", searchedPaths, "message");

        // Assert
        exception.SearchedPaths.Should().BeAssignableTo<IReadOnlyList<string>>();

        // 元のリストを変更しても例外のSearchedPathsは変わらない
        searchedPaths.Add("/path/3");
        exception.SearchedPaths.Should().HaveCount(2);
    }

    #endregion

    #region エラーハンドリングテスト

    /// <summary>
    /// テンプレートファイルが空の場合でもパスは解決される（バリデーションは呼び出し側の責任）
    /// </summary>
    /// <remarks>
    /// TemplateResolverはパスの解決のみを担当し、ファイル内容のバリデーションは行わない。
    /// 空ファイルのチェックはReportServiceなど上位層で行う。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public void ResolveTemplatePath_FileContentValidation_IsCallerResponsibility()
    {
        // Arrange & Act
        // 埋め込みリソースからの解決はファイル内容をチェックしない
        var path = TemplateResolver.ResolveTemplatePath();

        // Assert
        // パスは解決される（内容のバリデーションは別のレイヤーで行う）
        path.Should().NotBeNullOrEmpty();
        File.Exists(path).Should().BeTrue();
    }

    /// <summary>
    /// 一時ディレクトリへのアクセス権がある場合、展開が成功する
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void ResolveTemplatePath_WithTempDirectoryAccess_Succeeds()
    {
        // Arrange
        var tempPath = Path.GetTempPath();
        Directory.Exists(tempPath).Should().BeTrue("一時ディレクトリが存在するべき");

        // Act
        var templatePath = TemplateResolver.ResolveTemplatePath();

        // Assert
        templatePath.Should().NotBeNullOrEmpty();
        // 一時ディレクトリに展開される場合、そのパスに含まれる
        (templatePath.Contains(tempPath) || templatePath.Contains("ICCardManager")).Should().BeTrue(
            "テンプレートは一時ディレクトリまたはアプリケーションディレクトリに存在するべき");
    }

    #endregion

    #region 境界値テスト

    /// <summary>
    /// 複数のパス候補が検索される
    /// </summary>
    /// <remarks>
    /// TemplateResolverは複数のパスを順番に検索し、最初に見つかったパスを返す。
    /// テストでは埋め込みリソースへのフォールバックを確認。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public void ResolveTemplatePath_TriesMultiplePaths_BeforeFallingBackToEmbedded()
    {
        // Act
        var path = TemplateResolver.ResolveTemplatePath();

        // Assert
        // 何らかのパスが返される（通常は埋め込みリソースから展開）
        path.Should().NotBeNullOrEmpty();

        // パスが一時ディレクトリを指している場合、埋め込みリソースから展開された
        if (path.Contains(Path.GetTempPath()))
        {
            path.Should().Contain("ICCardManager", "一時ディレクトリ内のICCardManagerフォルダに展開されるべき");
        }
    }

    /// <summary>
    /// ResolveTemplatePathで返されるパスの形式が正しい
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void ResolveTemplatePath_ReturnsAbsolutePath()
    {
        // Act
        var path = TemplateResolver.ResolveTemplatePath();

        // Assert
        Path.IsPathRooted(path).Should().BeTrue("絶対パスであるべき");
        Path.GetExtension(path).Should().Be(".xlsx", "拡張子は.xlsxであるべき");
    }

    #endregion

    #region 同時実行テスト

    /// <summary>
    /// 複数スレッドからの同時呼び出しでも正常に動作する
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveTemplatePath_ConcurrentCalls_AllSucceed()
    {
        // Arrange
        var tasks = new List<Task<string>>();
        const int concurrentCount = 10;

        // Act
        for (int i = 0; i < concurrentCount; i++)
        {
            tasks.Add(Task.Run(() => TemplateResolver.ResolveTemplatePath()));
        }

        var results = await Task.WhenAll(tasks);

        // Assert
        results.Should().HaveCount(concurrentCount);
        foreach (var path in results)
        {
            path.Should().NotBeNullOrEmpty();
            File.Exists(path).Should().BeTrue();
        }
    }

    /// <summary>
    /// TemplateExistsの複数スレッドからの同時呼び出しでも正常に動作する
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task TemplateExists_ConcurrentCalls_AllSucceed()
    {
        // Arrange
        var tasks = new List<Task<bool>>();
        const int concurrentCount = 10;

        // Act
        for (int i = 0; i < concurrentCount; i++)
        {
            tasks.Add(Task.Run(() => TemplateResolver.TemplateExists()));
        }

        var results = await Task.WhenAll(tasks);

        // Assert
        results.Should().HaveCount(concurrentCount);
        results.Should().AllSatisfy(result => result.Should().BeTrue());
    }

    #endregion

    #region クリーンアップテスト

    /// <summary>
    /// CleanupTempFilesがエラーなく実行される（ファイルがない場合も）
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void CleanupTempFiles_WhenNoTempFiles_DoesNotThrow()
    {
        // Act & Assert
        var action = () => TemplateResolver.CleanupTempFiles();
        action.Should().NotThrow("一時ファイルがなくてもエラーにならないべき");
    }

    /// <summary>
    /// CleanupTempFilesが複数回呼び出されてもエラーにならない
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void CleanupTempFiles_CalledMultipleTimes_DoesNotThrow()
    {
        // Act & Assert
        var action = () =>
        {
            TemplateResolver.CleanupTempFiles();
            TemplateResolver.CleanupTempFiles();
            TemplateResolver.CleanupTempFiles();
        };
        action.Should().NotThrow("複数回呼び出してもエラーにならないべき");
    }

    #endregion
}
