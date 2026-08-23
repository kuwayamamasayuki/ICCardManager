using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using ICCardManager.Common;
using ICCardManager.Common.Messages;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Models;
using ICCardManager.Services;

namespace ICCardManager.ViewModels
{
/// <summary>
    /// 職員管理画面のViewModel
    /// </summary>
    public partial class StaffManageViewModel : ViewModelBase
    {
        private readonly IStaffRepository _staffRepository;
        private readonly ICardReader _cardReader;
        private readonly IValidationService _validationService;
        private readonly OperationLogger _operationLogger;
        private readonly IDialogService _dialogService;
        private readonly IStaffAuthService _staffAuthService;
        private readonly IMessenger _messenger;
        /// <summary>
        /// UI スレッドへのディスパッチ。Issue #1843: 生の <c>Dispatcher.InvokeAsync</c> は
        /// <c>DispatcherOperation&lt;Task&gt;</c> を返すため内側の <c>Task</c> の例外を観測できない。
        /// この実装（<c>WpfDispatcherService</c>）は <c>Unwrap()</c> して観測しログへ残す（Issue #1725）。
        /// </summary>
        private readonly IDispatcherService _dispatcherService;

        [ObservableProperty]
        private ObservableCollection<StaffDto> _staffList = new();

        [ObservableProperty]
        private StaffDto? _selectedStaff;

        [ObservableProperty]
        private bool _isEditing;

        [ObservableProperty]
        private bool _isNewStaff;

        [ObservableProperty]
        private string _editStaffIdm = string.Empty;

        [ObservableProperty]
        private string _editName = string.Empty;

        [ObservableProperty]
        private string _editNumber = string.Empty;

        [ObservableProperty]
        private string _editNote = string.Empty;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        [ObservableProperty]
        private bool _isStatusError;

        [ObservableProperty]
        private bool _isWaitingForCard;

        /// <summary>
        /// 新規登録・更新・復元後にハイライト表示する職員のIDm
        /// </summary>
        [ObservableProperty]
        private string? _newlyRegisteredIdm;

        /// <summary>
        /// 職員証タッチで IDm が取り込まれ、新規登録モードに遷移した直後に発火する。
        /// View 側で氏名入力欄にフォーカスを移すために購読する（Issue #1429）。
        /// </summary>
        public event EventHandler? RequestNameFocus;

        /// <summary>
        /// 編集中の対象を「一覧に載っている表記」で名指しするための退避値（Issue #1761）
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>編集フォームが開いている間、編集対象を指すのは <see cref="EditStaffIdm"/>（主キー）であって
        /// <see cref="SelectedStaff"/> ではない。</b> <c>SelectedItem="{Binding SelectedStaff}"</c> は
        /// TwoWay バインドのため、選択行の Ctrl+クリック（<c>SelectionMode=Single</c> でも選択解除できる）や
        /// <c>StaffList.Clear()</c>（一覧の再読込）によって <see cref="SelectedStaff"/> <b>だけ</b>が null に戻る。
        /// 編集フォームは <c>IsEditing</c> にのみ連動するのでそのまま開いており、入力内容も残っている。
        /// </para>
        /// <para>
        /// 競合の案内は<b>一覧に載っている値</b>で対象を名指しする必要がある（Issue #1759）。
        /// 未保存の入力値で代替すると、氏名を書き換えている途中に競合したとき
        /// 「一覧のどこにも存在しない氏名」で「職員一覧で状態を確認してください」と案内することになる。
        /// カード側（<c>CardManageViewModel</c>）と同型のため同じ扱いにする。
        /// </para>
        /// </remarks>
        private string _editTargetName = string.Empty;
        private string _editTargetNumber = string.Empty;

        public StaffManageViewModel(
            IStaffRepository staffRepository,
            ICardReader cardReader,
            IValidationService validationService,
            OperationLogger operationLogger,
            IDialogService dialogService,
            IStaffAuthService staffAuthService,
            IMessenger messenger,
            IDispatcherService dispatcherService)
        {
            _staffRepository = staffRepository;
            _cardReader = cardReader;
            _validationService = validationService;
            _operationLogger = operationLogger;
            _dialogService = dialogService;
            _staffAuthService = staffAuthService;
            _messenger = messenger;
            _dispatcherService = dispatcherService;

            // カード読み取りイベント
            _cardReader.CardRead += OnCardRead;
        }

        /// <summary>
        /// 初期化
        /// </summary>
        public async Task InitializeAsync()
        {
            await LoadStaffAsync();
        }

        /// <summary>
        /// ユーザー向けメッセージで職員を特定するための表示名を組み立てる（氏名（職員番号））。
        /// </summary>
        /// <remarks>
        /// Issue #1759: 復元提案のダイアログと競合エラーの文言で同じ表記を使うため 1 か所に集約する。
        /// 職員番号は任意入力のため、空のときは氏名だけにする。
        /// </remarks>
        private static string FormatStaffLabel(string name, string number)
            => string.IsNullOrWhiteSpace(number) ? name : $"{name}（{number}）";

