using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Common;
using ICCardManager.Services;

namespace ICCardManager.ViewModels
{
    /// <summary>
    /// 同一とみなす駅・バス停の編集ダイアログの ViewModel（Issue #1905）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「天神日銀前」と「天神中央郵便局前」のように道路を挟んで向かい合う実質同一の停留所を
    /// 登録すると、往復の折り返しとして認識され、目的地が摘要から消えなくなる。
    /// </para>
    /// <para>
    /// 追加・更新・削除はそのつど DB へ書き込む（「保存し忘れ」の状態を作らないため、
    /// カード管理・職員管理と同じ流儀）。
    /// </para>
    /// </remarks>
    public partial class TransferStationGroupViewModel : ViewModelBase
    {
        private readonly ITransferStationGroupService _groupService;
        private readonly IDialogService _dialogService;

        /// <summary>
        /// 入力欄で名前を区切る文字（全角読点・半角カンマの両方を受け付ける）
        /// </summary>
        private static readonly char[] NameSeparators = { '、', ',', '，' };

        /// <summary>
        /// 一覧・入力欄で名前を連結するときの区切り
        /// </summary>
        internal const string NameJoiner = "、";

        public TransferStationGroupViewModel(
            ITransferStationGroupService groupService,
            IDialogService dialogService)
        {
            _groupService = groupService ?? throw new ArgumentNullException(nameof(groupService));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        }

        [ObservableProperty]
        private ObservableCollection<TransferStationGroupItem> _groups = new();

        /// <summary>
        /// 一覧の選択行。
        /// </summary>
        /// <remarks>
        /// Issue #1761: これは「一覧の選択状態」を表すだけの値であり、<b>操作対象の識別子として使わない</b>。
        /// TwoWay バインドのため <c>Groups.Clear()</c> や Ctrl+クリックで ViewModel の
        /// 与り知らぬところで null に戻る。操作対象は <see cref="TransferStationGroupItem.Id"/> で特定し、
        /// 最初の <c>await</c> より前にローカル変数へ確定させること。
        /// </remarks>
        [ObservableProperty]
        private TransferStationGroupItem _selectedGroup;

        [ObservableProperty]
        private bool _isEditing;

        /// <summary>
        /// 編集中の入力文字列（読点区切り）
        /// </summary>
        [ObservableProperty]
        private string _editingNames = string.Empty;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        [ObservableProperty]
        private bool _isStatusError;

        /// <summary>
        /// 編集中の対象。新規追加なら null
        /// </summary>
        private Guid? _editingGroupId;

        public bool HasSelectedGroup => SelectedGroup != null;

        public string EditFormTitle => _editingGroupId.HasValue ? "グループの編集" : "グループの追加";

        partial void OnSelectedGroupChanged(TransferStationGroupItem value)
        {
            OnPropertyChanged(nameof(HasSelectedGroup));
            EditCommand.NotifyCanExecuteChanged();
            DeleteCommand.NotifyCanExecuteChanged();
        }

        /// <summary>
        /// 現在有効なグループを読み込む
        /// </summary>
        public async Task LoadAsync()
        {
            using (BeginBusy("読み込み中..."))
            {
                var groups = await _groupService.GetGroupsAsync();

                Groups.Clear();
                foreach (var group in groups)
                {
                    Groups.Add(new TransferStationGroupItem(group));
                }
            }
        }

        [RelayCommand]
        public void New()
        {
            _editingGroupId = null;
            EditingNames = string.Empty;
            IsEditing = true;
            SetStatus(string.Empty, false);
            OnPropertyChanged(nameof(EditFormTitle));
        }

        [RelayCommand(CanExecute = nameof(HasSelectedGroup))]
        public void Edit()
        {
            var target = SelectedGroup;
            if (target == null)
            {
                return;
            }

            _editingGroupId = target.Id;
            EditingNames = string.Join(NameJoiner, target.Names);
            IsEditing = true;
            SetStatus(string.Empty, false);
            OnPropertyChanged(nameof(EditFormTitle));
        }

        [RelayCommand]
        public void CancelEdit()
        {
            IsEditing = false;
            _editingGroupId = null;
            EditingNames = string.Empty;
            SetStatus(string.Empty, false);
            OnPropertyChanged(nameof(EditFormTitle));
        }

