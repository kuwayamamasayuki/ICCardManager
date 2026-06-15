using System;
using System.IO;
using System.Reflection;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// Issue #1471: <see cref="PathValidator"/> のエラーメッセージ品質を検証する専用テスト。
/// 「何が問題か」「なぜ問題か」「どうすれば解決するか」の3要素が含まれることを保証する。
/// </summary>
/// <remarks>
/// Issue #1275 で <see cref="Services.ValidationService"/> に対して導入した
/// <c>ValidationServiceErrorMessageQualityTests</c> と同じ品質基準を <see cref="PathValidator"/>
/// にも適用する follow-up 対応。
/// </remarks>
public class PathValidatorErrorMessageQualityTests : IDisposable
{
    private readonly string _testDirectory;

    public PathValidatorErrorMessageQualityTests()
    {
        _testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"PathValidatorQualityTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// エラーメッセージの最小品質基準: 一定の長さがあり、句点を含み、
    /// 最後に「してください」相当の行動指示を持つ。
    /// </summary>
    private static void AssertQualityCriteria(string message)
    {
        message.Should().NotBeNullOrWhiteSpace("エラーメッセージは空であってはならない");
        message.Length.Should().BeGreaterThanOrEqualTo(20,
            "エラーメッセージは十分な説明を含むべき（最低20文字）");
        message.Should().Contain("。",
            "メッセージは句点で複数の要素を分離すべき");
        message.Should().MatchRegex(
            "してください。?$|入力してください。?$|選択してください。?$|設定してください。?$",
            "メッセージは行動指示（～してください）で終わるべき");
    }

    [Fact]
    public void ValidateBackupPath_Empty_MeetsQualityAndIncludesExample()
    {
        var result = PathValidator.ValidateBackupPath(string.Empty);

        result.IsValid.Should().BeFalse();
        AssertQualityCriteria(result.ErrorMessage);
        result.ErrorMessage.Should().Contain("指定されていません",
            "何が問題か: パス未指定");
        result.ErrorMessage.Should().ContainAny(@"C:\", @"\\",
            "どう解決か: ローカル/UNC のいずれかの形式例を示す");
    }

    [Fact]
    public void ValidateBackupPath_TooLong_MeetsQualityAndIncludesActualLength()
    {
        var longPath = @"C:\" + new string('a', 300);

        var result = PathValidator.ValidateBackupPath(longPath);

        result.IsValid.Should().BeFalse();
        AssertQualityCriteria(result.ErrorMessage);
        result.ErrorMessage.Should().Contain(longPath.Length.ToString(),
            "実際の入力長を含む");
        result.ErrorMessage.Should().Contain("260",
            "上限値を含む");
        result.ErrorMessage.Should().Contain("長すぎます",
            "なぜ問題か: 長さ超過");
    }

    [Fact]
    public void ValidateBackupPath_InvalidCharacters_MeetsQualityAndListsForbiddenChars()
    {
        var result = PathValidator.ValidateBackupPath("C:\\backup<test");

        result.IsValid.Should().BeFalse();
        AssertQualityCriteria(result.ErrorMessage);
        result.ErrorMessage.Should().Contain("使用できない文字",
            "何が問題か: 不正文字");
        result.ErrorMessage.Should().ContainAny("<", ">", "|", "?", "*", "\"",
            "どう解決か: 具体的な予約文字例");
    }

    [Fact]
    public void ValidateBackupPath_RelativePath_MeetsQualityAndIncludesExample()
    {
        var result = PathValidator.ValidateBackupPath("backup/folder");

        result.IsValid.Should().BeFalse();
        AssertQualityCriteria(result.ErrorMessage);
        result.ErrorMessage.Should().Contain("絶対パス",
            "何が問題か: 絶対パスでない");
        result.ErrorMessage.Should().Contain(@"C:\",
            "どう解決か: ローカルパスの形式例");
        result.ErrorMessage.Should().Contain(@"\\",
            "どう解決か: UNCパスの形式例");
    }

    [Fact]
    public void ValidateBackupPath_UncMissingShare_MeetsQualityAndIncludesExample()
    {
        var result = PathValidator.ValidateBackupPath(@"\\server");

        result.IsValid.Should().BeFalse();
        AssertQualityCriteria(result.ErrorMessage);
        result.ErrorMessage.Should().Contain("サーバー名と共有名",
            "何が問題か: 必須要素の欠落");
        result.ErrorMessage.Should().Contain(@"\\server\share",
            "どう解決か: 形式例");
    }

    [Fact]
    public void ValidateBackupPath_UncEmptyServer_MeetsQuality()
    {
        // \\\\\share のように空のサーバー名で始める
        var result = PathValidator.ValidateBackupPath(@"\\ \share");

        result.IsValid.Should().BeFalse();
        AssertQualityCriteria(result.ErrorMessage);
        result.ErrorMessage.Should().Contain("サーバー名",
            "何が問題か: サーバー名関連");
        result.ErrorMessage.Should().ContainAny("ホスト名", "IP",
            "どう解決か: 具体的な指定方法");
    }

    [Fact]
    public void ValidateBackupPath_UncEmptyShare_MeetsQuality()
    {
        var result = PathValidator.ValidateBackupPath(@"\\server\ ");

        result.IsValid.Should().BeFalse();
        AssertQualityCriteria(result.ErrorMessage);
        result.ErrorMessage.Should().Contain("共有名",
            "何が問題か: 共有名関連");
        result.ErrorMessage.Should().Contain(@"\\",
            "どう解決か: 形式例");
    }

    [Fact]
    public void ValidateBackupPath_PathTraversal_MeetsQuality()
    {
        var result = PathValidator.ValidateBackupPath(@"C:\backup\..\Windows\System32");

        result.IsValid.Should().BeFalse();
        AssertQualityCriteria(result.ErrorMessage);
        result.ErrorMessage.Should().Contain("..",
            "何が問題か: トラバーサル指定");
        result.ErrorMessage.Should().Contain("親ディレクトリ",
            "なぜ問題か: 親ディレクトリへの移動");
    }

    [Fact]
    public void ValidateBackupPath_UnreachableUnc_MeetsQuality()
    {
        // 到達性チェッカーを「常に false」のスタブに差し替えて UNC 到達不可エラーを再現する
        var method = typeof(PathValidator).GetMethod(
            "ValidateBackupPath",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(string), typeof(Func<string, int, bool>), typeof(int) },
            modifiers: null);

        method.Should().NotBeNull("internal テスト用オーバーロードが存在するべき");

        var result = (PathValidator.ValidationResult)method!.Invoke(
            null,
            new object[] { @"\\nonexistent-test-server\share", (Func<string, int, bool>)((_, _) => false), 5000 });

        result.IsValid.Should().BeFalse();
        AssertQualityCriteria(result.ErrorMessage);
        result.ErrorMessage.Should().Contain("到達できません",
            "何が問題か: 到達不可");
        result.ErrorMessage.Should().Contain("ネットワーク接続",
            "どう解決か: 確認項目を示す");
    }

