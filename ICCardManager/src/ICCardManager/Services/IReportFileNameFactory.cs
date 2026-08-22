namespace ICCardManager.Services
{
    /// <summary>
    /// 帳票（年度ファイル）のファイル名を生成する（Issue #1820）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 組織設定 <see cref="ReportLayoutOptions.FileNameFormat"/> を唯一の書式の出所とする。
    /// 以前は <c>ReportService.GetFiscalYearFileName</c> が <c>static</c> だったため注入済みの
    /// <see cref="OrganizationOptions"/> を参照できず、<c>new OrganizationOptions()</c> の
    /// 既定値をハードコードで使っていた（同じ帳票の <c>TitleText</c> / <c>ClassificationText</c> 等は
    /// 設定が効くため「一部だけ効かない」という最も紛らわしい状態になっていた）。
    /// </para>
    /// <para>
    /// 生成を 1 か所に集約することで、消費側（<see cref="ReportService"/> /
    /// <see cref="ReportExportStatusService"/> / <c>ReportViewModel</c>）が書式を再実装して
    /// 静かに乖離することを防ぐ。
    /// </para>
    /// </remarks>
    public interface IReportFileNameFactory
    {
        /// <summary>
        /// 年度ファイル名を生成する
        /// </summary>
        /// <param name="cardType">カード種別</param>
        /// <param name="cardNumber">カード番号（管理番号）</param>
        /// <param name="fiscalYear">年度</param>
        /// <returns>ファイル名（既定書式の例: 物品出納簿_はやかけん_H001_2024年度.xlsx）</returns>
        string GetFiscalYearFileName(string cardType, string cardNumber, int fiscalYear);
    }
}
