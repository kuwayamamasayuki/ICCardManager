using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// Issue #2261: エラーログの記録で、ログフォルダーへ <c>BUILTIN\Users : FullControl</c> を付与しないこと。
/// </summary>
/// <remarks>
/// <para>
/// <c>ErrorDialogHelper</c> はエラーを記録するたびにログフォルダーへ Users のフルコントロール（継承あり）を
/// <c>AddAccessRule</c> していた。<c>DbContext</c> の同じ付与は Issue #1455 で撤廃済みで、片側だけ残っていた。
/// フルコントロールはアクセス許可の変更（WRITE_DAC）と所有権の取得を含み、一般ユーザーがフォルダーを
/// ジャンクションへ差し替えて昇格したインストーラーの書き込み・削除を任意の場所へ向ける足掛かりになる。
/// </para>
/// <para>
/// 対で表明する: ①記録した後のログフォルダーに、Users へのアクセス許可の変更・所有権の取得を含む明示的な ACE が無いこと、
/// ②ログ自体は従来どおり書かれること（ACL の付与ごと記録を消した実装を緑にしない）、③繰り返し記録しても
/// 明示的な ACE が増えないこと。ログフォルダーはテストのアセンブリが一時フォルダーへ差し替えている（#2098）。
/// 加えて、本番コードのどこにもフルコントロール等を付与する記述が無いことを静的検査で固定する
/// （ログフォルダー以外の場所で同じ付与が再発しても検出する）。
/// </para>
/// </remarks>
public class ErrorDialogHelperLogDirectoryAclTests
{
    private static readonly SecurityIdentifier BuiltinUsers = new(WellKnownSidType.BuiltinUsersSid, null);

    /// <summary>アクセス許可の変更・所有権の取得（フルコントロールと「変更」の差）。</summary>
    private const FileSystemRights DangerousRights = FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private static readonly Regex ForbiddenRights = new(
        @"FileSystemRights\.(?<right>FullControl|ChangePermissions|TakeOwnership)\b",
        RegexOptions.Compiled);

    [Fact]
    public void LogException_ログフォルダーのUsersにアクセス許可の変更や所有権の取得を付与しないこと()
    {
        ErrorDialogHelper.LogException(new InvalidOperationException("Issue #2261 ACL の検査"), "ACL の検査");

        var users = ExplicitAllowRulesForUsers(ErrorDialogHelper.LogDirectory);
        users.Where(r => (r.FileSystemRights & DangerousRights) != 0)
            .Select(r => r.FileSystemRights.ToString())
            .Should().BeEmpty("Users にフルコントロール（WRITE_DAC・所有権の取得）を付与しない。書き込み権限はインストーラーの users-modify が与える");
    }

    [Fact]
    public void LogException_ログファイルへは従来どおり記録すること()
    {
        var marker = $"Issue2261-{Guid.NewGuid():N}";

        ErrorDialogHelper.LogException(new InvalidOperationException(marker), "ACL の検査");

        var logFile = Path.Combine(
            ErrorDialogHelper.LogDirectory,
            $"error_{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");
        File.Exists(logFile).Should().BeTrue("ログフォルダーを作って記録する");
        File.ReadAllText(logFile).Should().Contain(marker);
    }

    [Fact]
    public void LogException_繰り返し記録しても明示的なACEが増えないこと()
    {
        ErrorDialogHelper.LogException(new InvalidOperationException("Issue #2261 1 回目"), "ACL の検査");
        var before = ExplicitRuleCount(ErrorDialogHelper.LogDirectory);

        for (var i = 0; i < 3; i++)
        {
            ErrorDialogHelper.LogException(new InvalidOperationException("Issue #2261 繰り返し"), "ACL の検査");
        }

        ExplicitRuleCount(ErrorDialogHelper.LogDirectory).Should().Be(before, "記録のたびに ACL を変更しない");
    }

    [Fact]
    public void 本番コードはファイルシステムのフルコントロール等を付与する記述を持たないこと()
    {
        var root = TestPaths.GetProductionSourceRoot();
        var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        // 空振り防止: 本番コードを走査できていること（ErrorDialogHelper を含む）
        files.Should().Contain(f => f.EndsWith("ErrorDialogHelper.cs", StringComparison.Ordinal));

        var violations = files
            .SelectMany(f => FindForbiddenRights(File.ReadAllText(f)).Select(v => $"{Path.GetFileName(f)}: {v}"))
            .ToList();
        violations.Should().BeEmpty("全ユーザーで共有するフォルダーの権限はインストーラーが Users の「変更」で与える（Issue #1455 / #2261）");
    }

    [Fact]
    public void 検査ロジック_コードの付与を検出しコメントと文字列の記述は数えないこと()
    {
        var source = string.Join("\n", new[]
        {
            "// FileSystemRights.FullControl は付与しない（Issue #1455）",
            "/// <c>FileSystemRights.TakeOwnership</c> の説明",
            "var s = \"FileSystemRights.ChangePermissions\";",
            "var rule = new FileSystemAccessRule(users, FileSystemRights.FullControl, AccessControlType.Allow);",
            "var other = FileSystemRights.Modify | FileSystemRights.TakeOwnership;",
        });

        FindForbiddenRights(source).Should().Equal("FullControl", "TakeOwnership");
    }

    private static string[] FindForbiddenRights(string source)
    {
        var code = StripStringLiterals(DomainBoundaryConventionTests.StripComments(source));
        return ForbiddenRights.Matches(code).Cast<Match>().Select(m => m.Groups["right"].Value).ToArray();
    }

    /// <summary>通常の文字列リテラル（<c>\</c> でエスケープ）の中身を除く。コメントは除去済みであること。</summary>
    private static string StripStringLiterals(string code)
        => Regex.Replace(code, @"""(?:\\.|[^""\\\n])*""", "\"\"");

    private static FileSystemAccessRule[] ExplicitAllowRulesForUsers(string directory)
        => new DirectoryInfo(directory).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(r => r.IdentityReference.Equals(BuiltinUsers) && r.AccessControlType == AccessControlType.Allow)
            .ToArray();

    private static int ExplicitRuleCount(string directory)
        => new DirectoryInfo(directory).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Count;
}
