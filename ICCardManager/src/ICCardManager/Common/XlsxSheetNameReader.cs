#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace ICCardManager.Common
{
    /// <summary>
    /// xlsx ファイルのワークシート名だけを読み取るユーティリティ（Issue #1691）
    /// </summary>
    /// <remarks>
    /// 帳票の「出力済み / 未出力」判定は、カード枚数（最大20枚程度）ぶんの年度ファイルを
    /// 画面を開くたびに調べる。ClosedXML の <c>XLWorkbook</c> でブックを開くとセル値・書式まで
    /// 解析されるため、シート構成を知りたいだけの用途には重すぎる。
    ///
    /// xlsx は OPC（ZIP）パッケージなので、<c>xl/workbook.xml</c> の
    /// <c>&lt;sheet name="..."/&gt;</c> だけを読めばシート構成が分かる。
    /// また <see cref="FileShare.ReadWrite"/> で開くため、対象のブックを Excel で開いたままでも
    /// 判定できる（月次作業中に前月分を開きっぱなしにする運用を想定）。
    /// </remarks>
    public static class XlsxSheetNameReader
    {
        /// <summary>
        /// xlsx パッケージ内でブック構造を保持するパート
        /// </summary>
        private const string WorkbookPartPath = "xl/workbook.xml";

        /// <summary>
        /// 読み込む <c>xl/workbook.xml</c> の大きさの上限（展開後 4MB。Issue #2272）
        /// </summary>
        /// <remarks>
        /// workbook.xml はシート名・定義名・ブックの設定だけを持つパートで、本システムの帳票
        /// （最大 12 か月ぶん＋累計のシート）では数KB、名前定義を大量に持つ一般のブックでも数百KB にとどまる。
        /// 出力先フォルダーに書き込める者が同じ名前の ZIP 爆弾を置いた場合に、展開してメモリを
        /// 圧迫させないための上限であり、正当なファイルには 10 倍以上の余裕を取っている。
        /// </remarks>
        internal const long MaxWorkbookPartBytes = 4L * 1024 * 1024;

        /// <summary>
        /// 開く xlsx ファイル自体の大きさの上限（32MB。Issue #2272）
        /// </summary>
        /// <remarks>
        /// <see cref="ZipArchive"/> は開いた時点で中央ディレクトリを全件読み込むため、エントリ数を
        /// 膨らませた巨大な ZIP では workbook.xml にたどり着く前にメモリを使う。ファイルの大きさで先に断る。
        /// 本システムの年度ファイルはテンプレートが約 15KB、1 か月のシートが数十KB で、
        /// 12 か月ぶんを追記しても 1MB 前後にとどまるため、正当なファイルには 30 倍前後の余裕がある。
        /// </remarks>
        internal const long MaxXlsxFileBytes = 32L * 1024 * 1024;

        /// <summary>
        /// SpreadsheetML の名前空間
        /// </summary>
        private static readonly XNamespace SpreadsheetMlNamespace =
            "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        /// <summary>
        /// workbook.xml を読むときの XML リーダー設定（DTD 禁止・外部リソース解決なし。Issue #2272）
        /// </summary>
        private static readonly XmlReaderSettings SafeXmlReaderSettings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            CloseInput = false,
        };

        /// <summary>
        /// xlsx ファイルからワークシート名の一覧を読み取る
        /// </summary>
        /// <param name="filePath">xlsx ファイルのパス</param>
        /// <param name="sheetNames">読み取れたシート名（失敗時は空）</param>
        /// <returns>読み取れた場合true。ファイルが無い/壊れている/読めない場合はfalse</returns>
        public static bool TryReadSheetNames(string filePath, out IReadOnlyList<string> sheetNames)
        {
            return TryReadSheetNames(filePath, MaxWorkbookPartBytes, out sheetNames);
        }

        /// <summary>
        /// xlsx ファイルからワークシート名の一覧を読み取る（workbook.xml の上限を指定する版。Issue #2272）
        /// </summary>
        /// <remarks>
        /// <para>
        /// 上限の境界を、実際に数MBの xlsx を作らずに単体テストで固定するためのオーバーロード。
        /// </para>
        /// <para>
        /// <b>上限は 2 段で守る</b>（その手前で、ファイル自体の大きさも <see cref="MaxXlsxFileBytes"/> で確かめる）。
        /// ZIP の中央ディレクトリが宣言する展開後サイズ
        /// （<see cref="ZipArchiveEntry.Length"/>）で読む前に断り、宣言を偽った ZIP に備えて
        /// 読み込み側でも上限バイトを超えた時点で打ち切る。いずれも「判定不能」（false）として扱う。
        /// </para>
        /// <para>
        /// <b>DTD は禁止する</b>。正当な workbook.xml は DTD を持たない。内部サブセットの実体を許すと、
        /// 小さなファイルから巨大な文字列を展開できる（billion laughs）ため、バイト数の上限を素通りする。
        /// 外部実体も解決しない（<see cref="XmlReaderSettings.XmlResolver"/> = null）。
        /// </para>
        /// </remarks>
        internal static bool TryReadSheetNames(string filePath, long maxWorkbookPartBytes, out IReadOnlyList<string> sheetNames)
        {
            return TryReadSheetNames(filePath, MaxXlsxFileBytes, maxWorkbookPartBytes, out sheetNames);
        }

        /// <summary>
        /// xlsx ファイルからワークシート名の一覧を読み取る（ファイル自体と workbook.xml の上限を指定する版。Issue #2272）
        /// </summary>
        /// <remarks>
        /// ファイルの大きさは <see cref="ZipArchive"/> を開く前（中央ディレクトリを読む前）に確かめる。
        /// 開いたストリームの長さで判定するため、確かめてから開くまでの間に差し替えられても上限を素通りしない。
        /// </remarks>
        internal static bool TryReadSheetNames(
            string filePath, long maxFileBytes, long maxWorkbookPartBytes, out IReadOnlyList<string> sheetNames)
        {
            sheetNames = new List<string>();

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return false;
            }

            try
            {
                using (var stream = new FileStream(
                    filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (stream.Length > maxFileBytes)
                    {
                        // 中央ディレクトリを読む前に断る（ZIP 爆弾対策の 0 段目）
                        return false;
                    }

                    return TryReadSheetNamesFromPackage(stream, maxWorkbookPartBytes, out sheetNames);
                }
            }
            catch (Exception)
            {
                // 破損ファイル・排他ロック・権限不足などはすべて「判定不能」として扱う。
                // 呼び出し側（ReportExportStatusService）が Unknown 状態としてユーザーに提示する。
                sheetNames = new List<string>();
                return false;
            }
        }

        /// <summary>
        /// 開いた xlsx パッケージから workbook.xml のシート名を読む（例外は呼び出し元が判定不能へ畳む）
        /// </summary>
        private static bool TryReadSheetNamesFromPackage(
            Stream stream, long maxWorkbookPartBytes, out IReadOnlyList<string> sheetNames)
        {
            sheetNames = new List<string>();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
            {
                var workbookEntry = archive.GetEntry(WorkbookPartPath);
                if (workbookEntry == null)
                {
                    // xlsx ではない ZIP（xls を拡張子だけ変えた等）
                    return false;
                }

                if (workbookEntry.Length > maxWorkbookPartBytes)
                {
                    // 展開後サイズの宣言が上限を超える。展開せずに判定不能とする（ZIP 爆弾対策）
                    return false;
                }

                using (var entryStream = new LengthLimitedReadStream(workbookEntry.Open(), maxWorkbookPartBytes))
                using (var xmlReader = XmlReader.Create(entryStream, SafeXmlReaderSettings))
                {
                    var document = XDocument.Load(xmlReader);
                    sheetNames = document
                        .Descendants(SpreadsheetMlNamespace + "sheet")
                        .Select(element => (string)element.Attribute("name"))
                        .Where(name => !string.IsNullOrEmpty(name))
                        .ToList();
                    return true;
                }
            }
        }

        /// <summary>
        /// xlsx ファイルに指定名のワークシートが含まれるかを判定する
        /// </summary>
        /// <param name="filePath">xlsx ファイルのパス</param>
        /// <param name="sheetName">探すシート名</param>
        /// <param name="contains">シートが含まれる場合true</param>
        /// <returns>判定できた場合true。ファイルを読めず判定不能な場合はfalse</returns>
        public static bool TryContainsSheet(string filePath, string sheetName, out bool contains)
        {
            contains = false;

            if (!TryReadSheetNames(filePath, out var sheetNames))
            {
                return false;
            }

            contains = sheetNames.Any(name => string.Equals(name, sheetName, StringComparison.Ordinal));
            return true;
        }

        /// <summary>
        /// 上限バイト数を超えて読もうとした時点で例外を投げる読み取り専用ストリーム（Issue #2272）
        /// </summary>
        /// <remarks>
        /// ZIP の展開後サイズは中央ディレクトリの宣言にすぎず、偽ることができる。
        /// 宣言を信じて読み始めた後も、実際に読んだバイト数で打ち切る。
        /// 上限ちょうどのデータは読み切れる（上限を超える 1 バイト目を読んだときに初めて失敗する）。
        /// </remarks>
        private sealed class LengthLimitedReadStream : Stream
        {
            private readonly Stream _inner;
            private readonly long _maxBytes;
            private long _totalRead;

            public LengthLimitedReadStream(Stream inner, long maxBytes)
            {
                _inner = inner;
                _maxBytes = maxBytes;
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => _totalRead;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                // 上限より 1 バイト多く読めるかを試し、読めたら超過と判定する
                var allowed = (int)Math.Min(count, _maxBytes - _totalRead + 1);
                var read = _inner.Read(buffer, offset, allowed);
                _totalRead += read;
                if (_totalRead > _maxBytes)
                {
                    throw new InvalidDataException(
                        $"The part exceeds the size limit of {_maxBytes} bytes.");
                }

                return read;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
