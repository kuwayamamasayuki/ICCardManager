using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using Xunit;

namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// Issue #2220: 台帳リポジトリのインターフェースが、実際に null を返す・受け付ける箇所を <c>?</c> で宣言していること。
/// </summary>
/// <remarks>
/// <para>
/// 移行前は <c>GetByIdAsync</c> などが実際には null を返すのに <c>Task&lt;Ledger&gt;</c> と宣言しており、
/// Null 許容が有効な呼び出し元（テストプロジェクト・移行済みの本体ファイル）でも null チェックの漏れが警告にならなかった。
/// 引数の <c>transaction</c> は、null なら自前で接続を開く<b>許容</b>のオーバーロードと、null なら例外を投げる
/// <b>防御ガード</b>のオーバーロードがあり、前者だけを <c>?</c> にする（development-conventions.md「引数の null チェックは 2 種類ある」）。
/// </para>
/// <para>
/// net48 には <c>NullabilityInfoContext</c> が無いため、コンパイラーが埋め込む <c>NullableAttribute</c> /
/// <c>NullableContextAttribute</c> を読んで判定する。注釈を外す（<c>?</c> を消す・<c>#nullable enable</c> を外す）と赤になる。
/// </para>
/// </remarks>
public class LedgerRepositoryNullableAnnotationTests
{
    private const byte NotAnnotated = 1;
    private const byte Annotated = 2;

    [Theory]
    [InlineData(typeof(ILedgerQueryService), nameof(ILedgerQueryService.GetByIdAsync))]
    [InlineData(typeof(ILedgerQueryService), nameof(ILedgerQueryService.GetLatestBeforeDateAsync))]
    [InlineData(typeof(ILedgerQueryService), nameof(ILedgerQueryService.GetLatestLedgerAsync))]
    [InlineData(typeof(ILedgerRepository), nameof(ILedgerRepository.GetLentRecordAsync))]
    public void 見つからないとnullを返すメソッドは戻り値をLedgerのnull許容で宣言していること(Type interfaceType, string methodName)
    {
        var method = interfaceType.GetMethod(methodName)!;

        // Task<Ledger?> の型引数（2 番目の要素）が null 許容
        NullableFlags(method.ReturnParameter).Should().Equal(NotAnnotated, Annotated);
    }

    [Fact]
    public void 必ず値を返すメソッドの戻り値はnull許容にしていないこと()
    {
        // 対の表明: すべてを ? にする実装（「迷ったら ? を付ける」）を検出する
        var method = typeof(ILedgerRepository).GetMethod(nameof(ILedgerRepository.GetAllLentRecordsAsync))!;

        NullableFlags(method.ReturnParameter).Should().OnlyContain(f => f == NotAnnotated);
    }

    [Theory]
    // null なら自前で接続を開く（許容）
    [InlineData(typeof(ILedgerRepository), nameof(ILedgerRepository.InsertAsync), Annotated)]
    [InlineData(typeof(ILedgerRepository), nameof(ILedgerRepository.UpdateAsync), Annotated)]
    [InlineData(typeof(ILedgerRepository), nameof(ILedgerRepository.DeleteAsync), Annotated)]
    [InlineData(typeof(ILedgerRepository), nameof(ILedgerRepository.InsertDetailAsync), Annotated)]
    [InlineData(typeof(ILedgerRepository), nameof(ILedgerRepository.InsertDetailsAsync), Annotated)]
    [InlineData(typeof(ILedgerRepository), nameof(ILedgerRepository.UpdateDetailBusStopsAsync), Annotated)]
    [InlineData(typeof(ILedgerMergeRepository), nameof(ILedgerMergeRepository.ReplaceDetailsAsync), Annotated)]
    [InlineData(typeof(ILedgerMergeRepository), nameof(ILedgerMergeRepository.UnmergeLedgersAsync), Annotated)]
    [InlineData(typeof(ILedgerMergeRepository), nameof(ILedgerMergeRepository.MarkMergeHistoryUndoneAsync), Annotated)]
    // null なら ArgumentNullException（防御ガード。呼び出し元のトランザクションの中でしか呼べない）
    [InlineData(typeof(ILedgerRepository), nameof(ILedgerRepository.UpdateSummaryAsync), NotAnnotated)]
    [InlineData(typeof(ILedgerRepository), nameof(ILedgerRepository.UpdateSummaryAndAmountsAsync), NotAnnotated)]
    [InlineData(typeof(ILedgerMergeRepository), nameof(ILedgerMergeRepository.MergeLedgersAsync), NotAnnotated)]
    public void transaction引数は許容ならnull許容で防御ガードなら非nullで宣言していること(
        Type interfaceType, string methodName, byte expected)
    {
        var transactionParameters = interfaceType.GetMethods()
            .Where(m => m.Name == methodName)
            .SelectMany(m => m.GetParameters())
            .Where(p => p.ParameterType == typeof(SQLiteTransaction))
            .ToList();

        transactionParameters.Should().ContainSingle("transaction を取るオーバーロードは 1 つ");
        NullableFlags(transactionParameters[0]).Should().Equal(expected);
    }

