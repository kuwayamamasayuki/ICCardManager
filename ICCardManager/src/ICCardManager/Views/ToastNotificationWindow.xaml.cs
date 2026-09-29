using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ICCardManager.Common;
using ICCardManager.Models;
using ICCardManager.Views.Helpers;

namespace ICCardManager.Views
{
/// <summary>
    /// トースト通知の種類
    /// </summary>
    public enum ToastType
    {
        /// <summary>
        /// 貸出（いってらっしゃい）
        /// </summary>
        Lend,

        /// <summary>
        /// 返却（おかえりなさい）
        /// </summary>
        Return,

        /// <summary>
        /// 情報
        /// </summary>
        Info,

        /// <summary>
        /// 警告
        /// </summary>
        Warning,

        /// <summary>
        /// エラー
        /// </summary>
        Error
    }

    /// <summary>
    /// トースト通知ウィンドウ
    /// </summary>
    /// <remarks>
    /// 設定された画面隅（<see cref="ToastPosition"/>。既定は右上）に表示される、
    /// フォーカスを奪わない通知ウィンドウ。
    /// 貸出・返却時の「いってらっしゃい！」「おかえりなさい！」メッセージを
    /// メインウィンドウとは別に表示し、職員の操作を妨げないようにする。
    /// </remarks>
    public partial class ToastNotificationWindow : Window
    {
        private readonly DispatcherTimer _autoCloseTimer;
        private const int DefaultDisplayDurationMs = 3000;

        /// <summary>
        /// 警告の表示時間（Issue #2141）
        /// </summary>
        /// <remarks>
        /// 貸出・返却の通知（3 秒）と同じでは、職員の判断を要する警告（共有 DB の切断・
        /// 残額の食い違い・履歴の確認）を読み切る前に消える。記録済みの案内は自動では消さない
        /// （<see cref="Show"/> の <c>autoClose: false</c>）。
        /// </remarks>
        internal const int WarningDisplayDurationMs = 10000;

        /// <summary>
        /// 自動では消えない通知の案内（Issue #2141）
        /// </summary>
        /// <remarks>
        /// 閉じる手段を増やしたら、それを述べる文言も併せて直す（#2077 / #2078）。
        /// トーストはフォーカスを受けない（<c>ShowActivated="False"</c>）ため、キーはメイン画面で受ける。
        /// </remarks>
        internal const string PersistentCloseHint = "クリックまたは Esc キーで閉じる";

        private bool _autoCloseEnabled = true;
        private bool _isClosing;

        /// <summary>
        /// 自動では消えない通知の置き場（Issue #2141）。同時に 1 枚だけ置く。
        /// </summary>
        /// <remarks>
        /// 旧実装は <c>ShowError</c> のたびに新しい窓を同じ画面隅へ重ね、1 枚ずつクリックしないと
        /// 消えなかった。新しい通知を置いたら古い通知を閉じる。UI スレッドからのみ触る。
        /// </remarks>
        private static readonly ReplaceableSlot<ToastNotificationWindow> PersistentSlot =
            new ReplaceableSlot<ToastNotificationWindow>(toast => toast.FadeOutAndClose());

        /// <summary>
        /// 現在のトースト表示位置
        /// </summary>
        public static ToastPosition CurrentPosition { get; set; } = ToastPosition.TopRight;

        public ToastNotificationWindow()
        {
            InitializeComponent();

            // 自動クローズタイマー（表示時間は Show が種類に応じて設定する）
            _autoCloseTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(DefaultDisplayDurationMs)
            };
            _autoCloseTimer.Tick += OnAutoCloseTimerTick;

            // 設定された表示位置（CurrentPosition）に配置する（OnLoaded → PositionToast）
            Loaded += OnLoaded;

            // クリックで閉じる（エラー通知など自動消去されない場合用）
            MouseLeftButtonDown += (s, e) => FadeOutAndClose();

            // 自分で閉じた（クリック・自動消去）ときは置き場を空ける。
            // 別の通知へ差し替わった後なら何もしない（ReplaceableSlot.Release）
            Closed += (s, e) => PersistentSlot.Release(this);
        }

        /// <summary>
        /// 通知の種類ごとの表示時間（Issue #2141）。自動では消えない通知には使わない。
        /// </summary>
        internal static TimeSpan GetDisplayDuration(ToastType type)
            => TimeSpan.FromMilliseconds(type == ToastType.Warning ? WarningDisplayDurationMs : DefaultDisplayDurationMs);

        /// <summary>
        /// 自動では消えない通知（エラー・記録済みの案内）をすべて閉じる（Issue #2141）
        /// </summary>
        /// <returns>閉じた通知があれば true</returns>
        /// <remarks>
        /// 次の職員証タッチ（＝次の操作の開始）と、メイン画面の Esc キーから呼ばれる。
        /// 前の職員宛ての案内を次の職員の操作の上に残さないため。
        /// </remarks>
        public static bool DismissPersistent()
        {
            var dismissed = false;
            Application.Current?.Dispatcher.Invoke(() => dismissed = PersistentSlot.Dismiss());
            return dismissed;
        }

