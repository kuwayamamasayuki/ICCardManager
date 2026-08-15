using FluentAssertions;
using ICCardManager.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// <see cref="ConcurrencyConflictMessage"/> の文言品質テスト（Issue #1759）
/// </summary>
/// <remarks>
/// <para>
/// <c>.claude/rules/error-messages.md</c> の「何が」「なぜ」「どうすれば」3要素を固定する。
/// 修正前の「更新に失敗しました」（8文字）はいずれも満たしていなかった。
/// </para>
/// <para>
/// 検査対象はリフレクションで全ファクトリを列挙する。個別に列挙すると、
/// ファクトリを足したときに品質検証の追随漏れが静かに起きる
/// （<c>ConnectionDiagnosticsServiceTests</c> と同じ「対象の網羅も併せて表明する」方針）。
/// </para>
/// </remarks>
public class ConcurrencyConflictMessageTests
{
    private const string Target = "交通系ICカード「はやかけん H-001」";
    private const string ListName = "カード一覧";

    /// <summary>
    /// 曖昧すぎて「なぜ」「どうすれば」を伝えない禁止パターン
    /// （<c>.claude/rules/error-messages.md</c>「禁止パターン」）
    /// </summary>
    private static readonly string[] VaguePhrases =
    {
        "エラーが発生しました",
        "不正な値です",
        "入力が正しくありません",
        "予期しないエラー"
    };

    /// <summary>
    /// 全ファクトリ（public static メソッド）を列挙する。
    /// </summary>
    public static IEnumerable<object[]> AllFactories()
        => typeof(ConcurrencyConflictMessage)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => new object[] { m.Name });

    private static string Invoke(string factoryName)
    {
        // 引数の型まで指定する。指定しないと、将来オーバーロードが増えたときに
        // AmbiguousMatchException／TargetParameterCountException という
        // 「何を直せばよいか分からない失敗」になる。
        var method = typeof(ConcurrencyConflictMessage)
            .GetMethod(
                factoryName,
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(string), typeof(string) },
                modifiers: null);
        method.Should().NotBeNull(
            $"{factoryName} は (string target, string listName) を受け取るファクトリであること。" +
            "別の形のファクトリを足すなら、本テストの呼び出し方も併せて更新する");
        return (string)method!.Invoke(null, new object[] { Target, ListName })!;
    }

    /// <summary>
    /// すべてのファクトリの文言が3要素の品質基準を満たすこと
    /// </summary>
    [Theory]
    [MemberData(nameof(AllFactories))]
    public void AllFactories_SatisfyErrorMessageQualityCriteria(string factoryName)
    {
        var message = Invoke(factoryName);

        // 最小文字数（情報不足の検出）
        message.Length.Should().BeGreaterOrEqualTo(20, $"{factoryName} の文言が短すぎないこと");

        // 何が: 対象の説明を必ず含む（どのカード／職員かが分からないと確認できない）
        message.Should().Contain(Target, $"{factoryName} が対象を明示すること");

        // なぜ: 競合の可能性を示す（断定しない）
        message.Should().Contain("可能性があります", $"{factoryName} が原因の候補を示すこと");

        // どうすれば: 再読込済みであることと、次の行動を示して行動指示で終わる
        message.Should().Contain(ListName, $"{factoryName} が再読込した一覧を示すこと");
        message.Should().EndWith("やり直してください。", $"{factoryName} が行動指示で終わること");

        foreach (var vague in VaguePhrases)
        {
            message.Should().NotContain(vague, $"{factoryName} が曖昧な定型文を含まないこと");
        }
    }

    /// <summary>
    /// 操作ごとに「なぜ」が異なること（取り違えの検出）
    /// </summary>
    /// <remarks>
    /// Issue #1744 の「『判別できない』と『判別できたが読めない』で文言を分ける」と同じ理由。
    /// 復元できなかった原因は「先に復元された」であって「削除された」ではない。
    /// 取り違えると、既に復元済みのデータに対して「削除された可能性」を案内することになり、
    /// 利用者を真の原因から遠ざける。
    /// </remarks>
    [Fact]
    public void EachFactory_ExplainsItsOwnCause()
    {
        var update = ConcurrencyConflictMessage.ForUpdate(Target, ListName);
        var restore = ConcurrencyConflictMessage.ForRestore(Target, ListName);
        var delete = ConcurrencyConflictMessage.ForDelete(Target, ListName);

        update.Should().Contain("更新できませんでした").And.Contain("削除された可能性");
        restore.Should().Contain("復元できませんでした").And.Contain("先に復元された可能性");
        delete.Should().Contain("削除できませんでした").And.Contain("先に削除された可能性");

        // 更新の「なぜ」は復元・削除のものと混同されない
        restore.Should().NotContain("削除された可能性");
        new[] { update, restore, delete }.Distinct().Should().HaveCount(3);
    }
}
