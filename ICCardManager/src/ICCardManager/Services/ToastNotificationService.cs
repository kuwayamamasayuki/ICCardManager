using ICCardManager.Views;

namespace ICCardManager.Services;

/// <summary>
/// トースト通知サービスの実装
/// </summary>
/// <remarks>
/// ToastNotificationWindowを使用して画面右上に通知を表示する。
/// フォーカスを奪わないため、職員の操作を妨げない。
/// </remarks>
public class ToastNotificationService : IToastNotificationService
{
    /// <summary>
    /// 貸出通知を表示
    /// </summary>
    public void ShowLendNotification(string cardType, string cardNumber)
    {
        var cardInfo = $"{cardType} {cardNumber}";
        ToastNotificationWindow.ShowLend(cardInfo);
    }

    /// <summary>
    /// 返却通知を表示
    /// </summary>
    public void ShowReturnNotification(string cardType, string cardNumber, int balance, bool isLowBalance = false)
    {
        var cardInfo = $"{cardType} {cardNumber}";
        ToastNotificationWindow.ShowReturn(cardInfo, balance, isLowBalance);
    }

    /// <summary>
    /// 情報通知を表示
    /// </summary>
    public void ShowInfo(string title, string message)
    {
        ToastNotificationWindow.Show(ToastType.Info, title, message);
    }

    /// <summary>
    /// 警告通知を表示
    /// </summary>
    public void ShowWarning(string title, string message)
    {
        ToastNotificationWindow.Show(ToastType.Warning, title, message);
    }

    /// <summary>
    /// エラー通知を表示
    /// </summary>
    public void ShowError(string title, string message)
    {
        ToastNotificationWindow.Show(ToastType.Error, title, message);
    }
}
