using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Threading;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// カバレッジの計装（coverlet）が本体を書き戻せる前提の検査（Issue #2399）。
/// </summary>
/// <remarks>
/// <para>
/// coverlet は計装したモジュールを Mono.Cecil で書き戻す。そのとき enum 型の定数
/// （既定値付きの引数・<c>const</c> フィールド）は、基になる整数型を知るために enum の定義を解決する。
/// データコレクターは .NET（Core）上で動き GAC を探さないため、enum を定義するアセンブリが
/// モジュールと同じフォルダ（テストの出力）に無いと「Unable to instrument module」で
/// <b>そのモジュールだけが計測から黙って抜ける</b>。#2399 では
/// <c>DispatcherObservation.InvokeAsyncObserved</c> の <c>DispatcherPriority priority = DispatcherPriority.Normal</c>
/// が WindowsBase を要求し、本体（ICCardManager.exe）が一度も計測されていなかった。
/// </para>
/// <para>
/// テストの csproj（<c>CopyWpfReferenceAssembliesForCoverage</c>）が WPF の参照アセンブリを出力へ置いて解決させている。
/// 本テストは、計測する各モジュールの enum 定数が要求するアセンブリがすべて出力にあること（または mscorlib であること）を確かめ、
/// 出力に無いアセンブリの enum を既定値に使ったときに、CI のカバレッジが黙って欠けるより先に赤くする。
/// </para>
/// </remarks>
public class CoverageInstrumentationPrerequisiteTests
{
    /// <summary>
    /// データコレクター自身のランタイムが型の転送で解決できるアセンブリ（出力に無くてよい）。
    /// 本体の計装が実際に成功していること（Issue #2399 で実測）がこの扱いの根拠。
    /// </summary>
    private static readonly HashSet<string> ResolvedByCollectorRuntime =
        new(StringComparer.OrdinalIgnoreCase) { "mscorlib" };

    /// <summary>計測の対象になるモジュール（テストが参照し、出力へ PDB ごと置かれるプロジェクト）。</summary>
    public static IEnumerable<object[]> MeasuredModules() => new[]
    {
        new object[] { typeof(ICCardManager.App).Assembly.GetName().Name! },
        new object[] { typeof(DebugDataViewer.App).Assembly.GetName().Name! },
    };

    [Theory]
    [MemberData(nameof(MeasuredModules))]
    public void 計測するモジュールのenum定数が要求するアセンブリはテストの出力にあること(string moduleName)
    {
        var module = Assembly.Load(moduleName);
        var outputDirectory = Path.GetDirectoryName(new Uri(module.CodeBase).LocalPath)!;

        var missing = FindEnumConstants(GetLoadableTypes(module))
            .Where(c => c.EnumType.Assembly != module)
            .Where(c => !ResolvedByCollectorRuntime.Contains(c.EnumType.Assembly.GetName().Name!))
            .Where(c => !ExistsInDirectory(outputDirectory, c.EnumType.Assembly.GetName().Name!))
            .Select(c => $"{c.Member}: {c.EnumType.FullName}（{c.EnumType.Assembly.GetName().Name}）")
            .ToList();

        missing.Should().BeEmpty(
            $"{moduleName} の enum 定数の定義がテストの出力（{outputDirectory}）に無いと、coverlet が {moduleName} を計装できず" +
            "カバレッジから黙って抜ける。ICCardManager.Tests.csproj の CopyWpfReferenceAssembliesForCoverage に" +
            "そのアセンブリを加えること");
    }

    /// <summary>
    /// 検出ロジック自体を既知の入力で固定する。実データ（本体）から enum 定数の既定値が無くなっても、
    /// 検出が空振りしていないことはこちらで表明し続ける（development-conventions.md「空振り検出を各対象が非空で書かない」）。
    /// </summary>
    [Fact]
    public void enum定数の検出は既定値付きの引数とconstフィールドとNullableの既定値を拾うこと()
    {
        var found = FindEnumConstants(new[] { typeof(Sample) }).ToList();

        found.Select(c => (c.Member, c.EnumType)).Should().BeEquivalentTo(new[]
        {
            ("Sample.Dispatch(priority)", typeof(DispatcherPriority)),
            ("Sample.Sample(color)", typeof(ConsoleColor)),
            ("Sample.MaybeDispatch(priority)", typeof(DispatcherPriority)),
            ("Sample.DefaultPriority", typeof(DispatcherPriority)),
        });
    }

    private static IEnumerable<(string Member, Type EnumType)> FindEnumConstants(IEnumerable<Type> types)
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                 BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var type in types)
        {
            var methods = type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All));
            foreach (var method in methods)
            {
                foreach (var parameter in method.GetParameters())
                {
                    var enumType = AsEnum(parameter.ParameterType);
                    if (enumType != null && parameter.HasDefaultValue)
                    {
                        yield return ($"{type.Name}.{(method.IsConstructor ? type.Name : method.Name)}({parameter.Name})", enumType);
                    }
                }
            }

            foreach (var field in type.GetFields(All).Where(f => f.IsLiteral))
            {
                var enumType = AsEnum(field.FieldType);
                if (enumType != null)
                {
                    yield return ($"{type.Name}.{field.Name}", enumType);
                }
            }
        }
    }

    private static Type? AsEnum(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsEnum ? underlying : null;
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

    private static bool ExistsInDirectory(string directory, string assemblyName) =>
        File.Exists(Path.Combine(directory, assemblyName + ".dll")) ||
        File.Exists(Path.Combine(directory, assemblyName + ".exe"));

    private class Sample
    {
        public const DispatcherPriority DefaultPriority = DispatcherPriority.Background;

        public Sample(ConsoleColor color = ConsoleColor.Red)
        {
        }

        public static void Dispatch(DispatcherPriority priority = DispatcherPriority.Normal)
        {
        }

        public static void MaybeDispatch(DispatcherPriority? priority = DispatcherPriority.Normal)
        {
        }

        public static void NoDefault(DispatcherPriority priority)
        {
        }

        public static void NotEnum(int count = 1)
        {
        }
    }
}
