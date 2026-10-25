using System;
using System.Collections.Generic;
using System.Globalization;
using ICCardManager.Infrastructure.CardReader;

namespace ICCardManager.Tests.Infrastructure.CardReader;

/// <summary>
/// felicalib の代わりに、リーダーに載っているカードと履歴ブロックを台本どおりに返す <see cref="IFelicaDevice"/>（Issue #2301）。
/// </summary>
/// <remarks>
/// <para>
/// 振る舞いは FelicaLib.DotNet 1.2.67 に合わせている（DLL を ildasm で逆アセンブルして確認）。
/// 呼び出しのたびにポーリングし直し、カードが無ければ <see cref="InvalidOperationException"/>（<see cref="CardNotFoundMessage"/>）を投げる。
/// <c>ReadBlocksWithoutEncryption</c> は全ブロックを先に読み切り、途中で読めなければ <see cref="BlockNotReadMessage"/> を投げる。
/// 例外の文言は DLL の文字列リテラルから転記した（本番の判定 <see cref="FelicaCardReader.IsCardNotPresentException"/> とは別に持つ）。
/// </para>
/// <para>
/// 実機でしか起きない「タッチしてすぐ離す」「途中で離れる」「確認の後に載せ替える」「読み終えた直後に離す」を各プロパティで再現する。
/// </para>
/// </remarks>
internal sealed class FakeFelicaDevice : IFelicaDevice
{
    /// <summary>FelicaLib.DotNet 1.2.67 がポーリングでカードを見つけられないときの例外メッセージ</summary>
    public const string CardNotFoundMessage = "IC カードが見つかりません。または、システム コードが一致しません。";

    /// <summary>FelicaLib.DotNet 1.2.67 がブロックを読めないときの例外メッセージ</summary>
    public const string BlockNotReadMessage = "指定されたサービス コードおよびアドレスのデータが存在しません。";

    private readonly Dictionary<string, List<byte[]>> _blocksByIdm = new Dictionary<string, List<byte[]>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>リーダーに載っているカードの IDm（null はカード無し）</summary>
    public string? CurrentIdm { get; set; }

    /// <summary>カード無しのとき、例外ではなく null を返す（1.2.67 はそうしないが、防御の経路を通すため）</summary>
    public bool ReturnNullIdmWhenNoCard { get; set; }

    /// <summary>このブロック数を読んだところでカードが離れる（null なら離れない。0 なら最初のブロックで失敗する）</summary>
    public int? RemoveCardDuringReadAfterBlocks { get; set; }

    /// <summary><c>ReadBlocksWithoutEncryption</c> のポーリングの直前に、このカードへ載せ替える（null なら載せ替えない）</summary>
    public string? SwapBeforeReadBlocksTo { get; set; }

    /// <summary><c>ReadBlocksWithoutEncryption</c> を読み終えた直後にカードが離れる</summary>
    public bool RemoveCardAfterReadBlocks { get; set; }

    /// <summary><c>ReadBlocksWithoutEncryption</c> を読み終えた直後に、このカードへ載せ替える（null なら載せ替えない）</summary>
    public string? SwapAfterReadBlocksTo { get; set; }

    /// <summary><c>ReadBlocksWithoutEncryption</c> を読み終えた後の <c>GetIDm</c> で投げる例外（読み終えた直後に USB が抜けた等。null なら投げない）</summary>
    public Exception? ThrowOnGetIdmAfterReadBlocks { get; set; }

    private bool _readBlocksCompleted;

    /// <summary>履歴ブロックを読むときに投げる例外（リーダーの故障の再現。null なら投げない）</summary>
    public Exception? ThrowOnReadBlocks { get; set; }

    /// <summary><see cref="ReadBlocksWithoutEncryption"/> が呼ばれた回数</summary>
    public int ReadBlocksCallCount { get; private set; }

    /// <summary>カードに残っている履歴ブロック（新しい順）。未設定のカードは利用 0 件（全ブロック 0 埋め）</summary>
    public List<byte[]> BlocksOf(string idm)
    {
        if (!_blocksByIdm.TryGetValue(idm, out var blocks))
        {
            blocks = new List<byte[]>();
            _blocksByIdm[idm] = blocks;
        }

        return blocks;
    }

    public byte[]? GetIDm(int systemCode)
    {
        if (_readBlocksCompleted && ThrowOnGetIdmAfterReadBlocks is not null)
        {
            throw ThrowOnGetIdmAfterReadBlocks;
        }

        if (CurrentIdm is null)
        {
            return ReturnNullIdmWhenNoCard ? null : throw new InvalidOperationException(CardNotFoundMessage);
        }

        return ParseHex(CurrentIdm);
    }

    public byte[]? ReadWithoutEncryption(int systemCode, int serviceCode, int address)
    {
        var idm = CurrentIdm ?? throw new InvalidOperationException(CardNotFoundMessage);
        var blocks = BlocksOf(idm);
        return address < blocks.Count ? blocks[address] : new byte[16];
    }

    public IEnumerable<byte[]?> ReadBlocksWithoutEncryption(int systemCode, int serviceCode, int addressStart, int addressCount)
    {
        ReadBlocksCallCount++;
        if (ThrowOnReadBlocks is not null)
        {
            throw ThrowOnReadBlocks;
        }

        if (SwapBeforeReadBlocksTo is not null)
        {
            CurrentIdm = SwapBeforeReadBlocksTo;
        }

        // ポーリング: カードが無ければ失敗する
        var idm = CurrentIdm ?? throw new InvalidOperationException(CardNotFoundMessage);
        var blocks = BlocksOf(idm);

        // 1.2.67 は 1 回のポーリングの中で全ブロックを先に読み切る（遅延評価しない）
        var result = new List<byte[]?>();
        for (var i = 0; i < addressCount; i++)
        {
            if (RemoveCardDuringReadAfterBlocks == i)
            {
                CurrentIdm = null;
                throw new InvalidOperationException(BlockNotReadMessage);
            }

            var address = addressStart + i;
            // 実機の交通系ICカードは使っていないブロックを 0 で埋めて返す
            result.Add(address < blocks.Count ? blocks[address] : new byte[16]);
        }

        if (RemoveCardAfterReadBlocks)
        {
            CurrentIdm = null;
        }

        if (SwapAfterReadBlocksTo is not null)
        {
            CurrentIdm = SwapAfterReadBlocksTo;
        }

        _readBlocksCompleted = true;

        return result.ToArray();
    }

    private static byte[] ParseHex(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return bytes;
    }

    /// <summary>
    /// 16 バイトの履歴ブロック（鉄道利用。日付と残額だけを持つ）を作る。
    /// </summary>
    /// <remarks>バイト配置は <c>FelicaHistoryBlockDecoderTests.BuildBlock</c> と同じ。</remarks>
    public static byte[] BuildRailBlock(DateTime date, int balance)
    {
        var block = new byte[16];
        block[1] = 0x16; // 利用種別: 鉄道（運賃支払）
        var dateValue = ((date.Year - 2000) << 9) | (date.Month << 5) | date.Day;
        block[4] = (byte)((dateValue >> 8) & 0xFF);
        block[5] = (byte)(dateValue & 0xFF);
        block[10] = (byte)(balance & 0xFF);
        block[11] = (byte)((balance >> 8) & 0xFF);
        return block;
    }
}
