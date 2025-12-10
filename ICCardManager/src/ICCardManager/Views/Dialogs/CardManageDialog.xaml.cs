using System.Windows;
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

        Loaded += async (s, e) =>
        {
            await _viewModel.InitializeAsync();
            // IDmが事前に設定されている場合は新規登録モードで開始
            if (!string.IsNullOrEmpty(_presetIdm))
            {
                _viewModel.StartNewCardWithIdm(_presetIdm);
            }
        };
        Closed += (s, e) => _viewModel.Cleanup();
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
