#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ICCardManager.Common;
using ICCardManager.ViewModels;
using ICCardManager.Views.Helpers;

namespace ICCardManager.Views.Dialogs
{
    /// <summary>
    /// 職員管理ダイアログ
    /// </summary>
    public partial class StaffManageDialog : Window
    {
        private readonly StaffManageViewModel _viewModel;
        private string? _presetIdm;

        // Issue #1429: ContentRendered と RequestNameFocus の到着順は実行経路によって入れ替わるため、
        // 両方が揃ったタイミングで NameTextBox.Focus() を確定させる。
        private bool _focusRequestPending;
        private bool _contentRendered;

        /// <summary>最後に読み上げた職員証の認証の保持の表示（Issue #2494。同じ文言を繰り返し読み上げない）</summary>
        private string? _lastAnnouncedHeldAuthenticationText;

        public StaffManageDialog(StaffManageViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = _viewModel;

            Loaded += StaffManageDialog_Loaded;
            ContentRendered += StaffManageDialog_ContentRendered;
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _viewModel.RequestNameFocus += ViewModel_RequestNameFocus;
            Closed += StaffManageDialog_Closed;

            // Issue #2494: この画面への操作（キー入力・マウスのボタン・ホイール）を、無操作の時間の数え直しとして知らせる。
            // 入力欄や一覧が処理済みにしたイベントも拾うよう handledEventsToo で受ける。トンネル（Preview）では受けない
            // （編集フォームの Escape の扱いを EditFormKeyPolicy に一本化している規約の検査と紛れないように。#2080）
            AddHandler(KeyDownEvent, new KeyEventHandler(Dialog_UserActivity), handledEventsToo: true);
            AddHandler(MouseDownEvent, new MouseButtonEventHandler(Dialog_UserActivity), handledEventsToo: true);
            AddHandler(MouseWheelEvent, new MouseWheelEventHandler(Dialog_UserActivity), handledEventsToo: true);
        }

        private async void StaffManageDialog_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await _viewModel.InitializeAsync();
                // IDmが事前に設定されている場合は新規登録モードで開始
                if (_presetIdm is { } presetIdm && !string.IsNullOrEmpty(presetIdm))
                {
                    // Issue #284対応: タッチ時点で削除済み/登録済みチェックを行う
                    var shouldClose = await _viewModel.StartNewStaffWithIdmAsync(presetIdm);
                    if (shouldClose)
                    {
                        // 削除済み職員の復元完了、または登録済み職員の場合はダイアログを閉じる
                        Close();
                    }
                }
            }
            catch (Exception ex)
            {
                // Issue #1844: 初期化に失敗したまま開いたままにすると、
                // StartNewStaffWithIdmAsync が入口で取得したカード読み取り抑制（#1807）と、
                // メイン画面側の UnregisteredCardDialog 抑制（ShowDialog を囲む using スコープ）が
                // どちらもダイアログを閉じるまで保持され、その間メイン画面は全カードタッチを無言で無視する。
                // 復帰手段が「職員が自分でダイアログを閉じる」1つしかない状態を残さない（#1725）。
                try
                {
                    // Issue #1614 / #1817: 生の ex.Message は出さず 3 要素の文言へ変換し、技術的詳細はログへ残す。
                    // 復旧可能な通常エラーのため ShowError（致命エラー用の GetErrorInfo）は使わない。
                    ErrorDialogHelper.LogException(ex, "職員管理画面の初期化");
                    // Issue #1794 / #1837: MessageBox を表示する 3 つの手段のうち、Window の
                    // コードビハインドは自ウィンドウ（this）をオーナーに渡す形を使う。
                    // OwnedMessageBox（DialogOwnerResolver）は「this を渡せない層」のための集約先で、
                    // 解決に失敗すると ownerless へ退化する（このダイアログの背後が押せる状態が残る）。
                    MessageBox.Show(
                        this,
                        ExceptionMessageFormatter.ToUserMessage(ex, "職員管理画面の初期化") +
                        "\n\n職員管理画面を閉じます。復旧したら、もう一度開いてください。",
                        "初期化エラー",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                finally
                {
                    // Issue #1745: catch の中の後始末は、それ自体が失敗し得る。
                    // 案内の表示が失敗しても、抑制の回収（Closed → Cleanup）は必ず行う。
                    Close();
                }
            }
        }

        private void StaffManageDialog_Closed(object sender, EventArgs e)
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _viewModel.RequestNameFocus -= ViewModel_RequestNameFocus;
            _viewModel.Cleanup();
        }

        /// <summary>
        /// Issue #1429: ViewModel が「氏名欄にフォーカスを当てたい」と要求した。
        /// 編集パネルは <c>IsEditing</c> による Visibility="Collapsed → Visible" の切替直後で、
        /// 視覚ツリーのレイアウト・描画パスがまだ走っていない可能性がある。
        /// ここではフラグだけ立て、ContentRendered と AND を取った上でフォーカスを確定する。
        /// </summary>
        private void ViewModel_RequestNameFocus(object? sender, EventArgs e)
        {
            _focusRequestPending = true;
            TryFocusNameTextBox();
        }

