using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Threading;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using ICCardManager.Common.Messages;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Infrastructure.Sound;
using ICCardManager.Services;
using Microsoft.Extensions.Options;

namespace ICCardManager.Views.Dialogs
{
    /// <summary>
    /// 職員証認証ダイアログ
    /// </summary>
    /// <remarks>
    /// Issue #429: 重要な操作の前に職員証タッチを必須とする
    /// </remarks>
    public partial class StaffAuthDialog : Window
    {
        private readonly IStaffRepository _staffRepository;
        private readonly ICardReader _cardReader;
        private readonly ISoundPlayer _soundPlayer;
        private readonly IMessenger _messenger;
        private readonly DispatcherTimer _timeoutTimer;
        private int _remainingSeconds;

        /// <summary>
        /// 認証成功時の職員IDm
        /// </summary>
        public string? AuthenticatedIdm { get; private set; }

        /// <summary>
        /// 認証成功時の職員名
        /// </summary>
        public string? AuthenticatedStaffName { get; private set; }

        /// <summary>
        /// 認証が成功したかどうか
        /// </summary>
        public bool IsAuthenticated => !string.IsNullOrEmpty(AuthenticatedIdm);

        /// <summary>
        /// 操作内容の説明
        /// </summary>
        public string OperationDescription
        {
            get => OperationDescriptionText.Text;
            set => OperationDescriptionText.Text = value;
        }

        public StaffAuthDialog(
            IStaffRepository staffRepository,
            ICardReader cardReader,
            ISoundPlayer soundPlayer,
            IMessenger messenger,
            IOptions<AppOptions> appOptions)
        {
            InitializeComponent();

            _staffRepository = staffRepository;
            _cardReader = cardReader;
            _soundPlayer = soundPlayer;
            _messenger = messenger;
            _remainingSeconds = appOptions.Value.StaffCardTimeoutSeconds;
            TimeoutText.Text = $"{_remainingSeconds}秒";

            // タイムアウトタイマーの設定
            _timeoutTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timeoutTimer.Tick += OnTimeoutTick;

            // イベント購読
            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 認証モードを有効化（MainViewModelのカード処理を抑制）Issue #852
            _messenger.Send(new CardReadingSuppressedMessage(true, CardReadingSource.Authentication));

            // カード読み取りイベントを購読
            _cardReader.CardRead += OnCardRead;

            // タイムアウトタイマー開始
            _timeoutTimer.Start();

#if DEBUG
            // Issue #688: デバッグモードでは仮想タッチボタンを表示
            if (_cardReader is HybridCardReader)
            {
                DebugVirtualTouchButton.Visibility = Visibility.Visible;
            }
#endif
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            // タイマー停止
            _timeoutTimer.Stop();

            // カード読み取りイベントを解除
            _cardReader.CardRead -= OnCardRead;

            // 認証モードを解除（Issue #852）
            _messenger.Send(new CardReadingSuppressedMessage(false, CardReadingSource.Authentication));
        }

        private void OnTimeoutTick(object? sender, EventArgs e)
        {
            _remainingSeconds--;
            TimeoutText.Text = $"{_remainingSeconds}秒";

            if (_remainingSeconds <= 10)
            {
                // 10秒以下は赤色で表示
                TimeoutText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xD3, 0x2F, 0x2F));
            }

            if (_remainingSeconds <= 0)
            {
                // タイムアウト
                _timeoutTimer.Stop();
                _soundPlayer.Play(SoundType.Warning);
                ShowStatus("認証がタイムアウトしました", isError: true);

                // 少し待ってから閉じる
                var closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                closeTimer.Tick += (s, args) =>
                {
                    closeTimer.Stop();
                    DialogResult = false;
                    Close();
                };
                closeTimer.Start();
            }
        }

        private async void OnCardRead(object? sender, CardReadEventArgs e)
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    // 職員として登録されているか確認
                    var staff = await _staffRepository.GetByIdmAsync(e.Idm);

                    if (staff != null)
                    {
                        // 職員として登録されている → 認証成功
                        AuthenticatedIdm = e.Idm;
                        AuthenticatedStaffName = staff.Name;

                        _soundPlayer.Play(SoundType.Notify);
                        _timeoutTimer.Stop();

                        DialogResult = true;
                        Close();
                    }
                    else
                    {
                        // 職員として登録されていない → エラー
                        _soundPlayer.Play(SoundType.Error);
                        ShowStatus("このカードは職員証として登録されていません。\n登録済みの職員証をタッチしてください。", isError: true);
                    }
                }
                catch (Exception ex)
                {
                    ShowStatus($"エラー: {ex.Message}", isError: true);
                }
            });
        }

        /// <summary>
        /// ステータスメッセージを表示し、スクリーンリーダーに LiveRegion 通知を発火する。
        /// </summary>
        /// <remarks>
        /// Issue #1509: Text 更新だけでは LiveRegionChanged が確実に発火しないため、
        /// UIElementAutomationPeer.RaiseAutomationEvent で明示的に通知する。
        /// Issue #1392: 色値は AccessibilityStyles.xaml のブラシキーを FindResource で参照する
        /// （色値の Single Source of Truth）。
        /// </remarks>
        private void ShowStatus(string message, bool isError)
        {
            StatusText.Text = message;

            // 視覚的状態の切り替え（DynamicResource ブラシ参照、Issue #1392）
            var bgKey = isError ? "ErrorBackgroundBrush" : "SuccessBackgroundBrush";
            var fgKey = isError ? "ErrorForegroundBrush" : "SuccessForegroundBrush";
            var borderKey = isError ? "ErrorBorderBrush" : "SuccessBorderBrush";

            StatusBorder.Background = (Brush)Application.Current.FindResource(bgKey);
            StatusBorder.BorderBrush = (Brush)Application.Current.FindResource(borderKey);
            StatusBorder.BorderThickness = new Thickness(1);
            StatusText.Foreground = (Brush)Application.Current.FindResource(fgKey);

            // スクリーンリーダーへの即時通知（Issue #1509）
            var peer = UIElementAutomationPeer.FromElement(StatusText)
                       ?? UIElementAutomationPeer.CreatePeerForElement(StatusText);
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        /// <summary>
        /// Issue #688: デバッグ用仮想タッチボタンのクリックハンドラ
        /// </summary>
        private void DebugVirtualTouchButton_Click(object sender, RoutedEventArgs e)
        {
#if DEBUG
            if (_cardReader is HybridCardReader hybridReader)
            {
                hybridReader.SimulateCardRead("FFFF000000000001");
            }
#endif
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
