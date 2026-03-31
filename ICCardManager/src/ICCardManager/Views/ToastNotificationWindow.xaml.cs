using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ICCardManager.Models;

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
    /// 画面右上に表示されるフォーカスを奪わない通知ウィンドウ。
    /// 貸出・返却時の「いってらっしゃい！」「おかえりなさい！」メッセージを
    /// メインウィンドウとは別に表示し、職員の操作を妨げないようにする。
    /// </remarks>
    public partial class ToastNotificationWindow : Window
    {
        private readonly DispatcherTimer _autoCloseTimer;
        private const int DefaultDisplayDurationMs = 3000;
        private bool _autoCloseEnabled = true;

        /// <summary>
        /// 現在のトースト表示位置
        /// </summary>
        public static ToastPosition CurrentPosition { get; set; } = ToastPosition.TopRight;

        public ToastNotificationWindow()
        {
            InitializeComponent();

            // 自動クローズタイマー
            _autoCloseTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(DefaultDisplayDurationMs)
            };
            _autoCloseTimer.Tick += OnAutoCloseTimerTick;

            // 画面右上に配置
            Loaded += OnLoaded;

            // クリックで閉じる（エラー通知など自動消去されない場合用）
            MouseLeftButtonDown += (s, e) => FadeOutAndClose();
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
            var subMessage = isLowBalance
                ? $"⚠️ 残額が少なくなっています（しきい値: {warningBalance:N0}円）"
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
        /// <param name="autoClose">自動消去するかどうか（デフォルト: true、エラー時はfalse推奨）</param>
        public static void Show(ToastType type, string title, string message, string additionalInfo = null, string subMessage = null, bool autoClose = true)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var toast = new ToastNotificationWindow();
                toast._autoCloseEnabled = autoClose;
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

                // エラー時は自動消去しない場合、クリックで閉じるヒントを表示
                if (!autoClose)
                {
                    toast.SubMessageText.Text = string.IsNullOrEmpty(subMessage)
                        ? "クリックして閉じる"
                        : $"{subMessage}\n（クリックして閉じる）";
                    toast.SubMessageText.Visibility = Visibility.Visible;
                }

                toast.Show();
            });
        }

        /// <summary>
        /// 通知種類に応じたスタイルを適用
        /// </summary>
        private void ApplyStyle(ToastType type)
        {
            switch (type)
            {
                case ToastType.Lend:
                    // 貸出: 暖色系オレンジ（アクセシビリティ対応）
                    IconText.Text = "🚃";
                    ToastBorder.Background = new SolidColorBrush(Color.FromRgb(255, 243, 224)); // #FFF3E0
                    ToastBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 152, 0));  // #FF9800
                    TitleText.Foreground = new SolidColorBrush(Color.FromRgb(230, 81, 0));     // #E65100
                    MessageText.Foreground = new SolidColorBrush(Color.FromRgb(66, 66, 66));    // #424242
                    SubMessageText.Foreground = new SolidColorBrush(Color.FromRgb(66, 66, 66));
                    break;

                case ToastType.Return:
                    // 返却: 寒色系青（アクセシビリティ対応）
                    IconText.Text = "🏠";
                    ToastBorder.Background = new SolidColorBrush(Color.FromRgb(227, 242, 253)); // #E3F2FD
                    ToastBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(33, 150, 243)); // #2196F3
                    TitleText.Foreground = new SolidColorBrush(Color.FromRgb(13, 71, 161));    // #0D47A1
                    MessageText.Foreground = new SolidColorBrush(Color.FromRgb(66, 66, 66));    // #424242
                    SubMessageText.Foreground = new SolidColorBrush(Color.FromRgb(66, 66, 66));
                    break;

                case ToastType.Info:
                    IconText.Text = "ℹ️";
                    ToastBorder.Background = new SolidColorBrush(Color.FromRgb(227, 242, 253)); // #E3F2FD
                    ToastBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(33, 150, 243)); // #2196F3
                    TitleText.Foreground = new SolidColorBrush(Color.FromRgb(13, 71, 161));    // #0D47A1
                    MessageText.Foreground = new SolidColorBrush(Color.FromRgb(66, 66, 66));
                    SubMessageText.Foreground = new SolidColorBrush(Color.FromRgb(66, 66, 66));
                    break;

                case ToastType.Warning:
                    IconText.Text = "⚠️";
                    ToastBorder.Background = new SolidColorBrush(Color.FromRgb(255, 243, 224)); // #FFF3E0
                    ToastBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 152, 0));  // #FF9800
                    TitleText.Foreground = new SolidColorBrush(Color.FromRgb(230, 81, 0));     // #E65100
                    MessageText.Foreground = new SolidColorBrush(Color.FromRgb(66, 66, 66));
                    SubMessageText.Foreground = new SolidColorBrush(Color.FromRgb(66, 66, 66));
                    break;

                case ToastType.Error:
                    IconText.Text = "❌";
                    ToastBorder.Background = new SolidColorBrush(Color.FromRgb(255, 235, 238)); // #FFEBEE
                    ToastBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(244, 67, 54));  // #F44336
                    TitleText.Foreground = new SolidColorBrush(Color.FromRgb(183, 28, 28));    // #B71C1C
                    MessageText.Foreground = new SolidColorBrush(Color.FromRgb(66, 66, 66));
                    SubMessageText.Foreground = new SolidColorBrush(Color.FromRgb(66, 66, 66));
                    break;
            }
        }
    }
}
