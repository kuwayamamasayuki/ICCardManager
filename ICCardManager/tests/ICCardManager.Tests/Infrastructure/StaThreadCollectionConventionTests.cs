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
/// </remarks>
public class StaThreadCollectionConventionTests
{
    /// <summary>STA スレッドを組み立ててよい唯一の場所。</summary>
    private const string HelperFileName = "StaTestRunner.cs";

    /// <summary>
    /// 自前で STA スレッドを組んでいることを示す字句。
    /// </summary>
    /// <remarks>
    /// <c>ApartmentState.STA</c> ではなく <c>SetApartmentState</c> を見る。
    /// 前者は「STA で走っていること」を表明するテスト側にも現れるため、
    /// 正当なコードを違反として検出してしまう（誤検出はガード自体の寿命を縮める。#1764）。
    /// </remarks>
    private const string ThreadApartmentMarker = "SetApartment" + "State";

    /// <summary>共有ヘルパーの利用を示す字句。</summary>
    private const string RunnerInvocationMarker = "StaTestRunner" + ".Run";

    private static readonly Regex TopLevelClassPattern = new(
        @"(?m)^(?:public\s+|internal\s+)?(?:sealed\s+|static\s+|abstract\s+|partial\s+)*class\s+(\w+)",
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
        var users = FindClassesUsing(RunnerInvocationMarker);

        users.Should().NotBeEmpty("走査が 0 件に縮んでいたら、この検査は何も守っていない");

        foreach (var (fileName, className) in users)
        {
            var type = ResolveTestType(className, fileName);
            var attributeData = CustomAttributeData.GetCustomAttributes(type)
                .FirstOrDefault(a => a.AttributeType == typeof(CollectionAttribute));

            attributeData.Should().NotBeNull(
                $"{className}（{fileName}）は StaTestRunner を使うので [Collection(StaThreadCollection.Name)] が必要");
            (attributeData!.ConstructorArguments[0].Value as string).Should().Be(StaThreadCollection.Name,
                $"{className}（{fileName}）の Collection 名が一致しない（StaThreadCollection.Name を参照すること）");
        }
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

    [Fact]
    public void STAスレッドの組み立てが共有ヘルパーの外に複製されていないこと()
    {
        var duplicates = EnumerateTestSources()
            .Where(path => !string.Equals(Path.GetFileName(path), HelperFileName, StringComparison.Ordinal))
            .Where(path => TestSourceInspection.ToCodeOnly(File.ReadAllText(path)).IndexOf(ThreadApartmentMarker, StringComparison.Ordinal) >= 0)
            .Select(path => Path.GetFileName(path))
            .ToList();

        duplicates.Should().BeEmpty(
            "STA スレッドの組み立ては StaTestRunner 1 か所に留めること。" +
            "複製があると、待ち方の方針を変える人が片方を取りこぼす（#1763）。" +
            $"複製: {string.Join(", ", duplicates)}");
    }

    /// <summary>
    /// 上の検査が探している形が、ヘルパー自身には実際に存在すること（極性の確認）。
    /// </summary>
    [Fact]
    public void 共有ヘルパー自身はSTAスレッドを組み立てていること()
    {
        var helper = EnumerateTestSources()
            .Single(path => string.Equals(Path.GetFileName(path), HelperFileName, StringComparison.Ordinal));

        TestSourceInspection.ToCodeOnly(File.ReadAllText(helper))
            .Should().Contain(ThreadApartmentMarker,
                "検査対象の字句がヘルパーにも無いなら、この検査は何も検出できない");
    }

    private static IEnumerable<string> EnumerateTestSources()
    {
        var root = Path.Combine(TestPaths.GetSolutionRoot(), "tests", "ICCardManager.Tests");
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => path.IndexOf($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) < 0)
            .Where(path => path.IndexOf($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) < 0);
    }

    private static IReadOnlyList<(string FileName, string ClassName)> FindClassesUsing(string marker)
    {
        var results = new List<(string, string)>();

        foreach (var path in EnumerateTestSources())
        {
            if (string.Equals(Path.GetFileName(path), HelperFileName, StringComparison.Ordinal))
            {
                continue;
            }

            var code = TestSourceInspection.ToCodeOnly(File.ReadAllText(path));
            if (code.IndexOf(marker, StringComparison.Ordinal) < 0)
            {
                continue;
            }

            foreach (Match match in TopLevelClassPattern.Matches(code))
            {
                results.Add((Path.GetFileName(path), match.Groups[1].Value));
            }
        }

        return results;
    }

    private static Type ResolveTestType(string className, string fileName)
    {
        var candidates = typeof(StaThreadCollection).Assembly.GetTypes()
            .Where(t => !t.IsNested && string.Equals(t.Name, className, StringComparison.Ordinal))
            .ToList();

        candidates.Should().ContainSingle(
            $"{className}（{fileName}）の型を一意に解決できること（同名のトップレベル型があると検査対象がずれる）");
        return candidates[0];
    }
}
