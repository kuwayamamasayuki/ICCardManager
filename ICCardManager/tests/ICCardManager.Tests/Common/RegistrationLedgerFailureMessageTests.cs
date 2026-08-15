using FluentAssertions;
using ICCardManager.Common;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// <see cref="RegistrationLedgerFailureMessage"/> の文言品質テスト（Issue #1763）
/// </summary>
/// <remarks>
/// <para>
/// <c>.claude/rules/error-messages.md</c> の「何が」「なぜ」「どうすれば」3要素を固定する。
/// あわせて「登録は成立したが台帳の受入行が入らなかった」という区別が保たれていることも
/// 表明する。ここを「登録に失敗しました」と書くと、職員は再登録を試みて
/// 「既に登録されています」に突き当たる（Issue #1727）。
/// </para>
/// <para>
/// 検査対象はリフレクションで全ファクトリを列挙する。個別に列挙すると、
/// ファクトリを足したときに品質検証の追随漏れが静かに起きる
/// （<c>ConcurrencyConflictMessageTests</c> と同じ「対象の網羅も併せて表明する」方針）。
/// </para>
/// </remarks>
public class RegistrationLedgerFailureMessageTests
{
    private const string CardNumber = "H-001";

    /// <summary>
    /// <c>LendingService.GetHistoryImportFailureReason</c> が返す「なぜ」の実例。
    /// </summary>
    private const string Reason = "他のPCがデータベースを使用中で、書き込みが競合しました。";

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
        => typeof(RegistrationLedgerFailureMessage)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => new object[] { m.Name });

    private static RegistrationLedgerFailureMessage Invoke(string factoryName, string reason = Reason)
    {
        // 引数の型まで指定する。指定しないと、将来オーバーロードが増えたときに
        // AmbiguousMatchException／TargetParameterCountException という
        // 「何を直せばよいか分からない失敗」になる。
        var method = typeof(RegistrationLedgerFailureMessage)
            .GetMethod(
                factoryName,
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(string), typeof(string) },
                modifiers: null);
        method.Should().NotBeNull(
            $"{factoryName} は (string cardNumber, string reason) を受け取るファクトリであること。" +
            "別の形のファクトリを足すなら、本テストの呼び出し方も併せて更新する");
        return (RegistrationLedgerFailureMessage)method!.Invoke(null, new object[] { CardNumber, reason })!;
    }

    /// <summary>
    /// 空振り検出: 列挙が壊れてファクトリを 1 つも検査しない状態にならないこと
    /// </summary>
    /// <remarks>
    /// <c>.claude/rules/development-conventions.md</c>「空振り検出」。
    /// リフレクションの条件を間違えると、全 Theory が 0 ケースで緑になる。
    /// </remarks>
    [Fact]
    public void AllFactories_列挙が空にならないこと()
    {
        AllFactories().Should().HaveCountGreaterOrEqualTo(2,
            "履歴あり経路（#1727）と履歴なし経路（#1763）の 2 つのファクトリがあること");
    }

    /// <summary>
    /// すべてのファクトリのダイアログ本文が3要素の品質基準を満たすこと
    /// </summary>
    [Theory]
    [MemberData(nameof(AllFactories))]
    public void AllFactories_DialogMessage_SatisfiesErrorMessageQualityCriteria(string factoryName)
    {
        var message = Invoke(factoryName).DialogMessage;

        // 最小文字数（情報不足の検出）
        message.Length.Should().BeGreaterOrEqualTo(20, $"{factoryName} の文言が短すぎないこと");

        // 何が: 対象の交通系ICカードを管理番号で特定できる
        message.Should().Contain("交通系ICカード", $"{factoryName} が対象の種類を明示すること");
        message.Should().Contain(CardNumber, $"{factoryName} がどのカードで起きたかを明示すること");

        // なぜ: サービス層が組み立てた原因をそのまま含む
        message.Should().Contain(Reason, $"{factoryName} が失敗の原因を含むこと");

        // なぜ（影響）: 台帳がどういう状態になったかを説明する
        message.Should().Contain("台帳", $"{factoryName} が台帳への影響を説明すること");

        // どうすれば: 行動指示で終わる
        message.TrimEnd().Should().EndWith("してください。", $"{factoryName} が行動指示で終わること");

        // 曖昧な定型文を使わない
        foreach (var vague in VaguePhrases)
        {
            message.Should().NotContain(vague, $"{factoryName} が曖昧な定型文を含まないこと");
        }
    }

    /// <summary>
    /// すべてのファクトリが「カード登録自体は成立した」ことを明示すること
    /// </summary>
    /// <remarks>
    /// Issue #1727: カード行と操作ログはコミット済み。「登録に失敗しました」と読める文言だと、
    /// 職員は再登録を試みて「既に登録されています」に突き当たる。
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllFactories))]
    public void AllFactories_DialogMessage_StatesCardRegistrationItselfSucceeded(string factoryName)
    {
        var result = Invoke(factoryName);

        result.DialogMessage.Should().Contain("登録は完了しました",
            $"{factoryName} はカード行が登録済みであることを先に伝えること");
        result.StatusMessage.Should().Contain("カードは登録しました",
            $"{factoryName} のステータス欄も登録の成立を伝えること");
    }

    /// <summary>
    /// すべてのファクトリのステータス欄文言が、簡潔さと行動指示を両立すること
    /// </summary>
    /// <remarks>
    /// ステータス欄は幅が限られるため <c>error-messages.md</c> の20文字基準は適用しないが、
    /// 「どうすれば」を落として単なる失敗報告にしない。
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllFactories))]
    public void AllFactories_StatusMessage_EndsWithActionInstruction(string factoryName)
    {
        var status = Invoke(factoryName).StatusMessage;

        status.TrimEnd().Should().EndWith("してください。", $"{factoryName} のステータス欄が行動指示で終わること");
        foreach (var vague in VaguePhrases)
        {
            status.Should().NotContain(vague, $"{factoryName} のステータス欄が曖昧な定型文を含まないこと");
        }
    }

    /// <summary>
    /// すべてのファクトリがダイアログのタイトルを持つこと
    /// </summary>
    [Theory]
    [MemberData(nameof(AllFactories))]
    public void AllFactories_DialogTitle_IsNotEmpty(string factoryName)
    {
        Invoke(factoryName).DialogTitle.Should().NotBeNullOrWhiteSpace(
            $"{factoryName} のダイアログにタイトルがあること");
    }

    /// <summary>
    /// 失敗理由が空でも「なぜ」を欠かないこと
    /// </summary>
    /// <remarks>
    /// <c>HistoryImportResult.FailureReason</c> が空で返ることは想定していないが、
    /// そのまま埋め込むと「ただし、〜できませんでした。」の直後が途切れて 3 要素を欠く。
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllFactories))]
    public void AllFactories_WhenReasonIsEmpty_StillProvidesReason(string factoryName)
    {
        var message = Invoke(factoryName, reason: "").DialogMessage;

        message.Should().Contain("データベースへの書き込み中に問題が発生しました。",
            $"{factoryName} は理由が得られなくても代替の「なぜ」を示すこと");
        message.TrimEnd().Should().EndWith("してください。");
    }

    /// <summary>
    /// 履歴なし経路（Issue #1763）は、実行できない復旧手段を案内しないこと
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>.claude/rules/error-messages.md</c>「文言を 1 か所へ集約しても、『どうすれば』は
    /// 経路によって変わり得る」。この経路はカード内に取り込める利用履歴が無いため、
    /// 「CSVインポートで利用履歴を取り込んでください」は実行できない指示になる。
    /// </para>
    /// <para>
    /// 併せて互いの文言を含まないことも表明し、ファクトリの取り違えを検出する。
    /// </para>
    /// </remarks>
    [Fact]
    public void ForInitialBalance_DoesNotSuggestImportingNonExistentHistory()
    {
        var initialBalance = RegistrationLedgerFailureMessage.ForInitialBalance(CardNumber, Reason);
        var historyImport = RegistrationLedgerFailureMessage.ForHistoryImport(CardNumber, Reason);

        initialBalance.DialogMessage.Should().NotContain("CSVインポート",
            "取り込む利用履歴が存在しない経路で CSV インポートを案内しない");
        initialBalance.StatusMessage.Should().NotContain("CSVインポート");
        initialBalance.DialogMessage.Should().Contain("残高の行を手動で追加してください",
            "この経路で取れる唯一の復旧手段を示すこと");

        // 履歴あり経路は従来どおり CSV インポートを案内する（取り違えの検出）
        historyImport.DialogMessage.Should().Contain("CSVインポート");
        historyImport.DialogTitle.Should().NotBe(initialBalance.DialogTitle,
            "何に失敗したのかがタイトルで区別できること");
    }

    /// <summary>
    /// 履歴なし経路は「唯一の受入行が欠けた」という帳票への影響を述べること
    /// </summary>
    /// <remarks>
    /// Issue #1763: 失われるのは「新規購入 / ○月から繰越」＝そのカード唯一の受入行で、
    /// 影響は「残額が合わない」に留まらず年度を通した収支の不一致になる。
    /// 「なぜ」の重大さが伝わらないと、職員は復旧を後回しにする。
    /// </remarks>
    [Fact]
    public void ForInitialBalance_ExplainsMissingIncomeRowBreaksMonthlyReport()
    {
        var message = RegistrationLedgerFailureMessage.ForInitialBalance(CardNumber, Reason).DialogMessage;

        message.Should().Contain("受入", "欠落したのが受入行であることを示すこと");
        message.Should().Contain("物品出納簿", "どの帳票に影響するかを示すこと");
    }
}
