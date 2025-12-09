using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

public class CardTypeDetectorTests
{
    private readonly CardTypeDetector _detector = new();

    [Theory]
    [InlineData("01FE112233445566", CardType.Suica)]
    [InlineData("02FE112233445566", CardType.PASMO)]
    [InlineData("03FE112233445566", CardType.ICOCA)]
    [InlineData("04FE112233445566", CardType.PiTaPa)]
    [InlineData("05FE112233445566", CardType.Nimoca)]
    [InlineData("06FE112233445566", CardType.SUGOCA)]
    [InlineData("07FE112233445566", CardType.Hayakaken)]
    [InlineData("08FE112233445566", CardType.Kitaca)]
    [InlineData("09FE112233445566", CardType.TOICA)]
    [InlineData("0AFE112233445566", CardType.Manaca)]
    [InlineData("0aFE112233445566", CardType.Manaca)]
    public void Detect_KnownIssuerCode_ReturnsCorrectCardType(string idm, CardType expected)
    {
        // Act
        var result = _detector.Detect(idm);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("FFFE112233445566")]
    [InlineData("00FE112233445566")]
    [InlineData("AAFE112233445566")]
    public void Detect_UnknownIssuerCode_ReturnsUnknown(string idm)
    {
        // Act
        var result = _detector.Detect(idm);

        // Assert
        result.Should().Be(CardType.Unknown);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    public void Detect_InvalidIdm_ReturnsUnknown(string? idm)
    {
        // Act
        var result = _detector.Detect(idm!);

        // Assert
        result.Should().Be(CardType.Unknown);
    }

    [Theory]
    [InlineData(CardType.Suica, "Suica")]
    [InlineData(CardType.PASMO, "PASMO")]
    [InlineData(CardType.Hayakaken, "はやかけん")]
    [InlineData(CardType.Nimoca, "nimoca")]
    [InlineData(CardType.Unknown, "その他")]
    public void GetDisplayName_ReturnsCorrectName(CardType cardType, string expected)
    {
        // Act
        var result = CardTypeDetector.GetDisplayName(cardType);

        // Assert
        result.Should().Be(expected);
    }
}
