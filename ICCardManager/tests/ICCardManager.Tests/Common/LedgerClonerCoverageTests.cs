using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// Issue #1959: <see cref="LedgerCloner"/> のコピー漏れを検出する検査。
/// </summary>
/// <remarks>
/// <para>
/// クローンは監査ログの <c>BeforeData</c>（6 年保存）の中身そのものであり、列を足したときに
/// コピーへ書き足し忘れても<b>コンパイルは通り、テストも緑のまま</b>で、記録だけが静かに古い値になる。
/// 個別の挙動テストは列の増減に追随できないため、モデルの書き込み可能プロパティを
/// リフレクションで列挙して走査する（`.claude/rules/error-messages.md` #1764）。
/// </para>
/// <para>
/// 検査は「複製されること」と「別インスタンスであること」を対で表明する。前者だけだと
/// 元のインスタンスをそのまま返す実装（＝Issue #1959 の欠陥そのもの）でも緑になる。
/// </para>
/// </remarks>
public class LedgerClonerCoverageTests
{
    /// <summary>
    /// リフレクションによる一括走査の対象外にするプロパティと、その理由。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Ledger.Details"/>: 明細は下の専用テストが中身まで比較する（本表は走査で埋める対象から外すだけ）。
    /// </para>
    /// <para>
    /// <see cref="LedgerDetail.Ledger"/>: 親への逆参照で、複製すると親 → 明細 → 親 の循環参照になり
    /// <see cref="JsonSerializer"/> がシリアライズできない。親の情報は <see cref="LedgerDetail.LedgerId"/> で足りる。
    /// </para>
    /// <para>
    /// <b>除外は「型」ではなく「プロパティ名」で書く</b>。型（例: <c>List&lt;LedgerDetail&gt;</c>）で書くと、
    /// 同じ型の別プロパティを足した日に走査が静かに素通りする（fail-open にしない、Issue #1944）。
    /// </para>
    /// </remarks>
    private static readonly IReadOnlyDictionary<Type, string[]> NotScannedByReflection =
        new Dictionary<Type, string[]>
        {
            [typeof(Ledger)] = new[] { nameof(Ledger.Details) },
            [typeof(LedgerDetail)] = new[] { nameof(LedgerDetail.Ledger) }
        };

    public static IEnumerable<object[]> ModelTypes() => new[]
    {
        new object[] { typeof(Ledger) },
        new object[] { typeof(LedgerDetail) }
    };

    [Fact]
    public void Clone_はLedgerの書き込み可能な全プロパティを複製すること()
    {
        var source = new Ledger();
        FillWithDistinctValues(source, seed: 1);
        source.Details = new List<LedgerDetail> { CreateFilledDetail(seed: 2) };
        source.DetailCount = source.Details.Count;

        var clone = LedgerCloner.Clone(source);

        foreach (var property in ScannedProperties(typeof(Ledger)))
        {
            property.GetValue(clone).Should().Be(
                property.GetValue(source),
                $"Ledger.{property.Name} が複製されていない。監査ログの BeforeData に古い値が残る（Issue #1959）");
        }
    }

    [Fact]
    public void CloneDetail_は書き込み可能な全プロパティを複製すること()
    {
        var source = CreateFilledDetail(seed: 3);

        var clone = LedgerCloner.CloneDetail(source);

        foreach (var property in ScannedProperties(typeof(LedgerDetail)))
        {
            if (property.PropertyType == typeof(byte[]))
            {
                ((byte[])property.GetValue(clone)).Should().Equal(
                    (byte[])property.GetValue(source),
                    $"LedgerDetail.{property.Name} が複製されていない");
                continue;
            }

            property.GetValue(clone).Should().Be(
                property.GetValue(source),
                $"LedgerDetail.{property.Name} が複製されていない。統合中の書き換えが監査ログへ漏れる（Issue #1959）");
        }
    }

