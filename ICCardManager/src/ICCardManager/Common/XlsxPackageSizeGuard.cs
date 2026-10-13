#nullable enable

using System;
using System.IO;
using System.IO.Compression;

namespace ICCardManager.Common
{
    /// <summary>
    /// xlsx（OPC/ZIP パッケージ）の大きさの検査結果（Issue #2272）
    /// </summary>
    public enum XlsxPackageSizeCheck
    {
        /// <summary>上限内</summary>
        WithinLimit,

        /// <summary>ファイル自体の大きさが上限を超える</summary>
        FileTooLarge,

        /// <summary>全パートの展開後サイズ（ZIP の宣言値）の合計が上限を超える</summary>
        ExpandedTooLarge,

        /// <summary>
        /// 調べられなかった（ファイルが無い・ZIP として読めない・ロック・権限不足など）。
        /// 判断は実際に開く側へ委ね、その側の従来のエラー処理に任せる
        /// </summary>
        Undetermined,
    }

    /// <summary>
    /// xlsx を丸ごと開く前に、ファイルと展開後の大きさを確かめる（Issue #2272）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 帳票作成は出力先の既存の年度ファイルを ClosedXML（<c>new XLWorkbook(path)</c>）で開き、全パートを展開する。
    /// 出力先フォルダーに書き込める者が年度ファイルと同じ名前で ZIP 爆弾を置くと、「帳票作成」を押した時点で
    /// メモリを圧迫させられる。ZIP 爆弾はファイル自体が小さいため、ファイルの大きさだけでは止められない。
    /// 中央ディレクトリが宣言する展開後サイズの合計も併せて見る。
    /// </para>
    /// <para>
    /// <b>限界</b>: 展開後サイズは ZIP の宣言値にすぎず、偽ることができる。宣言を偽った ZIP は本検査を通り、
    /// ClosedXML 側の展開は止められない（ClosedXML / Open XML SDK に展開量の上限を渡す手段が無い）。
    /// シート名だけを読む <see cref="XlsxSheetNameReader"/> は読み込み側でも打ち切るが、ブックを丸ごと開く経路では
    /// 本検査が防げるのは宣言どおりの ZIP 爆弾までである。
    /// </para>
    /// </remarks>
    public static class XlsxPackageSizeGuard
    {
        /// <summary>
        /// 開く xlsx ファイル自体の大きさの上限（8MB）
        /// </summary>
        /// <remarks>
        /// 本システムの年度ファイルはテンプレートが約 15KB、1 か月のシートが数十KB で、
        /// 12 か月ぶんを追記しても 1MB 前後にとどまるため、正当なファイルには 8 倍前後の余裕がある。
        /// </remarks>
        public const long MaxFileBytes = 8L * 1024 * 1024;

        /// <summary>
        /// 全パートの展開後サイズ（宣言値）の合計の上限（64MB）
        /// </summary>
        /// <remarks>
        /// xlsx の各パートは XML で圧縮率が高く、展開後はファイルの 10 倍前後になる。年度ファイル（1MB 前後）の
        /// 展開後は 10MB 前後で、ファイルの上限（8MB）いっぱいの正当なファイルでも 64MB 前後に収まる。
        /// これを超える宣言は、正当な年度ファイルではなく ZIP 爆弾とみなす。
        /// </remarks>
        public const long MaxExpandedBytes = 64L * 1024 * 1024;

        /// <summary>
        /// 既定の上限でファイルと展開後の大きさを確かめる（パスから開いて閉じる版。シート名の読み取り・テスト用）
        /// </summary>
        /// <param name="filePath">xlsx ファイルのパス</param>
        public static XlsxPackageSizeResult Check(string filePath)
        {
            return Check(filePath, MaxFileBytes, MaxExpandedBytes);
        }

        /// <summary>
        /// 上限を指定してファイルと展開後の大きさを確かめる（境界を単体テストで固定するための版）
        /// </summary>
        /// <remarks>
        /// 例外は投げない。調べられなかったときは <see cref="XlsxPackageSizeCheck.Undetermined"/> を返す。
        /// </remarks>
        internal static XlsxPackageSizeResult Check(string filePath, long maxFileBytes, long maxExpandedBytes)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return XlsxPackageSizeResult.Undetermined(null);
            }

