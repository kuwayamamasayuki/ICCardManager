using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;

namespace ICCardManager.ViewModels;

/// <summary>
/// 返却時の同行者数入力画面の ViewModel（Issue #1906）
/// </summary>
/// <remarks>
/// 複数名が同一経路を 1 枚の交通系ICカードで利用した場合、物品出納簿の氏名欄を
/// 「博多 花子 外１名」のようにまとめて記載できるよう、返却で作られた利用行ごとに
/// 本人を除く同行者数を入力させる。既定は 0（同行者なし）で、Enter だけで閉じられる。
/// 0 の行は書き込まない（返却時に既に 0 で INSERT 済み）。
/// 保存に失敗しても返却そのものは記録済みなので、「再タッチ」ではなく
/// 履歴の行編集から後で入力できることを案内する。
/// </remarks>
public partial class CompanionCountInputViewModel : ViewModelBase
{
    private readonly ILedgerRepository _ledgerRepository;

    [ObservableProperty]
    private ObservableCollection<CompanionCountInputItem> _items = new();

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>
    /// 保存完了（またはスキップ）フラグ。ダイアログはこれを購読して閉じる
    /// </summary>
    [ObservableProperty]
    private bool _isSaved;

    public CompanionCountInputViewModel(ILedgerRepository ledgerRepository)
    {
        _ledgerRepository = ledgerRepository ?? throw new ArgumentNullException(nameof(ledgerRepository));
    }

    /// <summary>
    /// 返却で作られた利用行を指定して初期化する。
    /// 利用行（払出 &gt; 0 かつ貸出中レコードでない）だけを対象にし、チャージ・ポイント還元は除く。
    /// </summary>
    public void Initialize(IEnumerable<Ledger> ledgers)
    {
        Items.Clear();
        foreach (var ledger in SelectTargetLedgers(ledgers))
        {
            Items.Add(new CompanionCountInputItem(ledger));
        }

        StatusMessage = Items.Count == 0
            ? "同行者数を入力する利用履歴がありません"
            : $"{Items.Count}件の利用があります。複数名で利用した場合は本人を除く人数を入力してください（1人で利用した場合は 0 のまま保存）。";
    }

    /// <summary>
    /// 同行者数の入力対象となる行を選ぶ純関数（MainViewModel 側の判定と共有する）
    /// </summary>
    public static List<Ledger> SelectTargetLedgers(IEnumerable<Ledger> ledgers)
    {
        return (ledgers ?? Enumerable.Empty<Ledger>())
            .Where(l => l != null && !l.IsLentRecord && l.Id > 0 && l.Expense > 0)
            .ToList();
    }

    /// <summary>
    /// 入力された同行者数を保存する。0 の行は書き込まない
    /// </summary>
    [RelayCommand]
    public async Task SaveAsync()
    {
        var invalid = Items.Where(i => !i.IsValid).ToList();
        if (invalid.Count > 0)
        {
            StatusMessage = $"同行者数「{invalid[0].CompanionCountText}」は数値として読み取れないか範囲外です。" +
                            $"本人を除く人数を0～{StaffNameFormatter.MaxCompanionCount}の整数で入力してください。";
            return;
        }

        var targets = Items.Where(i => i.CompanionCount > 0).ToList();
        if (targets.Count == 0)
        {
            // 全行 0 ＝ 同行者なし。書き込みは不要
            IsSaved = true;
            return;
        }

        using (BeginBusy("保存中..."))
        {
            try
            {
                var conflicted = new List<CompanionCountInputItem>();
                foreach (var item in targets)
                {
                    var ok = await _ledgerRepository.UpdateCompanionCountAsync(item.Ledger.Id, item.CompanionCount);
                    if (ok)
                    {
                        item.Ledger.CompanionCount = item.CompanionCount;
                    }
                    else
                    {
                        conflicted.Add(item);
                    }
                }

                if (conflicted.Count > 0)
                {
                    // Issue #1753: 影響行数 0 ＝ 対象行が別の操作で削除された競合
                    StatusMessage = $"{conflicted[0].UseDateDisplay} の利用履歴は他のパソコンや別の操作で削除された可能性があるため、同行者数を保存できませんでした。" +
                                    "返却は記録済みです。履歴画面で状態を確認し、必要なら行編集から同行者数を入力してください。";
                    return;
                }

                StatusMessage = "保存しました";
                IsSaved = true;
            }
            catch (Exception ex)
            {
                // 返却は記録済み。「再タッチ」と案内すると 30 秒ルールの逆処理で返却が取り消される（#1725）
                ErrorDialogHelper.LogException(ex, "同行者数の保存");
                StatusMessage = ExceptionMessageFormatter.ToUserMessage(ex, "同行者数の保存") +
                                Environment.NewLine +
                                "返却は記録済みです。再タッチせず、後で履歴の行編集から同行者数を入力してください。";
            }
        }
    }

    /// <summary>
    /// 入力せずに閉じる（後で履歴の行編集から入力できる）
    /// </summary>
    [RelayCommand]
    public void Skip()
    {
        IsSaved = true;
    }
}

/// <summary>
/// 同行者数入力の 1 行分
/// </summary>
public partial class CompanionCountInputItem : ObservableObject
{
    public Ledger Ledger { get; }

    /// <summary>
    /// 入力欄のテキスト。数値以外の入力を保存時に検出できるよう文字列で保持する
    /// </summary>
    [ObservableProperty]
    private string _companionCountText = "0";

    public CompanionCountInputItem(Ledger ledger)
    {
        Ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _companionCountText = ledger.CompanionCount.ToString();
    }

    public string UseDateDisplay => Ledger.Date.ToString("yyyy/MM/dd");

    public string Summary => Ledger.Summary;

    public string ExpenseDisplay => $"{Ledger.Expense:N0}円";

    /// <summary>
    /// 入力が 0～<see cref="StaffNameFormatter.MaxCompanionCount"/> の整数として読めるか
    /// </summary>
    public bool IsValid => TryParse(out _);

    /// <summary>
    /// 解析済みの同行者数（不正入力時は 0）
    /// </summary>
    public int CompanionCount => TryParse(out var value) ? value : 0;

    /// <summary>
    /// 氏名欄の表示プレビュー（「博多 花子 外1名」）
    /// </summary>
    public string DisplayStaffNamePreview =>
        StaffNameFormatter.Format(Ledger.StaffName, CompanionCount);

    private bool TryParse(out int value)
    {
        var text = (CompanionCountText ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            value = 0;
            return true;
        }
        return int.TryParse(text, out value) && value >= 0 && value <= StaffNameFormatter.MaxCompanionCount;
    }

    partial void OnCompanionCountTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(CompanionCount));
        OnPropertyChanged(nameof(DisplayStaffNamePreview));
    }
}
