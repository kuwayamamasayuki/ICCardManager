using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common.Exceptions;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Models;
using ICCardManager.Services;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Infrastructure.CardReader;

/// <summary>
/// 履歴の読み取り中に交通系ICカードが離れた・載せ替えられた場合に、読み取りを「利用 0 件で成功」にしないこと（Issue #2301）。
/// </summary>
/// <remarks>
/// <para>
/// 以前の <see cref="FelicaCardReader.TryReadHistoryAsync"/> は、読み取り前の IDm の不一致（別のカード）を <c>Ok(空リスト)</c> とし、
/// 確認の後に載せ替えると<b>載せ替えた先のカードの履歴</b>を <c>Ok</c> で返していた。返却フローはこれを受けて記録していた。
/// </para>
/// <para>
/// felicalib は <see cref="IFelicaDevice"/> の継ぎ目で <see cref="FakeFelicaDevice"/>（FelicaLib.DotNet 1.2.67 の振る舞いを再現）に差し替える。
/// 「欠陥を突く側」と「正当な動作を塞いでいない側」を対で置く。後方互換の <see cref="FelicaCardReader.ReadHistoryAsync"/>（カード登録時の事前読み取り）は
/// 挙動を変えないことを固定する。
/// </para>
/// </remarks>
public class FelicaCardReaderHistoryReadTests
{
    private const string TargetIdm = "0123456789ABCDEF";
    private const string OtherIdm = "FEDCBA9876543210";

    private readonly FakeFelicaDevice _device = new FakeFelicaDevice();
    private readonly ICCardManager.Tests.Infrastructure.RecordingLogger<FelicaCardReader> _logger = new();
    private readonly List<Exception> _raisedErrors = new List<Exception>();
    private readonly FelicaCardReader _reader;

    public FelicaCardReaderHistoryReadTests()
    {
        var stationMaster = new Mock<IStationMasterService>();
        _reader = new FelicaCardReader(_logger, stationMaster.Object, _device);
        _reader.Error += (_, e) => _raisedErrors.Add(e);
    }

    /// <summary>新しい順に 3 件（残額 700 → 800 → 1000）の利用が残っている対象のカードを載せる</summary>
    private void PlaceTargetCardWithThreeUsages()
    {
        _device.CurrentIdm = TargetIdm;
        var blocks = _device.BlocksOf(TargetIdm);
        blocks.Add(FakeFelicaDevice.BuildRailBlock(new DateTime(2026, 10, 3), balance: 700));
        blocks.Add(FakeFelicaDevice.BuildRailBlock(new DateTime(2026, 10, 2), balance: 800));
        blocks.Add(FakeFelicaDevice.BuildRailBlock(new DateTime(2026, 10, 1), balance: 1000));
    }

    /// <summary>別のカード（残額 5000 の利用 1 件）。載せ替え先</summary>
    private void PrepareOtherCardWithOneUsage()
    {
        _device.BlocksOf(OtherIdm).Add(FakeFelicaDevice.BuildRailBlock(new DateTime(2026, 9, 30), balance: 5000));
    }

    private void AssertCardRemovedWithoutErrorEvent(CardReadResult<IReadOnlyList<LedgerDetail>> result, string because)
    {
        result.Success.Should().BeFalse(because + " " + _logger.FormatEntries());
        result.Error.IsCardRemoved.Should().BeTrue($"カードが載っていなかったことを示す（実際: {result.Error.ErrorCode}）");
        result.Error.UserFriendlyMessage.Should().Be(
            "読み取りの途中で交通系ICカードが離されました。職員証のタッチからやり直し、音が鳴るまで載せたままにしてください。",
            "返却のトーストと同じ手順で案内する");
        _raisedErrors.Should().BeEmpty("利用者の操作でありリーダーの故障ではない。発火すると警告エリアへ「カードリーダーエラー」が積まれる");
    }

    #region 正当な動作を塞いでいない側

