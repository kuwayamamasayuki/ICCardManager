using System;
using System.IO;
using ICCardManager.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace ICCardManager.Services
{
    /// <summary>
    /// 組織設定 <see cref="ReportLayoutOptions.FileNameFormat"/> に従って帳票ファイル名を生成する（Issue #1820）
    /// </summary>
    public class ReportFileNameFactory : IReportFileNameFactory
    {
        private readonly OrganizationOptions _orgOptions;

        /// <summary>
        /// 既定のファイル名フォーマット（設定が空・不正なときのフォールバック先）
        /// </summary>
        /// <remarks>
        /// Issue #1818 と同じ方針: 空設定（"" / 空白 / null）は既定値へフォールバックする。
        /// 空書式のまま <see cref="string.Format(string, object[])"/> を通すとファイル名が
        /// 空文字になり、<see cref="Path.Combine(string, string)"/> の結果が
        /// 「出力フォルダそのもの」になって帳票の保存が壊れる。
        /// </remarks>
        internal static readonly string DefaultFileNameFormat =
            new ReportLayoutOptions().FileNameFormat;

        public ReportFileNameFactory(IOptions<OrganizationOptions> orgOptions = null)
        {
            _orgOptions = orgOptions?.Value ?? new OrganizationOptions();
        }

        /// <inheritdoc/>
        public string GetFiscalYearFileName(string cardType, string cardNumber, int fiscalYear)
            => Build(_orgOptions.ReportLayout?.FileNameFormat, cardType, cardNumber, fiscalYear);

        /// <summary>
        /// ファイル名を組み立てる純関数（Issue #1820）
        /// </summary>
        /// <remarks>
        /// <para>
        /// Issue #1703: CardType / CardNumber は CSV 取込・共有DB 経由でパス区切りを含みうる。
        /// ファイル名構成要素としてサニタイズし、Path.Combine + SaveAs 解決時の
        /// 出力フォルダ外へのパストラバーサルを防ぐ（名前生成の単一チョークポイント）。
        /// </para>
        /// <para>
        /// Issue #1820: 書式を設定値から受け取るようになったため、<b>書式そのもの</b>が
        /// パス構造や不正なプレースホルダを持ちうる。構成要素のサニタイズだけでは
        /// #1703 の保証（生成名が単一のファイル名である）が書式側から破られるため、
        /// 組み立て結果も検査して、破れていれば既定書式へフォールバックする。
        /// 管理者の設定ミスで帳票作成が例外終了することも防ぐ。
        /// </para>
        /// </remarks>
        /// <param name="fileNameFormat">書式（null / 空白 / 不正な場合は既定書式を使う）</param>
        /// <param name="cardType">カード種別</param>
        /// <param name="cardNumber">カード番号（管理番号）</param>
        /// <param name="fiscalYear">年度</param>
        internal static string Build(string fileNameFormat, string cardType, string cardNumber, int fiscalYear)
        {
            var safeCardType = FileNameSanitizer.SanitizeComponent(cardType);
            var safeCardNumber = FileNameSanitizer.SanitizeComponent(cardNumber);

            var format = string.IsNullOrWhiteSpace(fileNameFormat)
                ? DefaultFileNameFormat
                : fileNameFormat;

            var fileName = TryFormat(format, safeCardType, safeCardNumber, fiscalYear);

            // 書式由来のパス構造（"..\\evil\\{0}.xlsx" 等）は #1703 の保証を破るため既定書式へ倒す。
            // 「判定できない」を異常に丸めないため、判定は Path.GetFileName との一致のみで行う。
            if (fileName == null || !IsSingleFileName(fileName))
            {
                fileName = TryFormat(DefaultFileNameFormat, safeCardType, safeCardNumber, fiscalYear);
            }

            return fileName;
        }

        /// <summary>
        /// <see cref="string.Format(string, object[])"/> を試み、書式が不正なら null を返す
        /// </summary>
        private static string TryFormat(string format, string cardType, string cardNumber, int fiscalYear)
        {
            try
            {
                return string.Format(format, cardType, cardNumber, fiscalYear);
            }
            catch (FormatException)
            {
                // プレースホルダの誤り（"{3}" / 閉じ括弧の欠落等）。既定書式へ倒す。
                return null;
            }
        }

        /// <summary>
        /// 生成名がパス構造を持たない単一のファイル名かを判定する
        /// </summary>
        private static bool IsSingleFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            try
            {
                return Path.GetFileName(fileName) == fileName;
            }
            catch (ArgumentException)
            {
                // 不正文字を含む場合（Path.GetFileName は .NET Framework 4.8 で例外を投げうる）
                return false;
            }
        }
    }
}
