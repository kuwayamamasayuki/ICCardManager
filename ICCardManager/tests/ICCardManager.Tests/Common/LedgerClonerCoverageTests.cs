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
    /// 複製しないプロパティと、その理由。
    /// </summary>
    /// <remarks>
    /// <see cref="LedgerDetail.Ledger"/> は親への逆参照で、複製すると
    /// 親 → 明細 → 親 の循環参照になり <see cref="JsonSerializer"/> がシリアライズできない。
    /// 親の情報は <see cref="LedgerDetail.LedgerId"/> で足りる。
    /// </remarks>
    private static readonly string[] IntentionallyNotCloned = { nameof(LedgerDetail.Ledger) };

    [Fact]
    public void Clone_はLedgerの書き込み可能な全プロパティを複製すること()
    {
        var source = new Ledger();
        FillWithDistinctValues(source, seed: 1);
        source.Details = new List<LedgerDetail> { CreateFilledDetail(seed: 2) };
        source.DetailCount = source.Details.Count;

        var clone = LedgerCloner.Clone(source);

        foreach (var property in WritableProperties<Ledger>())
        {
            if (property.Name == nameof(Ledger.Details))
            {
                continue; // 明細は下の専用テストで中身まで比較する
            }

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

        foreach (var property in WritableProperties<LedgerDetail>()
                     .Where(p => !IntentionallyNotCloned.Contains(p.Name)))
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
    [Fact]
    public void 検査は既定値のままのプロパティを残さないこと()
    {
        var detail = CreateFilledDetail(seed: 7);

        foreach (var property in WritableProperties<LedgerDetail>()
                     .Where(p => !IntentionallyNotCloned.Contains(p.Name)))
        {
            property.GetValue(detail).Should().NotBe(
                GetDefault(property.PropertyType),
                $"LedgerDetail.{property.Name} が既定値のままだと、複製漏れがあっても値が一致してしまう");
        }
    }

    private static IEnumerable<PropertyInfo> WritableProperties<T>() =>
        typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0);

    private static LedgerDetail CreateFilledDetail(int seed)
    {
        var detail = new LedgerDetail();
        FillWithDistinctValues(detail, seed);
        return detail;
    }

    /// <summary>
    /// 書き込み可能な各プロパティへ、既定値と異なる値を入れる。
    /// </summary>
    /// <remarks>
    /// 未対応の型が現れたら例外にする。黙って飛ばすと、その型のプロパティを足した日に
    /// 検査が静かに素通りする（fail-open にしない、Issue #1944）。
    /// </remarks>
    private static void FillWithDistinctValues(object target, int seed)
    {
        var index = seed;

        foreach (var property in target.GetType()
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0))
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
            else if (type == typeof(List<LedgerDetail>) || type == typeof(Ledger))
            {
                // Details は呼び出し側で組み立て、親への逆参照は複製対象外
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
