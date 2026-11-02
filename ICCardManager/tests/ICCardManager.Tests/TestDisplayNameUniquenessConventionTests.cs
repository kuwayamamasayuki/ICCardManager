#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// テストメソッドの表示名が（理論の引数を除いて）一意であることを固定する規約テスト。
/// </summary>
/// <remarks>
/// <para>
/// CI はテストをシャードに分けて回し、<c>ICCardManager/tools/ci-test-shards.ps1</c> の <c>Get-TestKey</c> が
/// 「実行された表示名（trx。理論の引数は除く）の和集合」と「<c>--list-tests</c> の表示名」を突き合わせて、
/// 全件がちょうど 1 回ずつ実行されたことを確かめる。表示名が別々のメソッドで重複していると、
/// 片方が実行されなくても表示名の集合は一致してしまい、落ちたことを検出できない。
/// </para>
/// <para>
/// 検出ロジックは既知のサンプル入力で固定する（#1786「空振り検出を実データの非空で書かない」）。
/// </para>
/// </remarks>
public class TestDisplayNameUniquenessConventionTests
{
    [Fact]
    public void テストメソッドの表示名は引数を除いて一意であること()
    {
        var duplicates = FindDuplicateDisplayNames(EnumerateTestMethods(typeof(TestDisplayNameUniquenessConventionTests).Assembly));

        duplicates.Should().BeEmpty(
            "シャードの照合（ci-test-shards.ps1）は表示名で突き合わせるため、重複すると片方が実行されなくても検出できない。" +
            "重複した表示名の DisplayName を一意にしてください: " + Environment.NewLine + string.Join(Environment.NewLine, duplicates));
    }

    [Fact]
    public void 検出ロジックが表示名の重複を見つけられること()
    {
        var methods = typeof(SampleWithDuplicates).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => (Type: typeof(SampleWithDuplicates), Method: m))
            .ToList();

        var duplicates = FindDuplicateDisplayNames(methods);