    // =========================================================================
    // ValidatePathFormat（Issue #1599）のメッセージ品質
    // =========================================================================

    [Fact]
    public void ValidatePathFormat_RelativePath_MeetsQualityAndIncludesExample()
    {
        var result = PathValidator.ValidatePathFormat(@"relative\folder\iccard.db");

        result.IsValid.Should().BeFalse();
        AssertQualityCriteria(result.ErrorMessage);
        result.ErrorMessage.Should().Contain("絶対パス",
            "何が問題か: 絶対パスでない");
        result.ErrorMessage.Should().Contain(@"C:\",
            "どう解決か: ローカルパスの形式例");
        result.ErrorMessage.Should().Contain(@"\\",
            "どう解決か: UNCパスの形式例");
    }

    [Fact]
    public void ValidatePathFormat_Empty_MeetsQualityAndIncludesExample()
    {
        var result = PathValidator.ValidatePathFormat(string.Empty);

        result.IsValid.Should().BeFalse();
        AssertQualityCriteria(result.ErrorMessage);
        result.ErrorMessage.Should().Contain("指定されていません",
            "何が問題か: パス未指定");
        result.ErrorMessage.Should().ContainAny(@"C:\", @"\\",
            "どう解決か: 形式例を示す");
    }

    [Fact]
    public void ValidatePathFormat_InvalidChars_MeetsQuality()
    {
        var result = PathValidator.ValidatePathFormat("C:\\folder|name\\iccard.db");

        result.IsValid.Should().BeFalse();
        AssertQualityCriteria(result.ErrorMessage);
        result.ErrorMessage.Should().Contain("使用できない文字",
            "何が問題か: 不正文字");
    }

    [Fact]
    public void ValidatePathFormat_PathTraversal_MeetsQuality()
    {
        var result = PathValidator.ValidatePathFormat(@"C:\folder\..\other\iccard.db");

        result.IsValid.Should().BeFalse();
        AssertQualityCriteria(result.ErrorMessage);
        result.ErrorMessage.Should().Contain("親ディレクトリ",
            "なぜ問題か: 親ディレクトリへの移動");
    }
}
