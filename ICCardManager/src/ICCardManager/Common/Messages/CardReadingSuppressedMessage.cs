using CommunityToolkit.Mvvm.Messaging.Messages;

namespace ICCardManager.Common.Messages
{
    /// <summary>
    /// カード読み取り抑制の発生源
    /// </summary>
    public enum CardReadingSource
    {
        /// <summary>
        /// 職員証登録モード（StaffManageViewModel）
        /// </summary>
        StaffRegistration,

        /// <summary>
        /// 交通系ICカード登録モード（CardManageViewModel）
        /// </summary>
        CardRegistration,

        /// <summary>
        /// 職員証認証モード（StaffAuthDialog）
        /// </summary>
        Authentication,

        /// <summary>
        /// データインポートのカードタッチ待機モード（DataExportImportViewModel）
        /// </summary>
        /// <remarks>
        /// Issue #1514: データインポートでカードタッチ待機中に未登録の交通系ICカードを
        /// タッチすると、MainViewModel 側の CardTypeSelectionDialog と
        /// DataExportImportViewModel 側の「未登録カード」MessageBox が二重表示されてしまうため、
        /// データインポート画面側で MainViewModel.OnCardRead を抑制する用途で使用する。
        /// </remarks>
        DataImport,

        /// <summary>
        /// 未登録カードの種別選択〜登録ダイアログ（MainViewModel.HandleUnregisteredCardAsync）
        /// </summary>
        /// <remarks>
        /// Issue #1807: 種別選択ダイアログ（<c>ShowDialog</c> ＝ 入れ子のメッセージポンプ）を表示している間も
        /// <c>MainViewModel.HandleCardReadAsync</c> は実行されるため、別カードのタッチでダイアログが多重に開いたり
        /// 背後で貸出・返却が進んだりしていた。MainViewModel 自身が未登録カード処理の全区間
        /// （残高・履歴の事前読み取り〜種別選択〜登録ダイアログ）を抑制するために使用する。
        /// メッセージ経由ではなく MainViewModel が自身の抑制ソース集合を直接操作する（発生源の識別子としてのみ用いる）。
        /// </remarks>
        UnregisteredCardDialog,

        /// <summary>
        /// 残額の食い違い判定のためのカード読み取り（MainViewModel.CheckCardBalanceMismatchAsync）
        /// </summary>
        /// <remarks>
        /// Issue #1908: 実残額の読み取り（<c>ReadBalanceAsync</c>）はハードウェア I/O で数百 ms かかる。
        /// この await 中に届いた別のタッチは <c>HandleCardReadAsync</c> の入口ゲート
        /// （Processing でも抑制中でもない）を通過して並走するため、抑制しないと
        /// Issue #1807 と同じ二重購読（<c>Error -=</c> が no-op になり <c>finally</c> の
        /// <c>+=</c> が 2 回走る）と、Issue #1842 が塞いだ「再判定より後ろで前提が変わる窓」が
        /// そのまま再現する。判定の全区間を抑制して両方を閉じる。
        /// メッセージ経由ではなく MainViewModel が自身の抑制ソース集合を直接操作する。
        /// </remarks>
        BalanceMismatchCheck
    }

    /// <summary>
    /// カード読み取り抑制の状態変化を通知するメッセージ
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #852: 静的フラグ（App.IsStaffCardRegistrationActive等）を
    /// WeakReferenceMessengerベースのメッセージ通信に置き換える。
    /// </para>
    /// <para>
    /// Value=true で抑制開始、Value=false で抑制解除を表す。
    /// Sourceプロパティで抑制の発生源を識別する。
    /// </para>
    /// </remarks>
    public class CardReadingSuppressedMessage : ValueChangedMessage<bool>
    {
        /// <summary>
        /// 抑制の発生源
        /// </summary>
        public CardReadingSource Source { get; }

        /// <summary>
        /// コンストラクタ
        /// </summary>
        /// <param name="isSuppressed">true=抑制開始、false=抑制解除</param>
        /// <param name="source">抑制の発生源</param>
        public CardReadingSuppressedMessage(bool isSuppressed, CardReadingSource source)
            : base(isSuppressed)
        {
            Source = source;
        }
    }
}
