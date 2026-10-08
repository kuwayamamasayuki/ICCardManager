using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Dtos;
using ICCardManager.Models;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// Issue #2255: 繰越情報の復旧画面の入力の解釈・検証（<see cref="CarryoverInfoInput.Parse"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 要点は「失われた項目に既定値（1 / 0 円 / 空欄）を入れることを拒む」こと。既定値を書き戻しても
/// 値は戻らず、消失の検知は現在値が既定値のままなら警告を出し続ける。拒む側だけを固定すると
/// 既定値を常に拒む実装でも緑になるため、「失われていない項目なら既定値でもよい」側を対で置く。
/// </para>
/// </remarks>
public class CarryoverInfoInputTests
{
    /// <summary>今年度（テストの固定値）。本体が実時計を読む実装へ戻ると範囲の境界が食い違う</summary>
    private const int CurrentFiscalYear = 2026;

    private static CarryoverDataLossItem AllLost() => new()
    {
        CardIdm = "07FE112233445566",
        CardDisplayName = "はやかけん 001",
        LostStartingPageNumber = 7,
        LostCarryoverIncomeTotal = 45000,
        LostCarryoverExpenseTotal = 37500,
        LostCarryoverFiscalYear = 2025,
    };

    /// <summary>開始ページ番号だけが失われた（紙の出納簿から移行したが繰越累計は持っていなかった）カード</summary>
    private static CarryoverDataLossItem PageOnlyLost() => new()
    {
        CardIdm = "07FE112233445566",
        CardDisplayName = "はやかけん 001",
        LostStartingPageNumber = 7,
    };

    private static CarryoverInputParseResult Parse(
        string page, string income, string expense, string year, CarryoverDataLossItem? lost = null) =>
        CarryoverInfoInput.Parse(page, income, expense, year, lost ?? AllLost(), CurrentFiscalYear);

    #region 受け付ける入力

    [Fact]
    public void 失われた値をそのまま入れれば_その値で解釈すること()
    {
        var result = Parse("7", "45000", "37500", "2025");

        result.IsValid.Should().BeTrue(result.ErrorMessage);
        result.Value.Should().Be(new CarryoverInfo(7, 45000, 37500, 2025));
        result.ErrorMessage.Should().BeNull();
        result.ErrorField.Should().BeNull();
    }

    [Fact]
    public void 全角の数字_桁区切り_前後の空白を受け付けること()
    {
        // 日本語入力のまま打った数字や、一覧の「45,000円」を写した入力を拒まない
        var result = Parse("７", " 45,000 ", "３７，５００", "２０２５");

        result.Value.Should().Be(new CarryoverInfo(7, 45000, 37500, 2025));
    }

    [Fact]
    public void 失われていない項目は既定値でもよいこと()
    {
        // 開始ページ番号だけが失われたカード: 繰越累計 0 円・年度の空欄はもともとの値
        var result = Parse("7", "0", "0", "", PageOnlyLost());

        result.Value.Should().Be(new CarryoverInfo(7, 0, 0, null));
    }

    [Fact]
    public void 失われていない開始ページ番号は1でもよいこと()
    {
        var lost = AllLost();
        lost.LostStartingPageNumber = null;

        var result = Parse("1", "45000", "37500", "2025", lost);

        result.Value.Should().Be(new CarryoverInfo(1, 45000, 37500, 2025));
    }

    [Theory]
    [InlineData("2000", 2000)]   // 下限
    [InlineData("2026", 2026)]   // 今年度
    public void 対象年度は2000年度から今年度までを受け付けること(string year, int expected)
    {
        Parse("7", "45000", "37500", year).Value!.CarryoverFiscalYear.Should().Be(expected);
    }

    #endregion

    #region 拒む入力

