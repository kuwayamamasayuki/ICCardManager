using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Infrastructure;

/// <summary>
/// Issue #2083: STA スレッドを使うテストが <see cref="StaThreadCollection"/> に属し、
/// STA スレッドの組み立てが <see cref="StaTestRunner"/> 1 か所に留まることを静的検査で固定する。
/// </summary>
/// <remarks>
/// <para>
/// 個別の <c>[Collection]</c> 属性を <c>InlineData</c> で列挙する形（先行事例の
/// <c>DbContextUiThreadHookCollectionConfigurationTests</c>）にはしない。
/// 列挙はテストクラスが増えたときに静かに漏れるため、
/// <b>対象をソース走査から導出する</b>（<c>.claude/rules/development-conventions.md</c> #1786
/// 「ガードを書くときは経路を列挙する」／#1764「走査対象をファイル名で列挙しない」）。
/// </para>
/// <para>
/// 検査は「禁止形の不在」と「正しい形の存在」を対で表明する。
/// 不在だけを見ると、走査が 0 件に縮んだ状態でも緑になる。
/// </para>
/// <para>
/// <b>導出の軸も列挙になり得る。</b>初版は「行頭にあるクラス宣言」をトップレベルとみなしており、
/// <b>ブロック形式の <c>namespace</c>（本プロジェクトに 23 ファイル実在）では宣言が字下げされるため
/// 1 クラスも拾わなかった</b>。そのファイルから <see cref="StaTestRunner"/> を呼んでも
/// 付与漏れが検出されない fail-open で、コードレビューで検出した。
/// 字下げを許すだけではネストクラスを拾ってしまうので、<b>波括弧の深さ</b>で判定する
/// （<see cref="ExtractTopLevelTypeNames"/>）。
/// </para>
/// </remarks>
public class StaThreadCollectionConventionTests
{
    /// <summary>
    /// STA スレッドを組み立ててよい唯一の場所（ソリューションルートからの相対パス）。
    /// </summary>
    /// <remarks>
    /// ファイル名だけで照合すると、別フォルダーに同名のファイルを置くだけで免除される。
    /// </remarks>
    private static readonly string HelperRelativePath =
        Path.Combine("tests", "ICCardManager.Tests", "Infrastructure", "StaTestRunner.cs");

    /// <summary>
    /// 自前で STA スレッドを組んでいることを示す字句。
    /// </summary>
    /// <remarks>
    /// <c>ApartmentState.STA</c> ではなく <c>SetApartmentState</c> を見る。
    /// 前者は「STA で走っていること」を表明するテスト側（<see cref="StaTestRunnerTests"/>）にも現れるため、
    /// 正当なコードを違反として検出してしまう（誤検出はガード自体の寿命を縮める。#1764）。
    /// </remarks>
    private const string ThreadApartmentMarker = "SetApartment" + "State";

    /// <summary>共有ヘルパーの利用を示す字句。</summary>
    private const string RunnerInvocationMarker = "StaTestRunner" + ".Run";

    /// <summary>
    /// 行頭（字下げは許容）に現れる型宣言。<c>where T : class</c> のような制約に一致しないよう、
    /// 宣言キーワードの前に置けるのは修飾子だけに限る。
    /// </summary>
    private static readonly Regex TypeDeclarationPattern = new(
        @"(?m)^[ \t]*(?:(?:public|internal|private|protected|sealed|static|abstract|partial|file|unsafe)\s+)*(?:class|record|struct|interface)\s+(?<name>\w+)",
        RegexOptions.Compiled);

    /// <summary>ファイルスコープ名前空間（<c>namespace X;</c>）。</summary>
    private static readonly Regex FileScopedNamespacePattern = new(
        @"(?m)^\s*namespace\s+[\w.]+\s*;",
        RegexOptions.Compiled);

    [Fact]
    public void Collection定義が並列実行を無効化していること()
    {
        var attributeData = CustomAttributeData.GetCustomAttributes(typeof(StaThreadCollection))
            .FirstOrDefault(a => a.AttributeType == typeof(CollectionDefinitionAttribute));

        attributeData.Should().NotBeNull("StaThreadCollection は CollectionDefinition 属性を持つ必要がある");
        (attributeData!.ConstructorArguments[0].Value as string).Should().Be(StaThreadCollection.Name,
            "CollectionDefinition に渡された名前は定数 Name と一致する必要がある");
        attributeData.NamedArguments
            .FirstOrDefault(a => a.MemberName == nameof(CollectionDefinitionAttribute.DisableParallelization))
            .TypedValue.Value
            .Should().Be(true,
                "DisableParallelization=true でないと STA スレッドが他のコレクションと CPU を奪い合う（Issue #2083 の再発）");
    }

