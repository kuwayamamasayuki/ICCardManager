#nullable enable
using System.Threading.Tasks;

namespace ICCardManager.Services
{
    /// <summary>
    /// パスを受け取る版の取り込み・プレビュー（取り込むときにパスからファイルを読む。Issue #2284）
    /// </summary>
    /// <remarks>
    /// <para>
    /// データ入出力画面のプレビュー経由の取り込みは、プレビューしたファイルと照合したバイト列を
    /// <see cref="CsvImportSource.FromContent"/> で渡す版を呼ぶ（照合の後にパスから読み直すと、
    /// 照合から読み込みまでの間に書き換えられた内容が取り込まれる）。ここにある版は、照合を伴わない呼び出し
    /// （単体テスト等）のための入口で、<see cref="CsvImportSource.FromFile"/> へ委譲するだけにする
    /// （取り込みの処理を読み取り元ごとに書き分けない）。
    /// </para>
    /// <para>
    /// 直接インポート（<see cref="ImportNewCardsAsync"/>）はプレビュー・照合を経ないため、パスを受け取る版だけを持つ。
    /// </para>
    /// </remarks>
    public partial class CsvImportService
    {
        /// <inheritdoc cref="ImportCardsAsync(CsvImportSource, bool)"/>
        public virtual Task<CsvImportResult> ImportCardsAsync(string filePath, bool skipExisting = true)
            => ImportCardsAsync(CsvImportSource.FromFile(filePath), skipExisting);

        /// <inheritdoc cref="PreviewCardsAsync(CsvImportSource, bool)"/>
        public virtual Task<CsvImportPreviewResult> PreviewCardsAsync(string filePath, bool skipExisting = true)
            => PreviewCardsAsync(CsvImportSource.FromFile(filePath), skipExisting);

        /// <inheritdoc cref="ImportStaffAsync(CsvImportSource, bool)"/>
        public virtual Task<CsvImportResult> ImportStaffAsync(string filePath, bool skipExisting = true)
            => ImportStaffAsync(CsvImportSource.FromFile(filePath), skipExisting);

        /// <inheritdoc cref="PreviewStaffAsync(CsvImportSource, bool)"/>
        public virtual Task<CsvImportPreviewResult> PreviewStaffAsync(string filePath, bool skipExisting = true)
            => PreviewStaffAsync(CsvImportSource.FromFile(filePath), skipExisting);

        /// <inheritdoc cref="ImportLedgersAsync(CsvImportSource, bool, string?)"/>
        public virtual Task<CsvImportResult> ImportLedgersAsync(string filePath, bool skipExisting = true, string? targetCardIdm = null)
            => ImportLedgersAsync(CsvImportSource.FromFile(filePath), skipExisting, targetCardIdm);

        /// <inheritdoc cref="PreviewLedgersAsync(CsvImportSource, bool, string?)"/>
        public virtual Task<CsvImportPreviewResult> PreviewLedgersAsync(string filePath, bool skipExisting = true, string? targetCardIdm = null)
            => PreviewLedgersAsync(CsvImportSource.FromFile(filePath), skipExisting, targetCardIdm);

        /// <inheritdoc cref="ImportLedgerDetailsAsync(CsvImportSource)"/>
        public virtual Task<CsvImportResult> ImportLedgerDetailsAsync(string filePath)
            => ImportLedgerDetailsAsync(CsvImportSource.FromFile(filePath));

        /// <inheritdoc cref="PreviewLedgerDetailsAsync(CsvImportSource)"/>
        public virtual Task<CsvImportPreviewResult> PreviewLedgerDetailsAsync(string filePath)
            => PreviewLedgerDetailsAsync(CsvImportSource.FromFile(filePath));
    }
}
