#nullable enable
using System;
using System.Globalization;
using System.Text;
using ICCardManager.Dtos;
using ICCardManager.Models;

namespace ICCardManager.Common
{
    /// <summary>
    /// 繰越情報の復旧画面の入力欄（Issue #2255）
    /// </summary>
    public enum CarryoverInputField
    {
        /// <summary>開始ページ番号</summary>
        StartingPageNumber,

        /// <summary>繰越累計受入</summary>
        CarryoverIncomeTotal,

        /// <summary>繰越累計払出</summary>
        CarryoverExpenseTotal,

        /// <summary>対象年度</summary>
        CarryoverFiscalYear
    }

    /// <summary>
    /// 繰越情報の入力を解釈した結果（Issue #2255）
    /// </summary>
    public sealed class CarryoverInputParseResult
    {
        private CarryoverInputParseResult(CarryoverInfo? value, string? errorMessage, CarryoverInputField? errorField)
        {
            Value = value;
            ErrorMessage = errorMessage;
            ErrorField = errorField;
        }

        /// <summary>解釈できた繰越情報。入力に誤りがあれば null</summary>
        public CarryoverInfo? Value { get; }

        /// <summary>入力の誤りを「何が／なぜ／どうすれば」で述べた文言。誤りが無ければ null</summary>
        public string? ErrorMessage { get; }

        /// <summary>誤りのある入力欄（フォーカスを移す先）。誤りが無ければ null</summary>
        public CarryoverInputField? ErrorField { get; }

        /// <summary>入力に誤りが無いか</summary>
        public bool IsValid => Value is not null;

        internal static CarryoverInputParseResult Success(CarryoverInfo value) => new(value, null, null);

        internal static CarryoverInputParseResult Failure(CarryoverInputField field, string message) => new(null, message, field);
    }

    /// <summary>
    /// 繰越情報の復旧画面の入力を解釈・検証する（Issue #2255）
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>失われた項目に既定値を入れることを拒む</b>のが要点。開始ページ番号 1・繰越累計 0 円・対象年度の空欄は
    /// 登録時の既定値で、消失の検知（<c>CarryoverDataLossDetector</c>）は「非既定値 → 既定値へ落ち、
    /// 現在も既定値のまま」を消失とみなす。失われた項目へ既定値を書き戻しても値は戻らず、
    /// メイン画面の警告も消えない。保存してから「直したのに警告が消えない」に気付かせるのではなく、
    /// 保存の前に理由を添えて止める。
    /// </para>
    /// <para>
    /// 失われていない項目は既定値でもよい（もともと既定値だった項目を、復旧のついでに書き換えさせない）。
    /// </para>
    /// <para>
    /// 判断を画面から切り離した純関数にしているのは、<c>Window</c> を実体化せずに境界を網羅して検査するため。
    /// </para>
    /// </remarks>
    public static class CarryoverInfoInput
    {
        /// <summary>対象年度として受け付ける最も古い年度（西暦）</summary>
        public const int MinFiscalYear = 2000;

