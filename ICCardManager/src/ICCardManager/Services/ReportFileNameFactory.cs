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

        /// <summary>
        /// 年度ファイルとして保存できる拡張子（Issue #2041）
        /// </summary>
        /// <remarks>
        /// ClosedXML の <c>SaveAs(string)</c> / <c>new XLWorkbook(string)</c> は拡張子で形式を決め、
        /// 対応しない拡張子を <see cref="ArgumentException"/> で拒否する。書式が拡張子を持たなければ
        /// <b>全カードの帳票作成が失敗する</b>ため、生成名の拡張子をここで検査して既定書式へ倒す。
        /// </remarks>
        internal static readonly string[] AllowedExtensions = { ".xlsx", ".xlsm" };

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
                "プレースホルダは {{0}} {{1}} {{2}} のみで、3 つすべてを含める必要があります。" +
                "フォルダー区切りとファイル名に使えない文字は指定できず、拡張子は .xlsx か .xlsm にしてください。",
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
            // Issue #2041: カード・年度を区別しない書式と、Excel の拡張子で終わらない書式も倒す。
            usedFallback = fileName == null
                || !IsSingleFileName(fileName)
                || !HasAllowedExtension(fileName)
                || !DistinguishesInputs(format);
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
        /// 生成名が年度ファイルとして保存できる拡張子で終わるかを判定する（Issue #2041）
        /// </summary>
        private static bool HasAllowedExtension(string fileName)
        {
            foreach (var extension in AllowedExtensions)
            {
                if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 書式が「カード種別・管理番号・年度」のすべてを名前に反映するかを判定する（Issue #2041）
        /// </summary>
        /// <remarks>
        /// <para>
        /// 年度ファイルは 1 枚のカードの 1 年度ぶんの月シートを積み上げる。3 つのいずれかが
        /// 名前に現れない書式は<b>別のカード／別の年度が同じファイルへ書かれる</b>ことを意味し、
        /// 一括作成では各カードが同じ月シートをクリアして書き直すため、最後の 1 枚以外の台帳が
        /// 失われる。しかも生成自体は成功するので全件が「出力済み」と表示され、欠落に気付けない。
        /// </para>
        /// <para>
        /// 判定は「書式に <c>{0}</c> が含まれるか」という<b>綴り</b>ではなく、値を変えて 2 回整形し
        /// 結果が変わるかという<b>性質</b>で行う。綴りで見ると整列・書式指定子を伴う正当な書式
        /// （<c>{0,10}</c> / <c>{2:0000}</c>）を取りこぼし、正当な設定を既定書式へ倒してしまう。
        /// </para>
        /// <para>
        /// 管理番号だけでは足りない（部分ユニークインデックス <c>idx_card_type_number_active</c> が示すとおり、
        /// 管理番号はカード種別と組でしか一意でない）ため、3 つすべてを必須とする。
        /// </para>
        /// </remarks>
        private static bool DistinguishesInputs(string format)
        {
            const string ProbeCardType = "";
            const string ProbeCardTypeAlt = "";
            const string ProbeCardNumber = "";
            const string ProbeCardNumberAlt = "";
            const int ProbeFiscalYear = 2000;
            const int ProbeFiscalYearAlt = 2001;

            var baseline = TryFormat(format, ProbeCardType, ProbeCardNumber, ProbeFiscalYear);
            if (baseline == null)
            {
                return false;
            }

            return baseline != TryFormat(format, ProbeCardTypeAlt, ProbeCardNumber, ProbeFiscalYear)
                && baseline != TryFormat(format, ProbeCardType, ProbeCardNumberAlt, ProbeFiscalYear)
                && baseline != TryFormat(format, ProbeCardType, ProbeCardNumber, ProbeFiscalYearAlt);
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
