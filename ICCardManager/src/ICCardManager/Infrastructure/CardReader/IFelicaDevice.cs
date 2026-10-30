#nullable enable
using System.Collections.Generic;
using FelicaLib;

namespace ICCardManager.Infrastructure.CardReader
{
    /// <summary>
    /// <see cref="FelicaCardReader"/> が felicalib（<see cref="FelicaUtility"/>）へ行う呼び出しの継ぎ目（Issue #2301）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="FelicaUtility"/> は静的クラスで差し替えられないため、「読み取りの途中でカードが離れた」
    /// 「別のカードへ載せ替えられた」といった実機でしか起きない状況を単体テストで再現できなかった。
    /// 本番は <see cref="FelicaUtilityDevice"/> が <see cref="FelicaUtility"/> へそのまま委譲する。
    /// </para>
    /// <para>
    /// FelicaLib.DotNet 1.2.67 の実際の振る舞い（DLL を ildasm で逆アセンブルして確認）: どのメソッドも呼び出しのたびに
    /// PaSoRi を開いてポーリングし直す。カードが無ければ <see cref="System.InvalidOperationException"/>
    /// 「IC カードが見つかりません。または、システム コードが一致しません。」を投げ、<c>GetIDm</c> は null を返さない。
    /// <c>ReadBlocksWithoutEncryption</c> は 1 回のポーリングの中で全ブロックを先に読み切り（遅延評価しない）、
    /// 1 ブロックでも読めなければ同じ型で「指定されたサービス コードおよびアドレスのデータが存在しません。」を投げる
    /// （読めなかったブロックを null で返さない）。<see cref="FelicaCardReader"/> はこれらの例外をカードを見失ったものと判定する
    /// （<see cref="FelicaCardReader.IsCardNotPresentException"/>）。
    /// </para>
    /// <para>
    /// 戻り値を null 許容にしているのは版が変わったときに備えた防御のため（呼び出し側は null も「読めない」として扱う）。
    /// </para>
    /// </remarks>
    internal interface IFelicaDevice
    {
        /// <summary>製造 ID（IDm）を取得する。<see cref="FelicaUtility.GetIDm(int)"/> と同じ。</summary>
        byte[]? GetIDm(int systemCode);

        /// <summary>非暗号化領域の 1 ブロックを読む。<see cref="FelicaUtility.ReadWithoutEncryption(int, int, int)"/> と同じ。</summary>
        byte[]? ReadWithoutEncryption(int systemCode, int serviceCode, int address);

        /// <summary>非暗号化領域の連続した複数ブロックを読む。<see cref="FelicaUtility.ReadBlocksWithoutEncryption(int, int, int, int)"/> と同じ。</summary>
        IEnumerable<byte[]?> ReadBlocksWithoutEncryption(int systemCode, int serviceCode, int addressStart, int addressCount);
    }

    /// <summary>
    /// <see cref="IFelicaDevice"/> の本番実装。<see cref="FelicaUtility"/> へそのまま委譲する。
    /// </summary>
    internal sealed class FelicaUtilityDevice : IFelicaDevice
    {
        /// <summary>共有インスタンス（状態を持たない）</summary>
        public static readonly FelicaUtilityDevice Instance = new FelicaUtilityDevice();

        private FelicaUtilityDevice()
        {
        }

        public byte[]? GetIDm(int systemCode) => FelicaUtility.GetIDm(systemCode);

        public byte[]? ReadWithoutEncryption(int systemCode, int serviceCode, int address)
            => FelicaUtility.ReadWithoutEncryption(systemCode, serviceCode, address);

        public IEnumerable<byte[]?> ReadBlocksWithoutEncryption(int systemCode, int serviceCode, int addressStart, int addressCount)
            => FelicaUtility.ReadBlocksWithoutEncryption(systemCode, serviceCode, addressStart, addressCount);
    }
}