    [Fact]
    public void 失われた開始ページ番号に1を入れたら_既定値では戻らないと案内すること()
    {
        var result = Parse("1", "45000", "37500", "2025");

        result.IsValid.Should().BeFalse();
        result.ErrorField.Should().Be(CarryoverInputField.StartingPageNumber);
        result.ErrorMessage.Should().Be(
            "開始ページ番号が1のままです。1は登録時の既定値のため、失われた値が戻らず警告も消えません。" +
            "紙の出納簿の続きのページ番号（2以上）を入力してください。");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    public void 開始ページ番号が1未満なら拒むこと(string page)
    {
        var result = Parse(page, "45000", "37500", "2025", PageOnlyLostWithTotals());

        result.ErrorField.Should().Be(CarryoverInputField.StartingPageNumber);
        result.ErrorMessage.Should().Contain($"開始ページ番号が{page}です");
    }

    [Fact]
    public void 開始ページ番号が空欄なら拒むこと()
    {
        var result = Parse("", "45000", "37500", "2025");

        result.ErrorField.Should().Be(CarryoverInputField.StartingPageNumber);
        result.ErrorMessage.Should().Be("開始ページ番号が空欄です。物品出納簿の開始ページ番号を1以上の整数で入力してください。");
    }

    [Theory]
    [InlineData("七")]
    [InlineData("7ページ")]
    [InlineData("1.5")]
    public void 開始ページ番号が整数として読めなければ拒むこと(string page)
    {
        var result = Parse(page, "45000", "37500", "2025");

        result.ErrorField.Should().Be(CarryoverInputField.StartingPageNumber);
        result.ErrorMessage.Should().Be($"開始ページ番号「{page}」は数値として読めません。1以上の整数を入力してください。");
    }

    [Fact]
    public void 失われた繰越累計受入に0円を入れたら_既定値では戻らないと案内すること()
    {
        var result = Parse("7", "0", "37500", "2025");

        result.ErrorField.Should().Be(CarryoverInputField.CarryoverIncomeTotal);
        result.ErrorMessage.Should().StartWith("繰越累計受入が0円です。0円は登録時の既定値のため");
    }

    [Fact]
    public void 失われた繰越累計払出に0円を入れたら_既定値では戻らないと案内すること()
    {
        var result = Parse("7", "45000", "0", "2025");

        result.ErrorField.Should().Be(CarryoverInputField.CarryoverExpenseTotal);
        result.ErrorMessage.Should().StartWith("繰越累計払出が0円です。0円は登録時の既定値のため");
    }

    [Fact]
    public void 繰越累計がマイナスなら拒むこと()
    {
        var result = Parse("7", "-1500", "37500", "2025");

        result.ErrorField.Should().Be(CarryoverInputField.CarryoverIncomeTotal);
        result.ErrorMessage.Should().Contain("（マイナス）");
    }

    [Theory]
    [InlineData("45000円")]
    [InlineData("abc")]
    public void 繰越累計が金額として読めなければ拒むこと(string income)
    {
        var result = Parse("7", income, "37500", "2025");

        result.ErrorField.Should().Be(CarryoverInputField.CarryoverIncomeTotal);
        result.ErrorMessage.Should().Be($"繰越累計受入「{income}」は金額として読めません。0以上の整数を円単位で入力してください。");
    }

    [Fact]
    public void 繰越累計が空欄なら_累計が無い場合も0と入れるよう案内すること()
    {
        // 失われていない払出を空欄にした場合。0 と区別のつかない空欄を黙って 0 と読まない
        var result = Parse("7", "45000", " ", "2025", PageAndIncomeLost());

        result.ErrorField.Should().Be(CarryoverInputField.CarryoverExpenseTotal);
        result.ErrorMessage.Should().Be("繰越累計払出が空欄です。累計が無い場合も0と入力し、ある場合は0以上の整数を円単位で入力してください。");
    }

    [Fact]
    public void 失われた対象年度を空欄にしたら_既定値では戻らないと案内すること()
    {
        var result = Parse("7", "45000", "37500", "");

        result.ErrorField.Should().Be(CarryoverInputField.CarryoverFiscalYear);
        result.ErrorMessage.Should().StartWith("対象年度が空欄です。空欄は登録時の既定値のため");
    }

    [Fact]
    public void 繰越累計があるのに対象年度が空欄なら_どの年度にも加算されないと案内すること()
    {
        // 年度が失われていないカードでも、累計を持たせるなら年度が要る（帳票は年度が一致したときだけ加算する）
        var lost = PageOnlyLost();

        var result = Parse("7", "45000", "0", "", lost);

        result.ErrorField.Should().Be(CarryoverInputField.CarryoverFiscalYear);
        result.ErrorMessage.Should().StartWith("対象年度が空欄のため、繰越累計（受入・払出）が物品出納簿のどの年度の累計にも加算されません。");
    }

    [Theory]
    [InlineData("1999")]
    [InlineData("2027")]   // 今年度の翌年度
    public void 対象年度が範囲外なら拒むこと(string year)
    {
        var result = Parse("7", "45000", "37500", year);

        result.ErrorField.Should().Be(CarryoverInputField.CarryoverFiscalYear);
        result.ErrorMessage.Should().Be(
            $"対象年度が{year}年度です。繰越累計を加算できるのは2000年度から今年度（2026年度）までのため、" +
            "この範囲の年度を西暦4桁で入力してください。");
    }

    [Theory]
    [InlineData("R7")]
    [InlineData("令和7")]
    public void 対象年度が西暦として読めなければ拒むこと(string year)
    {
        var result = Parse("7", "45000", "37500", year);

        result.ErrorField.Should().Be(CarryoverInputField.CarryoverFiscalYear);
        result.ErrorMessage.Should().Be($"対象年度「{year}」は年として読めません。西暦4桁（例: 2025）で入力してください。");
    }

    [Fact]
    public void 複数の欄に誤りがあれば_画面の上の欄から1つずつ案内すること()
    {
        // フォーカスを移す先を 1 つに決めるため。下の欄を先に案内すると、上の欄を直した後に
        // 「さっきは出なかった」誤りが現れる
        var result = Parse("1", "0", "0", "");

        result.ErrorField.Should().Be(CarryoverInputField.StartingPageNumber);
    }

    #endregion

    #region 文言の品質（error-messages.md）

    public static IEnumerable<object[]> FailingInputs() => new[]
    {
        new object[] { "", "45000", "37500", "2025" },
        new object[] { "abc", "45000", "37500", "2025" },
        new object[] { "0", "45000", "37500", "2025" },
        new object[] { "1", "45000", "37500", "2025" },
        new object[] { "7", "", "37500", "2025" },
        new object[] { "7", "abc", "37500", "2025" },
        new object[] { "7", "-1", "37500", "2025" },
        new object[] { "7", "0", "37500", "2025" },
        new object[] { "7", "45000", "0", "2025" },
        new object[] { "7", "45000", "37500", "" },
        new object[] { "7", "45000", "37500", "R7" },
        new object[] { "7", "45000", "37500", "1999" },
    };

    [Theory]
    [MemberData(nameof(FailingInputs))]
    public void 誤りの案内は何が_なぜ_どうすればを含み行動指示で終わること(
        string page, string income, string expense, string year)
    {
        var message = Parse(page, income, expense, year).ErrorMessage;

        message.Should().NotBeNull();
        message!.Length.Should().BeGreaterThanOrEqualTo(20);
        Regex.IsMatch(message, "(してください|入力してください|選択してください|設定してください)。?$")
            .Should().BeTrue($"行動指示で終わること: {message}");
        message.Should().NotContain("エラーが発生しました").And.NotContain("不正な値");
        message.Should().NotContain("ICカード", "交通系ICカードを指す語は「交通系ICカード」と書く");
    }

    [Fact]
    public void 品質を検査する入力は_すべての入力欄の誤りを含むこと()
    {
        // 入力欄を足したのに品質の検査へ載せていない状態を検出する
        var fields = FailingInputs()
            .Select(args => Parse((string)args[0], (string)args[1], (string)args[2], (string)args[3]).ErrorField)
            .ToHashSet();

        fields.Should().BeEquivalentTo(
            System.Enum.GetValues(typeof(CarryoverInputField)).Cast<CarryoverInputField?>());
    }

    #endregion

    private static CarryoverDataLossItem PageAndIncomeLost()
    {
        var lost = AllLost();
        lost.LostCarryoverExpenseTotal = null;
        return lost;
    }

    private static CarryoverDataLossItem PageOnlyLostWithTotals()
    {
        var lost = AllLost();
        lost.LostStartingPageNumber = null;
        return lost;
    }
}