    [Fact]
    public async Task 最後まで載っていれば読めた明細をOkで返すこと()
    {
        PlaceTargetCardWithThreeUsages();

        var result = await _reader.TryReadHistoryAsync(TargetIdm);

        result.Success.Should().BeTrue(_logger.FormatEntries());
        result.Value.Select(d => d.Balance).Should().Equal(new int?[] { 700, 800, 1000 });
        _raisedErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task 最後まで載っていて利用が本当に0件ならOkの空リストを返すこと()
    {
        _device.CurrentIdm = TargetIdm; // 履歴ブロックはすべて 0 埋め

        var result = await _reader.TryReadHistoryAsync(TargetIdm);

        result.Success.Should().BeTrue("対象のカードを最後まで読めて利用が無いのは正常なゼロ件。" + _logger.FormatEntries());
        result.Value.Should().BeEmpty();
        _raisedErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task IDmの大文字小文字が違っても同じカードとして読むこと()
    {
        PlaceTargetCardWithThreeUsages();

        var result = await _reader.TryReadHistoryAsync(TargetIdm.ToLowerInvariant());

        result.Success.Should().BeTrue(_logger.FormatEntries());
        result.Value.Should().HaveCount(3);
    }

    #endregion

    #region 欠陥を突く側

    [Fact]
    public async Task 読み取り前に別のカードが載っていればFailを返すこと()
    {
        // Issue #2301 の本体: 以前は Ok(空リスト) ＝ 利用 0 件の返却になった
        PlaceTargetCardWithThreeUsages();
        _device.CurrentIdm = OtherIdm;

        var result = await _reader.TryReadHistoryAsync(TargetIdm);

        AssertCardRemovedWithoutErrorEvent(result, "別のカードの状態は正常なゼロ件ではない");
        _device.ReadBlocksCallCount.Should().Be(0);
    }

    [Fact]
    public async Task 読み取り前にカードが無くfelicalibが例外を投げたらCardRemovedのFailを返すこと()
    {
        // 実機（1.2.67）で「タッチしてすぐ離す」とこうなる
        _device.CurrentIdm = null;

        var result = await _reader.TryReadHistoryAsync(TargetIdm);

        AssertCardRemovedWithoutErrorEvent(result, "カードが無い");
        result.Error.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be(FakeFelicaDevice.CardNotFoundMessage);
    }

    [Fact]
    public async Task 読み取り前にGetIDmがnullを返したらFailを返しブロックを読まないこと()
    {
        // 1.2.67 は null を返さないが、版が変わったときの防御
        _device.CurrentIdm = null;
        _device.ReturnNullIdmWhenNoCard = true;

        var result = await _reader.TryReadHistoryAsync(TargetIdm);

        AssertCardRemovedWithoutErrorEvent(result, "カードが無い");
        _device.ReadBlocksCallCount.Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task 読み取りの途中でカードが離れたらCardRemovedのFailを返すこと(int readableBlocks)
    {
        PlaceTargetCardWithThreeUsages();
        _device.RemoveCardDuringReadAfterBlocks = readableBlocks;

        var result = await _reader.TryReadHistoryAsync(TargetIdm);

        AssertCardRemovedWithoutErrorEvent(result, $"{readableBlocks} ブロックで離れた");
        result.Error.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be(FakeFelicaDevice.BlockNotReadMessage);
    }

    [Fact]
    public async Task 確認の後に別のカードへ載せ替えたらその履歴を返さずFailを返すこと()
    {
        // felicalib は呼び出しのたびにポーリングし直すので、載せ替えた先のカードの履歴を読む
        PlaceTargetCardWithThreeUsages();
        PrepareOtherCardWithOneUsage();
        _device.SwapBeforeReadBlocksTo = OtherIdm;

        var result = await _reader.TryReadHistoryAsync(TargetIdm);

        AssertCardRemovedWithoutErrorEvent(result, "読んだのは別のカードの履歴");
    }

    [Fact]
    public async Task 読み終えた直後に離れて後の確認ができなければFailを返すこと()
    {
        // 読めた内容が対象のカードのものだと確かめられない。やり直せば記録できるので失敗側へ倒す
        PlaceTargetCardWithThreeUsages();
        _device.RemoveCardAfterReadBlocks = true;

        var result = await _reader.TryReadHistoryAsync(TargetIdm);

        AssertCardRemovedWithoutErrorEvent(result, "後の確認でカードを検出できない");
    }

    #endregion

    #region リーダーの故障

    [Fact]
    public async Task リーダーの例外は従来どおりErrorイベントを発火してHistoryReadFailedのFailを返すこと()
    {
        PlaceTargetCardWithThreeUsages();
        // 1.2.67 が PaSoRi の初期化に失敗したときの文言（カードを見失った例外ではない）
        _device.ThrowOnReadBlocks = new InvalidOperationException("PaSoRi に接続できません。");

        var result = await _reader.TryReadHistoryAsync(TargetIdm);

        var expectedCode = CardReaderException.HistoryReadFailed().ErrorCode;
        result.Success.Should().BeFalse();
        result.Error.ErrorCode.Should().Be(expectedCode);
        result.Error.IsCardRemoved.Should().BeFalse();
        _raisedErrors.Should().ContainSingle()
            .Which.Should().BeOfType<CardReaderException>()
            .Which.ErrorCode.Should().Be(expectedCode);
    }

    [Fact]
    public async Task 読み終えた直後の確認でリーダーの例外が出たらカードが離れたとは扱わずErrorを発火すること()
    {
        // 読み終えた直後に USB が抜けた等。「交通系ICカードが離れました」と表示しない
        PlaceTargetCardWithThreeUsages();
        _device.ThrowOnGetIdmAfterReadBlocks = new InvalidOperationException("PaSoRi に接続できません。");

        var result = await _reader.TryReadHistoryAsync(TargetIdm);

        var expectedCode = CardReaderException.HistoryReadFailed().ErrorCode;
        result.Success.Should().BeFalse("読めた内容が対象のカードのものか確かめられない");
        result.Error.IsCardRemoved.Should().BeFalse();
        result.Error.ErrorCode.Should().Be(expectedCode);
        _raisedErrors.Should().ContainSingle()
            .Which.Should().BeOfType<CardReaderException>()
            .Which.ErrorCode.Should().Be(expectedCode);
    }

    [Fact]
    public async Task 読み終えた直後に離れて後の確認でGetIDmがnullを返したらFailを返すこと()
    {
        // 1.2.67 は null を返さないが、版が変わったときの防御（後の確認の null は「見失った」）
        PlaceTargetCardWithThreeUsages();
        _device.ReturnNullIdmWhenNoCard = true;
        _device.RemoveCardAfterReadBlocks = true;

        var result = await _reader.TryReadHistoryAsync(TargetIdm);

        AssertCardRemovedWithoutErrorEvent(result, "後の確認でカードを検出できない");
        _device.ReadBlocksCallCount.Should().Be(1, "前提: 読み取り前の確認は通過し、ブロックを読み終えている");
    }

    [Fact]
    public async Task 読み終えた直後に別のカードへ載せ替えたらFailを返すこと()
    {
        PlaceTargetCardWithThreeUsages();
        _device.SwapAfterReadBlocksTo = OtherIdm;

        var result = await _reader.TryReadHistoryAsync(TargetIdm);

        AssertCardRemovedWithoutErrorEvent(result, "後の確認で別のカードが載っている");
    }

    [Theory]
    [InlineData(FakeFelicaDevice.CardNotFoundMessage, true)]
    [InlineData(FakeFelicaDevice.BlockNotReadMessage, true)]
    [InlineData("PaSoRi に接続できません。", false)]
    [InlineData("felicalib.dll を開けません。", false)]
    [InlineData("", false)]
    public void IsCardNotPresentExceptionはfelicalibがカードを見失った例外だけを判定すること(string message, bool expected)
    {
        FelicaCardReader.IsCardNotPresentException(new InvalidOperationException(message)).Should().Be(expected);
    }

    [Fact]
    public void IsCardNotPresentExceptionはInvalidOperationException以外とnullを判定しないこと()
    {
        FelicaCardReader.IsCardNotPresentException(new Exception(FakeFelicaDevice.CardNotFoundMessage)).Should().BeFalse();
        FelicaCardReader.IsCardNotPresentException(null).Should().BeFalse();
    }

    #endregion

    #region 後方互換の ReadHistoryAsync（カード登録時の事前読み取り）は挙動を変えない

    [Fact]
    public async Task ReadHistoryAsyncは読み終えた直後に離れても従来どおり読めた明細を返すこと()
    {
        // 登録の事前読み取りは警告音の直後に離されやすい。後の確認で捨てると、以前なら取り込めた登録が黙って履歴なしになる
        PlaceTargetCardWithThreeUsages();
        _device.RemoveCardAfterReadBlocks = true;

        var details = (await _reader.ReadHistoryAsync(TargetIdm)).ToList();

        details.Select(d => d.Balance).Should().Equal(new int?[] { 700, 800, 1000 });
        _raisedErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadHistoryAsyncは確認の後に載せ替えて別のカードの履歴を読んだら空のリストを返すこと()
    {
        // 登録すると別のカードの履歴が導入行・履歴になる。読み取り前の不一致と同じく空にする
        PlaceTargetCardWithThreeUsages();
        PrepareOtherCardWithOneUsage();
        _device.SwapBeforeReadBlocksTo = OtherIdm;

        var details = (await _reader.ReadHistoryAsync(TargetIdm)).ToList();

        details.Should().BeEmpty(_logger.FormatEntries());
        _raisedErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadHistoryAsyncは読み終えた直後に別のカードへ載せ替えたら空のリストを返すこと()
    {
        PlaceTargetCardWithThreeUsages();
        _device.SwapAfterReadBlocksTo = OtherIdm;

        var details = (await _reader.ReadHistoryAsync(TargetIdm)).ToList();

        details.Should().BeEmpty(_logger.FormatEntries());
        _raisedErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadHistoryAsyncは読み終えた直後の確認でリーダーの例外が出ても従来どおり読めた明細を返しErrorを発火しないこと()
    {
        // 修正前は後の確認そのものが無かった。登録の挙動を変えない
        PlaceTargetCardWithThreeUsages();
        _device.ThrowOnGetIdmAfterReadBlocks = new InvalidOperationException("PaSoRi に接続できません。");

        var details = (await _reader.ReadHistoryAsync(TargetIdm)).ToList();

        details.Select(d => d.Balance).Should().Equal(new int?[] { 700, 800, 1000 });
        _raisedErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadHistoryAsyncは読み取り前に別のカードなら従来どおり空のリストを返しErrorを発火しないこと()
    {
        PlaceTargetCardWithThreeUsages();
        _device.CurrentIdm = OtherIdm;

        var details = (await _reader.ReadHistoryAsync(TargetIdm)).ToList();

        details.Should().BeEmpty();
        _raisedErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadHistoryAsyncは途中で離れたら従来どおりErrorを発火して空のリストを返すこと()
    {
        PlaceTargetCardWithThreeUsages();
        _device.RemoveCardDuringReadAfterBlocks = 1;

        var details = (await _reader.ReadHistoryAsync(TargetIdm)).ToList();

        details.Should().BeEmpty();
        _raisedErrors.Should().ContainSingle()
            .Which.Should().BeOfType<CardReaderException>()
            .Which.ErrorCode.Should().Be(CardReaderException.HistoryReadFailed().ErrorCode);
    }

    #endregion
}
