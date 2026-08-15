using System;
using System.Collections.Generic;
using System.Linq;
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

        public StaffManageViewModel(
            IStaffRepository staffRepository,
            ICardReader cardReader,
            IValidationService validationService,
            OperationLogger operationLogger,
            IDialogService dialogService,
            IStaffAuthService staffAuthService,
            IMessenger messenger)
        {
            _staffRepository = staffRepository;
            _cardReader = cardReader;
            _validationService = validationService;
            _operationLogger = operationLogger;
            _dialogService = dialogService;
            _staffAuthService = staffAuthService;
            _messenger = messenger;

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
                            var restoredStaff = await _staffRepository.GetByIdmAsync(idm);
                            if (restoredStaff != null)
                            {
                                await _operationLogger.LogStaffRestoreAsync(restoredStaff);
                            }

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
                                var confirmed = _dialogService.ShowConfirmation(
                                    $"この職員証は以前 {identifier} として登録されていましたが、削除されています。\n\n復元しますか？",
                                    "削除済み職員");

                                if (confirmed)
                                {
                                    var restored = await _staffRepository.RestoreAsync(EditStaffIdm);
                                    if (restored)
                                    {
                                        // 操作ログを記録（復元後のデータを取得）
                                        var restoredStaff = await _staffRepository.GetByIdmAsync(EditStaffIdm);
                                        if (restoredStaff != null)
                                        {
                                            await _operationLogger.LogStaffRestoreAsync(restoredStaff);
                                        }

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
                                    _dialogService.ShowInformation(
                                        $"この職員証は以前 {identifier} として登録されていたため、新規登録はできません。\n\n" +
                                        "異なる名前等で登録したい場合は、先に復元を行い、その後に編集してください。",
                                        "ご案内");
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
                            await NotifyUpdateConflictAsync(sanitizedName, sanitizedNumber);
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
                            await NotifyUpdateConflictAsync(sanitizedName, sanitizedNumber);
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
        /// <param name="fallbackName">一覧に対象が残っていないときに名指しへ使う氏名</param>
        /// <param name="fallbackNumber">同じく職員番号</param>
        /// <remarks>
        /// <para>
        /// Issue #1753: 再読込を先に行うのは文言が「再読み込みしました」と述べるため。
        /// <c>CancelEdit()</c> は呼ばない（入力内容を消さない）。
        /// </para>
        /// <para>
        /// 「何が」は<b>編集後の入力値ではなく一覧に載っている値</b>で名指しする。
        /// 氏名を書き換えている途中なら、編集後の氏名は一覧のどこにも存在せず
        /// 「一覧で状態を確認して」という案内が実行できなくなる。
        /// 一覧の再読込で DataGrid の選択が解除され <see cref="SelectedStaff"/> は null に
        /// なる（<c>SelectedItem</c> は TwoWay バインド）ため、再読込より前に確定させる。
        /// </para>
        /// <para>
        /// Issue #1760: この順序を守る箇所が複数（更新前データの欠落・更新の影響行数 0）に
        /// 増えたため、呼び出し側へ書き写さずここへ集約する。
        /// </para>
        /// </remarks>
        private async Task NotifyUpdateConflictAsync(string fallbackName, string fallbackNumber)
        {
            var conflictLabel = SelectedStaff != null
                ? FormatStaffLabel(SelectedStaff.Name, SelectedStaff.Number)
                : FormatStaffLabel(fallbackName, fallbackNumber);
            await LoadStaffAsync();
            StatusMessage = ConcurrencyConflictMessage.ForUpdate($"職員「{conflictLabel}」", "職員一覧");
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
                    var staff = await _staffRepository.GetByIdmAsync(targetIdm);

                    var success = await _staffRepository.DeleteAsync(targetIdm);
                    if (success)
                    {
                        // 操作ログを記録（Issue #429: 認証済み職員のIDmを使用）
                        if (staff != null)
                        {
                            await _operationLogger.LogStaffDeleteAsync(staff);
                        }

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
                        await LoadStaffAsync();
                        StatusMessage = ConcurrencyConflictMessage.ForDelete(
                            $"職員「{targetLabel}」", "職員一覧");
                        IsStatusError = true;
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
            System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                EditStaffIdm = e.Idm;
                IsWaitingForCard = false;

                // 即座に登録済みチェックを実行（Issue #284）
                var existing = await _staffRepository.GetByIdmAsync(e.Idm, includeDeleted: true);
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
                            var restored = await _staffRepository.RestoreAsync(e.Idm);
                            if (restored)
                            {
                                // 操作ログを記録（復元後のデータを取得）
                                var restoredStaff = await _staffRepository.GetByIdmAsync(e.Idm);
                                if (restoredStaff != null)
                                {
                                    await _operationLogger.LogStaffRestoreAsync(restoredStaff);
                                }

                                var restoredIdm = e.Idm;
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

                    // カード読み取り完了後、抑制を解除（Issue #852）
                    _messenger.Send(new CardReadingSuppressedMessage(false, CardReadingSource.StaffRegistration));
                    return;
                }

                // 未登録職員証の場合は通常処理
                StatusMessage = "職員証を読み取りました";
                IsStatusError = false;

                // カード読み取り完了後、抑制を解除（Issue #852）
                _messenger.Send(new CardReadingSuppressedMessage(false, CardReadingSource.StaffRegistration));
            });
        }

        /// <summary>
        /// 選択職員変更時の処理
        /// </summary>
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