        /// <summary>
        /// 編集中の対象を、編集開始時に一覧へ載っていた表記で名指しする（Issue #1761）
        /// </summary>
        private string EditTargetLabel => FormatStaffLabel(_editTargetName, _editTargetNumber);

        /// <summary>
        /// 編集対象の退避値を確定させる（Issue #1761）
        /// </summary>
        /// <param name="staff">編集対象として一覧から選ばれた職員</param>
        private void SetEditTarget(StaffDto staff)
        {
            _editTargetName = staff.Name;
            _editTargetNumber = staff.Number ?? string.Empty;
        }

        /// <summary>
        /// 編集対象の退避値を破棄する（新規登録の開始時・編集の終了時。Issue #1761）
        /// </summary>
        private void ClearEditTarget()
        {
            _editTargetName = string.Empty;
            _editTargetNumber = string.Empty;
        }

        /// <summary>
        /// 職員一覧を読み込み
        /// </summary>
        [RelayCommand]
        public async Task LoadStaffAsync()
        {
            using (BeginBusy("読み込み中..."))
            {
                var staffList = await _staffRepository.GetAllAsync();
                StaffList.Clear();
                foreach (var staff in staffList.OrderBy(s => s.Number).ThenBy(s => s.Name))
                {
                    StaffList.Add(staff.ToDto());
                }
            }
        }

        /// <summary>
        /// 新規登録モードを開始
        /// </summary>
        [RelayCommand]
        public void StartNewStaff()
        {
            SelectedStaff = null;
            IsEditing = true;
            IsNewStaff = true;
            EditStaffIdm = string.Empty;
            EditName = string.Empty;
            EditNumber = string.Empty;
            EditNote = string.Empty;
            // Issue #1761: 新規登録には「一覧に載っている表記」が存在しない
            ClearEditTarget();
            StatusMessage = "職員証をタッチするとIDmを読み取ります";
            IsStatusError = false;
            IsWaitingForCard = true;

            // MainViewModelでの未登録カード処理を抑制（Issue #852）
            _messenger.Send(new CardReadingSuppressedMessage(true, CardReadingSource.StaffRegistration));
        }

        /// <summary>
        /// IDmを指定して新規登録モードを開始（未登録カード検出時用）
        /// </summary>
        /// <param name="idm">職員証のIDm</param>
        /// <returns>処理が完了したかどうか（削除済み職員の復元で完了した場合はtrue）</returns>
        public async Task<bool> StartNewStaffWithIdmAsync(string idm)
        {
            // MainViewModelでの未登録カード処理を抑制（Issue #852）
            // Issue #1807: この経路（未登録カード → 種別選択 → 職員登録）は抑制を一度も送っておらず、
            // 氏名入力中の別カードタッチが背後の貸出・返却や 2 枚目のダイアログを引き起こしていた。
            // 「新規登録」ボタン経由（StartNewStaff）と同じく入口で取得し、解放は CancelEdit / Cleanup に限る
            // （登録済み等でダイアログを閉じる経路は Cleanup が回収する）。
            _messenger.Send(new CardReadingSuppressedMessage(true, CardReadingSource.StaffRegistration));

            // Issue #284対応: タッチ時点で削除済み職員チェックを行う
            var existing = await _staffRepository.GetByIdmAsync(idm, includeDeleted: true);
            if (existing != null)
            {
                // 識別子を決定（名前優先、なければ職員番号）
                var identifier = !string.IsNullOrEmpty(existing.Name) ? existing.Name : existing.Number;

                if (existing.IsDeleted)
                {
                    // 削除済み職員の場合は復元を提案
                    var confirmed = _dialogService.ShowConfirmation(
                        $"この職員証は以前 {identifier} として登録されていましたが、削除されています。\n\n復元しますか？",
                        "削除済み職員");

                    if (confirmed)
                    {
                        var restored = await _staffRepository.RestoreAsync(idm);
                        if (restored)
                        {
                            // 操作ログを記録（復元後のデータを取得）
                            // Issue #1760: 再読取が null になるのは復元の直後に他 PC が削除した場合だけ。
                            // 復元は確定済みなので記録を落とさず、復元前のデータで補う。
                            var restoredStaff = await _staffRepository.GetByIdmAsync(idm)
                                ?? CreateRestoredSnapshot(existing);
                            await _operationLogger.LogStaffRestoreAsync(restoredStaff);

                            _dialogService.ShowInformation(
                                $"{identifier} を復元しました",
                                "復元完了");
                            return true; // ダイアログを閉じる
                        }
                        else
                        {
                            _dialogService.ShowError(
                                "復元に失敗しました",
                                "エラー");
                            return true; // ダイアログを閉じる
                        }
                    }
                    else
                    {
                        // Issue #314: 復元しない場合は案内メッセージを表示
                        _dialogService.ShowInformation(
                            $"この職員証は以前 {identifier} として登録されていたため、新規登録はできません。\n\n" +
                            "異なる情報で登録したい場合は、先に復元を行い、その後に編集してください。",
                            "ご案内");
                        return true; // ダイアログを閉じる
                    }
                }
                else
                {
                    // 既に登録済みの場合はメッセージを表示して終了
                    _dialogService.ShowInformation(
                        $"この職員証は {identifier} として既に登録されています",
                        "登録済み職員証");
                    return true; // ダイアログを閉じる
                }
            }

            // 未登録職員証の場合は通常処理
            SelectedStaff = null;
            IsEditing = true;
            IsNewStaff = true;
            EditStaffIdm = idm;
            EditName = string.Empty;
            EditNumber = string.Empty;
            EditNote = string.Empty;
            // Issue #1761: 新規登録には「一覧に載っている表記」が存在しない
            ClearEditTarget();
            StatusMessage = "職員証を読み取りました。氏名を入力してください。";
            IsStatusError = false;
            IsWaitingForCard = false; // すでにIDmがあるので待機しない

            // Issue #1429: 氏名入力欄へ自動フォーカス（View 側で購読）
            RequestNameFocus?.Invoke(this, EventArgs.Empty);

            return false; // ダイアログは開いたまま
        }

