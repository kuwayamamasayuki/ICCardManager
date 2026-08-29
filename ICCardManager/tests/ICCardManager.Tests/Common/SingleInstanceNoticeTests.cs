using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// <see cref="SingleInstanceNotice"/> の文言品質テスト（Issue #1910）
/// </summary>
/// <remarks>
/// <c>.claude/rules/error-messages.md</c> の「何が」「なぜ」「どうすれば」3 要素を固定する。
/// 検査対象は <c>ConcurrencyConflictMessageTests</c> と同じくリフレクションで列挙し、
/// 文言を足したときに品質検証の追随漏れが起きないようにする。
/// </remarks>
public class SingleInstanceNoticeTests
{
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

    public static IEnumerable<object[]> AllMessages()
        => typeof(SingleInstanceNotice)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name.StartsWith("Build") && m.ReturnType == typeof(string) && m.GetParameters().Length == 0)
            .Select(m => new object[] { m.Name, (string)m.Invoke(null, null) });

    [Fact]
    public void 文言のファクトリが漏れなく検査対象になっていること()
    {
        // 対象の網羅も併せて表明する（列挙が 0 件へ縮んでも緑になる形を防ぐ）
        AllMessages().Should().HaveCount(2);
    }

    [Theory]
    [MemberData(nameof(AllMessages))]
    public void 何がなぜどうすればの3要素を満たすこと(string factoryName, string message)
    {
        // 何が: 中止したのが「ピッすいの起動」であることを名指しする
        message.Should().Contain("ピッすい", $"{factoryName} は対象を名指しすること");
        message.Should().Contain("起動を中止しました", $"{factoryName} は何が起きたかを述べること");

        // なぜ: 二重起動が業務上なぜ困るのかを述べる
        message.Should().MatchRegex("(2 回読み取られ|同時に使うことはできません)",
            $"{factoryName} は理由を述べること");

        // どうすれば: 行動指示で終わる。
        // 「切り替えてください」「依頼してください」の双方を通すため、語幹ではなく
        // 依頼形の語尾で照合する（error-messages.md の正規表現は例示であり、
        // 満たすべき性質は「行動指示で終わること」）。
        message.Should().EndWith("ください。", $"{factoryName} は行動指示で終わること");

        message.Length.Should().BeGreaterThan(20, $"{factoryName} は情報量の下限を満たすこと");

        foreach (var vague in VaguePhrases)
        {
            message.Should().NotContain(vague, $"{factoryName} に曖昧な定型文を含めないこと");
        }

        // 「ICカード」単独表記の禁止（CLAUDE.md 最重要ルール）。
        // 「カードリーダー」はハードウェア名称なので許容される。
        message.Replace("交通系ICカード", string.Empty)
            .Replace("ICカードリーダー", string.Empty)
            .Should().NotContain("ICカード", $"{factoryName} は交通系ICカードと明記すること");
    }

    [Fact]
    public void 別ユーザーセッション向けの案内はタスクバーでの切り替えを指示しないこと()
    {
        // 別セッションのウィンドウは自分のタスクバーに現れないため、
        // 同一セッション向けの「どうすれば」は実行できない指示になる（Issue #1757）。
        var otherUser = SingleInstanceNotice.BuildOtherUserSessionMessage();

        otherUser.Should().NotContain("タスクバー");
        otherUser.Should().Contain("ユーザー");
    }

    [Fact]
    public void 同一セッション向けの案内は切り替え先の操作を具体的に示すこと()
    {
        var sameSession = SingleInstanceNotice.BuildSameSessionMessage();

        sameSession.Should().Contain("タスクバー");
        sameSession.Should().NotBe(SingleInstanceNotice.BuildOtherUserSessionMessage());
    }
}
