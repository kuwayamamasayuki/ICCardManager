using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Infrastructure;

/// <summary>
/// Issue #2213 / #2221: スレッドプールを塞ぐテストが、他のテストと並列に走らないことを固定する。
/// </summary>
/// <remarks>
/// <para>
/// プールを塞いでいる間に他のテストが走ると、そのテストの <c>Task.Run</c> や <c>await</c> の続きが空きを待たされ、
/// 無関係なテストが時間切れで失敗し得る。塞ぐテストクラスは <see cref="ThreadPoolSaturationCollection"/>
/// （<c>DisableParallelization = true</c>）に属させる。属させ忘れても、塞ぐテスト自身や他のテストは緑のまま
/// （間欠的に失敗するだけ）なので、静的に検査する。
/// </para>
/// <para>
/// 塞ぐテストクラスは、メソッド本体（async メソッドの状態機械・ラムダの生成クラスを含む）の IL から
/// <see cref="ThreadPoolSaturator.Saturate"/> の呼び出しを探して<b>クラス単位で</b>導出する。
/// ソースの文字列照合だと、1 ファイルに 2 クラスあるときに片方の属させ忘れを見逃し、
/// コレクション名を文字列で書いた正当な形（<c>[Collection("Thread Pool Saturation")]</c>）を誤検出する。
/// 所属は <see cref="CollectionAttribute"/> の引数の値で判定するので、どちらの書き方も同じに扱う。
/// </para>
/// </remarks>
public class ThreadPoolSaturationCollectionConventionTests
{
    private static readonly MethodInfo SaturateMethod =
        typeof(ThreadPoolSaturator).GetMethod(nameof(ThreadPoolSaturator.Saturate), BindingFlags.Public | BindingFlags.Static)!;

    [Fact]
    public void プールを塞ぐコレクションは他のテストと並列に走らないこと()
    {
        // DisableParallelization を外すと、プールを塞いでいる間に他のテストの Task.Run や await の続きが
        // 空きを待たされ、無関係なテストが時間切れで失敗し得る（属性を消しても他のテストは緑のまま）
        var definition = CustomAttributeData.GetCustomAttributes(typeof(ThreadPoolSaturationCollection))
            .Single(a => a.AttributeType == typeof(CollectionDefinitionAttribute));

        definition.ConstructorArguments[0].Value.Should().Be(ThreadPoolSaturationCollection.Name);
        definition.NamedArguments
            .Single(a => a.MemberName == nameof(CollectionDefinitionAttribute.DisableParallelization))
            .TypedValue.Value.Should().Be(true);
    }

    [Fact]
    public void プールを塞ぐテストクラスはすべて並列に走らないコレクションに属していること()
    {
        var saturatingClasses = FindClassesCallingSaturate(typeof(ThreadPoolSaturator).Assembly)
            .Where(t => t.DeclaringType != typeof(ThreadPoolSaturationCollectionConventionTests))
            .ToList();

        // 空振り検出: 導出が 0 件に縮むと、下の検査は何も見ずに緑になる
        saturatingClasses.Select(t => t.Name).Should().Contain(
            new[] { "DbContextCheckConnectionThreadPoolTests", "ReportViewModelExportStatusThreadPoolTests" });

        saturatingClasses
            .Where(t => CollectionNameOf(t) != ThreadPoolSaturationCollection.Name)
            .Select(t => t.FullName)
            .Should().BeEmpty("プールを塞ぐテストクラスは ThreadPoolSaturationCollection に属すること");
    }

    [Fact]
    public void 導出がasyncメソッドとラムダの中の呼び出しを拾うこと()
    {
        // 検出ロジック自体を既知のサンプルで固定する（#1786）。サンプルは本クラスの入れ子で、上の検査の対象からは外す
        var found = FindClassesCallingSaturate(typeof(ThreadPoolSaturationCollectionConventionTests).Assembly);

        found.Should().Contain(new[] { typeof(SyncSample), typeof(AsyncSample), typeof(LambdaSample) });
        found.Should().NotContain(typeof(NonCallingSample));
    }

    /// <summary>
    /// <see cref="ThreadPoolSaturator.Saturate"/> を呼ぶメソッドを持つクラスを返す。
    /// async メソッドの状態機械やラムダの生成クラスはコンパイラーが作る入れ子の型になるので、それを書いたクラスへ寄せる
    /// （入れ子のテストクラスは xUnit では独立したテストクラスなので、それ自身として扱う）。
    /// </summary>
    private static IReadOnlyList<Type> FindClassesCallingSaturate(Assembly assembly)
    {
        var result = new HashSet<Type>();
        foreach (var type in GetLoadableTypes(assembly))
        {
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Cast<MethodBase>()
                .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));

            if (methods.Any(CallsSaturate))
            {
                result.Add(AuthoredType(type));
            }
        }

        return result.Where(t => t != typeof(ThreadPoolSaturator)).ToList();
    }

    private static bool CallsSaturate(MethodBase method)
    {
        byte[]? il;
        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or BadImageFormatException)
        {
            return false;
        }

        if (il == null)
        {
            return false;
        }

        // call（0x28）の後ろの 4 バイトがメソッドトークン。オペランドの途中の 0x28 を拾っても、
        // 解決できないか別のメソッドに解決されるだけなので、Saturate と一致したときだけ真にする
        for (var i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x28)
            {
                continue;
            }

            var token = BitConverter.ToInt32(il, i + 1);
            try
            {
                var target = method.Module.ResolveMethod(
                    token,
                    method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null,
                    method.IsGenericMethod ? method.GetGenericArguments() : null);
                if (target == SaturateMethod)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or BadImageFormatException or TypeLoadException or MissingMemberException)
            {
                // トークンではない（オペランドの途中の 0x28）
            }
        }

        return false;
    }

    private static string? CollectionNameOf(Type type)
        => CustomAttributeData.GetCustomAttributes(type)
            .Where(a => a.AttributeType == typeof(CollectionAttribute))
            .Select(a => a.ConstructorArguments[0].Value as string)
            .FirstOrDefault();

    private static Type AuthoredType(Type type)
    {
        while (type.DeclaringType != null && type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false))
        {
            type = type.DeclaringType;
        }

        return type;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null)!;
        }
    }

    // 以下は検出ロジックのサンプル（実行はしない）

    private sealed class SyncSample
    {
        public static void Use()
        {
            using var saturator = ThreadPoolSaturator.Saturate(1);
        }
    }

    private sealed class AsyncSample
    {
        public static async System.Threading.Tasks.Task UseAsync()
        {
            await System.Threading.Tasks.Task.Yield();
            using var saturator = ThreadPoolSaturator.Saturate(1);
        }
    }

    private sealed class LambdaSample
    {
        public static Action Use() => () =>
        {
            using var saturator = ThreadPoolSaturator.Saturate(1);
        };
    }

    private sealed class NonCallingSample
    {
        public static int Use() => 0x28;
    }
}