        /// <summary>
        /// 編集モードを開始
        /// </summary>
        [RelayCommand]
        public void StartEdit()
        {
            if (SelectedStaff == null) return;

            IsEditing = true;
            IsNewStaff = false;
            EditStaffIdm = SelectedStaff.StaffIdm;
            EditName = SelectedStaff.Name;
            EditNumber = SelectedStaff.Number ?? string.Empty;
            EditNote = SelectedStaff.Note ?? string.Empty;
            // Issue #1761: 以降 SelectedStaff は参照しない。編集対象は EditStaffIdm、
            // 名指しに使う表記はここで確定させた退避値が担う。
            SetEditTarget(SelectedStaff);
            StatusMessage = string.Empty;
            IsStatusError = false;
            IsWaitingForCard = false;
        }

        /// <summary>
        /// 保存
        /// </summary>
        [RelayCommand]
        public async Task SaveAsync()
        {
            try
            {
                // 入力値をサニタイズ
                var sanitizedName = InputSanitizer.SanitizeName(EditName);
                var sanitizedNumber = InputSanitizer.SanitizeStaffNumber(EditNumber);
                var sanitizedNote = InputSanitizer.SanitizeNote(EditNote);

                // バリデーション
                var idmResult = _validationService.ValidateStaffIdm(EditStaffIdm);
                if (!idmResult)
                {
                    StatusMessage = idmResult.ErrorMessage!;
                    IsStatusError = true;
                    return;
                }

                var nameResult = _validationService.ValidateStaffName(sanitizedName);
                if (!nameResult)
                {
                    StatusMessage = nameResult.ErrorMessage!;
                    IsStatusError = true;
                    return;
                }

                using (BeginBusy("保存中..."))
                {
                    if (IsNewStaff)
                    {
                        // 重複チェック
                        var existing = await _staffRepository.GetByIdmAsync(EditStaffIdm, includeDeleted: true);
                        if (existing != null)
                        {
                            var identifier = FormatStaffLabel(existing.Name, existing.Number);

                            if (existing.IsDeleted)
                            {
                                // 削除済み職員の場合は復元を提案
                                //
                                // Issue #1793: BeginBusy スコープの内側でモーダルを出すと、
                                // 全面オーバーレイと「保存中...」の不確定プログレスバーが
                                // ダイアログの背後で回り続ける。確認ダイアログは職員の判断を待つ設計であり、
                                // 背後の「処理中」表示はその判断を妨げる。
                                bool confirmed;
                                using (SuspendBusy())
                                {
                                    confirmed = _dialogService.ShowConfirmation(
                                        $"この職員証は以前 {identifier} として登録されていましたが、削除されています。\n\n復元しますか？",
                                        "削除済み職員");
                                }

                                if (confirmed)
                                {
                                    var restored = await _staffRepository.RestoreAsync(EditStaffIdm);
                                    if (restored)
                                    {
                                        // 操作ログを記録（復元後のデータを取得）
                                        // Issue #1760: 再読取が null になるのは復元の直後に他 PC が削除した場合だけ。
                                        // 復元は確定済みなので記録を落とさず、復元前のデータで補う。
                                        var restoredStaff = await _staffRepository.GetByIdmAsync(EditStaffIdm)
                                            ?? CreateRestoredSnapshot(existing);
                                        await _operationLogger.LogStaffRestoreAsync(restoredStaff);

                                        var restoredIdm = EditStaffIdm;
                                        await LoadStaffAsync();
                                        CancelEdit();
                                        SelectAndHighlight(restoredIdm);
                                        // Issue #1759: CancelEdit() は StatusMessage / IsStatusError をクリアするため、
                                        // 完了メッセージは必ず後処理のあとに設定する（先に設定すると一度も表示されない）。
                                        StatusMessage = $"{identifier} を復元しました";
                                        IsStatusError = false;
                                    }
                                    else
                                    {
                                        // Issue #1759: RestoreAsync が false を返すのは
                                        // UPDATE ... WHERE staff_idm = @staffIdm AND is_deleted = 1 が
                                        // 0 行に一致した場合だけ。つまり他 PC が先に復元したことを意味する。
                                        await LoadStaffAsync();
                                        StatusMessage = ConcurrencyConflictMessage.ForRestore(
                                            $"職員「{identifier}」", "職員一覧");
                                        IsStatusError = true;
                                    }
                                }
                                else
                                {
                                    // Issue #314: 復元しない場合は案内メッセージを表示
                                    // Issue #1793: BeginBusy スコープ内のモーダル表示は SuspendBusy で囲む
                                    using (SuspendBusy())
                                    {
                                        _dialogService.ShowInformation(
                                            $"この職員証は以前 {identifier} として登録されていたため、新規登録はできません。\n\n" +
                                            "異なる名前等で登録したい場合は、先に復元を行い、その後に編集してください。",
                                            "ご案内");
                                    }
                                    CancelEdit();
                                }
                                return;
                            }
                            else
                            {
                                StatusMessage = $"この職員証は {identifier} として既に登録されています";
                                IsStatusError = true;
                                return;
                            }
                        }

                        var staff = new Staff
                        {
                            StaffIdm = EditStaffIdm,
                            Name = sanitizedName,
                            Number = string.IsNullOrWhiteSpace(sanitizedNumber) ? null : sanitizedNumber,
                            Note = string.IsNullOrWhiteSpace(sanitizedNote) ? null : sanitizedNote
                        };

                        var success = await _staffRepository.InsertAsync(staff);
                        if (success)
                        {
                            // 操作ログを記録
                            await _operationLogger.LogStaffInsertAsync(staff);

                            var savedIdm = EditStaffIdm;
                            await LoadStaffAsync();
                            CancelEdit();
                            SelectAndHighlight(savedIdm);
                            // Issue #1759: CancelEdit() は StatusMessage / IsStatusError をクリアするため、
                            // 完了メッセージは必ず後処理のあとに設定する（先に設定すると一度も表示されない）。
                            StatusMessage = "登録しました";
                            IsStatusError = false;
                        }
                        else
                        {
                            StatusMessage = "登録に失敗しました";
                            IsStatusError = true;
                        }
                    }
                    else
                    {
                        // 更新前のデータを取得（操作ログ用）
                        var beforeStaff = await _staffRepository.GetByIdmAsync(EditStaffIdm);

                        // Issue #1760: 読み取れなかった時点で「対象の職員は現在 is_deleted = 0 として
                        // 存在しない」ことが確定しているため、UpdateAsync を呼ばずに競合として扱う。
                        // 通常は UpdateAsync の WHERE（同じ is_deleted = 0）も 0 行になるが、
                        // 読み取りと書き込みの間に他 PC が職員を復元すると 1 行に一致して成功し、
                        // 更新だけが通って operation_log には 1 行も残らない。カード側
                        // （CardManageViewModel）と同型の欠陥のため同じ扱いにする。
                        if (beforeStaff == null)
                        {
                            await NotifyUpdateConflictAsync(EditTargetLabel);
                            return;
                        }

                        // 更新
                        var staff = new Staff
                        {
                            StaffIdm = EditStaffIdm,
                            Name = sanitizedName,
                            Number = string.IsNullOrWhiteSpace(sanitizedNumber) ? null : sanitizedNumber,
                            Note = string.IsNullOrWhiteSpace(sanitizedNote) ? null : sanitizedNote
                        };

                        var success = await _staffRepository.UpdateAsync(staff);
                        if (success)
                        {
                            // 操作ログを記録（beforeStaff は上のガードで非 null が確定している。Issue #1760）
                            await _operationLogger.LogStaffUpdateAsync(beforeStaff, staff);

                            var updatedIdm = EditStaffIdm;
                            await LoadStaffAsync();
                            CancelEdit();
                            SelectAndHighlight(updatedIdm);
                            // Issue #1759: CancelEdit() は StatusMessage / IsStatusError をクリアするため、
                            // 完了メッセージは必ず後処理のあとに設定する（先に設定すると一度も表示されない）。
                            StatusMessage = "更新しました";
                            IsStatusError = false;
                        }
                        else
                        {
                            // Issue #1759: UpdateAsync が false を返すのは
                            // UPDATE ... WHERE staff_idm = @staffIdm AND is_deleted = 0 が
                            // 0 行に一致した場合だけ（Issue #1753）。つまり編集中に対象の職員が
                            // 論理削除されたことを意味する。カード側（CardManageViewModel）と同じ扱いにする。
                            await NotifyUpdateConflictAsync(EditTargetLabel);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Issue #1614: 生の ex.Message を UI に出さず、3要素準拠の文言を表示。技術詳細はログへ逃がす。
                ErrorDialogHelper.LogException(ex, "職員の保存");
                StatusMessage = ExceptionMessageFormatter.ToUserMessage(ex, "職員の保存");
                IsStatusError = true;
            }
        }

        /// <summary>
        /// 更新対象の職員が見つからなかった（競合）ことを案内し、職員一覧を再読込する
        /// </summary>
        /// <param name="targetLabel">対象職員の表示名（一覧に載っていた表記で確定させたもの）</param>
        /// <remarks>
        /// <para>
        /// Issue #1753: 再読込を先に行うのは文言が「再読み込みしました」と述べるため。
        /// <c>CancelEdit()</c> は呼ばない（入力内容を消さない）。
        /// </para>
        /// <para>
        /// 「何が」は<b>編集後の入力値ではなく一覧に載っている値</b>で名指しする。
        /// 氏名を書き換えている途中なら、編集後の氏名は一覧のどこにも存在せず
        /// 「一覧で状態を確認して」という案内が実行できなくなる。
        /// </para>
        /// <para>
        /// Issue #1760: この順序を守る箇所が複数（更新前データの欠落・更新の影響行数 0）に
        /// 増えたため、呼び出し側へ書き写さずここへ集約する。
        /// </para>
        /// <para>
        /// Issue #1761: ラベルの決定は<b>呼び出し側の責任</b>にした。以前はここで
        /// <see cref="SelectedStaff"/> を優先し、null のときだけ引数（未保存の入力値）へ
        /// 退避していたため、編集中に選択が外れると<b>一覧に存在しない氏名で名指し</b>していた。
        /// 編集経路は <see cref="EditTargetLabel"/> を渡す。
        /// </para>
        /// </remarks>
        private async Task NotifyUpdateConflictAsync(string targetLabel)
        {
            // Issue #1760: 更新前データを読めずに書き込みを中止した経路は、リポジトリの
            // 書き込みを 1 回も通らないため、影響行数 0 でのキャッシュ破棄（Issue #1759）が
            // 働かない。LoadStaffAsync() は GetAllAsync のキャッシュ（既定 TTL 60 秒／
            // 共有モード 30 秒）を読むため、ここで破棄しないと削除済みの職員を含む
            // 古い一覧が返り、「一覧を再読み込みしました」という案内が事実にならない。
            _staffRepository.InvalidateCache();
            await LoadStaffAsync();
            StatusMessage = ConcurrencyConflictMessage.ForUpdate($"職員「{targetLabel}」", "職員一覧");
            IsStatusError = true;
        }

        /// <summary>
        /// 削除
        /// </summary>
        [RelayCommand]
        public async Task DeleteAsync()
        {
            if (SelectedStaff == null) return;

            // Issue #1759: 削除対象の識別情報は**ここで確定させる**。
            // 失敗時に呼ぶ LoadStaffAsync() は StaffList.Clear() を行い、DataGrid の
            // SelectedItem="{Binding SelectedStaff}"（TwoWay）が選択解除を書き戻すため
            // SelectedStaff は null になる。再読込のあとで SelectedStaff を参照すると
            // NullReferenceException になり、案内文の代わりに例外の汎用メッセージが出る
            // （ViewModel 単体テストには View が無いため、この欠陥は検出されない）。
            var targetIdm = SelectedStaff.StaffIdm;
            var targetLabel = FormatStaffLabel(SelectedStaff.Name, SelectedStaff.Number);

            // Issue #429: 職員の削除は認証が必要
            var authResult = await _staffAuthService.RequestAuthenticationAsync("職員の削除");
            if (authResult == null)
            {
                // 認証キャンセルまたはタイムアウト
                return;
            }

            try
            {
                using (BeginBusy("削除中..."))
                {
                    // 削除前のデータを取得（操作ログ用）
                    //
                    // Issue #1760: 読めなければ削除自体を行わない。読み取れない時点で対象の職員は
                    // 現在存在しないが、その直後に他 PC が復元すると DeleteAsync（論理削除。
                    // WHERE is_deleted = 0）は 1 行に一致して成功し得る。従来の
                    // `if (staff != null)` ガードでは、論理削除だけが確定して
                    // operation_log には 1 行も残らなかった。カード側と同じ扱いにする。
                    var staff = await _staffRepository.GetByIdmAsync(targetIdm);
                    if (staff == null)
                    {
                        await NotifyDeleteConflictAsync(targetLabel);
                        return;
                    }

                    var success = await _staffRepository.DeleteAsync(targetIdm);
                    if (success)
                    {
                        // 操作ログを記録（Issue #429: 認証済み職員のIDmを使用）
                        await _operationLogger.LogStaffDeleteAsync(staff);

                        await LoadStaffAsync();
                        CancelEdit();
                        // Issue #1759: CancelEdit() は StatusMessage / IsStatusError をクリアするため、
                        // 完了メッセージは必ず後処理のあとに設定する（先に設定すると一度も表示されない）。
                        StatusMessage = "削除しました";
                        IsStatusError = false;
                    }
                    else
                    {
                        // Issue #1759: DeleteAsync（論理削除）が false を返すのは
                        // UPDATE ... WHERE staff_idm = @staffIdm AND is_deleted = 0 が
                        // 0 行に一致した場合だけ。つまり他 PC が先に削除したことを意味する。
                        // カード側の削除は CardOperationResult を返し Issue #1109 で是正済みだが、
                        // 職員側は bool のままで案内が「削除に失敗しました」の9文字だけだった。
                        // targetLabel はメソッド冒頭で確定済み（再読込後の SelectedStaff は null）。
                        await NotifyDeleteConflictAsync(targetLabel);
                    }
                }
            }
            catch (Exception ex)
            {
                // Issue #1614: 生の ex.Message を UI に出さず、3要素準拠の文言を表示。技術詳細はログへ逃がす。
                ErrorDialogHelper.LogException(ex, "職員の削除");
                StatusMessage = ExceptionMessageFormatter.ToUserMessage(ex, "職員の削除");
                IsStatusError = true;
            }
        }

        /// <summary>
        /// 削除対象の職員が見つからなかった（競合）ことを案内し、職員一覧を再読込する
        /// </summary>
        /// <param name="targetLabel">対象職員の表示名（再読込より前に確定させたもの）</param>
        /// <remarks>
        /// Issue #1760: 事前読み取りで検出した場合と影響行数 0 で検出した場合は<b>同じ条件</b>
        /// （対象行が無い）なので、同じ文言で案内する。書き込みを 1 回も通らない経路では
        /// <c>DeleteAsync</c> によるキャッシュ破棄（Issue #1759）が働かないため明示的に破棄する。
        /// </remarks>
        private async Task NotifyDeleteConflictAsync(string targetLabel)
        {
            _staffRepository.InvalidateCache();
            await LoadStaffAsync();
            StatusMessage = ConcurrencyConflictMessage.ForDelete($"職員「{targetLabel}」", "職員一覧");
            IsStatusError = true;
        }

        /// <summary>
        /// 復元後の職員の状態を、復元前に読み取ったデータから組み立てる
        /// </summary>
        /// <param name="deletedStaff">復元前に読み取った職員（<c>includeDeleted: true</c> で取得したもの）</param>
        /// <remarks>
        /// Issue #1760: 復元直後の再読取が失敗したときに、操作ログの <c>AfterData</c> として使う。
        /// <c>RestoreAsync</c> が変えるのは <c>is_deleted</c> / <c>deleted_at</c> の 2 列だけなので、
        /// それ以外は復元前の値をそのまま引き継ぐ。
        /// </remarks>
        private static Staff CreateRestoredSnapshot(Staff deletedStaff)
        {
            return new Staff
            {
                StaffIdm = deletedStaff.StaffIdm,
                Name = deletedStaff.Name,
                Number = deletedStaff.Number,
                Note = deletedStaff.Note,
                IsDeleted = false,
                DeletedAt = null
            };
        }

        /// <summary>
        /// 編集をキャンセル
        /// </summary>
        [RelayCommand]
        public void CancelEdit()
        {
            IsEditing = false;
            IsNewStaff = false;
            IsWaitingForCard = false;
            EditStaffIdm = string.Empty;
            EditName = string.Empty;
            EditNumber = string.Empty;
            EditNote = string.Empty;
            // Issue #1761: 編集対象の退避値も他の編集状態と同じタイミングで破棄する
            ClearEditTarget();
            StatusMessage = string.Empty;
            // Issue #1759: ステータス欄が編集フォームの外へ出て常時表示になったため、
            // エラー状態（赤色）を残したままにしない。CardManageViewModel.CancelEdit と揃える。
            IsStatusError = false;

            // 職員証登録モードを解除（Issue #852）
            _messenger.Send(new CardReadingSuppressedMessage(false, CardReadingSource.StaffRegistration));
        }

        /// <summary>
        /// カード読み取りイベント
        /// </summary>
        private void OnCardRead(object sender, CardReadEventArgs e)
        {
            if (!IsWaitingForCard) return;

            // UIスレッドで非同期実行（登録済みチェックを即座に行うため）
            // Issue #1843: 生の Dispatcher.InvokeAsync は DispatcherOperation<Task> を返すため、
            // 戻り値を await しても内側の Task の例外は観測されない（Unwrap() が要る。Issue #1725）。
            // IDispatcherService 経由なら Unwrap 済みの Task が観測され、失敗はログへ残る。
            // 本体（HandleCardReadAsync）の try/catch は受け皿として残すが、catch ブロック自身が
            // 失敗し得る（#1745）ため、ディスパッチ側の観測と二重に守る。
            _dispatcherService.InvokeAsync(() => HandleCardReadAsync(e.Idm));
        }

        /// <summary>
        /// 職員証タッチ待ち中に読み取った IDm を新規登録フォームへ反映する
        /// </summary>
        /// <remarks>
        /// <see cref="OnCardRead"/> から UI スレッドで呼ばれる本体。
        /// テストから直接呼べるよう分離している（Issue #1807）。
        /// <para>
        /// <b>本体全体を try/catch で包む</b>（Issue #1816）。<see cref="OnCardRead"/> は
        /// <c>Dispatcher.InvokeAsync</c> の戻り値を破棄する fire-and-forget であり、
        /// ここで例外が抜けると <see cref="EditStaffIdm"/> と <c>IsWaitingForCard = false</c> だけが
        /// 確定した「読み取れたように見える」状態で止まる（Issue #1725 / #1742）。
        /// 失敗時はタッチ待ちへ戻し、確認の済んでいない IDm をフォームに残さない。
        /// </para>
        /// </remarks>
        internal async Task HandleCardReadAsync(string idm)
        {
            // Issue #1816: 「復元が確定したか」は 1 回の読み取りに閉じた情報なので、
            // インスタンスフィールドではなく呼び出しごとのローカルで持つ。フィールドに置くと、
            // 後処理の await 中に別の読み取りが始まった場合（利用者が一覧再読込の最中に
            // 「新規登録」を押して次の職員証をタッチする／連続タッチで 2 件目が queue される）に
            // その呼び出しの先頭で false へ戻され、確定済みの復元が「読み取り失敗・もう一度タッチ」
            // として案内される＝この修正が防ごうとしている状態そのものに落ちる。
            var restoreCommitted = new StrongBox<bool>(false);

            try
            {
                await HandleCardReadCoreAsync(idm, restoreCommitted);
            }
            catch (Exception ex) when (restoreCommitted.Value)
            {
                // Issue #1816: 復元は確定済み。ここでの失敗は後処理（操作ログ・一覧再読込）の
                // 失敗であり、「もう一度タッチしてください」と案内すると、職員は既に復元済みの
                // 職員証を再タッチして「既に登録されています」を見ることになる
                // （.claude/rules/development-conventions.md「コミット確定後の後処理を、
                // 成否の判定に巻き込まない」#1727 / #1805）
                ErrorDialogHelper.LogException(ex, "職員の復元後の処理");

                // Issue #1816: 登録モードを終える。CancelEdit() は IsEditing / IsNewStaff を落とし、
                // StaffRegistration 抑制も解除する（#1807）。ここを飛ばすと、案内どおり画面を開き直すまで
                // メイン画面のカードタッチが抑制されたまま残り、フォームには空の IDm だけが残る。
                CancelEdit();

                // Issue #1759: CancelEdit() は StatusMessage / IsStatusError をクリアするため、
                // 完了・案内メッセージは必ず後処理のあとに設定する。
                StatusMessage = "職員の復元は記録済みですが、その後の画面の更新に失敗しました。" +
                    "もう一度タッチせず、職員管理画面を開き直して一覧を確認してください。";
                IsStatusError = true;
            }
            catch (Exception ex)
            {
                ErrorDialogHelper.LogException(ex, "職員証の読み取り");

                // Issue #1614: 生の ex.Message は出さず、3要素の文言へ変換する
                EditStaffIdm = string.Empty;
                IsWaitingForCard = true;
                StatusMessage = ExceptionMessageFormatter.ToUserMessage(ex, "職員証の読み取り") +
                    " 復旧したら、もう一度職員証をタッチしてください。";
                IsStatusError = true;
            }
        }

        /// <summary>
        /// 職員証読み取り処理の本体（例外は呼び出し元 <see cref="HandleCardReadAsync"/> が受け止める）
        /// </summary>
        /// <param name="idm">読み取った IDm</param>
        /// <param name="restoreCommitted">
        /// 復元の DB 更新が確定したら true を書き込む（Issue #1816）。
        /// コミット後の後処理で例外が出た場合に、読み取り失敗と区別して案内するために使う。
        /// 呼び出しごとのローカルとして渡し、並行する別の読み取りに書き換えられないようにする
        /// </param>
        private async Task HandleCardReadCoreAsync(string idm, StrongBox<bool> restoreCommitted)
        {
            // Issue #1816: 入口ゲート（OnCardRead）はカードリーダースレッドで判定され、
            // 解除は UI スレッドのここで初めて行われる。連続タッチでは 2 件目もゲートを
            // 通過済みで queue されているため、取得地点で再判定する（#1807 と同じ形）
            if (!IsWaitingForCard) return;

            EditStaffIdm = idm;
            IsWaitingForCard = false;

            // 即座に登録済みチェックを実行（Issue #284）
            var existing = await _staffRepository.GetByIdmAsync(idm, includeDeleted: true);
            if (existing != null)
            {
                var identifier = FormatStaffLabel(existing.Name, existing.Number);

                if (existing.IsDeleted)
                {
                    // 削除済み職員の場合は復元を提案
                    var confirmed = _dialogService.ShowConfirmation(
                        $"この職員証は以前 {identifier} として登録されていましたが、削除されています。\n\n復元しますか？",
                        "削除済み職員");

                    if (confirmed)
                    {
                        var restored = await _staffRepository.RestoreAsync(idm);
                        if (restored)
                        {
                            // 操作ログを記録（復元後のデータを取得）
                            // Issue #1760: 再読取が null になるのは復元の直後に他 PC が削除した場合だけ。
                            // 復元は確定済みなので記録を落とさず、復元前のデータで補う。
                            // Issue #1816: ここから先は「復元が確定した後の後処理」。
                            // 失敗しても復元は取り消されないため、読み取り失敗と混同する案内を出さない
                            restoreCommitted.Value = true;

                            var restoredStaff = await _staffRepository.GetByIdmAsync(idm)
                                ?? CreateRestoredSnapshot(existing);
                            await _operationLogger.LogStaffRestoreAsync(restoredStaff);

                            await LoadStaffAsync();
                            CancelEdit();
                            SelectAndHighlight(idm);
                            // Issue #1759: CancelEdit() は StatusMessage / IsStatusError をクリアするため、
                            // 完了メッセージは必ず後処理のあとに設定する（先に設定すると一度も表示されない）。
                            StatusMessage = $"{identifier} を復元しました";
                            IsStatusError = false;
                        }
                        else
                        {
                            // Issue #1759: 保存経路（SaveAsync）の復元分岐と同じ扱い。
                            // false は「他 PC が先に復元した」ことを意味する。
                            await LoadStaffAsync();
                            StatusMessage = ConcurrencyConflictMessage.ForRestore(
                                $"職員「{identifier}」", "職員一覧");
                            IsStatusError = true;
                        }
                    }
                    else
                    {
                        // Issue #314: 復元しない場合は案内メッセージを表示
                        _dialogService.ShowInformation(
                            $"この職員証は以前 {identifier} として登録されていたため、新規登録はできません。\n\n" +
                            "異なる名前等で登録したい場合は、先に復元を行い、その後に編集してください。",
                            "ご案内");
                        CancelEdit();
                    }
                }
                else
                {
                    // 既に登録済みの場合はメッセージを表示（赤色で目立たせる: Issue #286）
                    StatusMessage = $"この職員証は {identifier} として既に登録されています";
                    IsStatusError = true;
                    // フォームはそのままにして、ユーザーが確認できるようにする
                }

                return;
            }

            // 未登録職員証の場合は通常処理
            StatusMessage = "職員証を読み取りました";
            IsStatusError = false;

            // 注意: StaffRegistration 抑制はここで解除しない（Issue #1807）
            // IDm を読み取ってもダイアログはモーダルのまま氏名入力を待っており、ここで解除すると
            // 別カードのタッチが MainViewModel へ届いて背後で貸出・返却が進む。
            // CardManageViewModel.OnCardRead と同じく、ダイアログが開いている間は抑制を維持し、
            // CancelEdit() または Cleanup() でのみ解除する。
        }

        /// <summary>
        /// 選択職員変更時の処理
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>選択が外れた（<paramref name="value"/> が null）ときに編集フォームは閉じない</b>（Issue #1761）。
        /// 編集中の対象は <see cref="EditStaffIdm"/>（主キー）で特定されており、
        /// <see cref="SelectedStaff"/> は一覧の選択状態を表すだけなので、選択が外れても編集は継続できる。
        /// </para>
        /// <para>
        /// 選択は利用者の明示的な操作以外でも外れる（選択行の Ctrl+クリック、
        /// <c>LoadStaffAsync</c> の <c>StaffList.Clear()</c> による <c>SelectedItem</c> の書き戻し）。
        /// ここでフォームを閉じると<b>入力途中の氏名・備考が予告なく消える</b>ため、閉じる案は採らない。
        /// 代わりに「編集中に <see cref="SelectedStaff"/> を参照しない」ことを不変条件とし、
        /// 名指しに要る表記は <see cref="_editTargetName"/> / <see cref="_editTargetNumber"/> に退避しておく。
        /// この不変条件は <c>StaffManageViewModelTests</c> の Issue #1761 region で固定している。
        /// </para>
        /// </remarks>
        partial void OnSelectedStaffChanged(StaffDto? value)
        {
            // 新規登録モード中は選択変更を無視
            if (IsNewStaff) return;

            // 職員が選択された場合、編集中なら選択した職員の情報で更新
            if (value != null && IsEditing)
            {
                EditStaffIdm = value.StaffIdm;
                EditName = value.Name;
                EditNumber = value.Number ?? string.Empty;
                EditNote = value.Note ?? string.Empty;
                // Issue #1761: 編集対象が切り替わったので、名指しに使う表記も追随させる
                SetEditTarget(value);
                StatusMessage = string.Empty;
                IsStatusError = false;
            }
        }

#if DEBUG
        /// <summary>
        /// デバッグ用: カード読み取りをシミュレート
        /// </summary>
        [RelayCommand]
        public void SimulateCardRead()
        {
            if (!IsWaitingForCard) return;

            if (_cardReader is MockCardReader mockReader)
            {
                // 未使用のIDmを生成（職員証はFFFFで始まる）
                var newIdm = $"FFFF{Guid.NewGuid().ToString("N").Substring(0, 12).ToUpper()}";
                mockReader.SimulateCardRead(newIdm);
            }
        }
#endif

        /// <summary>
        /// 保存・更新・復元後にハイライト対象のIDmを設定する
        /// </summary>
        /// <remarks>
        /// View層がNewlyRegisteredIdmの変更を監視し、該当行のスクロール＋ハイライトを行う。
        /// 選択行の背景色と競合しないよう、SelectedStaffは設定しない（View層で選択解除する）。
        /// </remarks>
        /// <param name="idm">ハイライト対象の職員IDm</param>
        private void SelectAndHighlight(string idm)
        {
            // 同じIDmの連続操作でもPropertyChangedが発火するようリセット
            NewlyRegisteredIdm = null;
            NewlyRegisteredIdm = idm;
        }

        /// <summary>
        /// クリーンアップ
        /// </summary>
        public void Cleanup()
        {
            _cardReader.CardRead -= OnCardRead;

            // ダイアログ終了時に抑制を解除（Issue #852）
            _messenger.Send(new CardReadingSuppressedMessage(false, CardReadingSource.StaffRegistration));
        }
    }
}
