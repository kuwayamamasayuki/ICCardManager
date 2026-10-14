using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Infrastructure;

/// <summary>
/// Issue #2267: インストーラーのマップドドライブ検出が、他のユーザーに差し替えられ得る受け渡しファイルの内容を
/// 確かめずに <c>net use</c> へ渡さないこと。
/// </summary>
/// <remarks>
/// <para>
/// インストーラー（Inno Setup の <c>.iss</c>）はテストから実行できないため、スクリプトのテキストを静的に検査する。
/// 検証関数（<c>IsValidMappedRemotePath</c> 等）は Pascal Script で書かれており、C# から呼べない。同じ判定を C# で
/// 書き直してテストすると、Pascal 側だけ変わっても緑のまま残る（2 つの実装が別々にずれる）ため、判定の中身は
/// テストせず、<b>判定を通らない値が <c>net use</c> へ届く経路が無いこと</b>を構造として固定する
/// （判定の中身は実装時に Inno Setup 6.7.0 で動かして確かめた。開発者ガイド §5.4）。
/// </para>
/// <para>
/// 対で表明する: ①受け渡しファイルを <c>%PUBLIC%</c>（<c>C:\Users\Public</c>）に置かない、
/// ②マップドドライブの配列へ書くのは検証してから登録する 1 か所だけで、<c>net use</c> の直前でも検証する、
/// ③受け渡しファイルは元のユーザーの PowerShell が <c>-NoClobber</c>（CreateNew）で新しく作れたとき（終了コード 0）だけ、
/// 再解析ポイントでないことを確かめてから読む。名前は推測できない乱数で作る。
/// 検査ロジック自体はサンプル入力で固定する（#1786）。
/// </para>
/// </remarks>
public class InstallerMappedDriveHandoffConventionTests
{
    private const string Validator = "IsValidMappedDrive";
    private const string Register = "AddMappedDrive";
    private const string Remap = "RemapDrivesForElevatedSession";
    private const string Detect = "DetectMappedDrivesFromSession";
    private const string Reader = "ReadOwnedHandoffFile";
    private const string ReaderScript = "read-mapped-drives-handoff.ps1";

    /// <summary>読み取りスクリプトの、対話セッションのユーザーと昇格したユーザーの照合（取れない・違うなら読まない）。</summary>
    private const string SessionCheck =
        "if ([string]::IsNullOrEmpty($sessionUser) -or -not [string]::Equals($sessionUser, $elevatedUser, [System.StringComparison]::OrdinalIgnoreCase)) { 'RESULT=SESSION_USER'; exit 1 }";