    [Fact]
    public void STAヘルパーを使うテストクラスがCollectionに属していること()
    {
        var checkedClasses = new List<string>();

        foreach (var (fileName, className) in FindClassesUsing(RunnerInvocationMarker))
        {
            var type = ResolveTestType(className, fileName);
            if (!HasTestMethods(type))
            {
                // 同じファイルに同居するヘルパー・Fake にまで [Collection] を求めない（誤検出の回避。#1764）
                continue;
            }

            checkedClasses.Add(className);

            var attributeData = CustomAttributeData.GetCustomAttributes(type)
                .FirstOrDefault(a => a.AttributeType == typeof(CollectionAttribute));

            attributeData.Should().NotBeNull(
                $"{className}（{fileName}）は StaTestRunner を使うので [Collection(StaThreadCollection.Name)] が必要");
            (attributeData!.ConstructorArguments[0].Value as string).Should().Be(StaThreadCollection.Name,
                $"{className}（{fileName}）の Collection 名が一致しない（StaThreadCollection.Name を参照すること）");
        }

        checkedClasses.Should().NotBeEmpty("走査が 0 件に縮んでいたら、この検査は何も守っていない");
    }

    /// <summary>
    /// 走査が既知の対象を実際に拾えていること（前の検査の空振り検出）。
    /// </summary>
    [Fact]
    public void 走査が既知のSTAテストクラスを拾えていること()
    {
        var users = FindClassesUsing(RunnerInvocationMarker).Select(u => u.ClassName).ToList();

        users.Should().Contain(nameof(ICCardManager.Tests.ViewModels.PrintPreviewViewModelPrintTests));
        users.Should().Contain(nameof(ICCardManager.Tests.Services.DialogServiceOwnerTests));
        users.Should().Contain(nameof(StaTestRunnerTests));
    }

    /// <summary>
    /// クラス抽出が「ブロック形式 namespace の字下げ」と「ネストクラス」を区別できること。
    /// </summary>
    /// <remarks>
    /// 実データ（本リポジトリのソース）だけで表明すると、抽出が縮んでも
    /// 上の検査は既知 3 クラスが残る限り緑になり得る。検査ロジック自体を
    /// サンプル入力で固定する（#1786「検査ロジック自体を既知のサンプル入力で固定」）。
    /// </remarks>
    [Fact]
    public void クラス抽出が字下げとネストを区別できること()
    {
        const string fileScoped = @"namespace A.B;

public class Outer
{
    private sealed class Nested { }
}

internal static class Helper { }
";
        const string blockScoped = @"namespace A.B
{
    public class Outer
    {
        private sealed class Nested { }
    }

    internal static class Helper { }
}
";
        const string genericConstraint = @"namespace A.B;

public class WithConstraint<T>
    where T : class
{
}
";

        ExtractTopLevelTypeNames(fileScoped).Should().Equal(new[] { "Outer", "Helper" },
            "ファイルスコープ名前空間では深さ 0 がトップレベル");
        ExtractTopLevelTypeNames(blockScoped).Should().Equal(new[] { "Outer", "Helper" },
            "ブロック形式でも字下げされたトップレベルを拾うこと（初版はここで 0 件だった）");
        ExtractTopLevelTypeNames(genericConstraint).Should().Equal(new[] { "WithConstraint" },
            "where T : class を型宣言と読み違えないこと");
    }

    [Fact]
    public void STAスレッドの組み立てが共有ヘルパーの外に複製されていないこと()
    {
        var duplicates = EnumerateTestSources()
            .Where(path => !IsHelper(path))
            .Where(path => TestSourceInspection.ToCodeOnly(File.ReadAllText(path))
                .IndexOf(ThreadApartmentMarker, StringComparison.Ordinal) >= 0)
            .Select(ToSolutionRelativePath)
            .ToList();

        duplicates.Should().BeEmpty(
            "STA スレッドの組み立ては StaTestRunner 1 か所に留めること。" +
            "複製があると、待ち方の方針を変える人が片方を取りこぼす（#1763）。" +
            $"複製: {string.Join(", ", duplicates)}");
    }

