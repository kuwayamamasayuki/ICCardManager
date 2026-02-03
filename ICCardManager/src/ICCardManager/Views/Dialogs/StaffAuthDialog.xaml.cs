using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Infrastructure.Sound;

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
        private readonly DispatcherTimer _timeoutTimer;
        private int _remainingSeconds = 60;

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
            ISoundPlayer soundPlayer)
        {
            InitializeComponent();

            _staffRepository = staffRepository;
            _cardReader = cardReader;
            _soundPlayer = soundPlayer;

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
            // 認証モードを有効化（MainViewModelのカード処理を抑制）
            App.IsAuthenticationActive = true;

            // カード読み取りイベントを購読
            _cardReader.CardRead += OnCardRead;

            // タイムアウトタイマー開始
            _timeoutTimer.Start();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            // タイマー停止
            _timeoutTimer.Stop();

            // カード読み取りイベントを解除
            _cardReader.CardRead -= OnCardRead;

            // 認証モードを解除
            App.IsAuthenticationActive = false;
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

                        _soundPlayer.Play(SoundType.Lend);
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

        private void ShowStatus(string message, bool isError)
        {
            StatusText.Text = message;
            StatusBorder.Visibility = Visibility.Visible;

            if (isError)
            {
                StatusBorder.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xFF, 0xEB, 0xEE));
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xC6, 0x28, 0x28));
            }
            else
            {
                StatusBorder.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xE8, 0xF5, 0xE9));
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x2E, 0x7D, 0x32));
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
