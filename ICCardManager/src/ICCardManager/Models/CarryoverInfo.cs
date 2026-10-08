#nullable enable
using System;

namespace ICCardManager.Models
{
    /// <summary>
    /// 紙の出納簿から移行したカードの繰越情報（開始ページ番号・繰越累計・対象年度）（Issue #2255）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 4 項目は登録時にだけ確定し、通常のカード編集（<c>ICardRepository.UpdateAsync</c>）では書き換えない
    /// （Issue #1726）。書き換えるのは繰越情報の復旧（<c>ICardRepository.UpdateCarryoverInfoAsync</c>）だけで、
    /// その経路は 4 項目を 1 組として扱うため、ここへまとめる。
    /// </para>
    /// <para>
    /// 値は不変にする。復旧では「画面を開いたときに読んだ値」と「書き込む値」を並べて扱うため、
    /// どちらかを途中で書き換えられる形にすると、競合の判定（読んだ値のままか）が成り立たなくなる。
    /// </para>
    /// </remarks>
    public sealed class CarryoverInfo : IEquatable<CarryoverInfo>
    {
        /// <summary>
        /// 開始ページ番号の既定値（登録時・消失時の値。<see cref="IcCard.StartingPageNumber"/> の初期値と一致させる）
        /// </summary>
        /// <remarks>
        /// 消失の検知（<c>CarryoverDataLossDetector</c>）・復旧の入力検証（<c>CarryoverInfoInput</c>）・
        /// 一覧が古いことの判定（<c>CarryoverRecoveryViewModel.IsAlreadyRecovered</c>）が同じ値で判断するため、ここへ寄せる。
        /// </remarks>
        public const int DefaultStartingPageNumber = 1;

        /// <summary>繰越累計（受入・払出）の既定値。対象年度の既定値は null</summary>
        public const int DefaultCarryoverTotal = 0;

        public CarryoverInfo(
            int startingPageNumber,
            int carryoverIncomeTotal,
            int carryoverExpenseTotal,
            int? carryoverFiscalYear)
        {
            StartingPageNumber = startingPageNumber;
            CarryoverIncomeTotal = carryoverIncomeTotal;
            CarryoverExpenseTotal = carryoverExpenseTotal;
            CarryoverFiscalYear = carryoverFiscalYear;
        }

        /// <summary>物品出納簿の開始ページ番号（Issue #510）</summary>
        public int StartingPageNumber { get; }

        /// <summary>紙の出納簿時代の累計受入金額（Issue #1215）</summary>
        public int CarryoverIncomeTotal { get; }

        /// <summary>紙の出納簿時代の累計払出金額（Issue #1215）</summary>
        public int CarryoverExpenseTotal { get; }

        /// <summary>繰越累計を加算する年度（西暦。Issue #1215）。null は加算しない</summary>
        public int? CarryoverFiscalYear { get; }

        /// <summary>
        /// カードが現在持っている繰越情報を取り出す
        /// </summary>
        public static CarryoverInfo From(IcCard card)
        {
            if (card == null)
            {
                throw new ArgumentNullException(nameof(card));
            }

            return new CarryoverInfo(
                card.StartingPageNumber,
                card.CarryoverIncomeTotal,
                card.CarryoverExpenseTotal,
                card.CarryoverFiscalYear);
        }

        public bool Equals(CarryoverInfo? other)
        {
            return other is not null
                && StartingPageNumber == other.StartingPageNumber
                && CarryoverIncomeTotal == other.CarryoverIncomeTotal
                && CarryoverExpenseTotal == other.CarryoverExpenseTotal
                && CarryoverFiscalYear == other.CarryoverFiscalYear;
        }

        public override bool Equals(object? obj) => Equals(obj as CarryoverInfo);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StartingPageNumber;
                hash = (hash * 397) ^ CarryoverIncomeTotal;
                hash = (hash * 397) ^ CarryoverExpenseTotal;
                hash = (hash * 397) ^ (CarryoverFiscalYear ?? -1);
                return hash;
            }
        }
    }
}
