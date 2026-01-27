using System;
using System.Linq;
using DebugDataViewer;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Tools
{
    /// <summary>
    /// FelicaBlockParser のユニットテスト
    /// FeliCa履歴ブロック（16バイト）のビット単位フィールド解析を検証する
    /// </summary>
    [Trait("Category", "Unit")]
    public class FelicaBlockParserTests
    {
        /// <summary>
        /// テスト用の16バイトデータを生成するヘルパー
        /// </summary>
        private static byte[] CreateTestBlock(
            byte machineType = 0x16,
            byte usageType = 0x01,
            byte paymentType = 0x00,
            byte entryExitType = 0x00,
            byte dateHigh = 0x32, byte dateLow = 0x2F,
            byte entryHigh = 0x01, byte entryLow = 0x3A,
            byte exitHigh = 0x01, byte exitLow = 0x45,
            byte balanceLow = 0xE8, byte balanceHigh = 0x03,
            byte reserved0 = 0x00, byte reserved1 = 0x00,
            byte reserved2 = 0x00, byte reserved3 = 0x00)
        {
            return new byte[]
            {
                machineType, usageType, paymentType, entryExitType,
                dateHigh, dateLow,
                entryHigh, entryLow,
                exitHigh, exitLow,
                balanceLow, balanceHigh,
                reserved0, reserved1, reserved2, reserved3
            };
        }

        #region Parse - 基本テスト

        [Fact]
        public void Parse_ValidBlock_Returns9Fields()
        {
            // Arrange
            var block = CreateTestBlock();

            // Act
            var result = FelicaBlockParser.Parse(block);

            // Assert
            result.Should().HaveCount(9);
        }

        [Fact]
        public void Parse_ValidBlock_FieldNamesAreCorrect()
        {
            // Arrange
            var block = CreateTestBlock();

            // Act
            var result = FelicaBlockParser.Parse(block);

            // Assert
            var fieldNames = result.Select(f => f.FieldName).ToList();
            fieldNames.Should().ContainInOrder(
                "機器種別", "利用種別", "支払種別", "入出場種別",
                "日付", "入場駅コード", "出場駅コード", "残額", "予備");
        }

        [Fact]
        public void Parse_NullInput_ReturnsEmptyList()
        {
            // Act
            var result = FelicaBlockParser.Parse(null);

            // Assert
            result.Should().BeEmpty();
        }

        [Fact]
        public void Parse_TooShortInput_ReturnsEmptyList()
        {
            // Arrange - 8バイトしかない
            var shortBlock = new byte[] { 0x16, 0x01, 0x00, 0x00, 0x32, 0x2F, 0x01, 0x3A };

            // Act
            var result = FelicaBlockParser.Parse(shortBlock);

            // Assert
            result.Should().BeEmpty();
        }

        [Fact]
        public void Parse_EmptyInput_ReturnsEmptyList()
        {
            // Act
            var result = FelicaBlockParser.Parse(new byte[0]);

            // Assert
            result.Should().BeEmpty();
        }

        #endregion

        #region Parse - 日付フィールド解析

        [Theory]
        [InlineData(0x32, 0x2F, "2025/01/15")]  // year=25, month=1, day=15
        [InlineData(0x33, 0x01, "2025/08/01")]   // year=25, month=8, day=1
        [InlineData(0x00, 0x21, "2000/01/01")]   // year=0, month=1, day=1
        [InlineData(0xFF, 0x9F, "2127/12/31")]   // year=127, month=12, day=31 (最大値)
        public void Parse_DateField_DecodesCorrectly(byte dateHigh, byte dateLow, string expectedDate)
        {
            // Arrange
            var block = CreateTestBlock(dateHigh: dateHigh, dateLow: dateLow);

            // Act
            var result = FelicaBlockParser.Parse(block);
            var dateField = result.First(f => f.FieldName == "日付");

            // Assert
            dateField.DecodedValue.Should().Be(expectedDate);
        }

        [Fact]
        public void Parse_DateField_BinaryShowsBitGrouping()
        {
            // Arrange: 2025/01/15 → year=25(0011001), month=1(0001), day=15(01111)
            var block = CreateTestBlock(dateHigh: 0x32, dateLow: 0x2F);

            // Act
            var result = FelicaBlockParser.Parse(block);
            var dateField = result.First(f => f.FieldName == "日付");

            // Assert - ビットグループが角括弧で表示されること
            dateField.BinaryValue.Should().Contain("[");
            dateField.BinaryValue.Should().Contain("]");
            // [YYYYYYY][MMMM][DDDDD] 形式
            dateField.BinaryValue.Should().Be("[0011001][0001][01111]");
        }

        [Fact]
        public void Parse_DateField_DetailShowsComponents()
        {
            // Arrange: 2025/01/15
            var block = CreateTestBlock(dateHigh: 0x32, dateLow: 0x2F);

            // Act
            var result = FelicaBlockParser.Parse(block);
            var dateField = result.First(f => f.FieldName == "日付");

            // Assert - 詳細にビット位置と値が含まれること
            dateField.Detail.Should().Contain("年(bit15-9)=25");
            dateField.Detail.Should().Contain("月(bit8-5)=1");
            dateField.Detail.Should().Contain("日(bit4-0)=15");
        }

        [Fact]
        public void Parse_DateField_InvalidDate_ShowsInvalidLabel()
        {
            // Arrange: month=0, day=0（無効な日付）
            var block = CreateTestBlock(dateHigh: 0x32, dateLow: 0x00);

            // Act
            var result = FelicaBlockParser.Parse(block);
            var dateField = result.First(f => f.FieldName == "日付");

            // Assert
            dateField.DecodedValue.Should().Contain("無効");
        }

        #endregion

        #region Parse - 利用種別

        [Fact]
        public void Parse_ChargeUsageType_ShowsChargeLabel()
        {
            // Arrange: 利用種別 = 0x02（チャージ）
            var block = CreateTestBlock(usageType: 0x02);

            // Act
            var result = FelicaBlockParser.Parse(block);
            var usageField = result.First(f => f.FieldName == "利用種別");

            // Assert
            usageField.DecodedValue.Should().Contain("チャージ");
        }

        [Fact]
        public void Parse_MerchandiseUsageType_ShowsMerchandiseLabel()
        {
            // Arrange: 利用種別 = 0x07（物販）
            var block = CreateTestBlock(usageType: 0x07);

            // Act
            var result = FelicaBlockParser.Parse(block);
            var usageField = result.First(f => f.FieldName == "利用種別");

            // Assert
            usageField.DecodedValue.Should().Contain("物販");
        }

        [Fact]
        public void Parse_TransitUsageType_ShowsTransitLabel()
        {
            // Arrange: 利用種別 = 0x01（乗車）
            var block = CreateTestBlock(usageType: 0x01);

            // Act
            var result = FelicaBlockParser.Parse(block);
            var usageField = result.First(f => f.FieldName == "利用種別");

            // Assert
            usageField.DecodedValue.Should().Contain("乗車");
        }

        #endregion

        #region Parse - 残額フィールド（リトルエンディアン）

        [Theory]
        [InlineData(0xE8, 0x03, 1000)]   // LE: 0x03E8 = 1000
        [InlineData(0x00, 0x00, 0)]       // 0円
        [InlineData(0xFF, 0xFF, 65535)]   // 最大値
        [InlineData(0x50, 0x01, 336)]     // LE: 0x0150 = 336
        public void Parse_BalanceField_LittleEndianDecodeCorrectly(byte low, byte high, int expectedBalance)
        {
            // Arrange
            var block = CreateTestBlock(balanceLow: low, balanceHigh: high);

            // Act
            var result = FelicaBlockParser.Parse(block);
            var balanceField = result.First(f => f.FieldName == "残額");

            // Assert - 円記号と金額が含まれること
            balanceField.DecodedValue.Should().Contain($"{expectedBalance:N0}");
            balanceField.Detail.Should().Contain("LE");
        }

        #endregion

        #region Parse - 駅コードフィールド（ビッグエンディアン）

        [Theory]
        [InlineData(0x01, 0x3A, "0x013A", 314)]
        [InlineData(0x01, 0x45, "0x0145", 325)]
        [InlineData(0x00, 0x00, null, 0)]  // コード0はなし
        public void Parse_StationCode_BigEndianDecodeCorrectly(byte high, byte low, string expectedHex, int expectedDecimal)
        {
            // Arrange
            var block = CreateTestBlock(entryHigh: high, entryLow: low);

            // Act
            var result = FelicaBlockParser.Parse(block);
            var entryField = result.First(f => f.FieldName == "入場駅コード");

            // Assert
            if (expectedHex != null)
            {
                entryField.DecodedValue.Should().Contain(expectedHex);
                entryField.DecodedValue.Should().Contain($"({expectedDecimal})");
            }
            else
            {
                entryField.DecodedValue.Should().Contain("なし");
            }
        }

        #endregion

        #region Parse - Hexフィールド表示

        [Fact]
        public void Parse_MachineType_HexValueIsCorrect()
        {
            // Arrange
            var block = CreateTestBlock(machineType: 0xAB);

            // Act
            var result = FelicaBlockParser.Parse(block);
            var machineField = result.First(f => f.FieldName == "機器種別");

            // Assert
            machineField.HexValue.Should().Be("AB");
        }

        #endregion

        #region FormatBinary

        [Theory]
        [InlineData(0x00, "00000000")]
        [InlineData(0xFF, "11111111")]
        [InlineData(0xA5, "10100101")]
        [InlineData(0x01, "00000001")]
        [InlineData(0x80, "10000000")]
        public void FormatBinary_ReturnsCorrect8BitString(byte input, string expected)
        {
            // Act
            var result = FelicaBlockParser.FormatBinary(input);

            // Assert
            result.Should().Be(expected);
        }

        #endregion

        #region FormatAsText

        [Fact]
        public void FormatAsText_ValidFields_ContainsHeader()
        {
            // Arrange
            var block = CreateTestBlock();
            var fields = FelicaBlockParser.Parse(block);

            // Act
            var text = FelicaBlockParser.FormatAsText(fields);

            // Assert
            text.Should().Contain("ビット単位フィールド解析");
        }

        [Fact]
        public void FormatAsText_ValidFields_ContainsAllFieldNames()
        {
            // Arrange
            var block = CreateTestBlock();
            var fields = FelicaBlockParser.Parse(block);

            // Act
            var text = FelicaBlockParser.FormatAsText(fields);

            // Assert
            text.Should().Contain("機器種別");
            text.Should().Contain("利用種別");
            text.Should().Contain("支払種別");
            text.Should().Contain("入出場種別");
            text.Should().Contain("日付");
            text.Should().Contain("入場駅コード");
            text.Should().Contain("出場駅コード");
            text.Should().Contain("残額");
            text.Should().Contain("予備");
        }

        [Fact]
        public void FormatAsText_NullFields_ReturnsNoDataMessage()
        {
            // Act
            var text = FelicaBlockParser.FormatAsText(null);

            // Assert
            text.Should().Contain("解析データなし");
        }

        [Fact]
        public void FormatAsText_EmptyFields_ReturnsNoDataMessage()
        {
            // Act
            var text = FelicaBlockParser.FormatAsText(new System.Collections.Generic.List<FelicaFieldInfo>());

            // Assert
            text.Should().Contain("解析データなし");
        }

        #endregion

        #region ParseDateField - 内部メソッド直接テスト

        [Fact]
        public void ParseDateField_HexValueIsCorrect()
        {
            // Arrange & Act
            var field = FelicaBlockParser.ParseDateField(0x32, 0x2F);

            // Assert
            field.HexValue.Should().Be("32 2F");
        }

        [Fact]
        public void ParseDateField_OffsetLabelIsCorrect()
        {
            // Act
            var field = FelicaBlockParser.ParseDateField(0x32, 0x2F);

            // Assert
            field.OffsetLabel.Should().Be("[04-05]");
        }

        #endregion

        #region ParseBalanceField - 内部メソッド直接テスト

        [Fact]
        public void ParseBalanceField_HexValueShowsOriginalByteOrder()
        {
            // Arrange & Act: LE format - low byte first in memory
            var field = FelicaBlockParser.ParseBalanceField(0xE8, 0x03);

            // Assert - Hex表示はメモリ上のバイト順序で表示
            field.HexValue.Should().Be("E8 03");
        }

        [Fact]
        public void ParseBalanceField_DetailShowsLEHexValue()
        {
            // Act
            var field = FelicaBlockParser.ParseBalanceField(0xE8, 0x03);

            // Assert
            field.Detail.Should().Contain("LE");
            field.Detail.Should().Contain("0x03E8");
        }

        #endregion
    }
}