    /// <summary><c>net use</c> の呼び出し（引数がリテラル <c>'use '</c> で始まる <c>Exec</c>）。</summary>
    private static readonly Regex NetUseExec = new(
        @"\bExec\s*\(\s*\w+\s*,\s*'use\s",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MappedArrayAssignment = new(
        @"\bMappedDrive(?:Letters|RemotePaths)\s*\[[^\]]*\]\s*:=",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void 受け渡しファイルをPUBLICフォルダーに置かないこと()
    {
        FindPublicFolderReferences(CodeWithLiterals())
            .Should().BeEmpty("C:\\Users\\Public は同時にログオンしている別のユーザーの書き換えを ACL で止められる保証が無い。ProgramData 直下だけを使う");
    }

    [Theory]
    [InlineData("  Dirs[0] := GetEnv('PUBLIC');", true)]
    [InlineData("  Dirs[0] := GetEnv( 'Public' );", true)]
    [InlineData("  Dirs[0] := ExpandConstant('{%PUBLIC}');", true)]
    [InlineData("  Dirs[0] := 'C:\\Users\\Public\\x.txt';", true)]
    [InlineData("  Dirs[0] := ExpandConstant('{commondocs}');", true)]
    [InlineData("  // C:\\Users\\Public（%PUBLIC%）は使わない", false)]
    [InlineData("  Dirs[0] := ExpandConstant('{commonappdata}');", false)]
    [InlineData("  Dirs[0] := GetEnv('PUBLICKEY');", false)]
    public void 検査ロジック_PUBLICフォルダーの参照を検出すること(string line, bool expected)
    {
        var code = InstallerDataDirectoryAclConventionTests.StripPascalComments(
            "procedure P();\nbegin\n" + line + "\nend;\n", keepLiterals: true);

        FindPublicFolderReferences(code).Any().Should().Be(expected);
    }

    /// <summary>コメントを除いたコードのうち、C:\Users\Public を指す参照。</summary>
    private static List<string> FindPublicFolderReferences(string code)
        => Regex.Matches(
                code,
                @"GetEnv\s*\(\s*'PUBLIC'\s*\)|\{%PUBLIC\b|\\Users\\Public\b|\{commondocs\}",
                RegexOptions.IgnoreCase)
            .Cast<Match>()
            .Select(m => m.Value)
            .ToList();

    [Fact]
    public void 検証を通らない値をnet_useへ渡さないこと()
    {
        var violations = FindUnvalidatedNetUsePaths(CodeWithoutLiterals(), CodeWithLiterals());

        // 空振り防止: 再マッピングの net use を実際に見つけていること
        NetUseExec.IsMatch(InstallerDataDirectoryAclConventionTests.SplitRoutines(CodeWithLiterals())[Remap].Body)
            .Should().BeTrue(Remap + " の net use を見つけられること");

        violations.Should().BeEmpty(
            "net use の引数はエスケープできないので、マップドドライブの配列へは " + Register + " が " + Validator +
            " で確かめてから書き、" + Remap + " も net use の直前に確かめる");
    }

    [Fact]
    public void 検査ロジック_検証を経ない経路を検出すること()
    {
        foreach (var form in new[] { RemapForm.Else, RemapForm.ElseChain, RemapForm.Continue })
        {
            var good = Sample(addValidatesFirst: true, method2WritesArray: false, remap: form, netUseElsewhere: false);
            FindUnvalidatedNetUsePaths(good.Blanked, good.Literals).Should().BeEmpty($"正しい形（{form}）は違反にしない");
        }

        var direct = Sample(addValidatesFirst: true, method2WritesArray: true, remap: RemapForm.Else, netUseElsewhere: false);
        FindUnvalidatedNetUsePaths(direct.Blanked, direct.Literals)
            .Should().Equal(new[] { "DetectMappedDrivesFromSession: 配列へ直接書いている" });

        var late = Sample(addValidatesFirst: false, method2WritesArray: false, remap: RemapForm.Else, netUseElsewhere: false);
        FindUnvalidatedNetUsePaths(late.Blanked, late.Literals)
            .Should().Equal(new[] { "AddMappedDrive: 配列へ書く前に検証して抜けていない" });

        // 検証が無い・検証の後ろを ; で閉じて net use を次の文にした（検証の結果で分岐しない）・else の分岐を ; で閉じた後に net use がある
        foreach (var form in new[] { RemapForm.None, RemapForm.Semicolon, RemapForm.AfterElse })
        {
            var bad = Sample(addValidatesFirst: true, method2WritesArray: false, remap: form, netUseElsewhere: false);
            FindUnvalidatedNetUsePaths(bad.Blanked, bad.Literals)
                .Should().Equal(new[] { "RemapDrivesForElevatedSession: net use が検証を通った側の分岐にない" }, $"{form} は検証の結果で分岐していない");
        }

        var elsewhere = Sample(addValidatesFirst: true, method2WritesArray: false, remap: RemapForm.Else, netUseElsewhere: true);
        FindUnvalidatedNetUsePaths(elsewhere.Blanked, elsewhere.Literals)
            .Should().Equal(new[] { "Other: net use を " + Remap + " の外で呼んでいる" });
    }

    /// <summary>サンプル入力での、再マッピングの検証と net use の並べ方。</summary>
    public enum RemapForm
    {
        /// <summary><c>if not V then Log(…) else Exec(…)</c></summary>
        Else,

        /// <summary><c>if not V then Log(…) else if DirExists(…) then Log(…) else begin if Exec(…) … end;</c>（本番の形）</summary>
        ElseChain,

        /// <summary><c>if not V then begin Log(…); Continue; end; Exec(…)</c></summary>
        Continue,

        /// <summary>検証が無い</summary>
        None,

        /// <summary><c>if not V then Log(…); Exec(…)</c>（検証の結果で分岐しない。レビュー 1 回目の変異）</summary>
        Semicolon,

        /// <summary><c>if not V then Log(…) else Log(…); Exec(…)</c>（net use が else の外）</summary>
        AfterElse,
    }

    private static (string Blanked, string Literals) Sample(bool addValidatesFirst, bool method2WritesArray, RemapForm remap, bool netUseElsewhere)
    {
        var guard = new[]
        {
            "  if not IsValidMappedDrive(DriveLetter, RemotePath) then",
            "  begin",
            "    Log('x');",
            "    Exit;",
            "  end;",
        };
        var assign = new[]
        {
            "  MappedDriveLetters[MappedDriveCount] := DriveLetter;",
            "  MappedDriveRemotePaths[MappedDriveCount] := RemotePath;",
        };

        var lines = new List<string> { "procedure AddMappedDrive(const DriveLetter, RemotePath, Source: string);", "begin" };
        lines.AddRange(addValidatesFirst ? guard.Concat(assign) : assign.Concat(guard));
        lines.Add("end;");
        lines.AddRange(new[] { "", "procedure DetectMappedDrivesFromSession();", "begin" });
        lines.Add(method2WritesArray
            ? "  MappedDriveRemotePaths[MappedDriveCount] := Line; // IsValidMappedDrive をコメントに書いただけ"
            : "  AddMappedDrive(L, P, 'Method 2');");
        lines.Add("end;");
        lines.AddRange(new[] { "", "procedure RemapDrivesForElevatedSession();", "begin", "  for I := 0 to MappedDriveCount - 1 do", "  begin" });
        const string check = "    if not IsValidMappedDrive(MappedDriveLetters[I], MappedDriveRemotePaths[I]) then";
        const string netUse = "Exec(NetExe, 'use ' + MappedDriveLetters[I] + ' \"' + MappedDriveRemotePaths[I] + '\" /persistent:no', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)";
        switch (remap)
        {
            case RemapForm.Else:
                lines.AddRange(new[] { check, "      Log('x')", "    else", "      " + netUse + ";" });
                break;
            case RemapForm.ElseChain:
                lines.AddRange(new[]
                {
                    check, "      Log('x')", "    else if DirExists(MappedDriveLetters[I] + '\\') then", "      Log('y')", "    else",
                    "    begin", "      if " + netUse + " then", "        Log('z')", "      else", "        Log('w');", "    end;",
                });
                break;
            case RemapForm.Continue:
                lines.AddRange(new[] { check, "    begin", "      Log('x');", "      Continue;", "    end;", "    " + netUse + ";" });
                break;
            case RemapForm.None:
                lines.Add("    " + netUse + ";");
                break;
            case RemapForm.Semicolon:
                lines.AddRange(new[] { check, "      Log('x');", "    if DirExists(MappedDriveLetters[I] + '\\') then", "      Log('y')", "    else", "      " + netUse + ";" });
                break;
            case RemapForm.AfterElse:
                lines.AddRange(new[] { check, "      Log('x')", "    else", "      Log('y');", "    " + netUse + ";" });
                break;
        }

        lines.Add("  end;");
        lines.Add("end;");
        if (netUseElsewhere)
        {
            lines.AddRange(new[] { "", "procedure Other();", "begin", "  Exec(NetExe, 'use Z: \"' + P + '\"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);", "end;" });
        }

        var code = string.Join("\n", lines);
        return (InstallerDataDirectoryAclConventionTests.StripPascalComments(code, keepLiterals: false),
                InstallerDataDirectoryAclConventionTests.StripPascalComments(code, keepLiterals: true));
    }

    /// <summary>
    /// 検証を通らない値が <c>net use</c> へ届く経路を返す。
    /// <list type="bullet">
    /// <item>マップドドライブの配列（<c>MappedDriveLetters</c> / <c>MappedDriveRemotePaths</c>）の要素へ書くのは <c>AddMappedDrive</c> だけ</item>
    /// <item><c>AddMappedDrive</c> は最初の書き込みより前に <c>if not IsValidMappedDrive(DriveLetter, RemotePath) then … Exit</c> を持つ</item>
    /// <item><c>net use</c>（<c>'use '</c> で始まる引数の <c>Exec</c>）は <c>RemapDrivesForElevatedSession</c> だけで呼び、その前に
    /// <c>if not IsValidMappedDrive(MappedDriveLetters[I], MappedDriveRemotePaths[I]) then</c> を持つ</item>
    /// </list>
    /// </summary>
    private static List<string> FindUnvalidatedNetUsePaths(string blankedCode, string literalCode)
    {
        var blanked = InstallerDataDirectoryAclConventionTests.SplitRoutines(blankedCode);
        var literals = InstallerDataDirectoryAclConventionTests.SplitRoutines(literalCode);
        var violations = new List<string>();

        // 空振り防止: 登録と再マッピングの手続きが読めていること
        blanked.Should().ContainKeys(Register, Remap);

        foreach (var name in blanked.Keys)
        {
            var body = blanked[name].Body;
            var firstAssign = MappedArrayAssignment.Match(body);
            if (firstAssign.Success && !string.Equals(name, Register, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"{name}: 配列へ直接書いている");
            }

            if (firstAssign.Success && string.Equals(name, Register, StringComparison.OrdinalIgnoreCase))
            {
                var before = body.Substring(0, firstAssign.Index);
                if (!Regex.IsMatch(
                        before,
                        @"\bif\s+not\s+" + Validator + @"\s*\(\s*DriveLetter\s*,\s*RemotePath\s*\)\s+then\s+(?:begin\b(?:(?!\bend\b)[\s\S])*?)?\bExit\b",
                        RegexOptions.IgnoreCase))
                {
                    violations.Add($"{name}: 配列へ書く前に検証して抜けていない");
                }
            }

            // net use の呼び出し（リテラル 'use ' を引数に持つ Exec）
            var source = literals[name].Body;
            foreach (Match exec in NetUseExec.Matches(source))
            {
                if (!string.Equals(name, Remap, StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"{name}: net use を {Remap} の外で呼んでいる");
                    continue;
                }

                if (!IsInValidatedBranch(body.Substring(0, exec.Index)))
                {
                    violations.Add($"{name}: net use が検証を通った側の分岐にない");
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// <paramref name="prefix"/>（手続きの先頭から net use の直前まで）の最後の
    /// <c>if not IsValidMappedDrive(MappedDriveLetters[I], MappedDriveRemotePaths[I]) then</c> が、
    /// <c>begin … Continue|Exit … end</c> で抜けるか、単文の直後の <c>else</c> の分岐の中に net use を置く形か。
    /// 単文の後を <c>;</c> で閉じた形（検証の結果で分岐しない）や、<c>else</c> の分岐を閉じた後に net use がある形は数えない。
    /// </summary>
    private static bool IsInValidatedBranch(string prefix)
    {
        var guards = Regex.Matches(
            prefix,
            @"\bif\s+not\s+" + Validator + @"\s*\(\s*MappedDriveLetters\s*\[\s*I\s*\]\s*,\s*MappedDriveRemotePaths\s*\[\s*I\s*\]\s*\)\s+then\s+" +
            @"(?:(?<block>begin\b(?:(?!\bend\b)[\s\S])*?\b(?:Continue|Exit)\b)|(?<single>[^;]*?\belse\b))",
            RegexOptions.IgnoreCase);
        if (guards.Count == 0)
        {
            return false;
        }

        var guard = guards[guards.Count - 1];
        if (guard.Groups["block"].Success)
        {
            return true;
        }

        // else の後ろから net use までに、begin … end の外の ; があれば else の分岐は閉じている
        var depth = 0;
        foreach (Match token in Regex.Matches(prefix.Substring(guard.Index + guard.Length), @"\bbegin\b|\bend\b|;", RegexOptions.IgnoreCase))
        {
            if (token.Value.Equals("begin", StringComparison.OrdinalIgnoreCase))
            {
                depth++;
            }
            else if (token.Value.Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                depth--;
            }
            else if (depth <= 0)
            {
                return false;
            }
        }

        return true;
    }

    [Fact]
    public void 検証関数は引用符と末尾の円記号を拒否しドライブ文字と接続先の両方を確かめること()
    {
        // 判定の中身は C# で再実装しない（Pascal 側だけ変わっても緑のまま残るため）。判定に必要な形が
        // 関数の本体にあることだけを表明する（文字列リテラルを残し、コメントを除いた本文で見る）
        var routines = InstallerDataDirectoryAclConventionTests.SplitRoutines(CodeWithLiterals());
        routines.Should().ContainKeys("IsValidMappedDrive", "IsValidMappedRemotePath", "IsValidMappedDriveLetter");

        var drive = routines["IsValidMappedDrive"].Body;
        Regex.Matches(drive, @"\bResult\s*:=").Count.Should().Be(1, "結果の代入は 1 つだけ");
        Regex.IsMatch(drive, @"\bResult\s*:=\s*IsValidMappedDriveLetter\s*\(\s*DriveLetter\s*\)\s+and\s+IsValidMappedRemotePath\s*\(\s*RemotePath\s*\)\s*;")
            .Should().BeTrue("ドライブ文字と接続先の両方を確かめる");

        var letter = routines["IsValidMappedDriveLetter"].Body;
        Regex.IsMatch(letter, @"Length\s*\(\s*DriveLetter\s*\)\s*=\s*2").Should().BeTrue("2 文字");
        Regex.IsMatch(letter, @"DriveLetter\s*\[\s*1\s*\]\s*>=\s*'A'").Should().BeTrue("A 以上");
        Regex.IsMatch(letter, @"DriveLetter\s*\[\s*1\s*\]\s*<=\s*'Z'").Should().BeTrue("Z 以下");
        Regex.IsMatch(letter, @"DriveLetter\s*\[\s*2\s*\]\s*=\s*':'").Should().BeTrue("2 文字目はコロン");

        var path = routines["IsValidMappedRemotePath"].Body;
        Regex.IsMatch(path, @"^(?:(?!\bbegin\b)[\s\S])*\bbegin\s+Result\s*:=\s*False\s*;").Should().BeTrue("既定は拒否");
        Regex.Matches(path, @"\bResult\s*:=").Count.Should().Be(2, "既定の拒否と、最後の判定だけ");
        Regex.IsMatch(path, @"\bResult\s*:=\s*True\b").Should().BeFalse("無条件に受理しない");
        Regex.IsMatch(path, @"\bif\s+\(Length\s*\(\s*RemotePath\s*\)\s*<\s*5\)\s+or\s+\(Length\s*\(\s*RemotePath\s*\)\s*>\s*IccmMaxRemotePathLength\)\s+then\s+Exit\s*;")
            .Should().BeTrue("長さの上限");
        Regex.IsMatch(path, @"\bif\s+Copy\s*\(\s*RemotePath\s*,\s*1\s*,\s*2\s*\)\s*<>\s*'\\\\'\s+then\s+Exit\s*;").Should().BeTrue("\\\\ で始まる");
        Regex.IsMatch(path, @"\bif\s+RemotePath\s*\[\s*Length\s*\(\s*RemotePath\s*\)\s*\]\s*=\s*'\\'\s+then\s+Exit\s*;")
            .Should().BeTrue("末尾の \\ は閉じ引用符をエスケープするので拒否する");
        Regex.IsMatch(path, @"\bfor\s+I\s*:=\s*1\s+to\s+Length\s*\(\s*RemotePath\s*\)\s+do\b").Should().BeTrue("全文字を調べる");
        var charConditions = Regex.Matches(path, @"\bif\s+(?<cond>[^;]*?\bC\b[^;]*?)\s+then\s+Exit\s*;").Cast<Match>().Select(m => m.Groups["cond"].Value).ToList();
        charConditions.Should().ContainSingle("1 文字ずつの拒否は 1 つの条件にまとめている");
        charConditions[0].Should().MatchRegex(@"\bC\s*=\s*'""'", "二重引用符を拒否する（net use の引数を閉じさせない）");
        charConditions[0].Should().MatchRegex(@"Ord\s*\(\s*C\s*\)\s*<\s*32", "制御文字を拒否する");
        charConditions[0].Should().MatchRegex(@"\bC\s*=\s*'/'", "/ を拒否する");
        charConditions[0].Should().NotMatchRegex(@"\band\b", "or でつないだ拒否の条件を and で弱めない");
        Regex.IsMatch(path, @"\bif\s+Pos\s*\(\s*'\\\\'\s*,\s*Rest\s*\)\s*>\s*0\s+then\s+Exit\s*;").Should().BeTrue("空の要素を拒否する");
        Regex.IsMatch(path, @"\bResult\s*:=\s*Pos\s*\(\s*'\\'\s*,\s*Rest\s*\)\s*>\s*1\s*;").Should().BeTrue("サーバー名の後に共有名が続く");
    }

    [Fact]
    public void 肩越しの昇格では元のユーザーの検出結果を登録しないこと()
    {
        var blanked = InstallerDataDirectoryAclConventionTests.SplitRoutines(CodeWithoutLiterals());
        var literals = InstallerDataDirectoryAclConventionTests.SplitRoutines(CodeWithLiterals());
        blanked.Should().ContainKeys(Detect, Reader);

        // 照合の根拠はファイルの内容ではなく NTFS 上の所有者。内容は元のユーザーが自由に決められ、SID は秘密ではない
        var detectSource = literals[Detect].Body;
        Regex.IsMatch(detectSource, @"Sid\s*=", RegexOptions.IgnoreCase)
            .Should().BeFalse("受け渡しファイルに SID を書かない・ファイルの行から SID を読まない（照合に使わない）");
        detectSource.Should().NotContain("WindowsIdentity", "元のユーザーの PowerShell に SID を申告させない");

        // 受け渡しファイルを Pascal 側で直接読まない（照合した後に別の手段で読むと、その間に差し替えられ得る）
        var body = blanked[Detect].Body;
        body.Should().NotContain("LoadStringsFromFile(", "受け渡しファイルは照合と同じハンドルで読んだ写しだけを使う");

        // 最初の登録より前に、照合して読む関数が失敗したら抜ける（fail-closed）
        var firstAdd = body.IndexOf("AddMappedDrive(", StringComparison.Ordinal);
        firstAdd.Should().BeGreaterThan(-1);
        Regex.IsMatch(
                body.Substring(0, firstAdd),
                @"\bif\s+not\s+" + Reader + @"\s*\(\s*OutFile\s*,\s*Lines\s*,\s*Reason\s*\)\s+then\s+begin\b[\s\S]*?\bExit\s*;\s*end\s*;")
            .Should().BeTrue("照合できないときは、元のユーザーが選んだ接続先へ管理者の資格情報で接続しない");

        // 既に昇格した状態から起動した・UAC が無効（所有者が Administrators）は、肩越しの昇格と別の文言でログに残す
        Regex.IsMatch(body, @"\bif\s+Reason\s*=\s*'[^']*'\s+then\s+Log\(").Should().BeTrue();
        detectSource.Should().Contain("if Reason = 'OWNER_ADMINISTRATORS' then", "実害の無い不一致を肩越しの昇格と区別する");
        detectSource.Should().Contain("else if (Reason = 'SESSION_USER') or ", "対話セッションのユーザーと昇格したユーザーが違うときは、肩越しの昇格としてログに残す");
        Regex.Matches(detectSource, @"理由: ' \+ Reason \+ '）'").Count.Should().Be(2, "照合の失敗もスキップも、理由（OPEN・INFO・ERROR 等）を区別できるようにログへ残す");
    }

    [Fact]
    public void 受け渡しファイルの照合と読み取りは昇格した1回のPowerShellで同じハンドルに対して行うこと()
    {
        var literals = InstallerDataDirectoryAclConventionTests.SplitRoutines(CodeWithLiterals());
        literals.Should().ContainKey(Reader);
        var reader = literals[Reader].Body;

        // 昇格したままの 1 回の呼び出し（元のユーザーで実行すると、元のユーザーが結果を決められる）
        Regex.Matches(reader, @"\bExecAndCaptureOutput\s*\(").Count.Should().Be(1, "照合と読み取りを 1 回の PowerShell にまとめる");
        reader.Should().NotContain("ExecAsOriginalUser");
        reader.Should().Contain("ExtractTemporaryFile('" + ReaderScript + "')");
        reader.Should().Contain("Script := ExpandConstant('{tmp}\\" + ReaderScript + "');", "スクリプトは元のユーザーが書き込めない {tmp} から実行する");
        // -File で実行しない（グループ ポリシーの AllSigned・Restricted は -ExecutionPolicy Bypass より優先され、未署名の .ps1 が動かない）。
        // {tmp} のスクリプトの本文をスクリプト ブロックとして実行する（実行ポリシーはスクリプト ブロックに掛からない）
        Regex.IsMatch(reader, @"-File\b", RegexOptions.IgnoreCase).Should().BeFalse("スクリプト ファイルとして実行すると実行ポリシーで止まり得る");
        reader.Should().Contain("-Command \"& ([ScriptBlock]::Create([System.IO.File]::ReadAllText(''' + QuotedScript + '''))) ");
        reader.Should().Contain("'-Path ''' + QuotedPath + ''' -OutPath ''' + QuotedVerified + '''\"'");
        foreach (var name in new[] { "Script", "Path", "Verified" })
        {
            reader.Should().Contain($"Quoted{name} := {name};\n  StringChangeEx(Quoted{name}, '''', '''''', True);", "単一引用符の文字列へ埋めるパスの ' を '' にする");
        }
        reader.Should().Contain("Verified := ExpandConstant('{tmp}\\", "照合を通った内容の写しは {tmp} に置く");

        // 受け渡しファイルそのものは読まず、照合を通った写しだけを、結果が OK・終了コード 0 のときに読む
        Regex.IsMatch(reader, @"LoadStringsFromFile\s*\(\s*Path\b").Should().BeFalse("受け渡しファイルを直接読まない");
        var load = reader.IndexOf("LoadStringsFromFile(Verified, Lines)", StringComparison.Ordinal);
        load.Should().BeGreaterThan(-1);
        Regex.IsMatch(reader.Substring(0, load), @"\bif\s+\(Reason\s*<>\s*'OK'\)\s+or\s+\(ResultCode\s*<>\s*0\)\s+then\s+begin\b[\s\S]*?\bExit\s*;\s*end\s*;")
            .Should().BeTrue("照合を通らなかったときは写しを読まない");

        // スクリプトは [Files] に dontcopy で載せ、{tmp} へだけ取り出す
        ReadInstallerText().Should().Contain("Source: \"" + ReaderScript + "\"; Flags: dontcopy");

        var script = ReadReaderScript();
        AnalyzeReaderScript(script).Should().BeEmpty();
    }

    [Theory]
    [InlineData("none", new string[0])]
    [InlineData("no-links-check", new[] { "ハードリンクの数を確かめていない" })]
    [InlineData("no-owner-check", new[] { "所有者を昇格したユーザーと照合していない" })]
    [InlineData("follow-links", new[] { "リンクを辿らずに開いていない（FILE_FLAG_OPEN_REPARSE_POINT）" })]
    [InlineData("shared", new[] { "書き込み・削除の共有を許して開いている" })]
    [InlineData("ok-before-checks", new[] { "照合より前に内容を読むか結果を返している" })]
    [InlineData("read-by-path", new[] { "照合より前に内容を読むか結果を返している", "パスから読み直している" })]
    [InlineData("no-dacl-check", new[] { "所有者・SYSTEM・Administrators 以外に書き込みを許す ACE を確かめていない" })]
    [InlineData("no-session-check", new[] { "ファイルを開く前に、対話セッションのユーザーと昇格したユーザーの一致を確かめていない（取れなければ読まない形で）" })]
    [InlineData("session-unknown-passes", new[] { "ファイルを開く前に、対話セッションのユーザーと昇格したユーザーの一致を確かめていない（取れなければ読まない形で）" })]
    public void 検査ロジック_読み取りスクリプトの照合の欠落を検出すること(string mutation, string[] expected)
    {
        var script = ReadReaderScript();
        script = mutation switch
        {
            "no-links-check" => script.Replace("if ($info.NumberOfLinks -ne 1) { 'RESULT=LINKS'; exit 1 }", string.Empty),
            "no-owner-check" => script.Replace("if ($owner -ne [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value) { 'RESULT=OWNER_MISMATCH'; exit 1 }", string.Empty),
            "follow-links" => script.Replace("3, 0x00200000,", "3, 0,"),
            "shared" => script.Replace("[uint32]2147483648, 1,", "[uint32]2147483648, 3,"),
            "ok-before-checks" => script.Replace("        $reader = New-Object System.IO.StreamReader($stream, $true)", string.Empty)
                .Replace("$stream = New-Object System.IO.FileStream($handle, [System.IO.FileAccess]::Read)",
                    "$stream = New-Object System.IO.FileStream($handle, [System.IO.FileAccess]::Read)\n    $reader = New-Object System.IO.StreamReader($stream, $true)"),
            "read-by-path" => script.Replace("$text = $reader.ReadToEnd()", "$text = [System.IO.File]::ReadAllText($Path)"),
            "no-session-check" => script.Replace(SessionCheck, string.Empty),
            "session-unknown-passes" => script.Replace(SessionCheck, SessionCheck.Replace("[string]::IsNullOrEmpty($sessionUser) -or -not ", "-not [string]::IsNullOrEmpty($sessionUser) -and -not ")),
            "no-dacl-check" => script.Replace("if (([long][int]$rule.FileSystemRights -band $writeMask) -ne 0) { 'RESULT=DACL'; exit 1 }", string.Empty),
            _ => script,
        };
        if (mutation != "none")
        {
            script.Should().NotBe(ReadReaderScript(), "変異が実際に適用されていること");
        }

        AnalyzeReaderScript(script).Should().Equal(expected);
    }

    /// <summary>
    /// 読み取りスクリプト（コメント行を除く）が、1 つのハンドルで照合してから読む形になっているかを調べ、欠けているものを返す。
    /// </summary>
    private static List<string> AnalyzeReaderScript(string script)
    {
        var code = string.Join("\n", script.Split('\n').Where(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal)));
        var problems = new List<string>();

        // まず対話セッションのユーザー（WTS）と昇格したユーザーを照合し、違う・取れないなら何も開かずに抜ける。
        // 肩越しの昇格はファイルの属性に依らずここで除外する（DACL は読む時点の状態しか見ないので、WRITE_DAC を持つ
        // 一般ユーザーが移した後に書き換えた形を DACL の確認だけでは止められない）
        var session = code.IndexOf(SessionCheck, StringComparison.Ordinal);
        var sessionSource = code.Contains("$sessionUser = [IccmHandoffFile]::SessionUser()")
            && code.Contains("$elevatedUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name")
            && code.Contains("if (!WTSQuerySessionInformationW(IntPtr.Zero, -1, infoClass, out buffer, out bytes)) return null;")
            && code.Contains("if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(domain)) return null;");
        var firstOpen = code.IndexOf("[IccmHandoffFile]::CreateFileW(", StringComparison.Ordinal);
        if (session < 0 || !sessionSource || firstOpen < 0 || session > firstOpen)
        {
            problems.Add("ファイルを開く前に、対話セッションのユーザーと昇格したユーザーの一致を確かめていない（取れなければ読まない形で）");
        }

        var open = Regex.Match(code, @"\[IccmHandoffFile\]::CreateFileW\(\$Path,\s*\[uint32\]2147483648,\s*(?<share>\d+),\s*\[IntPtr\]::Zero,\s*3,\s*(?<flags>0x[0-9A-Fa-f]+|\d+),");
        if (!open.Success || open.Groups["flags"].Value != "0x00200000")
        {
            problems.Add("リンクを辿らずに開いていない（FILE_FLAG_OPEN_REPARSE_POINT）");
        }

        // 読み取りの共有（FILE_SHARE_READ）は許す（ウイルス対策ソフトの読み取りで失敗させない）。書き込み（2）・削除（4）は許さない
        if (open.Success && (int.Parse(open.Groups["share"].Value, System.Globalization.CultureInfo.InvariantCulture) & 6) != 0)
        {
            problems.Add("書き込み・削除の共有を許して開いている");
        }

        var links = code.IndexOf("if ($info.NumberOfLinks -ne 1) { 'RESULT=LINKS'; exit 1 }", StringComparison.Ordinal);
        if (links < 0)
        {
            problems.Add("ハードリンクの数を確かめていない");
        }

        var owner = code.IndexOf("if ($owner -ne [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value) { 'RESULT=OWNER_MISMATCH'; exit 1 }", StringComparison.Ordinal);
        var ownerFromHandle = code.Contains("$security = $stream.GetAccessControl()")
            && code.Contains("$owner = $security.GetOwner([System.Security.Principal.SecurityIdentifier]).Value");
        if (owner < 0 || !ownerFromHandle || !code.Contains("$stream = New-Object System.IO.FileStream($handle,"))
        {
            problems.Add("所有者を昇格したユーザーと照合していない");
        }

        // 同じハンドルの DACL: 所有者・SYSTEM・Administrators 以外に、書き込み・追記・削除・アクセス許可の変更・所有権の取得
        // （GENERIC_WRITE・GENERIC_ALL を含む）を許す ACE があれば拒否する。所有者の照合だけでは、管理者が所有し一般ユーザーが
        // 書き換えられるファイルを受け渡しファイルの名前へ移して書き換えた形を通してしまう
        var dacl = code.IndexOf("if (([long][int]$rule.FileSystemRights -band $writeMask) -ne 0) { 'RESULT=DACL'; exit 1 }", StringComparison.Ordinal);
        var daclSetup = code.Contains("$writeMask = 0x2 -bor 0x4 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x10000000 -bor 0x40000000")
            && code.Contains("$trusted = @($owner, 'S-1-5-18', 'S-1-5-32-544')")
            && code.Contains("foreach ($rule in $security.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]))")
            && code.Contains("if ($trusted -contains $rule.IdentityReference.Value) { continue }");
        if (dacl < 0 || !daclSetup)
        {
            problems.Add("所有者・SYSTEM・Administrators 以外に書き込みを許す ACE を確かめていない");
        }

        var reparse = code.IndexOf("if (($info.FileAttributes -band 0x400) -ne 0) { 'RESULT=REPARSE'; exit 1 }", StringComparison.Ordinal);
        var lastCheck = new[] { links, owner, reparse, dacl }.Max();
        var firstRead = new[]
        {
            code.IndexOf("New-Object System.IO.StreamReader($stream", StringComparison.Ordinal),
            code.IndexOf("ReadToEnd()", StringComparison.Ordinal),
            code.IndexOf("WriteAllText(", StringComparison.Ordinal),
            code.IndexOf("'RESULT=OK'", StringComparison.Ordinal),
        };
        if (firstRead.Any(i => i < 0) || firstRead.Min() < lastCheck || reparse < 0)
        {
            problems.Add("照合より前に内容を読むか結果を返している");
        }

        if (ReadsByPath(code))
        {
            problems.Add("パスから読み直している");
        }

        return problems;
    }

    /// <summary>
    /// 照合したハンドルを使わず、パスから読み直す形（<c>Get-Content</c> とその別名 <c>gc</c> / <c>type</c> / <c>cat</c>、
    /// <c>[IO.File]::</c> の読み取り（写しへの <c>WriteAllText</c> は除く）、パスを渡す <c>StreamReader</c>）があるか。
    /// </summary>
    private static bool ReadsByPath(string code)
        => Regex.IsMatch(
            code,
            @"Get-Content|(?<![\w-])(?:gc|type|cat)\s|ReadAll(?:Text|Lines|Bytes)|\[(?:System\.)?IO\.File\]::(?!WriteAllText\b)|StreamReader\s*\(\s*\$Path|StreamReader\]::new\s*\(\s*\$Path",
            RegexOptions.IgnoreCase);

    [Theory]
    [InlineData("$t = Get-Content -LiteralPath $Path -Raw", true)]
    [InlineData("$t = gc $Path", true)]
    [InlineData("$t = type $Path", true)]
    [InlineData("$t = cat $Path", true)]
    [InlineData("$t = [IO.File]::ReadAllText($Path)", true)]
    [InlineData("$f = [System.IO.File]::OpenRead($Path)", true)]
    [InlineData("$r = New-Object System.IO.StreamReader($Path)", true)]
    [InlineData("$r = [System.IO.StreamReader]::new($Path)", true)]
    [InlineData("Add-Type -TypeDefinition $source", false)]
    [InlineData("[System.IO.File]::WriteAllText($OutPath, $text, $utf8)", false)]
    [InlineData("$reader = New-Object System.IO.StreamReader($stream, $true)", false)]
    public void 検査ロジック_パスから読み直す形を検出すること(string line, bool expected)
    {
        ReadsByPath(line).Should().Be(expected);
    }

    private static string ReadReaderScript()
        => File.ReadAllText(Path.Combine(TestPaths.GetSolutionRoot(), "installer", ReaderScript)).Replace("\r\n", "\n");

    private static string ReadInstallerText()
        => string.Join("\n", InstallerDataDirectoryAclConventionTests.ReadInstallerLines());

    [Fact]
    public void 受け渡しファイルは元のユーザーが新しく作れたときだけ再解析ポイントを確かめてから読むこと()
    {
        var blanked = InstallerDataDirectoryAclConventionTests.SplitRoutines(CodeWithoutLiterals());
        var literals = InstallerDataDirectoryAclConventionTests.SplitRoutines(CodeWithLiterals());
        blanked.Should().ContainKey(Detect);
        var body = blanked[Detect].Body;
        var source = literals[Detect].Body;

        // 元のユーザー（非昇格）のセッションのマップドドライブを読むため、ExecAsOriginalUser で起動する
        source.Should().Contain("ExecAsOriginalUser(", "昇格したままではユーザーのセッションのマップドドライブが見えない");

        // 置き場所は ProgramData 直下
        Regex.IsMatch(source, @"OutFile\s*:=\s*AddBackslash\s*\(\s*ExpandConstant\s*\(\s*'\{commonappdata\}'\s*\)\s*\)")
            .Should().BeTrue("受け渡しファイルは ProgramData 直下に置く");

        // 既にある名前には書かない（CreateNew）。失敗を終了コードで返す
        source.Should().Contain("-NoClobber", "先回りして作られたファイル・リンクへ書かず、失敗させる");
        Regex.IsMatch(source, @"try\s*\{[\s\S]*?exit 0\s*\}\s*catch\s*\{\s*exit 1\s*\}")
            .Should().BeTrue("書き出しの成否を終了コードで返す（成功は 0、失敗は 1）");

        // 名前は推測できない乱数
        body.Should().Contain("NewUnpredictableToken(", "名前は RtlGenRandom による推測できない値で作る");
        Regex.IsMatch(body, @"\bRandom\s*\(").Should().BeFalse("Random は乱数の種の初期化に依存し、予測できないことを保証しない");

        // 読む前に、終了コードと再解析ポイントを確かめる
        var load = body.IndexOf("ReadOwnedHandoffFile(OutFile", StringComparison.Ordinal);
        load.Should().BeGreaterThan(-1);
        var before = body.Substring(0, load);
        Regex.IsMatch(before, @"\bif\s+ResultCode\s*<>\s*0\s+then\s+begin\b(?:(?!\bend\b)[\s\S])*?\bExit\b")
            .Should().BeTrue("元のユーザーの PowerShell が新しく作れなかったときは、そこにあるファイルを読まない");
        Regex.IsMatch(before, @"\bif\s+not\s+IsDataPathSafe\s*\(\s*OutFile\s*\)\s+then\s+(?:begin\b(?:(?!\bend\b)[\s\S])*?)?\bExit\b")
            .Should().BeTrue("ProgramData・受け渡しファイルが再解析ポイントなら読まない");

        // 消すときも再解析ポイントを確かめる部品（DeleteDataFile）を使う
        body.Should().Contain("DeleteDataFile(OutFile)");
        Regex.IsMatch(body, @"\bDeleteFile\s*\(").Should().BeFalse("確認を経ない DeleteFile で消さない");
    }

    private static string CodeWithLiterals()
        => InstallerDataDirectoryAclConventionTests.StripPascalComments(
            InstallerDataDirectoryAclConventionTests.ReadCodeSection(InstallerDataDirectoryAclConventionTests.ReadInstallerLines()),
            keepLiterals: true);

    private static string CodeWithoutLiterals()
        => InstallerDataDirectoryAclConventionTests.StripPascalComments(
            InstallerDataDirectoryAclConventionTests.ReadCodeSection(InstallerDataDirectoryAclConventionTests.ReadInstallerLines()),
            keepLiterals: false);
}