    /// <summary>
    /// 上の検査が探している形がヘルパー自身には実在し、かつ走査が
    /// <b>両方のテストプロジェクト</b>に届いていること（極性と範囲の確認）。
    /// </summary>
    /// <remarks>
    /// 初版は <c>ICCardManager.Tests</c> だけを走査しており、
    /// FlaUI で WPF を直接扱う <c>ICCardManager.UITests</c> が範囲外だった
    /// （#1786 の表が「走査対象から漏れたプロジェクト（ICCardManager.UITests）」を
    /// 過去の見落としとして名指ししている。同じ形の再発をコードレビューで検出）。
    /// </remarks>
    [Fact]
    public void 走査がヘルパーと両テストプロジェクトに届いていること()
    {
        var sources = EnumerateTestSources().ToList();

        var helper = sources.Single(IsHelper);
        TestSourceInspection.ToCodeOnly(File.ReadAllText(helper))
            .Should().Contain(ThreadApartmentMarker,
                "検査対象の字句がヘルパーにも無いなら、この検査は何も検出できない");

        var unitPrefix = Path.Combine("tests", "ICCardManager.Tests") + Path.DirectorySeparatorChar;
        var uiPrefix = Path.Combine("tests", "ICCardManager.UITests") + Path.DirectorySeparatorChar;

        sources.Should().Contain(p => ToSolutionRelativePath(p).StartsWith(unitPrefix, StringComparison.Ordinal));
        sources.Should().Contain(p => ToSolutionRelativePath(p).StartsWith(uiPrefix, StringComparison.Ordinal),
            "UI テストは WPF を直接扱うので、STA スレッドの複製が最も生まれやすい");
    }

    /// <summary>
    /// 両テストプロジェクトの <c>.cs</c> を列挙する（<c>bin</c> / <c>obj</c> を除く）。
    /// </summary>
    private static IEnumerable<string> EnumerateTestSources()
    {
        var root = Path.Combine(TestPaths.GetSolutionRoot(), "tests");
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => path.IndexOf($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) < 0)
            .Where(path => path.IndexOf($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) < 0);
    }

    private static bool IsHelper(string path)
        => string.Equals(ToSolutionRelativePath(path), HelperRelativePath, StringComparison.Ordinal);

    private static string ToSolutionRelativePath(string path)
    {
        var root = TestPaths.GetSolutionRoot() + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.Ordinal) ? path.Substring(root.Length) : path;
    }

    /// <summary>
    /// <paramref name="marker"/> を含むソースから、トップレベルの型名を集める。
    /// 対象は <c>ICCardManager.Tests</c>（型をリフレクションで解決できる唯一のアセンブリ）に限る。
    /// </summary>
    private static IReadOnlyList<(string FileName, string ClassName)> FindClassesUsing(string marker)
    {
        var testsRoot = Path.Combine("tests", "ICCardManager.Tests") + Path.DirectorySeparatorChar;
        var results = new List<(string, string)>();

        foreach (var path in EnumerateTestSources())
        {
            var relative = ToSolutionRelativePath(path);
            if (IsHelper(path) || !relative.StartsWith(testsRoot, StringComparison.Ordinal))
            {
                continue;
            }

            var code = TestSourceInspection.ToCodeOnly(File.ReadAllText(path));
            if (code.IndexOf(marker, StringComparison.Ordinal) < 0)
            {
                continue;
            }

            foreach (var name in ExtractTopLevelTypeNames(code))
            {
                results.Add((Path.GetFileName(path), name));
            }
        }

        return results;
    }

    /// <summary>
    /// コードのみのソースから、トップレベル（名前空間直下）の型名を宣言順に返す。
    /// </summary>
    /// <remarks>
    /// 行頭固定では<b>ブロック形式 namespace の字下げを拾えず</b>、字下げを許すだけでは
    /// <b>ネストクラスを拾ってしまう</b>。宣言位置までの波括弧の深さで判定する。
    /// </remarks>
    internal static IReadOnlyList<string> ExtractTopLevelTypeNames(string codeOnlySource)
    {
        var expectedDepth = FileScopedNamespacePattern.IsMatch(codeOnlySource) ? 0 : 1;
        var names = new List<string>();

        foreach (Match match in TypeDeclarationPattern.Matches(codeOnlySource))
        {
            if (DepthAt(codeOnlySource, match.Index) == expectedDepth)
            {
                names.Add(match.Groups["name"].Value);
            }
        }

        return names;
    }

    private static int DepthAt(string source, int index)
    {
        var depth = 0;
        for (var i = 0; i < index; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
            }
        }

        return depth;
    }

    private static bool HasTestMethods(Type type)
        => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Any(m => m.GetCustomAttributes(typeof(FactAttribute), inherit: true).Length > 0);

    private static Type ResolveTestType(string className, string fileName)
    {
        var candidates = typeof(StaThreadCollection).Assembly.GetTypes()
            .Where(t => !t.IsNested && string.Equals(t.Name, className, StringComparison.Ordinal))
            .ToList();

        candidates.Should().ContainSingle(
            $"{className}（{fileName}）の型を一意に解決できること（解決できない型があると検査対象が静かに縮む）");
        return candidates[0];
    }
}
