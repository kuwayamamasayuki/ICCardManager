using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using ICCardManager.Common.Charting;
using Xunit;

namespace ICCardManager.Tests.Common.Charting;

/// <summary>
/// 集約系列名フォーマッタの公開面（Issue #1884）
/// </summary>
/// <remarks>
/// Issue #1858 は「もはやどの系列の <c>Name</c> とも一致しない旧表示名の定数」
/// （<c>AdminDashboardService.OtherSeriesName</c>）を削除したが、同じ形の定数
/// （<c>ChartSeriesNameFormatter.OtherSeriesBaseName</c>）が同値・同スコープのまま
/// 隣のファイルで復活していた（Issue #1884）。個別テストは「その定数が存在しないこと」を
/// 表明できないため、フォーマッタの<b>公開面そのもの</b>を固定する。
/// <para>
/// 表示名を組み立てる関数だけを外へ出し、その材料（基底名・書式）は外から見えない。
/// こうしておくと <c>series.Name == 基底名</c> のような<b>静かに常に偽になる判定</b>を
/// そもそも書けない。規約でそう書いただけでは型は何も保証しない（Issue #1883）。
/// </para>
/// </remarks>
public class ChartSeriesNameFormatterSurfaceConventionTests
{
    private static readonly Type FormatterType = typeof(ChartSeriesNameFormatter);

    /// <summary>
    /// フォーマッタが外へ出してよい唯一のメンバー名。
    /// </summary>
    /// <remarks>
    /// <c>nameof(ChartSeriesNameFormatter.BuildOtherSeriesName)</c> で書くと、
    /// 「正規手段が外から呼べること」の表明が<b>実行時には決して落ちない</b>
    /// （メンバーを private にしたり消したりすると <c>nameof</c> 自体がコンパイルエラーになり、
    /// 表明はそもそも実行されない）。公開面を固定するテストなので、
    /// 固定したい名前はリテラルで書く（本番側の識別子から引かない）。
    /// </remarks>
    private const string AllowedMemberName = "BuildOtherSeriesName";

    [Fact]
    public void 表示名の組み立て以外のメンバーを外へ公開しないこと()
    {
        var exposed = GetExposedMemberNames();

        exposed.Should().OnlyContain(
            name => name == AllowedMemberName,
            "基底名や書式を外から参照できると、集約系列の Name と一致しない値での比較を書けてしまう "
                + "（Issue #1858 で削除した AdminDashboardService.OtherSeriesName と同じ形）。"
                + $"実際の公開メンバー: {string.Join(", ", exposed)}");
    }

    [Fact]
    public void 表示名の組み立ては外から呼べること()
    {
        // 対の表明。公開面を絞る検査だけを置くと、メンバーを全部 private にした（＝誰も使えない）
        // 実装でも緑になる。唯一の正規手段が実際に使えることを併せて固定する。
        GetExposedMemberNames().Should().Contain(AllowedMemberName);
    }

    [Fact]
    public void 検査が公開メンバーを実際に拾えること()
    {
        // 空振り検出。抽出条件（DeclaredOnly / コンパイラ生成の除外）が過剰になって
        // 収集結果が空になると、上の 2 件は表明したい性質を一度も検査しないまま緑になる。
        // 既知のサンプル型で「public / internal は拾い、private は拾わない」ことを固定する。
        var sample = GetExposedMemberNames(typeof(SurfaceSample));

        sample.Should().BeEquivalentTo(new[]
        {
            nameof(SurfaceSample.PublicMethod),
            nameof(SurfaceSample.PublicField),
            nameof(SurfaceSample.PublicProperty),
            nameof(SurfaceSample.PublicEvent),
            "InternalConst",
            "InternalNestedType",
        });
    }

    private static string[] GetExposedMemberNames() => GetExposedMemberNames(FormatterType);

    private static string[] GetExposedMemberNames(Type type)
    {
        const BindingFlags Flags = BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.Static
            | BindingFlags.Instance
            | BindingFlags.DeclaredOnly;

        return type.GetMembers(Flags)
            .Where(IsExposed)
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// アセンブリの外側（＝本テストのように <c>InternalsVisibleTo</c> 越し）から
    /// 名前で到達できるメンバーか。<c>private</c> / <c>protected</c> は到達できないので対象外。
    /// </summary>
    private static bool IsExposed(MemberInfo member)
    {
        if (member.GetCustomAttribute<CompilerGeneratedAttribute>() != null)
        {
            return false;
        }

        switch (member)
        {
            case FieldInfo field:
                return field.IsPublic || field.IsAssembly || field.IsFamilyOrAssembly;

            case MethodBase method:
                // プロパティのアクセサ（get_/set_）はプロパティ側で数えるため除外する。
                return !method.IsSpecialName
                    && (method.IsPublic || method.IsAssembly || method.IsFamilyOrAssembly);

            case PropertyInfo property:
                return property.GetAccessors(nonPublic: true).Any(
                    a => a.IsPublic || a.IsAssembly || a.IsFamilyOrAssembly);

            case Type nested:
                // 入れ子の型。private な実装補助（書式をまとめた private 入れ子クラス等）まで
                // 「公開面が増えた」と数えると、規約を守っている変更でこのガードが赤になり、
                // 修正者を「対象から外す」方向へ誘導する（Issue #1786）。到達できるものだけ数える。
                return nested.IsNestedPublic || nested.IsNestedAssembly || nested.IsNestedFamORAssem;

            case EventInfo evt:
                return evt.GetAddMethod(nonPublic: true) is MethodInfo add
                    && (add.IsPublic || add.IsAssembly || add.IsFamilyOrAssembly);

            default:
                // 上記以外。フォーマッタには存在しないが、
                // 追加されたら「公開面が増えた」として検出したい。
                return true;
        }
    }

    /// <summary>検査ロジックを固定するためのサンプル型（本番コードとは無関係）</summary>
    private sealed class SurfaceSample
    {
        internal const string InternalConst = "sample";

        public string PublicField = string.Empty;

        private readonly string _privateField = string.Empty;

        public string PublicProperty => _privateField;

        private string PrivateProperty => _privateField;

        public void PublicMethod()
        {
        }

        private void PrivateMethod()
        {
        }

        // 入れ子の型・イベントは MemberInfo の既定分岐へ落ちるため、
        // 到達可否で数えていることを public / internal / private の 3 通りで固定する。
        // イベントは明示アクセサで宣言する（自動実装イベントは未使用で CS0067 になる）。
        public event EventHandler PublicEvent
        {
            add { }
            remove { }
        }

        private event EventHandler PrivateEvent
        {
            add { }
            remove { }
        }

        internal sealed class InternalNestedType
        {
        }

        private sealed class PrivateNestedType
        {
        }
    }
}