        [RelayCommand]
        public async Task SaveAsync()
        {
            // Issue #1761: 操作対象は await より前にローカルへ確定させる
            var editingId = _editingGroupId;
            var names = ParseNames(EditingNames);

            var validation = Validate(names, Groups, editingId);
            if (validation != null)
            {
                SetStatus(validation, true);
                return;
            }

            var isNew = !editingId.HasValue;
            var label = string.Join(NameJoiner, names);

            // Issue #1614: DB 書き込みは共有フォルダーの一時断・SQLITE_BUSY で例外になり得る。
            // 捕まえないと AsyncRelayCommand が UI スレッドへ再スローし、
            // 「予期しないエラー（SYS999）」の致命エラーダイアログになる（他の管理画面と同じ流儀で受ける）
            try
            {
                using (BeginBusy("保存中..."))
                {
                    if (!await _groupService.SaveGroupsAsync(BuildSnapshot(names, editingId)))
                    {
                        SetStatus(
                            "同一視グループを保存できませんでした。" +
                            "データベースが他のパソコンや別の操作で使用中だった可能性があります。" +
                            "しばらく待ってから、もう一度保存してください。",
                            true);
                        return;
                    }

                    ApplyToList(names, editingId);
                }
            }
            catch (Exception ex)
            {
                // Issue #1614: 生の ex.Message を UI に出さず、3要素準拠の文言を表示。技術詳細はログへ逃がす
                ErrorDialogHelper.LogException(ex, "同一視グループの保存");
                SetStatus(ExceptionMessageFormatter.ToUserMessage(ex, "同一視グループの保存"), true);
                return;
            }

            // Issue #1759: CancelEdit() は StatusMessage をクリアするため、完了メッセージは必ずそのあとに設定する
            CancelEdit();
            SetStatus(isNew ? $"「{label}」を追加しました" : $"「{label}」に更新しました", false);
        }