    /// <summary>
    /// 対の表明: 明細が別インスタンスとして複製されること。
    /// </summary>
    /// <remarks>
    /// 値の一致だけを見ると、<c>Details</c> に同じリスト・同じ要素を持ち回る実装でも緑になる。
    /// 統合は共有の <see cref="LedgerDetail"/> を in-place で書き換える（<c>BusStops</c> の同期と
    /// <c>SequenceNumber</c> の一時再採番）ため、参照を共有した瞬間に本 Issue の欠陥が再現する。
    /// </remarks>
    [Fact]
    public void Clone_は明細を別インスタンスとして複製すること()
    {
        var source = new Ledger
        {
            Summary = "鉄道（薬院～博多）",
            Details = new List<LedgerDetail> { CreateFilledDetail(seed: 4) }
        };

        var clone = LedgerCloner.Clone(source);

        clone.Details.Should().NotBeSameAs(source.Details);
        clone.Details[0].Should().NotBeSameAs(source.Details[0]);

        // 複製後に元を書き換えても、クローン（＝BeforeData の材料）は動かないこと
        source.Details[0].BusStops = "天神日銀前";
        source.Details[0].SequenceNumber = 99;
        source.Summary = "バス（天神日銀前）";

        clone.Details[0].BusStops.Should().NotBe("天神日銀前");
        clone.Details[0].SequenceNumber.Should().NotBe(99);
        clone.Summary.Should().Be("鉄道（薬院～博多）");
    }

    /// <summary>
    /// 明細の値そのものも複製されること（別インスタンスであることの対）。
    /// </summary>
    [Fact]
    public void Clone_は明細の値を複製すること()
    {
        var source = new Ledger { Details = new List<LedgerDetail> { CreateFilledDetail(seed: 8) } };

        var clone = LedgerCloner.Clone(source);

        clone.Details.Should().HaveCount(1);
        foreach (var property in ScannedProperties(typeof(LedgerDetail))
                     .Where(p => p.PropertyType != typeof(byte[])))
        {
            property.GetValue(clone.Details[0]).Should().Be(
                property.GetValue(source.Details[0]),
                $"Ledger.Details[0].{property.Name} が複製されていない");
        }
    }

    /// <summary>
    /// 対の表明: 明細の <c>RawBytes</c> も配列ごと複製されること（参照共有だと書き換えが漏れる）。
    /// </summary>
    [Fact]
    public void CloneDetail_はRawBytesを配列ごと複製すること()
    {
        var source = CreateFilledDetail(seed: 5);

        var clone = LedgerCloner.CloneDetail(source);

        clone.RawBytes.Should().NotBeSameAs(source.RawBytes);
        clone.RawBytes.Should().Equal(source.RawBytes);
    }

    /// <summary>
    /// 親への逆参照を持ち回らないこと。監査ログは JSON 化するため、循環参照はそこで初めて例外になる。
    /// </summary>
    [Fact]
    public void Clone_は親への逆参照を持ち回らずJSON化できること()
    {
        var source = new Ledger { Details = new List<LedgerDetail> { CreateFilledDetail(seed: 6) } };
        source.Details[0].Ledger = source;

        var clone = LedgerCloner.Clone(source);

        clone.Details[0].Ledger.Should().BeNull("親 → 明細 → 親 の循環参照はシリアライズできない");

        Action serialize = () => JsonSerializer.Serialize(clone);
        serialize.Should().NotThrow("BeforeData / AfterData は Ledger 全体を JSON 化する");
    }

    /// <summary>
    /// 明細が未取得（<c>null</c>）なら <c>null</c> のまま複製すること。
    /// </summary>
    /// <remarks>
    /// 空リストへ丸めると「明細を持たない」と「明細を読んでいない」が 6 年保存の監査記録の中で
    /// 区別できなくなる（後者が前者に見える）。
    /// </remarks>
    [Fact]
    public void Clone_明細が未取得なら空リストへ丸めないこと()
    {
        var clone = LedgerCloner.Clone(new Ledger { Details = null });

        clone.Details.Should().BeNull();
    }

    /// <summary>
    /// <c>null</c> は <c>null</c> のまま返すこと（呼び出し元の null チェックを二重にしない）。
    /// </summary>
    [Fact]
    public void Clone_nullはnullを返すこと()
    {
        LedgerCloner.Clone(null).Should().BeNull();
        LedgerCloner.CloneDetail(null).Should().BeNull();
    }

