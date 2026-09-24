using System.IO;
using FluentAssertions;
using ICCardManager.Models;
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
[Collection(TemplateTempFileCollection.Name)]
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

    #region TemplateNotFoundException の文字列生成ロジック

    // 注: HasCorrectProperties / WithInnerException / SearchedPaths_IsReadOnly は
    // POCOレベルのgetter検証に過ぎないため削除済み。
    // GetDetailedMessage は単なるプロパティアクセスではなく string.Join を使った
    // 文字列生成ロジックを含むため、これのみ残す。

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

        // Assert — メッセージ本文・ラベル・全パスが含まれること（文字列生成ロジックの検証）
        detailedMessage.Should().Contain(message);
        detailedMessage.Should().Contain("検索したパス:");
        detailedMessage.Should().Contain("C:/app/Resources/template.xlsx");
        detailedMessage.Should().Contain("D:/templates/template.xlsx");
    }

    #endregion

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

    #region 埋め込みテンプレートの展開（Issue #2050）

    /// <summary>
    /// 同じ部署種別の展開を繰り返しても、一時ファイルは 1 つだけ作られ同じパスが返ること。
    /// 旧実装は呼ぶたびに新しい一時 .xlsx を作り、一括作成でカード枚数ぶん積み上がっていた。
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void ExtractEmbeddedTemplate_CalledRepeatedly_ReusesSingleTempFile()
    {
        var first = TemplateResolver.ExtractEmbeddedTemplate(DepartmentType.MayorOffice);
        var second = TemplateResolver.ExtractEmbeddedTemplate(DepartmentType.MayorOffice);
        var third = TemplateResolver.ExtractEmbeddedTemplate(DepartmentType.MayorOffice);

        first.Should().NotBeNullOrEmpty();
        second.Should().Be(first, "展開済みの一時テンプレートを再利用するべき");
        third.Should().Be(first);
        File.Exists(first).Should().BeTrue();
    }

    /// <summary>
    /// 部署種別が違えば別のテンプレートを返すこと（再利用が部署種別を取り違えない）。
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void ExtractEmbeddedTemplate_DifferentDepartments_ReturnDistinctTemplates()
    {
        var mayor = TemplateResolver.ExtractEmbeddedTemplate(DepartmentType.MayorOffice);
        var enterprise = TemplateResolver.ExtractEmbeddedTemplate(DepartmentType.EnterpriseAccount);
        var mayorAgain = TemplateResolver.ExtractEmbeddedTemplate(DepartmentType.MayorOffice);

        enterprise.Should().NotBe(mayor);
        mayorAgain.Should().Be(mayor, "別の部署種別を展開しても市長事務部局の展開結果は差し替わらないべき");
        File.ReadAllBytes(enterprise).Should().NotEqual(File.ReadAllBytes(mayor),
            "部署種別ごとに異なるテンプレートの内容であるべき");
    }

    /// <summary>
    /// 展開済みの一時ファイルが消えていたら、消えたパスを返さず展開し直すこと。
    /// 一時フォルダーの掃除で消えたパスを返すと、帳票作成がテンプレートを開けずに失敗する。
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void ExtractEmbeddedTemplate_CachedFileDeleted_ExtractsAgain()
    {
        var first = TemplateResolver.ExtractEmbeddedTemplate(DepartmentType.MayorOffice);
        var expectedBytes = File.ReadAllBytes(first);
        File.Delete(first);

        var second = TemplateResolver.ExtractEmbeddedTemplate(DepartmentType.MayorOffice);

        second.Should().NotBe(first);
        File.Exists(second).Should().BeTrue();
        File.ReadAllBytes(second).Should().Equal(expectedBytes);
    }

    /// <summary>
    /// 展開済みの一時ファイルの長さが埋め込みリソースと一致しなければ（途中で切れた等）、
    /// 再利用せず展開し直すこと。
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void ExtractEmbeddedTemplate_CachedFileTruncated_ExtractsAgain()
    {
        var first = TemplateResolver.ExtractEmbeddedTemplate(DepartmentType.MayorOffice);
        var expectedBytes = File.ReadAllBytes(first);
        File.WriteAllBytes(first, expectedBytes.Take(expectedBytes.Length / 2).ToArray());

        var second = TemplateResolver.ExtractEmbeddedTemplate(DepartmentType.MayorOffice);

        second.Should().NotBe(first, "長さが一致しない一時ファイルは再利用しないべき");
        File.ReadAllBytes(second).Should().Equal(expectedBytes);
    }

    /// <summary>
    /// 同時に展開しても全員が同じ完全なファイルを受け取ること。
    /// lock を外す退行はタイミング次第で緑になり得るため、その検出は保証しない。
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExtractEmbeddedTemplate_ConcurrentCalls_ShareOneCompleteFile()
    {
        // 前のテストの展開結果に依存しないよう、まず消してから競わせる
        var stale = TemplateResolver.ExtractEmbeddedTemplate(DepartmentType.EnterpriseAccount);
        var expectedBytes = File.ReadAllBytes(stale);
        File.Delete(stale);

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => TemplateResolver.ExtractEmbeddedTemplate(DepartmentType.EnterpriseAccount)))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        results.Distinct().Should().ContainSingle("同時に呼ばれても展開は 1 回だけであるべき");
        File.ReadAllBytes(results[0]).Should().Equal(expectedBytes);
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

    /// <summary>
    /// 起動時の掃除（<see cref="TemplateResolver.CleanupTempFiles"/>）が展開済みのテンプレートを消しても、
    /// 次の帳票作成では消えたパスを返さず展開し直すこと。
    /// </summary>
    /// <remarks>
    /// Issue #2108: <c>ReportServiceTests</c> にあった「帳票作成後もクリーンアップがエラーなく実行される」
    /// （<c>NotThrow</c> のみ）をここへ移した。テスト環境の帳票作成は出力フォルダーの実ファイルを使い
    /// 一時テンプレートに触れないため、あの 1 件のために <c>ReportServiceTests</c>（約 100 件）全体を
    /// 直列のコレクションに入れていた。掃除とキャッシュの関係は一時テンプレートを直接見て表明する。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public void CleanupTempFiles_展開済みテンプレートを消しても次の展開で作り直すこと()
    {
        var first = TemplateResolver.ExtractEmbeddedTemplate(DepartmentType.MayorOffice);
        var expectedBytes = File.ReadAllBytes(first);

        TemplateResolver.CleanupTempFiles();

        File.Exists(first).Should().BeFalse("掃除は展開済みの一時テンプレートを削除すること");
        var second = TemplateResolver.ExtractEmbeddedTemplate(DepartmentType.MayorOffice);
        second.Should().NotBe(first, "消えたパスをキャッシュから返さないこと");
        File.ReadAllBytes(second).Should().Equal(expectedBytes);
    }

    /// <summary>
    /// 蓄積した一時テンプレート（ICCardManager_Template_*.xlsx）を実際に削除し、
    /// プレフィックスに一致しない無関係なファイルは残すこと（Issue #1600）。
    /// </summary>
    /// <remarks>
    /// Issue #1600 で本メソッドを App.OnStartup から呼ぶようにしたため、
    /// 「削除対象だけを正しく消す」契約を回帰ガードとして固定する。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public void CleanupTempFiles_DeletesAccumulatedTemplates_ButPreservesUnrelatedFiles()
    {
        // Arrange — TemplateResolver が使う一時ディレクトリと同じ場所に
        //           「蓄積した一時テンプレート」と「無関係なファイル」を作る。
        // ※ プレフィックスは TemplateResolver.TempFilePrefix（private const）と一致させること。
        const string tempFilePrefix = "ICCardManager_Template_";
        var tempDir = Path.Combine(Path.GetTempPath(), "ICCardManager");
        Directory.CreateDirectory(tempDir);

        var accumulated = new[]
        {
            Path.Combine(tempDir, $"{tempFilePrefix}aaaaaaaaaaaa.xlsx"),
            Path.Combine(tempDir, $"{tempFilePrefix}bbbbbbbbbbbb.xlsx"),
        };
        // プレフィックス不一致（残すべき）と、拡張子不一致（残すべき）の 2 種類
        var unrelatedOtherName = Path.Combine(tempDir, "ユーザー作成ファイル.xlsx");
        var unrelatedOtherExt = Path.Combine(tempDir, $"{tempFilePrefix}cccccccccccc.txt");

        try
        {
            foreach (var f in accumulated)
            {
                File.WriteAllText(f, "dummy");
            }
            File.WriteAllText(unrelatedOtherName, "keep-me");
            File.WriteAllText(unrelatedOtherExt, "keep-me");

            // Act
            TemplateResolver.CleanupTempFiles();

            // Assert — 蓄積テンプレートは削除、無関係ファイルは残存
            foreach (var f in accumulated)
            {
                File.Exists(f).Should().BeFalse($"蓄積した一時テンプレート {Path.GetFileName(f)} は削除されるべき");
            }
            File.Exists(unrelatedOtherName).Should().BeTrue("プレフィックス不一致のファイルは残すべき");
            File.Exists(unrelatedOtherExt).Should().BeTrue("拡張子(.xlsx)不一致のファイルは残すべき");
        }
        finally
        {
            // Dispose() の CleanupTempFiles は無関係ファイルを消さないため明示的に後始末する
            foreach (var f in new[] { unrelatedOtherName, unrelatedOtherExt })
            {
                if (File.Exists(f)) File.Delete(f);
            }
        }
    }

    #endregion
}
