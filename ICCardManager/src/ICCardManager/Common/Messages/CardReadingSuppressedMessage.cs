#nullable enable

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
        BalanceMismatchCheck,

        /// <summary>
        /// 終了確認（<c>MainViewModel.ConfirmExit</c>）
        /// </summary>
        /// <remarks>
        /// Issue #2143: 確認の MessageBox は入れ子のメッセージポンプを回すため、表示中もカードの読み取りは処理される。
        /// 職員証タッチ後に閉じようとして確認の裏で交通系ICカードをタッチすると、貸出・返却が台帳に確定し、
        /// 「はい」でその後処理（バス停名・同行者数の入力）の途中に終了する（#1807 と同じ形）。確認の区間だけを抑制する。
        /// </remarks>
        ExitConfirmation,

        /// <summary>
        /// カードの履歴からの取り込みダイアログ（<c>CardHistoryImportViewModel</c>）
        /// </summary>
        /// <remarks>
        /// Issue #2293: ダイアログは交通系ICカードのタッチを待って利用履歴を読む。抑制が無いと、同じタッチが
        /// メイン画面にも届き、職員証タッチ待ちの分岐で履歴の表示と残額の食い違い判定が走る（職員証タッチ済みなら
        /// 貸出・返却まで進む）。ダイアログの表示範囲（開いてから閉じるまで）で抑制する（#1807 の「表示範囲と一致」）。
        /// </remarks>
        CardHistoryImport,

        /// <summary>
        /// 職員証の差し替え（<c>StaffManageViewModel.ReplaceStaffCardAsync</c>）
        /// </summary>
        /// <remarks>
        /// Issue #2461: 職員管理画面が新しい職員証のタッチを待つ間と、その後の確認ダイアログ・書き込みの間に、
        /// 同じタッチがメイン画面にも届くと、未登録のカードとして種別選択ダイアログが開く（#1807 と同じ形）。
        /// 差し替えの操作の範囲（認証の後から書き込みの後処理まで）で抑制する。登録モード（<see cref="StaffRegistration"/>）とは
        /// 取得・解放の契機が違うので、ソースを分ける（同じソースを使うと、編集の取り消しで差し替え中の抑制が解ける）。
        /// </remarks>
        StaffCardReplacement,

        /// <summary>
        /// メイン画面・履歴パネルが表示するモーダル（管理系ダイアログ・確認や案内の MessageBox・行編集等）
        /// （<c>ModalCardReadingSuppressingNavigationService</c>）
        /// </summary>
        /// <remarks>
        /// Issue #2484: モーダルは入れ子のメッセージポンプを回すため、表示中もメイン画面はカードを読み取る。
        /// 交通系ICカード管理ダイアログや確認の表示中に職員証をタッチすると「交通系ICカードのタッチ待ち」へ移り、
        /// 続く交通系ICカードのタッチがその職員の貸出・返却として記録されていた。表示の範囲（開いてから閉じるまで）で抑制する。
        /// メイン画面と履歴パネルが受け取った <c>INavigationService</c> を包むので、呼び出しごとには書かない。
        /// 開いているスコープの数を数え、最後のスコープが閉じたときだけ外す（<c>MainViewModel.BeginModalCardReadingSuppression</c>）。
        /// 入れ子だけでなく、先に開いた側が後から開いた側より先に閉じる順（履歴パネルの詳細の初期化中に管理ダイアログを開く等）でも早く解けない。
        /// </remarks>
        MainScreenModal
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
