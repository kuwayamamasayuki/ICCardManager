using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Infrastructure;

/// <summary>
/// Issue #2261: インストーラーがデータフォルダー（<c>C:\ProgramData\ICCardManager</c>）へ Users のフルコントロールを付けず、
/// 昇格中の書き込み・削除の前に再解析ポイント（ジャンクション・シンボリックリンク）を確かめること。
/// </summary>
/// <remarks>
/// <para>
/// インストーラー（Inno Setup の <c>.iss</c>）はテストから実行できないため、スクリプトのテキストを静的に検査する。
/// 対で表明する: ①フルコントロール（<c>users-full</c>・icacls の <c>F</c>）が無いことと、3 つのデータフォルダーが
/// <c>users-modify</c> で作られること、②既存のフォルダーの所有者を固定して ACL を作り直す処理が
/// <c>PrepareToInstall</c> から呼ばれること（<c>[Dirs]</c> の Permissions はアクセス許可を追加するだけで、
/// 旧版が付けた Users:F を外さない）、③データフォルダーへ書き込み・削除する手続きの各呼び出しが、
/// 同じパスについての再解析ポイントの確認を直前に持つこと。
/// </para>
/// <para>
/// ③の走査対象は手続きの名前で列挙せず、本体が <c>{commonappdata}\ICCardManager</c> を参照する手続きから導出する
/// （手続きが増えても漏れない。#1786）。導出が空振りしていないことは既知の 4 手続きが含まれることで表明し、
/// 検査ロジック自体はサンプル入力で固定する。
/// </para>
/// </remarks>
public class InstallerDataDirectoryAclConventionTests
{
    private static readonly string[] DataDirectories =
    {
        @"{commonappdata}\ICCardManager",
        @"{commonappdata}\ICCardManager\backup",
        @"{commonappdata}\ICCardManager\Logs",
    };

    /// <summary>昇格中にファイル・フォルダーを書き換え・削除する Inno Setup のサポート関数。</summary>
    private static readonly Regex DestructiveCall = new(
        @"\b(?<fn>SaveStringToFile|SaveStringsToFile|SaveStringsToUTF8File|DeleteFile|DelTree|RemoveDir|RenameFile|FileCopy|DelayDeleteFile)\s*\(\s*(?<arg>\w+)",
        RegexOptions.Compiled);

    /// <summary>再解析ポイントを確かめるガード（引数のパスについて確かめる）。</summary>
    private static readonly Regex GuardCall = new(
        @"\b(?:IsDataPathSafe|IsDataTreeSafeToDelete)\s*\(\s*(?<arg>\w+)\s*\)",
        RegexOptions.Compiled);

    private static readonly Regex RoutineHeader = new(
        @"^(?:procedure|function)\s+(?<name>\w+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DirsEntry = new(
        @"^Name:\s*""(?<name>[^""]+)""\s*;\s*Permissions:\s*(?<perm>[\w\- ]+?)\s*$",
        RegexOptions.Compiled);

    [Fact]
    public void データフォルダーの3つをusers_modifyで作ること()
    {
        var entries = ParseDirs(ReadInstallerLines());

        foreach (var dir in DataDirectories)
        {
            entries.Should().ContainKey(dir, $"[Dirs] で {dir} を作ること");
            entries[dir].Should().Be("users-modify", $"{dir} は Users の「変更」で作る（フルコントロールにしない）");
        }
    }

    [Fact]
    public void インストーラーはどこにもフルコントロールを付与しないこと()
    {
        var lines = ReadInstallerLines();

        // [Dirs] / [Files] 等の Permissions（コメント行を除く）
        lines.Select(l => l.Trim())
            .Where(l => !l.StartsWith(";", StringComparison.Ordinal))
            .Where(l => Regex.IsMatch(l, @"\bPermissions:\s*[^;]*\b[\w]+-full\b", RegexOptions.IgnoreCase))
            .Should().BeEmpty("Permissions に *-full（フルコントロール）を使わない。WRITE_DAC と所有権の取得はアプリに不要");

        // [Code] の icacls の付与（文字列リテラルに書かれる）
        var code = StripPascalComments(ReadCodeSection(lines), keepLiterals: true);
        Regex.Matches(code, @"/grant(?::r)?\s+[^'\s]*:(?:\((?:OI|CI|IO|NP)\))*\(?F\b")
            .Cast<Match>().Select(m => m.Value)
            .Should().BeEmpty("icacls でフルコントロール（F）を付与しない");
    }

