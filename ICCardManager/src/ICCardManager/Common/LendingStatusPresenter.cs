using System;

namespace ICCardManager.Common
{
    /// <summary>
    /// Issue #1274: 貸出/返却状態の表示要素を「アイコン＋短いテキスト＋
    /// スクリーンリーダー用詳細テキスト」の3点セットに正規化する純粋関数クラス。
    /// </summary>
    /// <remarks>
    /// <para>
    /// CLAUDE.md の「色・アイコン・テキスト・音の4要素で状態を伝達」原則を満たすため、
    /// UI 表示で色のみ／アイコンのみ／テキストのみに依存することを避ける。
    /// 本クラスは UI コンポーネント（MainWindow ダッシュボード・CardManageDialog・
    /// その他リストビュー）で状態表示を統一するための共通ヘルパー。
    /// </para>
    /// <para>
    /// 状態は <see cref="LendingStatus"/> enum で表現し、各状態に対して
    /// アイコン（絵文字）、短いラベル、アクセシビリティ用の完全な説明文を提供する。
    /// </para>
    /// </remarks>
    public static class LendingStatusPresenter
    {
        /// <summary>貸出中カードのアイコン</summary>
        public const string LentIcon = "📤";
        /// <summary>利用可（在庫）カードのアイコン</summary>
        public const string AvailableIcon = "📥";
        /// <summary>払戻済カードのアイコン</summary>
        public const string RefundedIcon = "🚫";

        /// <summary>
        /// カードの状態からアイコン・短ラベル・アクセシビリティテキストを決定する。
        /// </summary>
        /// <param name="isLent">貸出中か</param>
        /// <param name="isRefunded">払戻済か（払戻済は他より優先）</param>
        /// <param name="lentStaffName">貸出中の場合の貸出者名（null/空でも可）</param>
        /// <returns>アイコン・ラベル・説明文を含む結果</returns>
        public static LendingStatusPresentation Resolve(bool isLent, bool isRefunded, string lentStaffName = null)
        {
            // Issue #530: 払戻済は貸出状態より優先して表示
            if (isRefunded)
            {
                return new LendingStatusPresentation(
                    LendingStatus.Refunded,
                    icon: RefundedIcon,
                    shortText: "払戻済",
                    accessibilityText: "払戻済のカードです");
            }

            if (isLent)
            {
                var shortText = string.IsNullOrWhiteSpace(lentStaffName)
                    ? "貸出中"
                    : $"貸出中（{lentStaffName}）";
                var accessibilityText = string.IsNullOrWhiteSpace(lentStaffName)
                    ? "貸出中のカードです"
                    : $"{lentStaffName} さんに貸出中のカードです";
                return new LendingStatusPresentation(
                    LendingStatus.Lent,
                    icon: LentIcon,
                    shortText: shortText,
                    accessibilityText: accessibilityText);
            }

            return new LendingStatusPresentation(
                LendingStatus.Available,
                icon: AvailableIcon,
                shortText: "在庫",
                accessibilityText: "利用可能な在庫カードです");
        }

        /// <summary>
        /// アイコン＋短テキストを結合した表示文字列を返す（絵文字フォント非対応環境でも
        /// 文字自体は読める設計）。
        /// </summary>
        public static string FormatWithIcon(LendingStatusPresentation presentation)
        {
            if (presentation == null) throw new ArgumentNullException(nameof(presentation));
            return $"{presentation.Icon} {presentation.ShortText}";
        }
    }

    /// <summary>
    /// 貸出状態の種別。
    /// </summary>
    public enum LendingStatus
    {
        /// <summary>利用可能（在庫中）</summary>
        Available,
        /// <summary>貸出中</summary>
        Lent,
        /// <summary>払戻済（貸出対象外）</summary>
        Refunded,
    }

    /// <summary>
    /// Issue #1274: 貸出状態の表示要素 3点セット（アイコン・短テキスト・説明文）。
    /// </summary>
    public sealed class LendingStatusPresentation
    {
        public LendingStatusPresentation(
            LendingStatus status,
            string icon,
            string shortText,
            string accessibilityText)
        {
            Status = status;
            Icon = icon ?? throw new ArgumentNullException(nameof(icon));
            ShortText = shortText ?? throw new ArgumentNullException(nameof(shortText));
            AccessibilityText = accessibilityText ?? throw new ArgumentNullException(nameof(accessibilityText));
        }

        /// <summary>状態種別</summary>
        public LendingStatus Status { get; }

        /// <summary>画面表示用アイコン（絵文字）</summary>
        public string Icon { get; }

        /// <summary>画面表示用の短いラベル（UI の限られたスペース用）</summary>
        public string ShortText { get; }

        /// <summary>
        /// スクリーンリーダー向けの完全な説明文。
        /// <c>AutomationProperties.Name</c> に設定することで、
        /// 視覚障害のあるユーザーにも状態が明確に伝わる。
        /// </summary>
        public string AccessibilityText { get; }
    }
}
