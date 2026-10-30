using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #2332: ViewModels 層より内側（Services / Data / Common / Infrastructure / Dtos / Models）が
/// ViewModels 層を参照しないことを固定する規約テスト。
/// </summary>
/// <remarks>
/// <para>
/// 05_クラス設計書 §2 のレイヤー構成は Views → ViewModels → Services の一方向で、§3.3 は
/// サービス層が ViewModel を参照する形を「逆向きの依存」として解消してきた（ReportOutputFolderStore 等）。
/// それでも <c>DashboardService</c> の公開 API が <c>ViewModels/MainViewModel.cs</c> に置かれた
/// <c>DashboardSortOrder</c> を引数に取っており、設計書にも書かれていなかった。列挙型を Dtos へ移し、
/// 同じ形が戻らないよう 2 つの経路で検査する。
/// </para>
/// <list type="bullet">
/// <item>ソース: <c>using ICCardManager.ViewModels</c>・完全修飾名・<c>ViewModels.X</c> の部分修飾（名前空間
/// <c>ICCardManager.*</c> の内側では <c>ViewModels.X</c> で解決できる）</item>
/// <item>型のシグネチャ（リフレクション）: 内側の層の型のメンバーが引数・戻り値・フィールド・プロパティに
/// ViewModels の型を使っていないこと（ソースの書き方に依らず、公開 API に現れる依存を確実に拾う）</item>
/// </list>
/// <para>
/// Views への参照（<c>DialogService</c> などの UI 寄りのサービス）はこの規約の対象外。画面を開くこと
/// そのものが責務のサービスであり、05 にも記載がある。
/// </para>
/// </remarks>
public class LayerDependencyConventionTests
{
    /// <summary>ViewModels 層より内側の層（ソースのディレクトリ名 = 名前空間の第 2 階層）。</summary>
    private static readonly string[] InnerLayers = { "Services", "Data", "Common", "Infrastructure", "Dtos", "Models" };

    private const string ViewModelsNamespace = "ICCardManager.ViewModels";

    /// <summary>
    /// ViewModels 名前空間の参照。<c>ICCardManager.ViewModels</c>（using・完全修飾・global::）と、
    /// 識別子の一部でない <c>ViewModels.</c>（部分修飾）を拾う。<c>MyViewModels.</c> のような別名の一部には一致しない。
    /// </summary>
    private static readonly Regex ViewModelsReference = new Regex(
        @"\bICCardManager\s*\.\s*ViewModels\b|(?<![\w.])ViewModels\s*\.\s*[A-Za-z_]",
        RegexOptions.Compiled);