            try
            {
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    return Check(stream, maxFileBytes, maxExpandedBytes);
                }
            }
            catch (Exception)
            {
                // 無い・読めない（ロック・権限）は、開く側の従来の処理に任せる
                return XlsxPackageSizeResult.Undetermined(null);
            }
        }

        /// <summary>
        /// 既定の上限で、開いているストリームの大きさと展開後の大きさを確かめる
        /// </summary>
        /// <remarks>
        /// <b>検査したストリームをそのまま読み込みに使う</b>ための版。パスで検査してからパスで開き直すと、
        /// その間にファイルを差し替えられて検査を素通りできる。呼び出し元は削除・書き込みを共有しない
        /// （<see cref="FileShare.Read"/>）で開いたストリームを渡し、同じストリームから読み込む。
        /// 戻る時点でストリームの位置は先頭に戻す。
        /// </remarks>
        /// <param name="stream">シーク可能な読み取りストリーム</param>
        public static XlsxPackageSizeResult Check(Stream stream)
        {
            return Check(stream, MaxFileBytes, MaxExpandedBytes);
        }

        /// <summary>
        /// 上限を指定して、開いているストリームの大きさと展開後の大きさを確かめる
        /// </summary>
        /// <remarks>
        /// ファイルの大きさはストリームの長さで判定し、<see cref="ZipArchive"/> を開く（中央ディレクトリを読む）前に断る。
        /// 展開後サイズは全エントリの宣言値を最後まで合計する（ログに実寸を残すため。エントリ数はファイルの大きさで抑えられている）。
        /// 例外は投げない。ZIP として読めないときは <see cref="XlsxPackageSizeCheck.Undetermined"/> を返す。
        /// </remarks>
        internal static XlsxPackageSizeResult Check(Stream stream, long maxFileBytes, long maxExpandedBytes)
        {
            long? fileBytes = null;
            try
            {
                fileBytes = stream.Length;
                if (fileBytes.Value > maxFileBytes)
                {
                    return new XlsxPackageSizeResult(XlsxPackageSizeCheck.FileTooLarge, fileBytes, null);
                }

                stream.Position = 0;
                var expandedBytes = SumDeclaredExpandedBytes(stream);
                var status = expandedBytes > maxExpandedBytes
                    ? XlsxPackageSizeCheck.ExpandedTooLarge
                    : XlsxPackageSizeCheck.WithinLimit;
                return new XlsxPackageSizeResult(status, fileBytes, expandedBytes);
            }
            catch (Exception)
            {
                // 壊れている（ZIP として読めない）は、開く側の従来の処理（破損の案内）に任せる
                return XlsxPackageSizeResult.Undetermined(fileBytes);
            }
            finally
            {
                TryRewind(stream);
            }
        }

        /// <summary>
        /// ZIP の全エントリの宣言された展開後サイズを合計する（桁あふれ・負の宣言は <see cref="long.MaxValue"/> に飽和させる）
        /// </summary>
        private static long SumDeclaredExpandedBytes(Stream stream)
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
            {
                long total = 0;
                foreach (var entry in archive.Entries)
                {
                    // 足してから比べると、ZIP64 の巨大な宣言値で桁あふれし得るため、残りの枠と比べる
                    if (entry.Length < 0 || entry.Length > long.MaxValue - total)
                    {
                        return long.MaxValue;
                    }

                    total += entry.Length;
                }

                return total;
            }
        }

        private static void TryRewind(Stream stream)
        {
            try
            {
                if (stream.CanSeek)
                {
                    stream.Position = 0;
                }
            }
            catch (Exception)
            {
                // 巻き戻せないストリームは読み込む側の例外に任せる
            }
        }
    }

    /// <summary>
    /// xlsx の大きさの検査結果と、検査で得た実寸（Issue #2272。ログに残すため）
    /// </summary>
    public sealed class XlsxPackageSizeResult
    {
        internal XlsxPackageSizeResult(XlsxPackageSizeCheck status, long? fileBytes, long? declaredExpandedBytes)
        {
            Status = status;
            FileBytes = fileBytes;
            DeclaredExpandedBytes = declaredExpandedBytes;
        }

        /// <summary>検査結果</summary>
        public XlsxPackageSizeCheck Status { get; }

        /// <summary>ファイルの大きさ（バイト。調べられなかったときは null）</summary>
        public long? FileBytes { get; }

        /// <summary>全パートの展開後サイズの宣言値の合計（バイト。ファイルが大きすぎて ZIP を開かなかった・読めなかったときは null）</summary>
        public long? DeclaredExpandedBytes { get; }

        /// <summary>上限を超えた（開いてはならない）</summary>
        public bool IsTooLarge =>
            Status == XlsxPackageSizeCheck.FileTooLarge || Status == XlsxPackageSizeCheck.ExpandedTooLarge;

        internal static XlsxPackageSizeResult Undetermined(long? fileBytes) =>
            new XlsxPackageSizeResult(XlsxPackageSizeCheck.Undetermined, fileBytes, null);
    }
}
