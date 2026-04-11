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
    public void Dispose()
    {
        // TemplateResolverの一時ファイルをクリーンアップ
        TemplateResolver.CleanupTempFiles();
    }

    #region 正常系テスト

    /// <summary>
    /// テンプレートファイルが正常に解決され、有効なExcelファイルであること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void ResolveTemplatePath_WithEmbeddedResource_ReturnsValidExcelFile()
    {
        // Act
        var path = TemplateResolver.ResolveTemplatePath();

        // Assert
        path.Should().NotBeNullOrEmpty();
        Path.IsPathRooted(path).Should().BeTrue("絶対パスであるべき");
        Path.GetExtension(path).Should().Be(".xlsx", "Excelファイルであるべき");
        File.Exists(path).Should().BeTrue("埋め込みリソースから展開されたテンプレートが存在するべき");

        // Excelファイルのマジックナンバーをチェック（PKヘッダー = ZIP形式）
        var fileInfo = new FileInfo(path);
        fileInfo.Length.Should().BeGreaterThan(0, "テンプレートファイルは空でないべき");
        using var stream = File.OpenRead(path);
        var header = new byte[4];
        stream.Read(header, 0, 4);
        (header[0] == 0x50 && header[1] == 0x4B).Should().BeTrue("XLSXファイルはZIP形式（PKヘッダー）であるべき");
    }

    /// <summary>
    /// TemplateExistsがテンプレート存在時にtrueを返す
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void TemplateExists_WhenTemplateAvailable_ReturnsTrue()
    {
        // Act & Assert
        TemplateResolver.TemplateExists().Should().BeTrue("埋め込みリソースが存在するためtrueを返すべき");
    }

    #endregion

    // 注: TemplateNotFoundException のプロパティテスト(HasCorrectProperties/GetDetailedMessage/
    // WithInnerException/SearchedPaths_IsReadOnly) はPOCOレベルの検証に過ぎないため削除

    #region 同時実行テスト

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

    #endregion

    #region クリーンアップテスト

    [Fact]
    [Trait("Category", "Unit")]
    public void CleanupTempFiles_WhenNoTempFiles_DoesNotThrow()
    {
        // Act & Assert
        var action = () => TemplateResolver.CleanupTempFiles();
        action.Should().NotThrow("一時ファイルがなくてもエラーにならないべき");
    }

    #endregion
}