    [Fact]
    public void PrepareToInstallで既存のデータフォルダーの所有者を固定しACLを作り直すこと()
    {
        var routines = SplitRoutines(StripPascalComments(ReadCodeSection(ReadInstallerLines()), keepLiterals: true));

        routines.Should().ContainKey("PrepareToInstall", "インストール開始前（[Dirs] の処理より前）に ACL を作り直す");
        routines["PrepareToInstall"].Should().Contain("HardenDataDirectory(");

        routines.Should().ContainKey("HardenDataDirectory");
        var body = routines["HardenDataDirectory"];

        // 所有者を Administrators に固定 → 明示的な ACE を消して継承へ戻す → Users に「変更」を付与
        var setOwner = body.IndexOf("/setowner *S-1-5-32-544", StringComparison.Ordinal);
        var reset = body.IndexOf("/reset", StringComparison.Ordinal);
        var grant = body.IndexOf("/grant:r *S-1-5-32-545:(OI)(CI)M", StringComparison.Ordinal);
        setOwner.Should().BeGreaterThan(-1, "既存のフォルダーの所有者を Administrators に固定する（事前に作られたフォルダー対策）");
        reset.Should().BeGreaterThan(setOwner, "所有者を固定してから ACL を継承へ戻す（旧版の Users:F を外す）");
        grant.Should().BeGreaterThan(reset, "継承へ戻した後で Users に「変更」を付与する");

        // 配下まで（backup・Logs・DB・設定ファイル）作り直す
        Regex.IsMatch(body, @"\\\*"" /setowner \*S-1-5-32-544 /T").Should().BeTrue("配下の所有者も固定する");
        Regex.IsMatch(body, @"\\\*"" /reset /T").Should().BeTrue("配下の ACL も継承へ戻す（Logs に付いた Users:F を外す）");

        // icacls より前に再解析ポイントを確かめる（ジャンクションの先の ACL を書き換えない）
        var pathCheck = body.IndexOf("DataPathHasReparsePoint(", StringComparison.Ordinal);
        var treeCheck = body.IndexOf("TreeHasReparsePoint(", StringComparison.Ordinal);
        var recursive = body.IndexOf(" /T ", StringComparison.Ordinal);
        pathCheck.Should().BeGreaterThan(-1).And.BeLessThan(setOwner, "フォルダー自体の再解析ポイントを icacls より前に確かめる");
        treeCheck.Should().BeGreaterThan(-1).And.BeLessThan(recursive, "配下の再解析ポイントを再帰の icacls より前に確かめる");
    }

    [Fact]
    public void データフォルダーへ書き込み削除する手続きの各呼び出しは同じパスの再解析ポイントを先に確かめること()
    {
        var violations = FindUnguardedDestructiveCalls(ReadCodeSection(ReadInstallerLines()), out var checkedRoutines);

        // 空振り防止: 既知の書き込み・削除の手続きを走査できていること
        checkedRoutines.Should().Contain(new[]
        {
            "WriteDepartmentConfig", "WriteDatabaseConfig", "WriteReportOutputConfig", "CurUninstallStepChanged",
        });
        violations.Should().BeEmpty("昇格中の書き込み・削除は、IsDataPathSafe / IsDataTreeSafeToDelete で同じパスを確かめてから行う");
    }

    [Fact]
    public void 検査ロジック_確認の無い呼び出しと別のパスや後ろにある確認を違反として報告すること()
    {
        var code = string.Join("\n", new[]
        {
            "procedure Guarded();",
            "begin",
            "  F := ExpandConstant('{commonappdata}\\ICCardManager') + '\\a.txt';",
            "  if IsDataPathSafe(F) and FileExists(F) then",
            "    DeleteFile(F);",
            "  if not IsDataPathSafe(F) then Exit;",
            "  SaveStringToFile(F, 'x', False);",
            "end;",
            "",
            "procedure Unguarded();",
            "begin",
            "  F := ExpandConstant('{commonappdata}\\ICCardManager') + '\\b.txt';",
            "  // IsDataPathSafe(F) とコメントに書いただけ",
            "  SaveStringToFile(F, 'x', False);",
            "  if IsDataPathSafe(G) then DeleteFile(F);",
            "  DelTree(F, True, True, True);",
            "  IsDataTreeSafeToDelete(F);",
            "end;",
            "",
            "procedure ReusedGuard();",
            "begin",
            "  F := ExpandConstant('{commonappdata}\\ICCardManager');",
            "  if IsDataPathSafe(F) then",
            "    SaveStringToFile(F, 'x', False);",
            "  DeleteFile(F);",
            "end;",
            "",
            "procedure OutsideDataDir();",
            "begin",
            "  DeleteFile(ExpandConstant('{tmp}\\c.txt'));",
            "end;",
        });

        var violations = FindUnguardedDestructiveCalls(code, out var checkedRoutines);

        checkedRoutines.Should().BeEquivalentTo(new[] { "Guarded", "Unguarded", "ReusedGuard" },
            "データフォルダーを参照しない手続きは対象外");
        violations.Should().BeEquivalentTo(new[]
        {
            "Unguarded: SaveStringToFile(F",   // コメントの中の確認は数えない
            "Unguarded: DeleteFile(F",         // 別のパスの確認は数えない
            "Unguarded: DelTree(F",            // 呼び出しの後ろの確認は数えない
            "ReusedGuard: DeleteFile(F",       // 1 つの確認を 2 つの呼び出しで使い回さない
        });
    }

