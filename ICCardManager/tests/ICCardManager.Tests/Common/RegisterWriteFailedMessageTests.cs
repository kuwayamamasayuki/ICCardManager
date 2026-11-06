using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// <see cref="RegisterWriteFailedMessage"/> の文言品質テスト（Issue #2263 / #2382）
/// </summary>
/// <remarks>
/// 「何が」（登録しようとした対象）・「なぜ」（書き込めなかった／考えられる原因）・「どうすれば」（確認して再度保存）の
/// 3 要素を固定する。修正前のカード登録は「登録に失敗しました」（9 文字）でいずれも満たしていなかった。
/// </remarks>
public class RegisterWriteFailedMessageTests
{
    [Theory]
    [InlineData("交通系ICカード「はやかけん H-001」")]
    [InlineData("職員「田中太郎」")]
    public void Build_ShouldContainWhatWhyAndHow(string target)
    {
        var message = RegisterWriteFailedMessage.Build(target);

        message.Should().StartWith($"{target}を登録できませんでした。", "「何が」は登録しようとした対象で名指しする");
        message.Should().Contain("書き込みに失敗しました", "「なぜ」");
        message.Should().Contain("空き容量").And.Contain("書き込み権限").And.Contain("共有フォルダー",
            "原因の候補を示し、職員が確認できるようにする");
        message.Should().Contain("もう一度「保存」を押してください", "「どうすれば」");
        message.Should().MatchRegex("ください。$", "行動指示で終わる（error-messages.md）");
        message.Should().NotContain("エラーが発生しました");
        message.Should().NotContain("先に登録された", "重複（競合）の文言と取り違えない");
    }
}