    [Fact]
    public void 内側の層のソースはViewModels名前空間を参照しないこと()
    {
        var violations = InnerLayers
            .SelectMany(layer => ProductionSourceFiles.CSharp.Under(layer))
            .SelectMany(f => f.CodeOnlyPreservingLines.Split('\n')
                .Select((line, i) => (File: f.RelativePath, Line: i + 1, Text: line))
                .Where(x => ViewModelsReference.IsMatch(x.Text)))
            .Select(x => $"  {x.File}:{x.Line} {x.Text.Trim()}")
            .ToList();

        violations.Should().BeEmpty(
            "ViewModels より内側の層（Services / Data / Common / Infrastructure / Dtos / Models）が ViewModels を参照しています。" +
            "共有する型は Dtos か Common へ置いてください（05_クラス設計書 §2、Issue #2332）。\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void 内側の層の型のシグネチャにViewModelsの型が現れないこと()
    {
        var assembly = typeof(ICCardManager.Services.DashboardService).Assembly;
        var innerTypes = assembly.GetTypes()
            .Where(t => t.Namespace != null && InnerLayers.Any(l => IsInLayer(t.Namespace, l)))
            .ToList();

        var violations = new List<string>();
        foreach (var type in innerTypes)
        {
            foreach (var (member, referenced) in EnumerateSignatureTypes(type))
            {
                if (IsViewModelsType(referenced))
                {
                    violations.Add($"  {type.FullName}.{member} -> {referenced.FullName}");
                }
            }
        }

        violations.Distinct().Should().BeEmpty(
            "ViewModels より内側の層の型が、シグネチャで ViewModels の型を使っています（Issue #2332）。\n" +
            string.Join("\n", violations.Distinct()));
    }

    /// <summary>
    /// 走査が空振りしていないこと。各層のソースと型が実在し、移した列挙型が Dtos にあることを表明する。
    /// </summary>
    [Fact]
    public void 走査対象が実在し_ダッシュボードの並び順はDtosにあること()
    {
        foreach (var layer in InnerLayers)
        {
            ProductionSourceFiles.CSharp.Under(layer).Should().NotBeEmpty($"{layer} 配下の .cs が走査されていること");
        }

        typeof(ICCardManager.Dtos.DashboardSortOrder).Namespace.Should().Be("ICCardManager.Dtos");
        typeof(ICCardManager.Services.DashboardService)
            .GetMethod(nameof(ICCardManager.Services.DashboardService.SortItems))!
            .GetParameters().Select(p => p.ParameterType)
            .Should().Contain(typeof(ICCardManager.Dtos.DashboardSortOrder),
                "シグネチャ検査が実際にメソッドの引数型を見ていることの足場");
    }

    /// <summary>
    /// ソース検査のロジックを既知のサンプルで固定する。
    /// </summary>
    [Theory]
    [InlineData("using ICCardManager.ViewModels;", true)]
    [InlineData("using static ICCardManager.ViewModels.MainViewModel;", true)]
    [InlineData("global::ICCardManager.ViewModels.DashboardSortOrder x;", true)]
    [InlineData("public void Sort(ViewModels.DashboardSortOrder order)", true)]
    [InlineData("using ICCardManager.Views.Dialogs;", false)]
    [InlineData("var v = new Views.Dialogs.CardRegistrationModeDialog();", false)]
    [InlineData("var x = myViewModels.Count;", false)]
    [InlineData("using ICCardManager.ViewModelsHelpers;", false)]
    public void ソース検査はViewModels名前空間の参照だけを拾うこと(string line, bool expected)
    {
        ViewModelsReference.IsMatch(line).Should().Be(expected);
    }

    /// <summary>
    /// シグネチャ検査の判定を既知の型で固定する（配列・ジェネリック引数の内側も見ること）。
    /// </summary>
    [Fact]
    public void シグネチャ検査は配列とジェネリック引数の内側のViewModelsの型も拾うこと()
    {
        var vmType = typeof(ICCardManager.ViewModels.MainViewModel);
        IsViewModelsType(vmType).Should().BeTrue();
        IsViewModelsType(vmType.MakeArrayType()).Should().BeTrue();
        IsViewModelsType(typeof(List<>).MakeGenericType(vmType)).Should().BeTrue();
        IsViewModelsType(typeof(System.Threading.Tasks.Task<>).MakeGenericType(vmType)).Should().BeTrue();
        IsViewModelsType(typeof(List<ICCardManager.Dtos.DashboardSortOrder>)).Should().BeFalse();
        IsViewModelsType(typeof(string)).Should().BeFalse();
    }

    private static bool IsInLayer(string ns, string layer)
    {
        var prefix = "ICCardManager." + layer;
        return ns == prefix || ns.StartsWith(prefix + ".", StringComparison.Ordinal);
    }

    private static bool IsViewModelsType(Type type)
    {
        if (type.HasElementType)
        {
            return IsViewModelsType(type.GetElementType()!);
        }

        if (type.IsGenericType && !type.IsGenericTypeDefinition
            && type.GetGenericArguments().Any(IsViewModelsType))
        {
            return true;
        }

        var ns = type.Namespace;
        return ns != null && (ns == ViewModelsNamespace || ns.StartsWith(ViewModelsNamespace + ".", StringComparison.Ordinal));
    }

    private static IEnumerable<(string Member, Type Referenced)> EnumerateSignatureTypes(Type type)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                   | BindingFlags.Static | BindingFlags.DeclaredOnly;

        if (type.BaseType != null)
        {
            yield return ("(base)", type.BaseType);
        }

        foreach (var i in type.GetInterfaces())
        {
            yield return ("(interface)", i);
        }

        foreach (var f in type.GetFields(Flags))
        {
            yield return (f.Name, f.FieldType);
        }

        foreach (var p in type.GetProperties(Flags))
        {
            yield return (p.Name, p.PropertyType);
        }

        foreach (var m in type.GetMethods(Flags))
        {
            yield return (m.Name, m.ReturnType);
            foreach (var parameter in m.GetParameters())
            {
                yield return ($"{m.Name}({parameter.Name})", parameter.ParameterType);
            }
        }

        foreach (var c in type.GetConstructors(Flags))
        {
            foreach (var parameter in c.GetParameters())
            {
                yield return ($".ctor({parameter.Name})", parameter.ParameterType);
            }
        }
    }
}
