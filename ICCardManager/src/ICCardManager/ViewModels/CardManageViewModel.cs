using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Models;
using ICCardManager.Services;

namespace ICCardManager.ViewModels
{
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

            // MainViewModelでの未登録カード処理を抑制
            App.IsCardRegistrationActive = true;
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

            // カード種別はユーザーに手動選択させる（IDmからの自動判定は技術的に不可能なため）
            // デフォルトはnimoca（利用頻度が最も高いため）
            EditCardType = "nimoca";

            EditCardNumber = string.Empty;
            EditNote = string.Empty;
            StatusMessage = "カードを読み取りました。カード種別を確認してください。";
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
            // 入力値をサニタイズ
            var sanitizedCardNumber = InputSanitizer.SanitizeCardNumber(EditCardNumber);
            var sanitizedNote = InputSanitizer.SanitizeNote(EditNote);

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

            var numberResult = _validationService.ValidateCardNumber(sanitizedCardNumber);
            if (!numberResult)
            {
                StatusMessage = numberResult.ErrorMessage!;
                return;
            }

            if (string.IsNullOrWhiteSpace(sanitizedCardNumber))
            {
                // 自動採番
                sanitizedCardNumber = await _cardRepository.GetNextCardNumberAsync(EditCardType);
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
                        CardNumber = sanitizedCardNumber,
                        Note = string.IsNullOrWhiteSpace(sanitizedNote) ? null : sanitizedNote
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
                        CardNumber = sanitizedCardNumber,
                        Note = string.IsNullOrWhiteSpace(sanitizedNote) ? null : sanitizedNote,
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

            // ICカード登録モードを解除
            App.IsCardRegistrationActive = false;
        }

        /// <summary>
        /// カード読み取りイベント
        /// </summary>
        private void OnCardRead(object sender, CardReadEventArgs e)
        {
            if (!IsWaitingForCard) return;

            // UIスレッドで実行
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                EditCardIdm = e.Idm;

                // カード種別はユーザーに手動選択させる（IDmからの自動判定は技術的に不可能なため）
                // デフォルトはnimoca（利用頻度が最も高いため）
                EditCardType = "nimoca";

                StatusMessage = "カードを読み取りました。カード種別を確認してください。";
                IsWaitingForCard = false;

                // カード読み取り完了後、フラグを解除
                App.IsCardRegistrationActive = false;
            });
        }

        /// <summary>
        /// 選択カード変更時の処理
        /// </summary>
        partial void OnSelectedCardChanged(CardDto? value)
        {
            // 新規登録モード中は選択変更を無視
            if (IsNewCard) return;

            // カードが選択された場合、編集中なら選択したカードの情報で更新
            if (value != null && IsEditing)
            {
                EditCardIdm = value.CardIdm;
                EditCardType = value.CardType;
                EditCardNumber = value.CardNumber;
                EditNote = value.Note ?? string.Empty;
                StatusMessage = string.Empty;
            }
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
                var newIdm = $"07FE{Guid.NewGuid().ToString("N").Substring(0, 12).ToUpper()}";
                mockReader.SimulateCardRead(newIdm);
            }
        }

        /// <summary>
        /// クリーンアップ
        /// </summary>
        public void Cleanup()
        {
            _cardReader.CardRead -= OnCardRead;

            // ダイアログ終了時にフラグを解除
            App.IsCardRegistrationActive = false;
        }
    }
}