    /// <summary>
    /// 検査ロジック自体をサンプル入力で固定する（実データが変わっても空振りしないようにする、Issue #1786）。
    /// </summary>
    /// <remarks>
    /// 走査対象のプロパティがすべて既定値と異なる値で埋まっていないと、複製漏れがあっても
    /// クローン側と値が一致して検査が素通りする。<b>両モデルについて</b>表明する
    /// （片方だけだと、もう一方に埋め漏れる型のプロパティを足した日に静かに緑になる）。
    /// </remarks>
    [Theory]
    [MemberData(nameof(ModelTypes))]
    public void 検査は既定値のままのプロパティを残さないこと(Type modelType)
    {
        var instance = Activator.CreateInstance(modelType);
        FillWithDistinctValues(instance, seed: 7);

        foreach (var property in ScannedProperties(modelType))
        {
            property.GetValue(instance).Should().NotBe(
                GetDefault(property.PropertyType),
                $"{modelType.Name}.{property.Name} が既定値のままだと、複製漏れがあっても値が一致してしまう");
        }
    }

    /// <summary>
    /// 除外表そのものを固定する。除外が静かに増えると、その分だけ検査が縮む。
    /// </summary>
    [Fact]
    public void 走査の除外はDetailsと親への逆参照だけであること()
    {
        NotScannedByReflection[typeof(Ledger)].Should().Equal(new[] { "Details" });
        NotScannedByReflection[typeof(LedgerDetail)].Should().Equal(new[] { "Ledger" });
    }

    /// <summary>
    /// リフレクションで一括走査する対象（書き込み可能プロパティから除外表を引いたもの）。
    /// </summary>
    private static IEnumerable<PropertyInfo> ScannedProperties(Type modelType) =>
        WritableProperties(modelType).Where(p => !IsExcluded(modelType, p.Name));

    private static bool IsExcluded(Type modelType, string propertyName) =>
        NotScannedByReflection.TryGetValue(modelType, out var names) && names.Contains(propertyName);

    private static IEnumerable<PropertyInfo> WritableProperties(Type modelType) =>
        modelType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0);

    private static LedgerDetail CreateFilledDetail(int seed)
    {
        var detail = new LedgerDetail();
        FillWithDistinctValues(detail, seed);
        return detail;
    }

    /// <summary>
    /// 走査対象の各プロパティへ、既定値と異なる値を入れる。
    /// </summary>
    /// <remarks>
    /// 未対応の型が現れたら例外にする。黙って飛ばすと、その型のプロパティを足した日に
    /// 検査が静かに素通りする（fail-open にしない、Issue #1944）。
    /// </remarks>
    private static void FillWithDistinctValues(object target, int seed)
    {
        var index = seed;

        foreach (var property in ScannedProperties(target.GetType()))
        {
            index++;
            var type = property.PropertyType;

            if (type == typeof(string))
            {
                property.SetValue(target, $"{property.Name}-{index}");
            }
            else if (type == typeof(int))
            {
                property.SetValue(target, index);
            }
            else if (type == typeof(int?))
            {
                property.SetValue(target, (int?)index);
            }
            else if (type == typeof(bool))
            {
                property.SetValue(target, true);
            }
            else if (type == typeof(DateTime))
            {
                property.SetValue(target, new DateTime(2026, 1, 1).AddDays(index));
            }
            else if (type == typeof(DateTime?))
            {
                property.SetValue(target, (DateTime?)new DateTime(2026, 1, 1).AddDays(index));
            }
            else if (type == typeof(byte[]))
            {
                property.SetValue(target, new byte[] { (byte)index, 0x0A, 0x0B });
            }
            else
            {
                throw new NotSupportedException(
                    $"{target.GetType().Name}.{property.Name} の型 {type.Name} は本検査が未対応。" +
                    "LedgerCloner のコピーと本検査の両方を更新すること（Issue #1959）");
            }
        }
    }

    private static object GetDefault(Type type) =>
        type.IsValueType ? Activator.CreateInstance(type) : null;
}
