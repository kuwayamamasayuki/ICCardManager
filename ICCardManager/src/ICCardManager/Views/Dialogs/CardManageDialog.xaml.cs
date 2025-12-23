using System.Windows;
using ICCardManager.Common;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs;

/// <summary>
/// カード管理ダイアログ
/// </summary>
public partial class CardManageDialog : Window
{
    private readonly CardManageViewModel _viewModel;
    private string? _presetIdm;

    public CardManageDialog(CardManageViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = _viewModel;

        Loaded += CardManageDialog_Loaded;
        Closed += (s, e) => _viewModel.Cleanup();
    }

    private async void CardManageDialog_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.InitializeAsync();
            // IDmが事前に設定されている場合は新規登録モードで開始
            if (!string.IsNullOrEmpty(_presetIdm))
            {
                _viewModel.StartNewCardWithIdm(_presetIdm);
            }
        }
        catch (Exception ex)
        {
            ErrorDialogHelper.ShowError(ex, "初期化エラー");
        }
    }

    /// <summary>
    /// IDmを指定して新規登録モードで初期化
    /// </summary>
    /// <param name="idm">カードのIDm</param>
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
}
