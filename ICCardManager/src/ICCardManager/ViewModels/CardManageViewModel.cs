using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Models;
using ICCardManager.Services;

namespace ICCardManager.ViewModels;

/// <summary>
/// カード管理画面のViewModel
/// </summary>
public partial class CardManageViewModel : ViewModelBase
{
    private readonly ICardRepository _cardRepository;
    private readonly ICardReader _cardReader;
    private readonly CardTypeDetector _cardTypeDetector;
    private readonly IValidationService _validationService;

    [ObservableProperty]
    private ObservableCollection<CardDto> _cards = new();

    [ObservableProperty]
    private CardDto? _selectedCard;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private bool _isNewCard;

    [ObservableProperty]
    private string _editCardIdm = string.Empty;

    [ObservableProperty]
    private string _editCardType = string.Empty;

    [ObservableProperty]
    private string _editCardNumber = string.Empty;

    [ObservableProperty]
    private string _editNote = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isWaitingForCard;

    /// <summary>
    /// カード種別の選択肢
    /// </summary>
    public ObservableCollection<string> CardTypes { get; } = new()
    {
        "はやかけん",
        "nimoca",
        "SUGOCA",
        "Suica",
        "PASMO",
        "ICOCA",
        "PiTaPa",
        "Kitaca",
        "TOICA",
        "manaca",
        "その他"
    };

    public CardManageViewModel(
        ICardRepository cardRepository,
        ICardReader cardReader,
        CardTypeDetector cardTypeDetector,
        IValidationService validationService)
    {
        _cardRepository = cardRepository;
        _cardReader = cardReader;
        _cardTypeDetector = cardTypeDetector;
        _validationService = validationService;

        // カード読み取りイベント
        _cardReader.CardRead += OnCardRead;
    }

    /// <summary>
    /// 初期化
    /// </summary>
    public async Task InitializeAsync()
    {
        await LoadCardsAsync();
    }

    /// <summary>
    /// カード一覧を読み込み
    /// </summary>
    [RelayCommand]
    public async Task LoadCardsAsync()
    {
        using (BeginBusy("読み込み中..."))
        {
            var cards = await _cardRepository.GetAllAsync();
            Cards.Clear();
            foreach (var card in cards.OrderBy(c => c.CardType).ThenBy(c => c.CardNumber))
            {
                Cards.Add(card.ToDto());
            }
        }
    }

    /// <summary>
    /// 新規登録モードを開始
    /// </summary>
    [RelayCommand]
    public void StartNewCard()
    {
        SelectedCard = null;
        IsEditing = true;
        IsNewCard = true;
        EditCardIdm = string.Empty;
        EditCardType = "はやかけん";
        EditCardNumber = string.Empty;
        EditNote = string.Empty;
        StatusMessage = "カードをタッチするとIDmを読み取ります";
        IsWaitingForCard = true;
    }

    /// <summary>
    /// IDmを指定して新規登録モードを開始（未登録カード検出時用）
    /// </summary>
    /// <param name="idm">カードのIDm</param>
    public void StartNewCardWithIdm(string idm)
    {
        SelectedCard = null;
        IsEditing = true;
        IsNewCard = true;
        EditCardIdm = idm;

        // カード種別を自動判定
        var detectedType = _cardTypeDetector.Detect(idm);
        EditCardType = CardTypeDetector.GetDisplayName(detectedType);

        EditCardNumber = string.Empty;
        EditNote = string.Empty;
        StatusMessage = $"カードを読み取りました: {EditCardType}";
        IsWaitingForCard = false; // すでにIDmがあるので待機しない
    }

    /// <summary>
    /// 編集モードを開始
    /// </summary>
    [RelayCommand]
    public void StartEdit()
    {
        if (SelectedCard == null) return;

        IsEditing = true;
        IsNewCard = false;
        EditCardIdm = SelectedCard.CardIdm;
        EditCardType = SelectedCard.CardType;
        EditCardNumber = SelectedCard.CardNumber;
        EditNote = SelectedCard.Note ?? string.Empty;
        StatusMessage = string.Empty;
        IsWaitingForCard = false;
    }

