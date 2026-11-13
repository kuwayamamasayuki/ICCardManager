#nullable enable

namespace ICCardManager.Common.Messages
{
    /// <summary>
    /// 職員証を差し替えた（Issue #2461）ことを、同じ PC の画面へ知らせるメッセージ
    /// </summary>
    /// <remarks>
    /// <para>
    /// 差し替えは職員の主キー（職員証の IDm）を変える。メイン画面は 30 秒ルールのために直前の操作者の IDm
    /// （<c>MainViewModel._lastProcessedStaffIdm</c>）と、職員証タッチ後の確定済みの操作者（<c>MainViewModel._currentStaffIdm</c>）を
    /// 持っており、差し替えの直後に同じ交通系ICカードをタッチし直す・続けて交通系ICカードをタッチすると、もう存在しない古い IDm で
    /// 記録しようとして失敗する。受け取った画面は、どちらかが古い IDm なら新しい IDm へ置き換える（同じ職員のまま）。
    /// </para>
    /// <para>
    /// 届くのは同じ PC の画面だけ。共有フォルダーモードの他の PC が持つ古い IDm は置き換わらない
    /// （その PC で貸出・返却を記録しようとすると「職員証が登録されていません。」になる。04_機能設計書 §2.8）。
    /// </para>
    /// </remarks>
    public sealed class StaffCardReplacedMessage
    {
        /// <summary>差し替える前の職員証の IDm</summary>
        public string OldIdm { get; }

        /// <summary>差し替えた後の職員証の IDm</summary>
        public string NewIdm { get; }

        public StaffCardReplacedMessage(string oldIdm, string newIdm)
        {
            OldIdm = oldIdm;
            NewIdm = newIdm;
        }
    }
}
