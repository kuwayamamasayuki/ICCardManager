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
        Authentication
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