    /// <summary>
    /// 保存
    /// </summary>
    [RelayCommand]
    public async Task SaveAsync()
    {
        // バリデーション
        var idmResult = _validationService.ValidateCardIdm(EditCardIdm);
        if (!idmResult)
        {
            StatusMessage = idmResult.ErrorMessage!;
            return;
        }

        var typeResult = _validationService.ValidateCardType(EditCardType);
        if (!typeResult)
        {
            StatusMessage = typeResult.ErrorMessage!;
            return;
        }

        var numberResult = _validationService.ValidateCardNumber(EditCardNumber);
        if (!numberResult)
        {
            StatusMessage = numberResult.ErrorMessage!;
            return;
        }

        if (string.IsNullOrWhiteSpace(EditCardNumber))
        {
            // 自動採番
            EditCardNumber = await _cardRepository.GetNextCardNumberAsync(EditCardType);
        }

        using (BeginBusy("保存中..."))
        {
            if (IsNewCard)
            {
                // 重複チェック
                var existing = await _cardRepository.GetByIdmAsync(EditCardIdm, includeDeleted: true);
                if (existing != null)
                {
                    StatusMessage = "このカードは既に登録されています";
                    return;
                }

                var card = new IcCard
                {
                    CardIdm = EditCardIdm,
                    CardType = EditCardType,
                    CardNumber = EditCardNumber,
                    Note = string.IsNullOrWhiteSpace(EditNote) ? null : EditNote
                };

                var success = await _cardRepository.InsertAsync(card);
                if (success)
                {
                    StatusMessage = "登録しました";
                    await LoadCardsAsync();
                    CancelEdit();
                }
                else
                {
                    StatusMessage = "登録に失敗しました";
                }
            }
            else
            {
                // 更新
                var card = new IcCard
                {
                    CardIdm = EditCardIdm,
                    CardType = EditCardType,
                    CardNumber = EditCardNumber,
                    Note = string.IsNullOrWhiteSpace(EditNote) ? null : EditNote,
                    IsLent = SelectedCard!.IsLent,
                    LastLentAt = SelectedCard.LentAt,
                    LastLentStaff = SelectedCard.LastLentStaff
                };

                var success = await _cardRepository.UpdateAsync(card);
                if (success)
                {
                    StatusMessage = "更新しました";
                    await LoadCardsAsync();
                    CancelEdit();
                }
                else
                {
                    StatusMessage = "更新に失敗しました";
                }
            }
        }
    }

    /// <summary>
    /// 削除
    /// </summary>
    [RelayCommand]
    public async Task DeleteAsync()
    {
        if (SelectedCard == null) return;

        if (SelectedCard.IsLent)
        {
            StatusMessage = "貸出中のカードは削除できません";
            return;
        }

        using (BeginBusy("削除中..."))
        {
            var success = await _cardRepository.DeleteAsync(SelectedCard.CardIdm);
            if (success)
            {
                StatusMessage = "削除しました";
                await LoadCardsAsync();
                CancelEdit();
            }
            else
            {
                StatusMessage = "削除に失敗しました";
            }
        }
    }

    /// <summary>
    /// 編集をキャンセル
    /// </summary>
    [RelayCommand]
    public void CancelEdit()
    {
        IsEditing = false;
        IsNewCard = false;
        IsWaitingForCard = false;
        EditCardIdm = string.Empty;
        EditCardType = string.Empty;
        EditCardNumber = string.Empty;
        EditNote = string.Empty;
        StatusMessage = string.Empty;
    }

    /// <summary>
    /// カード読み取りイベント
    /// </summary>
    private void OnCardRead(object? sender, CardReadEventArgs e)
    {
        if (!IsWaitingForCard) return;

        // UIスレッドで実行
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            EditCardIdm = e.Idm;

            // カード種別を自動判定
            var detectedType = _cardTypeDetector.Detect(e.Idm);
            EditCardType = CardTypeDetector.GetDisplayName(detectedType);

            StatusMessage = $"カードを読み取りました: {EditCardType}";
            IsWaitingForCard = false;
        });
    }

    /// <summary>
    /// デバッグ用: カード読み取りをシミュレート
    /// </summary>
    [RelayCommand]
    public void SimulateCardRead()
    {
        if (!IsWaitingForCard) return;

        if (_cardReader is MockCardReader mockReader)
        {
            // 未使用のIDmを生成
            var newIdm = $"07FE{Guid.NewGuid().ToString("N")[..12].ToUpper()}";
            mockReader.SimulateCardRead(newIdm);
        }
    }

    /// <summary>
    /// クリーンアップ
    /// </summary>
    public void Cleanup()
    {
        _cardReader.CardRead -= OnCardRead;
    }
}
