using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// Issue #1465: SafeFilePathValidator の単体テスト。
/// </summary>
/// <remarks>
/// 純粋関数のため I/O は伴わない。実際のファイル存在チェックは <c>SafeFileLauncher</c> 側で行う。
/// </remarks>
public class SafeFilePathValidatorTests
{
    #region ValidateFolder

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateFolder_空パス_Failure(string path)
    {
        var result = SafeFilePathValidator.ValidateFolder(path);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("空");
    }

    [Theory]
    [InlineData("C:\\Users\\foo")]
    [InlineData("C:\\Backup\\2026")]
    [InlineData("D:\\ProgramData\\ICCardManager")]
    public void ValidateFolder_通常のローカルパス_Success(string path)
    {
        var result = SafeFilePathValidator.ValidateFolder(path);

        result.IsValid.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    [Theory]
    [InlineData("\\\\server\\share")]
    [InlineData("\\\\server\\share\\backup")]
    public void ValidateFolder_UNCパス_Success(string path)
    {
        var result = SafeFilePathValidator.ValidateFolder(path);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ValidateFolder_制御文字を含む_Failure()
    {
        var pathWithNul = "C:\\foo\0bar";

        var result = SafeFilePathValidator.ValidateFolder(pathWithNul);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("制御文字");
    }

    [Fact]
    public void ValidateFolder_改行を含む_Failure()
    {
        var pathWithNewline = "C:\\foo\nbar";

        var result = SafeFilePathValidator.ValidateFolder(pathWithNewline);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("制御文字");
    }

    #endregion

    #region ValidateFile - 空・null

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateFile_空パス_Failure(string path)
    {
        var result = SafeFilePathValidator.ValidateFile(path);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("空");
    }

    #endregion

    #region ValidateFile - 拡張子ホワイトリスト

    [Theory]
    [InlineData("C:\\report.xlsx")]
    [InlineData("C:\\export.csv")]
    [InlineData("D:\\Reports\\2026-04.xlsx")]
    [InlineData("\\\\server\\share\\export.csv")]
    public void ValidateFile_許可拡張子_Success(string path)
    {
        var result = SafeFilePathValidator.ValidateFile(path);

        result.IsValid.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    [Theory]
    [InlineData("C:\\report.XLSX")]    // 大文字
    [InlineData("C:\\report.Xlsx")]    // 混在
    [InlineData("C:\\export.CSV")]
    public void ValidateFile_拡張子大文字小文字無視_Success(string path)
    {
        var result = SafeFilePathValidator.ValidateFile(path);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("C:\\evil.exe")]
    [InlineData("C:\\evil.bat")]
    [InlineData("C:\\evil.com")]
    [InlineData("C:\\evil.cmd")]
    [InlineData("C:\\evil.vbs")]
    [InlineData("C:\\evil.ps1")]
    [InlineData("C:\\evil.js")]
    [InlineData("C:\\evil.scr")]
    [InlineData("C:\\evil.lnk")]
    [InlineData("C:\\evil.msi")]
    [InlineData("C:\\evil.hta")]
    public void ValidateFile_実行可能拡張子_Failure(string path)
    {
        var result = SafeFilePathValidator.ValidateFile(path);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("開けません");
    }

    [Theory]
    [InlineData("C:\\foo.pdf")]   // Issue #1465 で意図的に除外
    [InlineData("C:\\foo.txt")]
    [InlineData("C:\\foo.docx")]
    [InlineData("C:\\foo.zip")]
    public void ValidateFile_本アプリ非生成拡張子_Failure(string path)
    {
        var result = SafeFilePathValidator.ValidateFile(path);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("開けません");
    }

    [Fact]
    public void ValidateFile_拡張子なし_Failure()
    {
        var result = SafeFilePathValidator.ValidateFile("C:\\foo");

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("拡張子");
    }

    #endregion

    #region ValidateFile - 制御文字

    [Fact]
    public void ValidateFile_制御文字を含む_Failure()
    {
        var pathWithNul = "C:\\foo\0.xlsx";

        var result = SafeFilePathValidator.ValidateFile(pathWithNul);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("制御文字");
    }

    #endregion

    #region エラーメッセージ品質（Issue #1275）

    /// <summary>
    /// Issue #1275 の「何が・なぜ・どうすれば」原則に沿って、全エラーメッセージが
    /// 行動指示型語尾で終わり、最低 20 文字以上の情報量を持つこと。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("C:\\evil.exe")]
    [InlineData("C:\\foo")]
    public void ValidateFile_エラーメッセージは20文字以上で行動指示型(string path)
    {
        var result = SafeFilePathValidator.ValidateFile(path);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Length.Should().BeGreaterOrEqualTo(20,
            "エラーメッセージは「何が／なぜ／どうすれば」の3要素を含む情報量が必要");
    }

    [Fact]
    public void AllowedFileExtensions_xlsxとcsvのみを含む()
    {
        SafeFilePathValidator.AllowedFileExtensions.Should().BeEquivalentTo(new[] { ".xlsx", ".csv" });
    }

    #endregion
}