        /// <summary>
        /// ウィンドウ読み込み時に指定位置に配置
        /// </summary>
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            PositionToast();
            StartFadeInAnimation();
            if (_autoCloseEnabled)
            {
                _autoCloseTimer.Start();
            }
        }

        /// <summary>
        /// 設定に応じた位置に配置
        /// </summary>
        private void PositionToast()
        {
            var workArea = SystemParameters.WorkArea;
            const double margin = 20;

            switch (CurrentPosition)
            {
                case ToastPosition.TopRight:
                    Left = workArea.Right - ActualWidth - margin;
                    Top = workArea.Top + margin;
                    break;
                case ToastPosition.TopLeft:
                    Left = workArea.Left + margin;
                    Top = workArea.Top + margin;
                    break;
                case ToastPosition.BottomRight:
                    Left = workArea.Right - ActualWidth - margin;
                    Top = workArea.Bottom - ActualHeight - margin;
                    break;
                case ToastPosition.BottomLeft:
                    Left = workArea.Left + margin;
                    Top = workArea.Bottom - ActualHeight - margin;
                    break;
                default:
                    Left = workArea.Right - ActualWidth - margin;
                    Top = workArea.Top + margin;
                    break;
            }
        }

        /// <summary>
        /// フェードインアニメーション開始
        /// </summary>
        private void StartFadeInAnimation()
        {
            var storyboard = (Storyboard)FindResource("FadeInAnimation");
            storyboard.Begin(this);
        }

        /// <summary>
        /// フェードアウトして閉じる
        /// </summary>
        private void FadeOutAndClose()
        {
            // クリックと差し替え・一括クローズが重なっても、閉じる処理は 1 回だけ走らせる
            if (_isClosing)
            {
                return;
            }
            _isClosing = true;
            _autoCloseTimer.Stop();

            var storyboard = (Storyboard)FindResource("FadeOutAnimation");
            storyboard.Completed += (s, e) => Close();
            storyboard.Begin(this);
        }

        /// <summary>
        /// 自動クローズタイマーのTick
        /// </summary>
        private void OnAutoCloseTimerTick(object sender, EventArgs e)
        {
            _autoCloseTimer.Stop();
            FadeOutAndClose();
        }

        /// <summary>
        /// 貸出通知を表示
        /// </summary>
        /// <param name="cardInfo">カード情報（例: "はやかけん H-001"）</param>
        public static void ShowLend(string cardInfo)
        {
            Show(ToastType.Lend, "いってらっしゃい！", cardInfo);
        }

        /// <summary>
        /// 返却通知を表示
        /// </summary>
        /// <param name="cardInfo">カード情報（例: "はやかけん H-001"）</param>
        /// <param name="balance">残額</param>
        /// <param name="isLowBalance">残額警告フラグ</param>
        /// <param name="warningBalance">残額警告しきい値</param>
        public static void ShowReturn(string cardInfo, int balance, bool isLowBalance = false, int warningBalance = 0)
        {
            // Issue #1273: 文字サイズ「大/特大」でも折返しが増えないよう、警告文を簡潔化した。
            // Issue #2077: 境界の表記は判定（#1998 で「以下」へ統一）と同じ場所に置く。
            // ここで文言を組み立て直さないこと（BalanceWarningPolicy.FormatLowBalanceNotice へ委譲）。
            var subMessage = isLowBalance
                ? BalanceWarningPolicy.FormatLowBalanceNotice(warningBalance)
                : null;
            Show(ToastType.Return, "おかえりなさい！", cardInfo, $"残額: {balance:N0}円", subMessage);
        }

        /// <summary>
        /// 通知を表示
        /// </summary>
        /// <param name="type">通知種類</param>
        /// <param name="title">タイトル</param>
        /// <param name="message">メッセージ</param>
        /// <param name="additionalInfo">追加情報</param>
        /// <param name="subMessage">サブメッセージ</param>
        /// <param name="autoClose">
        /// 自動消去するかどうか（デフォルト: true）。false の通知は同時に 1 枚だけ置き（新しい通知が古い通知を閉じる）、
        /// クリック・メイン画面の Esc キー・次の職員証タッチ（<see cref="DismissPersistent"/>）で閉じる（Issue #2141）
        /// </param>
        public static void Show(ToastType type, string title, string message, string additionalInfo = null, string subMessage = null, bool autoClose = true)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var toast = new ToastNotificationWindow();
                toast._autoCloseEnabled = autoClose;
                toast._autoCloseTimer.Interval = GetDisplayDuration(type);
                toast.ApplyStyle(type);
                toast.TitleText.Text = title;
                toast.MessageText.Text = message;

                if (!string.IsNullOrEmpty(additionalInfo))
                {
                    toast.MessageText.Text = $"{message}\n{additionalInfo}";
                }

                if (!string.IsNullOrEmpty(subMessage))
                {
                    toast.SubMessageText.Text = subMessage;
                    toast.SubMessageText.Visibility = Visibility.Visible;
                }

                // 自動消去しない場合は閉じ方を表示する
                if (!autoClose)
                {
                    toast.SubMessageText.Text = string.IsNullOrEmpty(subMessage)
                        ? PersistentCloseHint
                        : $"{subMessage}\n（{PersistentCloseHint}）";
                    toast.SubMessageText.Visibility = Visibility.Visible;
                }

                toast.Show();

                // Issue #2141: 自動では消えない通知は 1 枚に差し替える（同じ画面隅へ重ねて積まない）
                if (!autoClose)
                {
                    PersistentSlot.Put(toast);
                }
            });
        }

        /// <summary>
        /// 通知種類に応じたスタイルを適用（Issue #1461: AccessibilityStyles.xaml の SSOT から取得）
        /// </summary>
        private void ApplyStyle(ToastType type)
        {
            if (!Enum.IsDefined(typeof(ToastType), type))
            {
                return;
            }

            IconText.Text = GetIconText(type);
            var backgroundKey = GetBackgroundKey(type);
            var borderKey = GetBorderKey(type);
            var titleForegroundKey = GetTitleForegroundKey(type);

            ToastBorder.Background = ResolveBrush(backgroundKey);
            ToastBorder.BorderBrush = ResolveBrush(borderKey);
            TitleText.Foreground = ResolveBrush(titleForegroundKey);

            var messageBrush = ResolveBrush("WaitingForegroundBrush");
            MessageText.Foreground = messageBrush;
            SubMessageText.Foreground = messageBrush;
        }

        // Issue #2141: 種類ごとの見た目を純関数へ切り出した（Window は STA 依存で xUnit から実行できないため）。
        // 警告は貸出のブラシ（Lending*）を流用していたため、「返却は記録済み・再タッチしないでください」が
        // 貸出と同じ暖色で出ていた。Lending* は貸出のシグナル専用で、返却フローに使わない（#2079）。
        // 警告には警告専用のブラシ（Warning*）を充てる。WarningForegroundBrush は貸出の文字色と明度差を
        // 開いて色覚多様性でも分離できるよう選ばれている（#2074）。

        /// <summary>
        /// 通知の種類ごとのアイコン
        /// </summary>
        internal static string GetIconText(ToastType type) => type switch
        {
            ToastType.Lend => "🚃",
            ToastType.Return => "🏠",
            ToastType.Info => "ℹ️",
            ToastType.Warning => "⚠️",
            ToastType.Error => "❌",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未知の通知種類です"),
        };

        /// <summary>
        /// 通知の種類ごとの背景ブラシのリソースキー
        /// </summary>
        internal static string GetBackgroundKey(ToastType type) => type switch
        {
            ToastType.Lend => "LendingBackgroundBrush",
            ToastType.Return => "ReturnBackgroundBrush",
            ToastType.Info => "ReturnBackgroundBrush",
            ToastType.Warning => "WarningBackgroundBrush",
            ToastType.Error => "ErrorBackgroundBrush",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未知の通知種類です"),
        };

        /// <summary>
        /// 通知の種類ごとの枠線ブラシのリソースキー
        /// </summary>
        internal static string GetBorderKey(ToastType type) => type switch
        {
            ToastType.Lend => "LendingBorderBrush",
            ToastType.Return => "ReturnBorderBrush",
            ToastType.Info => "ReturnBorderBrush",
            ToastType.Warning => "WarningBorderBrush",
            ToastType.Error => "ErrorBorderBrush",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未知の通知種類です"),
        };

        /// <summary>
        /// 通知の種類ごとのタイトル文字色のリソースキー
        /// </summary>
        internal static string GetTitleForegroundKey(ToastType type) => type switch
        {
            ToastType.Lend => "LendingForegroundBrush",
            ToastType.Return => "ReturnForegroundBrush",
            ToastType.Info => "ReturnForegroundBrush",
            ToastType.Warning => "WarningForegroundBrush",
            ToastType.Error => "ErrorForegroundBrush",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未知の通知種類です"),
        };

        /// <summary>
        /// アプリケーションリソースからブラシを解決する。リソースが見つからない場合は <see cref="Brushes.Transparent"/> を返す。
        /// </summary>
        /// <remarks>
        /// テスト環境やリソース未登録時のクラッシュを防ぐため、フォールバックを持つ。
        /// </remarks>
        internal static Brush ResolveBrush(string resourceKey)
        {
            return Application.Current?.TryFindResource(resourceKey) as Brush ?? Brushes.Transparent;
        }
    }
}
