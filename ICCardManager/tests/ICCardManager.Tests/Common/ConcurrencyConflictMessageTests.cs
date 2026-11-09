using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using ICCardManager.Common;
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

    /// <summary>
    /// 払い戻しの競合は、利用者が行った操作名で述べること（Issue #1760）
    /// </summary>
    /// <remarks>
    /// 「なぜ」（対象行が削除された）は更新と同じだが、「何が」は利用者が実際に行った
    /// 操作でなければならない。払い戻しを試みた職員に「更新できませんでした」と出すと、
    /// 自分が何をした結果なのか結び付かない。
    /// </remarks>
    [Fact]
    public void ForRefund_NamesTheRefundOperation()
    {
        var refund = ConcurrencyConflictMessage.ForRefund(Target, ListName);

        refund.Should().Contain("払い戻しできませんでした");
        refund.Should().Contain("削除された可能性");
        refund.Should().NotBe(ConcurrencyConflictMessage.ForUpdate(Target, ListName));
    }

    /// <summary>
    /// Issue #2421: 払戻済への更新が競合（行はあるが削除済み・払戻済）で拒否されたときは、
    /// 原因の候補に削除と払い戻しの両方を挙げること
    /// </summary>
    /// <remarks>
    /// 競合（<c>CardOperationResult.Conflict</c>）は、行が残っていて貸出中でもないのに WHERE に一致しなかった場合で、
    /// 削除済み（論理削除）か払戻済のどちらか。<see cref="ConcurrencyConflictMessage.ForRefund"/> の
    /// 「削除された可能性」だけでは、他のパソコンで先に払い戻された場合を説明しない。
    /// </remarks>
    [Fact]
    public void ForRefundStateChanged_NamesBothDeletionAndRefundAsCauses()
    {
        var message = ConcurrencyConflictMessage.ForRefundStateChanged(Target, ListName);

        message.Should().StartWith($"{Target}を払い戻しできませんでした。");
        message.Should().Contain("削除された");
        message.Should().Contain("払い戻された可能性があります");
        message.Should().NotBe(ConcurrencyConflictMessage.ForRefund(Target, ListName));
    }

    /// <summary>
    /// Issue #2421: 削除が競合で拒否され原因を確かめられなかったときは、削除の操作名で述べ、
    /// 原因の候補に削除と払い戻しの両方を挙げること（<see cref="ConcurrencyConflictMessage.ForRefundStateChanged"/> と対）
    /// </summary>
    [Fact]
    public void ForDeleteStateChanged_NamesBothDeletionAndRefundAsCauses()
    {
        var message = ConcurrencyConflictMessage.ForDeleteStateChanged(Target, ListName);

        message.Should().StartWith($"{Target}を削除できませんでした。");
        message.Should().Contain("削除された");
        message.Should().Contain("払い戻された可能性があります");
        message.Should().NotBe(ConcurrencyConflictMessage.ForDelete(Target, ListName));
    }

    /// <summary>
    /// Issue #2370: 払戻済のため削除できなかったときは、払い戻されたことを理由に挙げ、
    /// 「やり直す」ではなく状態の確認を促すこと（払戻済のカードは何度やり直しても削除できない）。
    /// </summary>
    /// <remarks>
    /// 「やり直してください」で終わらないため、公開のファクトリを走査する品質検証
    /// （<see cref="AllFactories_SatisfyErrorMessageQualityCriteria"/>）の対象外（internal）。3 要素はここで個別に固定する。
    /// </remarks>
    [Fact]
    public void ForDeleteRefunded_NamesTheRefundAsCauseAndAsksToCheckTheList()
    {
        var message = ConcurrencyConflictMessage.ForDeleteRefunded(Target, ListName);

        message.Should().StartWith($"{Target}を削除できませんでした。");
        message.Should().Contain("払い戻された可能性があります");
        message.Should().Contain("払戻済の交通系ICカードは削除できません");
        message.Should().Contain($"{ListName}を再読み込みしました");
        message.Should().EndWith("確認してください。");
        message.Should().NotContain("やり直してください", "払戻済のカードはやり直しても削除できない");
        message.Should().NotContain("先に削除された");
        foreach (var vague in VaguePhrases)
        {
            message.Should().NotContain(vague);
        }
    }

    /// <summary>
    /// Issue #2263: 登録の競合は「先に登録された」ことを理由に挙げ、ほかの操作の理由を含まないこと。
    /// </summary>
    [Fact]
    public void ForRegister_NamesTheRegisterOperationAndItsCause()
    {
        var register = ConcurrencyConflictMessage.ForRegister(Target, ListName);

        register.Should().Contain("登録できませんでした");
        register.Should().Contain("先に登録された可能性");
        register.Should().NotContain("削除された");
        register.Should().NotContain("復元された");
    }
    /// <summary>
    /// Issue #2262: CSV インポートの競合の文言は「なぜ」を公開のファクトリと共有し、
    /// 一覧の再読込（取り込みでは行わない）を述べず、取り込んでいないことと次の行動を示すこと
    /// </summary>
    [Fact]
    public void ForImportUpdateAndRestore_ShareTheCauseAndDoNotClaimAReload()
    {
        const string target = "職員「山田 太郎」";
        const string screen = "職員管理画面（F2）";
        var update = ConcurrencyConflictMessage.ForImportUpdate(target, screen);
        var restore = ConcurrencyConflictMessage.ForImportRestore(target, screen);

        update.Should().StartWith($"{target}を更新できませんでした。").And.Contain("削除された可能性があります");
        restore.Should().StartWith($"{target}を復元できませんでした。").And.Contain("先に復元された可能性があります");
        foreach (var message in new[] { update, restore })
        {
            message.Should().Contain("取り込みは行っていません").And.Contain(screen).And.EndWith("インポートしてください。");
            message.Should().NotContain("再読み込み", "取り込みは一覧を再読込しない");
        }
    }

    /// <summary>
    /// Issue #2382: 登録の競合で一覧の再読込にも失敗したときの文言は、「再読み込みしました」と偽らず、
    /// 「なぜ」を <see cref="ConcurrencyConflictMessage.ForRegister"/> と共有し、開き直しを指示すること
    /// </summary>
    [Fact]
    public void ForRegisterReloadFailed_ShouldNotClaimReloadAndShouldTellToReopen()
    {
        var message = ConcurrencyConflictMessage.ForRegisterReloadFailed(Target, ListName);

        message.Length.Should().BeGreaterOrEqualTo(20);
        message.Should().Contain(Target, "読み直した行で名指しする");
        message.Should().Contain("登録できませんでした");
        message.Should().Contain("先に登録された可能性があります");
        message.Should().Contain("カード一覧の再読み込みにも失敗したため");
        message.Should().NotContain("再読み込みしましたので");
        message.Should().EndWith($"画面を閉じて開き直し、一覧で{Target}を確認してからやり直してください。");
        foreach (var vague in VaguePhrases)
        {
            message.Should().NotContain(vague);
        }
    }

    /// <summary>
    /// Issue #2382: 復元の競合で一覧の再読込にも失敗したときの文言は、「再読み込みしました」と偽らず、
    /// 復元の「なぜ」を <see cref="ConcurrencyConflictMessage.ForRestore"/> と共有し、開き直しを指示すること
    /// </summary>
    [Fact]
    public void ForRestoreReloadFailed_ShouldNotClaimReloadAndShouldTellToReopen()
    {
        var message = ConcurrencyConflictMessage.ForRestoreReloadFailed(Target, ListName);

        message.Length.Should().BeGreaterOrEqualTo(20);
        message.Should().StartWith($"{Target}を復元できませんでした。");
        message.Should().Contain("先に復元された可能性があります");
        message.Should().Contain("カード一覧の再読み込みにも失敗したため");
        message.Should().NotContain("再読み込みしましたので");
        message.Should().EndWith($"画面を閉じて開き直し、一覧で{Target}を確認してからやり直してください。");
        foreach (var vague in VaguePhrases)
        {
            message.Should().NotContain(vague);
        }
    }

    /// <summary>
    /// Issue #2382: 読み直した行が削除済みだったときの文言は、一覧での確認（削除済みは一覧に出ない）を案内せず、
    /// もう一度保存して復元する道を示すこと（internal のため全ファクトリの品質検査の外にあり、ここで 3 要素を固定する）
    /// </summary>
    [Fact]
    public void ForRegisterFoundDeleted_ShouldGuideToRestoreWithoutClaimingReload()
    {
        var message = ConcurrencyConflictMessage.ForRegisterFoundDeleted(Target, restoreRequiresStaffCardAuthentication: false);
        var staffMessage = ConcurrencyConflictMessage.ForRegisterFoundDeleted(Target, restoreRequiresStaffCardAuthentication: true);

        // 職員の復元は「はい」の後に職員証の認証が続く（#2263）。カードにはその手順が無い
        staffMessage.Should().EndWith("「はい」を選択してから、登録済みの職員の職員証で認証してください。");
        message.Should().NotContain("職員証で認証");

        message.Length.Should().BeGreaterOrEqualTo(20);
        message.Should().Contain(Target);
        message.Should().Contain("削除済み").And.Contain("登録できませんでした");
        message.Should().Contain("可能性があります");
        message.Should().NotContain("再読み込み");
        message.Should().EndWith("もう一度「保存」を押し、表示される確認で「はい」を選択してください。");
        foreach (var vague in VaguePhrases)
        {
            message.Should().NotContain(vague);
        }
    }
}