    /// <summary>
    /// データフォルダーを参照する手続きの中で、書き込み・削除の呼び出しのうち、直前の呼び出し（なければ手続きの先頭）
    /// からその呼び出しまでの間に同じパスの確認を持たないものを返す。
    /// </summary>
    private static List<string> FindUnguardedDestructiveCalls(string codeSection, out List<string> checkedRoutines)
    {
        var withLiterals = SplitRoutines(StripPascalComments(codeSection, keepLiterals: true));
        var withoutLiterals = SplitRoutines(StripPascalComments(codeSection, keepLiterals: false));

        var violations = new List<string>();
        checkedRoutines = new List<string>();
        foreach (var routine in withoutLiterals)
        {
            var name = routine.Key;
            var body = routine.Value;
            if (!withLiterals[name].Contains(@"{commonappdata}\ICCardManager"))
            {
                continue;
            }

            var calls = DestructiveCall.Matches(body).Cast<Match>().ToList();
            if (calls.Count == 0)
            {
                continue;
            }

            checkedRoutines.Add(name);
            var windowStart = 0;
            foreach (var call in calls)
            {
                var window = body.Substring(windowStart, call.Index - windowStart);
                var guarded = GuardCall.Matches(window).Cast<Match>()
                    .Any(g => string.Equals(g.Groups["arg"].Value, call.Groups["arg"].Value, StringComparison.OrdinalIgnoreCase));
                if (!guarded)
                {
                    violations.Add($"{name}: {call.Groups["fn"].Value}({call.Groups["arg"].Value}");
                }

                windowStart = call.Index + call.Length;
            }
        }

        return violations;
    }

    private static string[] ReadInstallerLines()
        => File.ReadAllLines(Path.Combine(TestPaths.GetSolutionRoot(), "installer", "ICCardManager.iss"));

    private static Dictionary<string, string> ParseDirs(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inSection = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                inSection = string.Equals(line, "[Dirs]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection || line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal))
            {
                continue;
            }

            var m = DirsEntry.Match(line);
            if (m.Success)
            {
                result[m.Groups["name"].Value] = m.Groups["perm"].Value;
            }
        }

        result.Should().NotBeEmpty("[Dirs] の行を読めていること（空振り防止）");
        return result;
    }

    /// <summary><c>[Code]</c> 行の次から末尾まで（<c>[Code]</c> はスクリプトの最後の節）。</summary>
    private static string ReadCodeSection(string[] lines)
    {
        var start = Array.FindIndex(lines, l => string.Equals(l.Trim(), "[Code]", StringComparison.OrdinalIgnoreCase));
        start.Should().BeGreaterThan(-1, "[Code] 節を読めていること");
        return string.Join("\n", lines.Skip(start + 1));
    }

    /// <summary>
    /// トップレベルの <c>procedure</c> / <c>function</c> の見出し行で区切り、名前 → 本体（次の見出しまで）を返す。
    /// </summary>
    private static Dictionary<string, string> SplitRoutines(string code)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        var body = new StringBuilder();
        foreach (var line in code.Split('\n'))
        {
            var m = RoutineHeader.Match(line);
            if (m.Success)
            {
                if (current != null)
                {
                    result[current] = body.ToString();
                }

                current = m.Groups["name"].Value;
                body.Clear();
                continue;
            }

            body.Append(line).Append('\n');
        }

        if (current != null)
        {
            result[current] = body.ToString();
        }

        return result;
    }

    /// <summary>
    /// Pascal Script のコメント（<c>//</c>・<c>{ }</c>・<c>(* *)</c>）を除く。文字列リテラル（<c>'...'</c>、
    /// <c>''</c> でエスケープ）の中は除かない。<paramref name="keepLiterals"/> が false ならリテラルの中身を空白にする
    /// （<c>'{commonappdata}'</c> の波括弧をコメントと読み違えないよう、リテラルを先に認識する）。
    /// </summary>
    private static string StripPascalComments(string code, bool keepLiterals)
    {
        var sb = new StringBuilder(code.Length);
        var i = 0;
        while (i < code.Length)
        {
            var c = code[i];
            if (c == '\'')
            {
                sb.Append(c);
                i++;
                while (i < code.Length)
                {
                    if (code[i] == '\'')
                    {
                        if (i + 1 < code.Length && code[i + 1] == '\'')
                        {
                            sb.Append(keepLiterals ? "''" : "  ");
                            i += 2;
                            continue;
                        }

                        sb.Append('\'');
                        i++;
                        break;
                    }

                    sb.Append(keepLiterals || code[i] == '\n' ? code[i] : ' ');
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < code.Length && code[i + 1] == '/')
            {
                while (i < code.Length && code[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '{' || (c == '(' && i + 1 < code.Length && code[i + 1] == '*'))
            {
                var close = c == '{' ? "}" : "*)";
                var end = code.IndexOf(close, i + 1, StringComparison.Ordinal);
                var stop = end < 0 ? code.Length : end + close.Length;
                for (; i < stop; i++)
                {
                    if (code[i] == '\n')
                    {
                        sb.Append('\n');
                    }
                }

                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }
}
