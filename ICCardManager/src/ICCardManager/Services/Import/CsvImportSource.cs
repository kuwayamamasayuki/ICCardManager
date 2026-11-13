#nullable enable
using System;

namespace ICCardManager.Services
{
    /// <summary>
    /// 取り込む CSV の読み取り元（ファイルのパス、または読み取り済みの内容。Issue #2284）
    /// </summary>
    /// <remarks>
    /// <para>
    /// CSV インポートは、プレビューしたファイルの大きさ・SHA-256 を取り込む直前に照合する（Issue #2262）。
    /// 照合した後で <see cref="CsvImportService"/> がパスからファイルを読み直すと、照合から読み込みまでの間に
    /// 書き換えられた内容（照合していない内容）が取り込まれる（TOCTOU）。照合に使ったバイト列を
    /// <see cref="FromContent"/> で渡せば、サービスはファイルを開かずにその内容を取り込む。
    /// </para>
    /// <para>
    /// 内容を渡しても、読み込める大きさの上限（Issue #2272）と文字コードの判別（Issue #1744）は、
    /// ファイルから読む場合と同じ処理（<c>CsvImportService.ReadCsvAsync</c>）を通る。
    /// <see cref="FilePath"/> は、内容を渡したときはログ・例外の文言に載せる名前としてだけ使う。
    /// </para>
    /// </remarks>
    public sealed class CsvImportSource
    {
        private CsvImportSource(string filePath, byte[]? content, long? capturedLength)
        {
            FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            Content = content;
            CapturedLength = capturedLength;
        }

        /// <summary>
        /// CSV ファイルのパス。<see cref="Content"/> を持つときは、ここからは読み直さない
        /// </summary>
        public string FilePath { get; }

        /// <summary>
        /// 読み取り済みの内容（<see cref="FromFile"/> で作ったときは <c>null</c>＝取り込むときにパスから読む）
        /// </summary>
        /// <remarks>受け取った配列を複製せずに持つ。渡した側は、取り込みが終わるまで書き換えないこと</remarks>
        internal byte[]? Content { get; }

        /// <summary>
        /// 読み取ったときのファイルの大きさ（<see cref="FromFile"/> で作ったときは <c>null</c>）
        /// </summary>
        /// <remarks>
        /// <see cref="FromOversizedFile"/> で作ったときは、<see cref="Content"/> が <c>null</c>（上限を超えていたため読んでいない）で、
        /// この値だけを持つ。取り込みはこの大きさで上限超過として断る（パスから読み直さない）。
        /// </remarks>
        internal long? CapturedLength { get; }

        /// <summary>
        /// 取り込むときにパスからファイルを読む（プレビューを経ない直接インポート、単体テスト）
        /// </summary>
        public static CsvImportSource FromFile(string filePath) => new CsvImportSource(filePath, null, null);

        /// <summary>
        /// 読み取り済みの内容を取り込む（ファイルは開かない）
        /// </summary>
        /// <param name="filePath">読み取ったファイルのパス（ログ・例外の文言に載せる）</param>
        /// <param name="content">ファイルから読み取ったバイト列（照合に使ったもの）</param>
        public static CsvImportSource FromContent(string filePath, byte[] content)
            => new CsvImportSource(
                filePath,
                content ?? throw new ArgumentNullException(nameof(content)),
                content.LongLength);

        /// <summary>
        /// 読み込める大きさの上限を超えていたため、内容を読まなかったファイル（Issue #2284）
        /// </summary>
        /// <remarks>
        /// 照合の時点で上限を超えていたファイルを <see cref="FromFile"/> で渡すと、取り込むときにパスから読み直し、
        /// その間に縮められた（照合していない）内容を取り込み得る。大きさだけを渡し、取り込みはファイルを開かずに
        /// 上限超過（<c>FileOperationException.FileTooLarge</c>）で断る（ファイルから読んだときと同じ案内）。
        /// </remarks>
        /// <param name="filePath">読み取ろうとしたファイルのパス（ログ・例外の文言に載せる）</param>
        /// <param name="length">読み取ろうとしたときのファイルの大きさ（バイト）</param>
        internal static CsvImportSource FromOversizedFile(string filePath, long length)
            => new CsvImportSource(filePath, null, length);
    }
}