        duplicates.Should().HaveCount(1, "同じ DisplayName を持つ 2 メソッドは 1 件の重複として報告されるはず");
        duplicates[0].Should().Contain("重複する表示名").And.Contain(nameof(SampleWithDuplicates.A)).And.Contain(nameof(SampleWithDuplicates.B));
    }

    [Fact]
    public void 検出ロジックは理論の引数を除いた後で衝突する表示名を見つけること()
    {
        var methods = typeof(SampleCollidingAfterNormalization).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => (Type: typeof(SampleCollidingAfterNormalization), Method: m))
            .ToList();

        var duplicates = FindDuplicateDisplayNames(methods);

        duplicates.Should().HaveCount(2, "CI の照合（Get-TestKey）は末尾の「(引数名: 値)」を除いて比べるので、生の表示名が違っても正規化後に同じなら重複");
        duplicates.Should().Contain(d => d.Contains("同じキー") && d.Contains(nameof(SampleCollidingAfterNormalization.A)) && d.Contains(nameof(SampleCollidingAfterNormalization.B)));
        duplicates.Should().Contain(d => d.Contains("日本語の引数名") && d.Contains(nameof(SampleCollidingAfterNormalization.D)) && d.Contains(nameof(SampleCollidingAfterNormalization.E)),
            "日本語の引数名でも除くこと（Get-TestKey と同じ）");
    }

    [Fact]
    public void 検出ロジックは引数の値に改行があっても引数を除いて衝突を見つけること()
    {
        var methods = typeof(SampleMultilineArgument).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => (Type: typeof(SampleMultilineArgument), Method: m))
            .ToList();

        FindDuplicateDisplayNames(methods).Should().HaveCount(1, "改行を含む引数も Get-TestKey と同じく除かれ、引数の無い同名と衝突するはず");
    }

    [Fact]
    public void 検出ロジックは重複の無い入力で何も返さないこと()
    {
        var methods = typeof(SampleWithoutDuplicates).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => (Type: typeof(SampleWithoutDuplicates), Method: m))
            .ToList();

        FindDuplicateDisplayNames(methods).Should().BeEmpty("DisplayName が異なる・無い（型とメソッド名が異なる）メソッドは重複ではない");
    }

    /// <summary>xUnit が発見するテストメソッド（具象・公開の型。継承したものを含む）を列挙する。</summary>
    internal static IEnumerable<(Type Type, MethodInfo Method)> EnumerateTestMethods(Assembly assembly)
    {
        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract || type.IsGenericTypeDefinition)
            {
                continue;
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                if (method.GetCustomAttribute<FactAttribute>(inherit: true) != null)
                {
                    yield return (type, method);
                }
            }
        }
    }

    /// <summary>表示名が重複しているグループを「表示名と該当するメソッド」の文字列で返す。</summary>
    internal static IReadOnlyList<string> FindDuplicateDisplayNames(IEnumerable<(Type Type, MethodInfo Method)> methods)
    {
        return methods
            .Select(x => (Name: NormalizeToCiKey(GetDisplayName(x.Type, x.Method)), Where: $"{x.Type.FullName}.{x.Method.Name}"))
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .Where(g => g.Count() >= 2)
            .Select(g => $"重複する表示名 \"{g.Key}\": " + string.Join(", ", g.Select(x => x.Where)))
            .ToList();
    }

    // CI の照合のキー（ci-test-shards.ps1 の Get-TestKey）と同じ正規化: 末尾の理論の引数「(引数名: 値...)」を除く。
    // 引数名は Unicode の文字（日本語を含む）。値は改行を含み得る（Singleline = PowerShell の (?s)）。Get-TestKey の正規表現を変えたら、ここも同じに変えること。
    private static readonly Regex TheoryArgumentSuffix = new(@"\([\p{L}_][\p{L}\p{Nd}_]*: .*\)$", RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static string NormalizeToCiKey(string displayName) => TheoryArgumentSuffix.Replace(displayName, string.Empty);

    private static string GetDisplayName(Type type, MethodInfo method)
    {
        var displayName = method.GetCustomAttribute<FactAttribute>(inherit: true)?.DisplayName;
        return string.IsNullOrEmpty(displayName) ? $"{type.FullName}.{method.Name}" : displayName!;
    }

    // xUnit1000（テストクラスは公開）を、この 2 つのサンプルに限って抑制する。
    // 非公開の入れ子クラスは xUnit が発見しない（公開の型だけが対象）ので、[Fact] を付けても実行されるテストにならない。
    // 検出ロジックに「DisplayName が同じ 2 メソッド」という既知の入力を与えるために、実際の属性を持つメソッドが必要。
#pragma warning disable xUnit1000
    private sealed class SampleWithDuplicates
    {
        [Fact(DisplayName = "同じ表示名")]
        public void A() { }

        [Fact(DisplayName = "同じ表示名")]
        public void B() { }

        [Fact(DisplayName = "別の表示名")]
        public void C() { }
    }

    private sealed class SampleCollidingAfterNormalization
    {
        [Fact(DisplayName = "同じキー(a: 1)")]
        public void A() { }

        [Fact(DisplayName = "同じキー")]
        public void B() { }

        [Fact(DisplayName = "別のキー(a: 1)")]
        public void C() { }

        [Fact(DisplayName = "日本語の引数名(金額: 1)")]
        public void D() { }

        [Fact(DisplayName = "日本語の引数名")]
        public void E() { }
    }

    private sealed class SampleMultilineArgument
    {
        [Fact(DisplayName = "改行のキー(exception: System.ArgumentNullException: Value cannot be null.\nParameter name: param)")]
        public void A() { }

        [Fact(DisplayName = "改行のキー")]
        public void B() { }
    }

    private sealed class SampleWithoutDuplicates
    {
        [Fact(DisplayName = "表示名1")]
        public void A() { }

        [Fact]
        public void B() { }
    }
#pragma warning restore xUnit1000
}