    [Theory]
    // null で全カードを対象にする
    [InlineData(typeof(ILedgerQueryService), nameof(ILedgerQueryService.GetByDateRangeAsync), Annotated)]
    [InlineData(typeof(ILedgerQueryService), nameof(ILedgerQueryService.GetPagedAsync), Annotated)]
    // 対の表明: 1 枚のカードを指すメソッドは非 null（「迷ったら ? を付ける」形を検出する）
    [InlineData(typeof(ILedgerQueryService), nameof(ILedgerQueryService.GetByMonthAsync), NotAnnotated)]
    [InlineData(typeof(ILedgerQueryService), nameof(ILedgerQueryService.GetLatestLedgerAsync), NotAnnotated)]
    [InlineData(typeof(ILedgerQueryService), nameof(ILedgerQueryService.GetLatestBeforeDateAsync), NotAnnotated)]
    [InlineData(typeof(ILedgerRepository), nameof(ILedgerRepository.GetLentRecordAsync), NotAnnotated)]
    [InlineData(typeof(ILedgerRepository), nameof(ILedgerRepository.DeleteAllLentRecordsAsync), NotAnnotated)]
    public void cardIdm引数は全カードを対象にできるときだけnull許容で宣言していること(
        Type interfaceType, string methodName, byte expected)
    {
        var cardIdm = interfaceType.GetMethod(methodName)!.GetParameters()
            .Single(p => p.Name == "cardIdm");

        NullableFlags(cardIdm).Should().Equal(expected);
    }

    [Fact]
    public void バス停名の更新は非nullのバス停名を受け取ること()
    {
        // 呼び出し元（バス停名入力・履歴の行編集）は未入力をプレースホルダへ置き換えてから渡す。
        // (int SequenceNumber, string BusStops) の string 部分（フラグの 3 番目）が非 null
        var updates = typeof(ILedgerRepository).GetMethods()
            .Where(m => m.Name == nameof(ILedgerRepository.UpdateDetailBusStopsAsync))
            .Select(m => m.GetParameters().Single(p => p.Name == "updates"))
            .ToList();

        updates.Should().HaveCount(2);
        updates.Should().OnlyContain(p => !NullableFlags(p).Contains(Annotated));
    }

    [Fact]
    public void 読み取りロジックが注釈の有無を区別できること()
    {
        // 検査ロジック自体を既知のサンプルで固定する（#1786）
        var method = typeof(Samples).GetMethod(nameof(Samples.Sample))!;
        var parameters = method.GetParameters();

        NullableFlags(parameters[0]).Should().Equal(NotAnnotated);
        NullableFlags(parameters[1]).Should().Equal(Annotated);
        NullableFlags(method.ReturnParameter).Should().Equal(NotAnnotated, Annotated);
    }

    /// <summary>
    /// 型の Null 許容性のフラグ（0=未注釈、1=非 null、2=null 許容。ジェネリックは外側から順）を返す。
    /// 要素ごとの <c>NullableAttribute</c> が無ければ、メソッド・型の <c>NullableContextAttribute</c> の既定値を使う。
    /// </summary>
    private static byte[] NullableFlags(ParameterInfo parameter)
    {
        var nullable = parameter.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.NullableAttribute");
        if (nullable != null)
        {
            var argument = nullable.ConstructorArguments[0];
            return argument.Value is byte single
                ? new[] { single }
                : ((IEnumerable<CustomAttributeTypedArgument>)argument.Value!).Select(v => (byte)v.Value!).ToArray();
        }

        MemberInfo? scope = parameter.Member;
        while (scope != null)
        {
            var context = scope.GetCustomAttributesData()
                .FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.NullableContextAttribute");
            if (context != null)
            {
                return new[] { (byte)context.ConstructorArguments[0].Value! };
            }

            scope = scope.DeclaringType;
        }

        return new byte[] { 0 };
    }

    private static class Samples
    {
        public static System.Threading.Tasks.Task<string?> Sample(string notNull, string? maybeNull)
            => System.Threading.Tasks.Task.FromResult<string?>(notNull + maybeNull);
    }
}