        [RelayCommand(CanExecute = nameof(HasSelectedGroup))]
        public async Task DeleteAsync()
        {
            // Issue #1761: 選択は確認ダイアログの表示中にも外れ得る。
            // 対象と文言に載せる名前を最初の await より前に確定させる
            var target = SelectedGroup;
            if (target == null)
            {
                return;
            }

            var targetId = target.Id;
            var label = string.Join(NameJoiner, target.Names);

            if (!_dialogService.ShowConfirmation(
                    $"「{label}」を同一視グループから削除しますか？\n\n" +
                    "削除すると、これらの駅・バス停は別の場所として扱われ、" +
                    "以後に作成される摘要で往復・乗継としてまとめられなくなります。\n" +
                    "既に保存済みの履歴の摘要は書き換わりません。",
                    "同一視グループの削除"))
            {
                return;
            }

            // Issue #1614: SaveAsync と同じ理由で DB 例外を受け止める
            try
            {
                using (BeginBusy("削除中..."))
                {
                    var snapshot = Groups
                        .Where(g => g.Id != targetId)
                        .Select(g => g.Names.ToList())
                        .ToList();

                    if (!await _groupService.SaveGroupsAsync(snapshot))
                    {
                        SetStatus(
                            "同一視グループを削除できませんでした。" +
                            "データベースが他のパソコンや別の操作で使用中だった可能性があります。" +
                            "しばらく待ってから、もう一度削除してください。",
                            true);
                        return;
                    }

                    var removed = Groups.FirstOrDefault(g => g.Id == targetId);
                    if (removed != null)
                    {
                        Groups.Remove(removed);
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorDialogHelper.LogException(ex, "同一視グループの削除");
                SetStatus(ExceptionMessageFormatter.ToUserMessage(ex, "同一視グループの削除"), true);
                return;
            }

            // 削除したグループを編集中だった場合はフォームも閉じる。
            // 開いたままにすると、そのフォームの「保存」が BuildSnapshot の
            // 「該当 Id が無ければ末尾へ追加」経路を通り、一覧には現れないグループが
            // DB にだけ復活する（次の保存・削除でそれが黙って消える）
            if (_editingGroupId == targetId)
            {
                CancelEdit();
            }

            SetStatus($"「{label}」を削除しました", false);
        }

        /// <summary>
        /// 入力文字列を名前のリストへ分解する（前後の空白除去・空要素と重複の除去）
        /// </summary>
        internal static List<string> ParseNames(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return new List<string>();
            }

            var names = new List<string>();
            foreach (var part in input.Split(NameSeparators))
            {
                var trimmed = part.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (!names.Contains(trimmed, StringComparer.Ordinal))
                {
                    names.Add(trimmed);
                }
            }

            return names;
        }

        /// <summary>
        /// 入力の検証。問題がなければ null、あればユーザー向け文言を返す
        /// </summary>
        /// <remarks>
        /// 純関数として切り出し、<see cref="System.Windows.Window"/> を実体化せずに
        /// 文言の品質を単体テストで固定できるようにする（Issue #1794 と同じ形）。
        /// 文言は「何が」「なぜ」「どうすれば」の 3 要素を含み行動指示で終わる
        /// （<c>error-messages.md</c>）。
        /// </remarks>
        internal static string Validate(
            IReadOnlyList<string> names,
            IEnumerable<TransferStationGroupItem> existingGroups,
            Guid? editingGroupId)
        {
            if (names.Count == 0)
            {
                return "駅名・バス停名が入力されていません。" +
                       "同一とみなす名前を読点（、）で区切って2つ以上入力してください。";
            }

            if (names.Count < TransferStationGroupService.MinimumNamesPerGroup)
            {
                return $"入力された名前が「{names[0]}」の1つだけです。" +
                       "1つだけでは同一とみなす相手がないため、グループになりません。" +
                       "読点（、）で区切って2つ以上入力してください。";
            }

            foreach (var group in existingGroups)
            {
                if (editingGroupId.HasValue && group.Id == editingGroupId.Value)
                {
                    continue;
                }

                var duplicated = group.Names.FirstOrDefault(n => names.Contains(n, StringComparer.Ordinal));
                if (duplicated != null)
                {
                    return $"「{duplicated}」は既に別のグループ「{string.Join(NameJoiner, group.Names)}」に登録されています。" +
                           "1つの駅・バス停を複数のグループに登録することはできません。" +
                           "重複する名前を取り除くか、既存のグループのほうを編集してください。";
                }
            }

            return null;
        }

        /// <summary>
        /// 保存する全グループ（編集結果を反映したもの）を組み立てる
        /// </summary>
        private List<List<string>> BuildSnapshot(List<string> names, Guid? editingGroupId)
        {
            var snapshot = new List<List<string>>();
            var replaced = false;

            foreach (var group in Groups)
            {
                if (editingGroupId.HasValue && group.Id == editingGroupId.Value)
                {
                    snapshot.Add(names.ToList());
                    replaced = true;
                }
                else
                {
                    snapshot.Add(group.Names.ToList());
                }
            }

            if (!replaced)
            {
                snapshot.Add(names.ToList());
            }

            return snapshot;
        }

        /// <summary>
        /// 保存に成功した内容を一覧へ反映する
        /// </summary>
        /// <remarks>
        /// Issue #1761: <c>Groups.Clear()</c> して詰め直すと <c>SelectedGroup</c> の書き戻しで
        /// 選択が解除されるため、編集した行の <see cref="TransferStationGroupItem.Id"/> を
        /// 保ったまま差分だけを当てる。
        /// </remarks>
        private void ApplyToList(List<string> names, Guid? editingGroupId)
        {
            if (editingGroupId.HasValue)
            {
                Groups.FirstOrDefault(g => g.Id == editingGroupId.Value)?.SetNames(names);
                return;
            }

            Groups.Add(new TransferStationGroupItem(names));
        }

        private void SetStatus(string message, bool isError)
        {
            StatusMessage = message;
            IsStatusError = isError;
        }
    }

    /// <summary>
    /// 同一視グループ 1 件（Issue #1905）
    /// </summary>
    /// <remarks>
    /// <see cref="Id"/> は DB に保存されない画面内だけの識別子。
    /// 一覧の選択（<c>SelectedItem</c>）を操作対象の識別子に使わないために持つ（Issue #1761）。
    /// </remarks>
    public partial class TransferStationGroupItem : ObservableObject
    {
        public TransferStationGroupItem(IEnumerable<string> names)
        {
            Id = Guid.NewGuid();
            _names = names.ToList();
        }

        public Guid Id { get; }

        private List<string> _names;

        public IReadOnlyList<string> Names => _names;

        /// <summary>
        /// 一覧に表示する文字列（「天神、西鉄福岡(天神)」）
        /// </summary>
        public string DisplayText => string.Join(TransferStationGroupViewModel.NameJoiner, _names);

        /// <summary>
        /// 登録件数（一覧の第2列）
        /// </summary>
        public int NameCount => _names.Count;

        internal void SetNames(IEnumerable<string> names)
        {
            _names = names.ToList();
            OnPropertyChanged(nameof(Names));
            OnPropertyChanged(nameof(DisplayText));
            OnPropertyChanged(nameof(NameCount));
        }
    }
}