        /// <summary>
        /// 入力欄の文字列を繰越情報へ解釈する
        /// </summary>
        /// <param name="startingPageNumberText">開始ページ番号の入力</param>
        /// <param name="incomeTotalText">繰越累計受入の入力（円）</param>
        /// <param name="expenseTotalText">繰越累計払出の入力（円）</param>
        /// <param name="fiscalYearText">対象年度の入力（西暦。空欄は「年度なし」）</param>
        /// <param name="lost">どの項目が失われたか（<c>Lost*</c> が null でない項目が失われた項目）</param>
        /// <param name="currentFiscalYear">今年度（西暦）。これより先の年度は受け付けない</param>
        public static CarryoverInputParseResult Parse(
            string? startingPageNumberText,
            string? incomeTotalText,
            string? expenseTotalText,
            string? fiscalYearText,
            CarryoverDataLossItem lost,
            int currentFiscalYear)
        {
            if (lost == null)
            {
                throw new ArgumentNullException(nameof(lost));
            }

            // 開始ページ番号
            var pageText = Normalize(startingPageNumberText);
            if (pageText.Length == 0)
            {
                return CarryoverInputParseResult.Failure(
                    CarryoverInputField.StartingPageNumber,
                    "開始ページ番号が空欄です。物品出納簿の開始ページ番号を1以上の整数で入力してください。");
            }

            if (!TryParseInteger(pageText, out var startingPageNumber))
            {
                return CarryoverInputParseResult.Failure(
                    CarryoverInputField.StartingPageNumber,
                    $"開始ページ番号「{pageText}」は数値として読めません。1以上の整数を入力してください。");
            }

            if (startingPageNumber < 1)
            {
                return CarryoverInputParseResult.Failure(
                    CarryoverInputField.StartingPageNumber,
                    $"開始ページ番号が{startingPageNumber}です。ページ番号は1から始まるため、1以上の整数を入力してください。");
            }

            if (lost.LostStartingPageNumber.HasValue && startingPageNumber == 1)
            {
                return CarryoverInputParseResult.Failure(
                    CarryoverInputField.StartingPageNumber,
                    "開始ページ番号が1のままです。1は登録時の既定値のため、失われた値が戻らず警告も消えません。" +
                    "紙の出納簿の続きのページ番号（2以上）を入力してください。");
            }

            // 繰越累計受入・払出
            var incomeFailure = ParseAmount(
                incomeTotalText, "繰越累計受入", CarryoverInputField.CarryoverIncomeTotal,
                lost.LostCarryoverIncomeTotal.HasValue, out var incomeTotal);
            if (incomeFailure is not null)
            {
                return incomeFailure;
            }

            var expenseFailure = ParseAmount(
                expenseTotalText, "繰越累計払出", CarryoverInputField.CarryoverExpenseTotal,
                lost.LostCarryoverExpenseTotal.HasValue, out var expenseTotal);
            if (expenseFailure is not null)
            {
                return expenseFailure;
            }

            // 対象年度
            var yearText = Normalize(fiscalYearText);
            int? fiscalYear = null;
            if (yearText.Length == 0)
            {
                if (lost.LostCarryoverFiscalYear.HasValue)
                {
                    return CarryoverInputParseResult.Failure(
                        CarryoverInputField.CarryoverFiscalYear,
                        "対象年度が空欄です。空欄は登録時の既定値のため、失われた値が戻らず警告も消えません。" +
                        "繰越累計を加算する年度を西暦4桁（例: 2025）で入力してください。");
                }

                if (incomeTotal > 0 || expenseTotal > 0)
                {
                    return CarryoverInputParseResult.Failure(
                        CarryoverInputField.CarryoverFiscalYear,
                        "対象年度が空欄のため、繰越累計（受入・払出）が物品出納簿のどの年度の累計にも加算されません。" +
                        "繰越累計を加算する年度を西暦4桁（例: 2025）で入力してください。");
                }
            }
            else
            {
                if (!TryParseInteger(yearText, out var parsedYear))
                {
                    return CarryoverInputParseResult.Failure(
                        CarryoverInputField.CarryoverFiscalYear,
                        $"対象年度「{yearText}」は年として読めません。西暦4桁（例: 2025）で入力してください。");
                }

                if (parsedYear < MinFiscalYear || parsedYear > currentFiscalYear)
                {
                    return CarryoverInputParseResult.Failure(
                        CarryoverInputField.CarryoverFiscalYear,
                        $"対象年度が{parsedYear}年度です。繰越累計を加算できるのは{MinFiscalYear}年度から" +
                        $"今年度（{currentFiscalYear}年度）までのため、この範囲の年度を西暦4桁で入力してください。");
                }

                fiscalYear = parsedYear;
            }

            return CarryoverInputParseResult.Success(
                new CarryoverInfo(startingPageNumber, incomeTotal, expenseTotal, fiscalYear));
        }

        /// <summary>
        /// 繰越累計の金額を解釈する。誤りがあれば失敗の結果を、無ければ null を返す
        /// </summary>
        private static CarryoverInputParseResult? ParseAmount(
            string? text, string label, CarryoverInputField field, bool isLost, out int amount)
        {
            var normalized = Normalize(text);
            if (normalized.Length == 0)
            {
                amount = 0;
                return CarryoverInputParseResult.Failure(
                    field,
                    $"{label}が空欄です。累計が無い場合も0と入力し、ある場合は0以上の整数を円単位で入力してください。");
            }

            if (!TryParseInteger(normalized, out amount))
            {
                return CarryoverInputParseResult.Failure(
                    field,
                    $"{label}「{normalized}」は金額として読めません。0以上の整数を円単位で入力してください。");
            }

            if (amount < 0)
            {
                return CarryoverInputParseResult.Failure(
                    field,
                    $"{label}が{FormatAmount(amount)}円（マイナス）です。累計の金額はマイナスにならないため、" +
                    "0以上の整数を入力してください。");
            }

            if (isLost && amount == 0)
            {
                return CarryoverInputParseResult.Failure(
                    field,
                    $"{label}が0円です。0円は登録時の既定値のため、失われた値が戻らず警告も消えません。" +
                    "紙の出納簿の累計の金額（1円以上）を入力してください。");
            }

            return null;
        }

        /// <summary>
        /// 全角の数字・記号を半角へ寄せ、前後の空白を除く
        /// </summary>
        /// <remarks>
        /// 日本語入力のまま数字を打つと全角になる。拒んで打ち直させるより、同じ意味の文字として受け付ける。
        /// </remarks>
        private static string Normalize(string? text) =>
            (text ?? string.Empty).Normalize(NormalizationForm.FormKC).Trim();

        /// <summary>
        /// 整数として解釈する。桁区切りのカンマ（一覧の表示「45,000円」を写した入力）は受け付ける
        /// </summary>
        private static bool TryParseInteger(string text, out int value) =>
            int.TryParse(
                text,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out value);

        private static string FormatAmount(int amount) => amount.ToString("N0", CultureInfo.CurrentCulture);
    }
}
