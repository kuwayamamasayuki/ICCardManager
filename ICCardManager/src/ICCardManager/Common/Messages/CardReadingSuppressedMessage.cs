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
        /// 登録済みカードの残額食い違い判定（<c>MainViewModel.CheckCardBalanceMismatchAsync</c>）
        /// </summary>
        /// <remarks>
        /// Issue #1946: 判定は <c>AppState.WaitingForStaffCard</c> のまま <c>ReadBalanceAsync</c>（実機で数百ミリ秒）を
        /// 待つため、その間に届いた 2 枚目のタッチが入口ゲートを通過して判定へ再入し得た。再入すると
        /// <c>_cardReader.Error</c> の <c>-=</c> が no-op になり <c>finally</c> の <c>+=</c> が 2 回走って二重購読になる
        /// （<see cref="UnregisteredCardDialog"/> ＝ Issue #1807 と同型）。判定の区間だけを抑制するために使用する。
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
