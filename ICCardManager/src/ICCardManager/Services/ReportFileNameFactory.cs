using System;
using System.IO;
using ICCardManager.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ICCardManager.Services
{
    /// <summary>
    /// 組織設定 <see cref="ReportLayoutOptions.FileNameFormat"/> に従って帳票ファイル名を生成する（Issue #1820）
    /// </summary>
    public class ReportFileNameFactory : IReportFileNameFactory
    {
        private readonly OrganizationOptions _orgOptions;
        private readonly ILogger<ReportFileNameFactory> _logger;

        /// <summary>
        /// 既定書式へフォールバックしたことを記録済みの書式（同一書式で毎回ログを出さないため）
        /// </summary>
        /// <remarks>
        /// 帳票の一括出力ではカード枚数ぶん呼ばれるため、無条件に出すとログが肥大化する。
        /// 「初回だけ残す」判定は Issue #1819（FelicaCardReader のヘルスチェック）と同じ考え方。
        /// </remarks>
        private string _loggedFallbackFormat;
        private bool _hasLoggedFallback;

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

        public ReportFileNameFactory(
            IOptions<OrganizationOptions> orgOptions = null,
            ILogger<ReportFileNameFactory> logger = null)
        {
            _orgOptions = orgOptions?.Value ?? new OrganizationOptions();
            _logger = logger;
        }

        /// <inheritdoc/>
        public string GetFiscalYearFileName(string cardType, string cardNumber, int fiscalYear)
        {
            var configuredFormat = _orgOptions.ReportLayout?.FileNameFormat;
            var fileName = Build(configuredFormat, cardType, cardNumber, fiscalYear, out var usedFallback);

            if (usedFallback)
            {
                LogFallbackOnce(configuredFormat, fileName);
            }

            return fileName;
        }

        /// <summary>
        /// 既定書式へ倒したことを Information で 1 回だけ記録する
        /// </summary>
        /// <remarks>
        /// Issue #1819: 縮退（フォールバック）は正常終了するため、記録しないと管理者から見て
        /// 「設定したのに反映されない」＝本 Issue が是正した状態と区別が付かない。
        /// LogDebug は本番のログファイルに出力されないため Information にする。
        /// </remarks>
        private void LogFallbackOnce(string configuredFormat, string fileName)
        {
            if (_hasLoggedFallback && _loggedFallbackFormat == configuredFormat)
            {
                return;
            }

            _hasLoggedFallback = true;
            _loggedFallbackFormat = configuredFormat;

            _logger?.LogInformation(
                "帳票ファイル名の書式 ReportLayout.FileNameFormat=\"{ConfiguredFormat}\" は使用できないため、" +
                "既定の書式 \"{DefaultFormat}\" で出力します（生成例: {FileName}）。" +
                "プレースホルダは {{0}} {{1}} {{2}} のみ、フォルダー区切りとファイル名に使えない文字は指定できません。",
                configuredFormat,
                DefaultFileNameFormat,
                fileName);
        }

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
        /// <param name="usedFallback">既定書式へ倒したかどうか（呼び出し元がログに残すため）</param>
        internal static string Build(
            string fileNameFormat, string cardType, string cardNumber, int fiscalYear, out bool usedFallback)
        {
            var safeCardType = FileNameSanitizer.SanitizeComponent(cardType);
            var safeCardNumber = FileNameSanitizer.SanitizeComponent(cardNumber);

            var format = string.IsNullOrWhiteSpace(fileNameFormat)
                ? DefaultFileNameFormat
                : fileNameFormat;

            var fileName = TryFormat(format, safeCardType, safeCardNumber, fiscalYear);

            // 書式由来のパス構造（"..\\evil\\{0}.xlsx" 等）は #1703 の保証を破るため既定書式へ倒す。
            // ファイル名として使えない文字を含む書式も、SaveAs の時点で例外になるためここで倒す。
            usedFallback = fileName == null || !IsSingleFileName(fileName);
            if (usedFallback)
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

            // ファイル名として使えない文字（'*' '?' 等）は Path.GetInvalidPathChars に含まれないため
            // Path.GetFileName を通り抜ける。ここで弾かないと SaveAs / Path.Combine の時点で
            // 例外になり、「管理者の設定ミスで帳票作成を止めない」という本 Issue の目的が果たせない。
            if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
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