        /// <summary>
        /// Issue #1429: NameTextBox 自身の Visibility が変化したタイミングでフォーカスを確定する。
        /// 経路 A（ダイアログを素で開いて「新規登録」ボタン → 職員証タッチ）では、
        /// Window アクティベート時点で NameTextBox は Visibility=Collapsed のため、
        /// XAML の <c>FocusManager.FocusedElement</c> 指定は評価対象外として扱われる。
        /// その後 IsEditing=true で Visible 化された瞬間が **本イベントの発火点** であり、
        /// ここでフォーカスを当てるのが WPF "show-and-focus" の定番パターン。
        /// </summary>
        private void NameTextBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (NameTextBox.IsVisible)
            {
                // Issue #1873: ディスパッチした処理の例外を観測してログへ残す
                Dispatcher.InvokeAsyncObserved(
                    () =>
                    {
                        if (!IsActive)
                        {
                            Activate();
                        }
                        NameTextBox.UpdateLayout();
                        FocusManager.SetFocusedElement(this, NameTextBox);
                        Keyboard.Focus(NameTextBox);
                        NameTextBox.Focus();
                    },
                    "氏名入力欄へのフォーカス設定",
                    DispatcherPriority.ApplicationIdle);
            }
        }

        /// <summary>
        /// Issue #1429: Window の最終描画完了。Loaded より後に発火する。
        /// このタイミングでは Window がアクティベーション完了し NameTextBox の視覚ツリー登録も済んでいる。
        /// </summary>
        private void StaffManageDialog_ContentRendered(object? sender, EventArgs e)
        {
            _contentRendered = true;
            TryFocusNameTextBox();
        }

        /// <summary>
        /// Issue #1429: フォーカス要求と ContentRendered の両方が揃ったときのみフォーカスを当てる。
        /// 編集パネルが Visibility=Collapsed → Visible に切り替わった直後はレイアウト未完了で
        /// <c>Focus()</c> が空振りするため、(1) <c>UpdateLayout()</c> で強制レイアウト、
        /// (2) <c>Activate()</c> で Window アクティベーション保証、
        /// (3) <c>FocusManager.SetFocusedElement</c>（論理フォーカス）+ <c>Keyboard.Focus</c>（キーボードフォーカス）
        /// の順に呼び、<c>DispatcherPriority.ApplicationIdle</c>（最低優先度）でディスパッチして
        /// 全てのフレームワーク処理が落ち着いてから走らせる。
        /// </summary>
        private void TryFocusNameTextBox()
        {
            if (!_focusRequestPending || !_contentRendered)
            {
                return;
            }

            // Issue #1873: ディスパッチした処理の例外を観測してログへ残す
            Dispatcher.InvokeAsyncObserved(
                () =>
                {
                    if (!IsActive)
                    {
                        Activate();
                    }
                    NameTextBox.UpdateLayout();
                    FocusManager.SetFocusedElement(this, NameTextBox);
                    Keyboard.Focus(NameTextBox);
                    NameTextBox.Focus();
                },
                "氏名入力欄へのフォーカス設定",
                DispatcherPriority.ApplicationIdle);
        }

        /// <summary>
        /// ViewModelのプロパティ変更を監視し、ハイライト表示を実行
        /// </summary>
        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Issue #2494: 認証の保持の表示が変わった（認証して保持した・「認証を解除」・無操作で解除・氏名の変更）ことを
            // スクリーンリーダーへ知らせる。LiveSetting だけでは LiveRegionChanged は発火しない（#2142）
            // 通知は ViewModel が文言の変わったときだけ上げる。View でも同じ文言の読み上げを繰り返さない（二重の守り）
            if (e.PropertyName == nameof(StaffManageViewModel.HeldAuthenticationText))
            {
                var text = _viewModel.HeldAuthenticationText;
                if (!string.Equals(text, _lastAnnouncedHeldAuthenticationText, StringComparison.Ordinal))
                {
                    _lastAnnouncedHeldAuthenticationText = text;
                    LiveRegionAnnouncer.Announce(HeldAuthenticationText, "職員証の認証の保持のスクリーンリーダー通知");
                }
            }

            if (e.PropertyName == nameof(StaffManageViewModel.NewlyRegisteredIdm)
                && _viewModel.NewlyRegisteredIdm != null)
            {
                var idm = _viewModel.NewlyRegisteredIdm;
                // DataGridの描画完了を待ってからハイライト実行
                // Issue #1873: ディスパッチした処理の例外を観測してログへ残す
                Dispatcher.InvokeAsyncObserved(() =>
                {
                    var item = _viewModel.StaffList.FirstOrDefault(s => s.StaffIdm == idm);
                    if (item != null)
                    {
                        DataGridHighlightHelper.HighlightRow(StaffDataGrid, item);
                    }
                }, "登録済み職員のハイライト表示", DispatcherPriority.ContextIdle);
            }
        }

        /// <summary>
        /// IDmを指定して新規登録モードで初期化
        /// </summary>
        /// <param name="idm">職員証のIDm</param>
        public void InitializeWithIdm(string idm)
        {
            _presetIdm = idm;
        }

        /// <summary>
        /// 完了ボタンクリック
        /// </summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// Issue #2080: Escape キーの意味を編集状態から決める。
        /// 判断は <see cref="EditFormKeyPolicy"/> に置き、ここでは結線だけを行う。
        /// </summary>
        private void Dialog_KeyDown(object sender, KeyEventArgs e)
        {
            EditFormKeyPolicy.HandleEscape(this, _viewModel, e);
        }

        /// <summary>
        /// Issue #2494: この画面へのキー入力・マウスのボタン・ホイールの操作を「操作があった」として ViewModel へ知らせる
        /// （無操作が続いたときに保持している職員証の認証を破棄する時間を数え直す）。
        /// </summary>
        /// <remarks>
        /// コンストラクタで handledEventsToo を付けて登録する（入力欄や一覧が処理済みにしたイベントも拾う）。
        /// マウスの移動は操作とみなさない（席を離れた後にマウスが触れただけで保持が延びないように）。イベントは処理済みにしない。
        /// </remarks>
        private void Dialog_UserActivity(object sender, InputEventArgs e)
        {
            _viewModel.NotifyUserActivity();
        }
    }
}
