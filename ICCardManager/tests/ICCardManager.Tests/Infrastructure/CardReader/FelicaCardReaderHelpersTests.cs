using System;
using FluentAssertions;
using ICCardManager.Infrastructure.CardReader;
using Xunit;

namespace ICCardManager.Tests.Infrastructure.CardReader;

/// <summary>
/// <see cref="FelicaCardReader.IsReaderUnavailableException"/> の判定ロジックを検証する (Issue #1428)。
/// </summary>
/// <remarks>
/// PaSoRi 物理未接続を例外メッセージから判定する純粋関数。実機 felicalib に依存せず
/// 純粋関数として独立検証する。
/// </remarks>
public class FelicaCardReaderHelpersTests
{
    [Fact]
    public void IsReaderUnavailable_DllNotFoundException_TrueAlways()
    {
        var ex = new DllNotFoundException("felicalib.dll");

        FelicaCardReader.IsReaderUnavailableException(ex).Should().BeTrue(
            "DLL 不在時は PaSoRi 接続以前の問題のためリーダー利用不可と判定すべき");
    }

    [Fact]
    public void IsReaderUnavailable_DllNotFoundException_NullMessage_StillTrue()
    {
        var ex = new DllNotFoundException();

        FelicaCardReader.IsReaderUnavailableException(ex).Should().BeTrue(
            "DllNotFoundException は型でも判定されるためメッセージに依存しない");
    }

    [Theory]
    [InlineData("Failed to open PaSoRi")]
    [InlineData("pasori_open failed")]
    [InlineData("Device not found")]
    [InlineData("Reader not connected")]
    public void IsReaderUnavailable_KnownReaderUnavailablePatterns_ReturnsTrue(string message)
    {
        var ex = new InvalidOperationException(message);

        FelicaCardReader.IsReaderUnavailableException(ex).Should().BeTrue(
            $"PaSoRi 物理未接続を示すメッセージパターン \"{message}\" は切断と判定されるべき");
    }

    [Theory]
    [InlineData("PASORI not available")]
    [InlineData("OPEN failed")]
    [InlineData("DEVICE error")]
    [InlineData("READER missing")]
    public void IsReaderUnavailable_UpperCaseKeywords_ReturnsTrue(string message)
    {
        var ex = new InvalidOperationException(message);

        FelicaCardReader.IsReaderUnavailableException(ex).Should().BeTrue(
            "大文字小文字は無視して判定する（felicalib のメッセージ揺れ吸収）");
    }

    [Theory]
    [InlineData("Polling timeout")]
    [InlineData("No card detected")]
    [InlineData("System code not supported")]
    public void IsReaderUnavailable_CardAbsentPatterns_ReturnsFalse(string message)
    {
        var ex = new InvalidOperationException(message);

        FelicaCardReader.IsReaderUnavailableException(ex).Should().BeFalse(
            $"カードなし相当のメッセージ \"{message}\" は接続中扱いとし、ヘルスチェックの誤切断を防ぐ");
    }

    [Fact]
    public void IsReaderUnavailable_NullMessage_ReturnsFalse()
    {
        var ex = new InvalidOperationException();

        FelicaCardReader.IsReaderUnavailableException(ex).Should().BeFalse(
            "メッセージなしの一般例外はカードなし扱い（保守的に接続中とし、表示層で fail-safe 補完）");
    }

    [Fact]
    public void IsReaderUnavailable_EmptyMessage_ReturnsFalse()
    {
        var ex = new InvalidOperationException(string.Empty);

        FelicaCardReader.IsReaderUnavailableException(ex).Should().BeFalse();
    }

    [Fact]
    public void IsReaderUnavailable_NullException_ReturnsFalse()
    {
        FelicaCardReader.IsReaderUnavailableException(null!).Should().BeFalse(
            "null 例外で false を返すことで呼び出し側の null チェック責務を緩和する");
    }
}
