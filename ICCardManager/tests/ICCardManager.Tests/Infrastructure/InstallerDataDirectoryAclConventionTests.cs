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
/// Issue #2261: インストーラーがデータフォルダー（<c>C:\ProgramData\ICCardManager</c>）へ Users のフルコントロールも
/// フォルダー自体の削除も与えず、昇格中の書き込み・削除の前に再解析ポイント（ジャンクション・シンボリックリンク）を確かめること。
/// </summary>
/// <remarks>
/// <para>
/// インストーラー（Inno Setup の <c>.iss</c>）はテストから実行できないため、スクリプトのテキストを静的に検査する。
/// 対で表明する: ①フルコントロール（<c>*-full</c>・icacls の <c>F</c>）が無く、<c>[Dirs]</c> がデータフォルダーへ
/// Permissions を足さないこと（Inno は既存のフォルダーにも Permissions を足し、<c>users-modify</c> はフォルダー自体へ
/// DELETE を足し戻す）、②<c>PrepareToInstall</c> が所有者を固定して ACL を作り直し、フォルダー自体へは削除を含まない権限、
/// 配下へは「変更」を継承だけで与えること、③配下の検査が列挙できないフォルダーを危険として扱うこと、
/// ④ProgramData 配下へ書き込み・削除する各呼び出しが、同じパスについての確認の<b>結果で分岐して</b>、危険なら呼ばない形であること。
/// </para>
/// <para>
/// ④の走査対象は手続きの名前やリテラルの形で列挙せず、書き込み・削除の各呼び出しの第 1 引数を、手続きの中の代入を辿って
/// 解決して決める（<c>ExpandConstant('{commonappdata}') + '\ICCardManager'</c> のような組み立ても拾う）。引数が手続きの
/// 引数（パスを受け取る補助関数）なら、どこを指すか分からないので必ず確認を求める。確認の結果を使わない形
/// （<c>IsDataPathSafe(F);</c> 単独・<c>if IsDataPathSafe(F) then Exit;</c>・<c>or</c> でつないだ条件）は確認と数えない。
/// 検査ロジック自体はサンプル入力で固定する（#1786）。
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

    /// <summary>
    /// ProgramData 配下を対象にしていても、本 Issue の確認の対象外とする手続きと、その理由。
    /// </summary>
    /// <remarks>
    /// マップドドライブ検出の受け渡しファイル（ProgramData 直下のランダムな名前のファイル）は Issue #2267 で扱う。
    /// 例外が実際に該当していること（陳腐化していないこと）も表明する。
    /// </remarks>
    private static readonly Dictionary<string, string> Exceptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DetectMappedDrivesFromSession"] = "ProgramData 直下の受け渡しファイル（Issue #2267 で扱う）",
    };

    /// <summary>昇格中にファイル・フォルダーを作成・書き換え・削除する Inno Setup のサポート関数。</summary>
    private static readonly Regex DestructiveCall = new(
        @"\b(?<fn>SaveStringToFile|SaveStringsToFile|SaveStringsToUTF8File|DeleteFile|DelTree|RemoveDir|RenameFile|FileCopy|DelayDeleteFile|ForceDirectories|CreateDir)\s*\(",
        RegexOptions.Compiled);

    private const string GuardNames = @"(?:IsDataPathSafe|IsDataTreeSafeToDelete)";

    private static readonly Regex RoutineHeader = new(
        @"^(?:procedure|function)\s+(?<name>\w+)\s*(?:\((?<params>[^)]*)\))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Assignment = new(
        @"\b(?<var>\w+)(?:\[[^\]]*\])?\s*:=\s*(?<expr>[^;]*);",
        RegexOptions.Compiled);

    private static readonly Regex Identifier = new(@"\b[A-Za-z_]\w*\b", RegexOptions.Compiled);

    private static readonly Regex DirsEntry = new(
        @"^Name:\s*""(?<name>[^""]+)""(?<rest>.*)$",
        RegexOptions.Compiled);

    [Fact]
    public void データフォルダーの3つはDirsでPermissionsを指定しないこと()
    {
        var entries = ParseDirs(ReadInstallerLines());

        foreach (var dir in DataDirectories)
        {
            entries.Should().ContainKey(dir, $"[Dirs] で {dir} を作ること");
            entries[dir].Should().NotContain("Permissions",
                $"{dir} の権限は HardenDataDirectory が設定する（Inno は既存のフォルダーにも Permissions を足し、users-modify はフォルダー自体へ DELETE を足し戻す）");
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
        FindFullControlGrants(code).Should().BeEmpty("icacls でフルコントロール（F・GA）やアクセス許可の変更・所有権の取得（WDAC・WO）を付与しない");
    }

    [Theory]
    [InlineData("'/grant *S-1-5-32-545:(OI)(CI)F /Q'", true)]
    [InlineData("'/grant:r *S-1-5-32-545:F'", true)]
    [InlineData("'/grant *S-1-5-32-545:(OI)(CI)(GA)'", true)]
    [InlineData("'/grant *S-1-5-32-545:(RX,WDAC)'", true)]
    [InlineData("'/grant *S-1-5-32-545:(WO)'", true)]
    [InlineData("'/grant:r *S-1-5-32-545:(OI)(CI)(IO)M /L /Q'", false)]
    [InlineData("'/grant *S-1-5-32-545:(RX,WD,AD) /L /Q'", false)]
    public void 検査ロジック_フルコントロール相当の付与を検出すること(string code, bool expected)
    {
        FindFullControlGrants(code).Any().Should().Be(expected);
    }

    /// <summary>
    /// icacls の <c>/grant</c> のうち、フルコントロール（<c>F</c>・<c>GA</c>）、アクセス許可の変更（<c>WDAC</c>）、
    /// 所有権の取得（<c>WO</c>）を含む指定を返す。継承の指定（<c>OI</c>・<c>CI</c>・<c>IO</c>・<c>NP</c>・<c>I</c>）は権限として数えない。
    /// </summary>
    private static List<string> FindFullControlGrants(string code)
        => Regex.Matches(code, @"/grant(?::r)?\s+[^'\s]*?:(?<spec>(?:\([A-Z,]+\))*[A-Z]*)")
            .Cast<Match>()
            .Where(m => m.Groups["spec"].Value
                .Split(new[] { '(', ')', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(r => r is "F" or "GA" or "WDAC" or "WO"))
            .Select(m => m.Value)
            .ToList();

    [Fact]
    public void PrepareToInstallで所有者を固定しフォルダー自体へ削除を与えずにACLを作り直すこと()
    {
        var routines = SplitRoutines(StripPascalComments(ReadCodeSection(ReadInstallerLines()), keepLiterals: true));

        routines.Should().ContainKey("PrepareToInstall", "インストール開始前（[Dirs] の処理より前）に ACL を作り直す");
        routines["PrepareToInstall"].Body.Should().Contain("HardenDataDirectory(");

        routines.Should().ContainKey("HardenDataDirectory");
        var body = routines["HardenDataDirectory"].Body;

        // 所有者を固定 → 継承へ戻す → 配下へ「変更」を継承だけで → フォルダー自体へ削除を含まない権限
        var setOwner = body.IndexOf("/setowner *S-1-5-32-544", StringComparison.Ordinal);
        var reset = body.IndexOf("/reset", StringComparison.Ordinal);
        var inheritOnly = body.IndexOf("/grant:r *S-1-5-32-545:(OI)(CI)(IO)M", StringComparison.Ordinal);
        var thisFolder = Regex.Match(body, @"/grant \*S-1-5-32-545:\((?<rights>[A-Z,]+)\) ");
        setOwner.Should().BeGreaterThan(-1, "既存のフォルダーの所有者を Administrators に固定する（事前に作られたフォルダー対策）");
        reset.Should().BeGreaterThan(setOwner, "所有者を固定してから ACL を継承へ戻す（旧版の Users:F を外す）");
        inheritOnly.Should().BeGreaterThan(reset, "配下へは「変更」を継承だけ（IO）で与える");
        thisFolder.Success.Should().BeTrue("フォルダー自体へは「このフォルダーのみ」の権限を与える");
        thisFolder.Index.Should().BeGreaterThan(inheritOnly, "/grant:r で付け直した後に、このフォルダーのみの権限を足す");
        thisFolder.Groups["rights"].Value.Split(',').Should().BeEquivalentTo(new[] { "RX", "WD", "AD" },
            "フォルダー自体には読み取り・実行とファイル／フォルダーの作成だけを与え、削除（D / DE / DC / M / F）を与えない");

        // Users へフォルダー自体に効く「変更」（IO の無い (OI)(CI)M）を与えない
        Regex.IsMatch(body, @"S-1-5-32-545:\(OI\)\(CI\)M\b").Should().BeFalse("(OI)(CI)M はフォルダー自体へ DELETE を与える");

        // 直下（backup・Logs・DB・設定ファイル）も作り直す。再帰（/T）は使わない:
        // icacls の /T は /L を付けてもジャンクションの中へ降りる（実測）ため、検査の後に差し替えられた
        // backup・Logs のリンク先へ管理者権限で /setowner・/reset が掛かり得る
        Regex.IsMatch(body, @"\\\*"" /setowner \*S-1-5-32-544 /C /L ").Should().BeTrue("直下の所有者も固定する（リンク自体を処理する /L 付き）");
        Regex.IsMatch(body, @"\\\*"" /reset /C /L ").Should().BeTrue("直下の ACL も継承へ戻す（Logs に付いた Users:F を外す）");
        Regex.IsMatch(body, @"/T\b").Should().BeFalse("再帰（/T）はジャンクションの中へ降りるので使わない");

        // icacls より前に再解析ポイントを確かめる（ジャンクションの先の ACL を書き換えない）
        var pathCheck = body.IndexOf("IsDataPathSafe(Root)", StringComparison.Ordinal);
        var treeCheck = body.IndexOf("TreeHasReparsePoint(", StringComparison.Ordinal);
        var children = body.IndexOf("\\*\" /setowner", StringComparison.Ordinal);
        pathCheck.Should().BeGreaterThan(-1).And.BeLessThan(setOwner, "フォルダー自体の再解析ポイントを icacls より前に確かめる");
        treeCheck.Should().BeGreaterThan(-1).And.BeLessThan(children, "配下の再解析ポイントを直下への icacls より前に確かめる");
    }

    [Fact]
    public void 配下の検査は列挙できないフォルダーを危険として扱うこと()
    {
        var routines = SplitRoutines(StripPascalComments(ReadCodeSection(ReadInstallerLines()), keepLiterals: false));

        routines.Should().ContainKey("TreeHasReparsePoint");
        var body = routines["TreeHasReparsePoint"].Body;

        // FindFirst の失敗（列挙できない）で True を返して抜けること（fail-closed）
        Regex.IsMatch(body, @"if\s+not\s+Found\s+then\s+begin\b(?:(?!\bend\b)[\s\S])*?Result\s*:=\s*True\s*;(?:(?!\bend\b)[\s\S])*?\bExit\b")
            .Should().BeTrue("存在するフォルダーを列挙できないときは「リンクなし」として通さない");
    }

    [Fact]
    public void ProgramData配下へ書き込み削除する各呼び出しは確認の結果で分岐してから行うこと()
    {
        var result = Analyze(ReadCodeSection(ReadInstallerLines()));

        // 空振り防止: 既知の書き込み・削除の手続きを走査できていること
        result.Targeted.Should().Contain(new[]
        {
            "WriteDataFile", "DeleteDataFile", "HardenDataDirectory",
            "WriteDepartmentConfig", "WriteDatabaseConfig", "WriteReportOutputConfig", "CurUninstallStepChanged",
        });

        // 例外が陳腐化していないこと
        result.ExceptedHits.Should().BeEquivalentTo(Exceptions.Keys, "対象外とした手続きが実際に ProgramData 配下へ書いていること");

        result.Violations.Should().BeEmpty(
            "昇格中の書き込み・削除は、IsDataPathSafe / IsDataTreeSafeToDelete で同じパスを確かめ、危険なら呼ばない形で行う");
    }

    [Fact]
    public void 検査ロジック_対象の導出と確認の形を判定すること()
    {
        var code = string.Join("\n", new[]
        {
            "procedure Guarded();",
            "begin",
            "  F := ExpandConstant('{commonappdata}\\ICCardManager') + '\\a.txt';",
            "  if IsDataPathSafe(F) and FileExists(F) then",
            "    DeleteFile(F);",
            "  if not IsDataPathSafe(F) then",
            "    Exit;",
            "  SaveStringToFile(F, 'x', False);",
            "  if not IsDataPathSafe(F) then",
            "  begin",
            "    Log('x');",
            "    Exit;",
            "  end;",
            "  ForceDirectories(F);",
            "end;",
            "",
            "procedure Assembled();",
            "begin",
            "  D := ExpandConstant('{commonappdata}') + '\\ICCardManager';",
            "  F := AddBackslash(D) + 'b.txt';",
            "  SaveStringToFile(F, 'x', False);",
            "end;",
            "",
            "procedure Helper(const Path: string);",
            "begin",
            "  DeleteFile(Path);",
            "end;",
            "",
            "procedure Unused();",
            "begin",
            "  F := ExpandConstant('{commonappdata}\\ICCardManager\\c.txt');",
            "  // if not IsDataPathSafe(F) then Exit; とコメントに書いただけ",
            "  IsDataPathSafe(F);",
            "  SaveStringToFile(F, 'x', False);",
            "  if IsDataPathSafe(F) then",
            "    Exit;",
            "  DeleteFile(F);",
            "  if IsDataPathSafe(F) or Force then",
            "    DelTree(F, True, True, True);",
            "  if IsDataPathSafe(G) then",
            "    RenameFile(F, G);",
            "end;",
            "",
            "procedure Reused();",
            "begin",
            "  F := ExpandConstant('{commonappdata}\\ICCardManager\\d.txt');",
            "  if IsDataPathSafe(F) then",
            "    SaveStringToFile(F, 'x', False);",
            "  DeleteFile(F);",
            "end;",
            "",
            "procedure OutsideDataDir();",
            "begin",
            "  T := ExpandConstant('{tmp}\\e.txt');",
            "  DeleteFile(T);",
            "  DeleteFile(ExpandConstant('{app}\\f.txt'));",
            "end;",
            "",
            "procedure DetectMappedDrivesFromSession();",
            "begin",
            "  Dirs[1] := ExpandConstant('{commonappdata}');",
            "  OutFile := AddBackslash(Dirs[D]) + 'x.txt';",
            "  DeleteFile(OutFile);",
            "end;",
        });

        var result = Analyze(code);

        result.Targeted.Should().BeEquivalentTo(new[] { "Guarded", "Assembled", "Helper", "Unused", "Reused" },
            "ProgramData 配下を指す呼び出し（組み立てた形・手続きの引数を含む）を持つ手続きを対象にし、{tmp}・{app} だけの手続きは対象にしない");
        result.ExceptedHits.Should().BeEquivalentTo(new[] { "DetectMappedDrivesFromSession" });
        result.Violations.Should().BeEquivalentTo(new[]
        {
            "Assembled: SaveStringToFile(F",   // 組み立てたパスも対象
            "Helper: DeleteFile(Path",         // 手続きの引数のパスも対象
            "Unused: SaveStringToFile(F",      // 結果を使わない確認・コメントの中の確認は数えない
            "Unused: DeleteFile(F",            // 極性が逆（安全なら抜ける）の確認は数えない
            "Unused: DelTree(F",               // or でつないだ条件は確認と数えない
            "Unused: RenameFile(F",            // 別のパスの確認は数えない
            "Reused: DeleteFile(F",            // 1 つの確認を 2 つの呼び出しで使い回さない
        });
    }

    private sealed record Routine(string Name, IReadOnlyList<string> Parameters, string Body);

    private sealed record AnalysisResult(List<string> Targeted, List<string> ExceptedHits, List<string> Violations);

    /// <summary>
    /// 書き込み・削除の各呼び出しの第 1 引数を解決し、ProgramData 配下（または手続きの引数）を指すものについて、
    /// 直前の対象の呼び出し（なければ手続きの先頭）からその呼び出しまでの間に、同じパスの確認の結果で分岐する形があるかを調べる。
    /// </summary>
    private static AnalysisResult Analyze(string codeSection)
    {
        var withLiterals = SplitRoutines(StripPascalComments(codeSection, keepLiterals: true));
        var blanked = SplitRoutines(StripPascalComments(codeSection, keepLiterals: false));

        var targeted = new List<string>();
        var excepted = new List<string>();
        var violations = new List<string>();
        foreach (var name in blanked.Keys)
        {
            var body = blanked[name].Body;
            var source = withLiterals[name].Body;
            var parameters = blanked[name].Parameters;
            var assignments = Assignment.Matches(source).Cast<Match>()
                .GroupBy(m => m.Groups["var"].Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(m => m.Groups["expr"].Value).ToList(), StringComparer.OrdinalIgnoreCase);

            var windowStart = 0;
            var isTargeted = false;
            foreach (Match call in DestructiveCall.Matches(body))
            {
                var arg = FirstArgument(source, call.Index + call.Length);
                var isParameter = parameters.Contains(arg, StringComparer.OrdinalIgnoreCase);
                var resolved = Normalize(Resolve(arg, assignments, 0));
                if (!isParameter && !resolved.Contains("{commonappdata}"))
                {
                    continue;
                }

                if (Exceptions.ContainsKey(name))
                {
                    if (!excepted.Contains(name))
                    {
                        excepted.Add(name);
                    }

                    continue;
                }

                isTargeted = true;
                var window = body.Substring(windowStart, call.Index - windowStart);
                if (!IsGuardedByExit(window, arg) && !IsGuardedByCondition(window, arg))
                {
                    violations.Add($"{name}: {call.Groups["fn"].Value}({arg}");
                }

                windowStart = call.Index + call.Length;
            }

            if (isTargeted)
            {
                targeted.Add(name);
            }
        }

        return new AnalysisResult(targeted, excepted, violations);
    }

    /// <summary><c>if not Guard(P) then Exit;</c>（または <c>begin … Exit; end;</c>）が窓の中にあるか。</summary>
    private static bool IsGuardedByExit(string window, string arg)
        => Regex.IsMatch(
            window,
            @"\bif\s+not\s+" + GuardNames + @"\s*\(\s*" + Regex.Escape(arg) + @"\s*\)\s+then\s+(?:begin\b(?:(?!\bend\b)[\s\S])*?)?\bExit\b",
            RegexOptions.IgnoreCase);

    /// <summary>呼び出しの直前が <c>if … Guard(P) … then</c> で、確認を否定せず <c>or</c> でつながないか。</summary>
    private static bool IsGuardedByCondition(string window, string arg)
    {
        var m = Regex.Match(window, @"\bif\s+(?<cond>(?:(?!\bthen\b)[^;])*)\bthen\s*$", RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            return false;
        }

        var cond = m.Groups["cond"].Value;
        return Regex.IsMatch(cond, @"(?<!\bnot\s+)\b" + GuardNames + @"\s*\(\s*" + Regex.Escape(arg) + @"\s*\)", RegexOptions.IgnoreCase)
               && !Regex.IsMatch(cond, @"\bor\b", RegexOptions.IgnoreCase);
    }

    /// <summary>開き括弧の直後から、深さ 0 の <c>,</c> か <c>)</c> までを第 1 引数として返す。</summary>
    private static string FirstArgument(string source, int start)
    {
        var depth = 0;
        var inLiteral = false;
        for (var i = start; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '\'')
            {
                inLiteral = !inLiteral;
                continue;
            }

            if (inLiteral)
            {
                continue;
            }

            if (c == '(' || c == '[')
            {
                depth++;
            }
            else if ((c == ')' || c == ']') && depth > 0)
            {
                depth--;
            }
            else if ((c == ',' || c == ')') && depth == 0)
            {
                return source.Substring(start, i - start).Trim();
            }
        }

        return source.Substring(start).Trim();
    }

    /// <summary>式の中の識別子（文字列リテラルの外）を、手続きの中での代入の右辺（すべて）へ置き換えていく。</summary>
    private static string Resolve(string expr, Dictionary<string, List<string>> assignments, int depth)
    {
        if (depth > 8)
        {
            return expr;
        }

        var sb = new StringBuilder();
        var parts = expr.Split('\'');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                sb.Append('\'');
            }

            // 偶数番目がリテラルの外、奇数番目がリテラルの中
            sb.Append(i % 2 == 1
                ? parts[i]
                : Identifier.Replace(parts[i], m => assignments.TryGetValue(m.Value, out var exprs)
                    ? "(" + string.Join(" | ", exprs.Select(e => Resolve(e, assignments, depth + 1))) + ")"
                    : m.Value));
        }

        return sb.ToString();
    }

    /// <summary>
    /// 連結・定数の展開・引用符・空白を除いて比べられる形にする
    /// （<c>ExpandConstant('{commonappdata}') + '\ICCardManager'</c> → <c>{commonappdata}\ICCardManager</c>）。
    /// </summary>
    private static string Normalize(string expr)
        => Regex.Replace(expr, @"ExpandConstant|AddBackslash|['()+\s]", string.Empty, RegexOptions.IgnoreCase);

    private static string[] ReadInstallerLines()
        => File.ReadAllLines(Path.Combine(TestPaths.GetSolutionRoot(), "installer", "ICCardManager.iss"));

    /// <summary><c>[Dirs]</c> の行を、名前 → 名前より後ろの指定（Permissions 等）で返す。</summary>
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
                result[m.Groups["name"].Value] = m.Groups["rest"].Value;
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
    /// トップレベルの <c>procedure</c> / <c>function</c> の見出し行で区切り、名前 → 引数名と本体（次の見出しまで）を返す。
    /// </summary>
    private static Dictionary<string, Routine> SplitRoutines(string code)
    {
        var result = new Dictionary<string, Routine>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        var parameters = new List<string>();
        var body = new StringBuilder();
        foreach (var line in code.Split('\n'))
        {
            var m = RoutineHeader.Match(line);
            if (m.Success)
            {
                if (current != null)
                {
                    result[current] = new Routine(current, parameters, body.ToString());
                }

                current = m.Groups["name"].Value;
                parameters = ParseParameters(m.Groups["params"].Value);
                body.Clear();
                continue;
            }

            body.Append(line).Append('\n');
        }

        if (current != null)
        {
            result[current] = new Routine(current, parameters, body.ToString());
        }

        return result;
    }

    /// <summary><c>const Path, Value: string; var X: Integer</c> → <c>Path, Value, X</c>。</summary>
    private static List<string> ParseParameters(string text)
        => text.Split(';')
            .Select(p => p.Split(':')[0])
            .SelectMany(p => Regex.Replace(p, @"^\s*(?:const|var|out)\s+", string.Empty, RegexOptions.IgnoreCase).Split(','))
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

    /// <summary>
    /// Pascal Script のコメント（<c>//</c>・<c>{ }</c>・<c>(* *)</c>）を除く。文字列リテラル（<c>'...'</c>、
    /// <c>''</c> でエスケープ）の中は除かない。<paramref name="keepLiterals"/> が false ならリテラルの中身を同じ長さの空白にする
    /// （2 つの形で同じ位置が対応するようにする。<c>'{commonappdata}'</c> の波括弧をコメントと読み違えないよう、リテラルを先に認識する）。
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
