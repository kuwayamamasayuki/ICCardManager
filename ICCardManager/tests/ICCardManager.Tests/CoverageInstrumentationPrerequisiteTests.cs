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
/// coverlet は計装したモジュールを Mono.Cecil で書き戻す。そのとき enum 型の定数（既定値付きの引数・<c>const</c> フィールド）は、
/// 基になる整数型を知るために enum の定義を解決する。データコレクターは .NET（Core）上で動き GAC を探さないため、
/// enum を定義するアセンブリがモジュールと同じフォルダ（テストの出力）に無いと「Unable to instrument module」で
/// <b>そのモジュールだけが計測から黙って抜ける</b>。#2399 では
/// <c>DispatcherObservation.InvokeAsyncObserved</c> の <c>DispatcherPriority priority = DispatcherPriority.Normal</c>
/// が WindowsBase を要求し、本体（ICCardManager.exe）が一度も計測されていなかった。
/// </para>
/// <para>
/// テストの csproj（<c>CopyWpfReferenceAssembliesForCoverage</c>）が WPF の参照アセンブリを出力へ置いて解決させている。
/// 本テストは、計測する各モジュールの enum 定数が要求するアセンブリがすべて出力にあること（または
/// <see cref="ResolvedByCollectorRuntime"/> にあること）を確かめ、CI のカバレッジが黙って欠けるより先に赤くする。
/// </para>
/// <para>
/// <b>属性の引数の enum は対象にしない</b>。Cecil は変更しない属性の引数を元のバイト列のまま書き戻すため解決しない
/// （#2399 で実測: 出力に PresentationCore が無い状態で <c>[DefaultValue(Visibility.Collapsed)]</c> を付けた
/// DebugDataViewer も計装された。本体にも System の <c>EditorBrowsableState</c> を引数に持つ属性が多数ある）。
/// メソッド内のローカル定数は PDB にだけ残りリフレクションで読めないため対象外で、この経路で抜けたときは
/// CI の Report coverage の警告（本体のモジュールの欠落）が受け皿になる。
/// </para>
/// </remarks>
public class CoverageInstrumentationPrerequisiteTests
{
    /// <summary>
    /// データコレクター自身のランタイムが型の転送で解決できるアセンブリ（出力に無くてよい）。
    /// mscorlib の enum を既定値に持つ本体の計装が成功していること（Issue #2399 で実測）がこの扱いの根拠。
    /// System.dll など他の facade の enum で本テストが赤くなったときは、計装が実際に成功するか
    /// （<c>dotnet test --diag</c> のログに「Instrumented module」が出るか）を確かめてからここへ加える。
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

        var missing = FindUnresolvableEnumConstants(module, GetLoadableTypes(module), outputDirectory)
            .Select(c => $"{c.Member}: {c.EnumType.FullName}（{c.EnumType.Assembly.GetName().Name}）")
            .ToList();

        string.Join(Environment.NewLine, missing).Should().BeEmpty(
            $"{moduleName} の enum 定数の定義がテストの出力（{outputDirectory}）に無いと、coverlet が {moduleName} を計装できず" +
            "カバレッジから黙って抜ける。ICCardManager.Tests.csproj の CopyWpfReferenceAssembliesForCoverage に" +
            "そのアセンブリを加えること（データコレクターのランタイムが解決できる facade なら ResolvedByCollectorRuntime の注記を参照）");
    }

    /// <summary>
    /// 検出の前段（enum 定数を拾う）を既知の入力で固定する。実データ（本体）から enum 定数が無くなっても、
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
            ("Sample.LocalDefault(kind)", typeof(SampleKind)),
            ("Sample.DefaultPriority", typeof(DispatcherPriority)),
        });
    }

    /// <summary>
    /// 検出の後段（同じモジュールか・ランタイムが解決できるか・出力にあるか）を既知の入力で固定する。
    /// 後段が常に除外する形に壊れると、前段が拾っても実データの検査は緑のままになるため、前段とは別に表明する。
    /// </summary>
    [Fact]
    public void 出力に無いアセンブリのenumだけを解決できないと判定すること()
    {
        var directory = Path.Combine(Path.GetTempPath(), "coverage-prereq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var types = new[] { typeof(Sample) };
            var testAssembly = typeof(Sample).Assembly;

            // 出力に WindowsBase が無い: DispatcherPriority だけが残り、mscorlib（ConsoleColor）と
            // 同じモジュールの enum（SampleKind）は残らない
            FindUnresolvableEnumConstants(testAssembly, types, directory)
                .Select(c => c.EnumType).Distinct()
                .Should().Equal(typeof(DispatcherPriority));

            // 出力に WindowsBase がある: 何も残らない
            File.WriteAllBytes(Path.Combine(directory, "WindowsBase.dll"), Array.Empty<byte>());
            FindUnresolvableEnumConstants(testAssembly, types, directory).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static IEnumerable<(string Member, Type EnumType)> FindUnresolvableEnumConstants(
        Assembly module, IEnumerable<Type> types, string outputDirectory) =>
        FindEnumConstants(types)
            .Where(c => c.EnumType.Assembly != module)
            .Where(c => !ResolvedByCollectorRuntime.Contains(c.EnumType.Assembly.GetName().Name!))
            .Where(c => !ExistsInDirectory(outputDirectory, c.EnumType.Assembly.GetName().Name!));

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

    private enum SampleKind
    {
        First,
        Second,
    }

    private class Sample
    {
        public const DispatcherPriority DefaultPriority = DispatcherPriority.Background;

        public const int NotEnumConstant = 1;

        public Sample(ConsoleColor color = ConsoleColor.Red)
        {
        }

        public static void Dispatch(DispatcherPriority priority = DispatcherPriority.Normal)
        {
        }

        public static void MaybeDispatch(DispatcherPriority? priority = DispatcherPriority.Normal)
        {
        }

        public static void LocalDefault(SampleKind kind = SampleKind.Second)
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
