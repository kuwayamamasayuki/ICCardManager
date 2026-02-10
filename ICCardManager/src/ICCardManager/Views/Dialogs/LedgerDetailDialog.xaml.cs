using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ICCardManager.Dtos;
using ICCardManager.Models;
using ICCardManager.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ICCardManager.Views.Dialogs
{
    /// <summary>
    /// 利用履歴詳細ダイアログ
    /// 選択した履歴の詳細（個別の乗車記録）を表示・編集します。
    /// Issue #484: 乗車履歴の統合・分割機能に対応。
    /// </summary>
    public partial class LedgerDetailDialog : Window
    {
        private LedgerDetailViewModel? _viewModel;

        /// <summary>
        /// 保存が行われたかどうか（Issue #548: 履歴画面の即時反映用）
        /// </summary>
        public bool WasSaved { get; private set; }

        public LedgerDetailDialog()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 利用履歴詳細を表示（新しいViewModel使用）
        /// </summary>
        /// <param name="ledgerId">利用履歴ID</param>
        /// <param name="operatorIdm">操作者IDm（ログ記録用、オプション）</param>
        public async Task InitializeAsync(int ledgerId, string? operatorIdm = null)
        {
            _viewModel = App.Current.ServiceProvider.GetRequiredService<LedgerDetailViewModel>();
            DataContext = _viewModel;

            _viewModel.OnSaveCompleted = () =>
            {
                // Issue #548: 保存完了時にフラグを設定（履歴画面の即時反映用）
                WasSaved = true;
            };

            // Issue #634: 分割モード選択のコールバックを設定
            _viewModel.OnRequestSplitMode = () => ShowSplitModeDialog();

            await _viewModel.InitializeAsync(ledgerId, operatorIdm);
        }

        /// <summary>
        /// 履歴データで初期化（レガシー互換）
        /// </summary>
        /// <param name="ledger">表示する履歴データ</param>
        /// <remarks>
        /// 既存のコードとの互換性のために維持。
        /// 新しいコードではInitializeAsync(int ledgerId)を使用してください。
        /// </remarks>
        public void Initialize(LedgerDto ledger)
        {
            if (ledger == null) return;

            // 新しいViewModel方式で初期化
            _ = InitializeAsync(ledger.Id);
        }

        /// <summary>
        /// 分割線ボタンクリック時の処理（Issue #548: 分割線クリック方式UI）
        /// </summary>
        private void DividerButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.Tag is int index)
            {
                _viewModel?.ToggleDividerAt(index);
            }
        }

        /// <summary>
        /// 分割モード選択ダイアログを表示（Issue #634）
        /// </summary>
        private SplitSaveMode ShowSplitModeDialog()
        {
            var selectedMode = SplitSaveMode.Cancel;

            var dialog = new Window
            {
                Title = "保存方法の選択",
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow
            };

            var panel = new StackPanel { Margin = new Thickness(20) };

            var baseFontSize = (double)Application.Current.Resources["BaseFontSize"];
            var smallFontSize = (double)Application.Current.Resources["SmallFontSize"];

            panel.Children.Add(new TextBlock
            {
                Text = "グループが複数あります。どのように保存しますか？",
                TextWrapping = TextWrapping.Wrap,
                FontSize = baseFontSize,
                Margin = new Thickness(0, 0, 0, 8)
            });

            panel.Children.Add(new TextBlock
            {
                Text = "「別々の履歴に分割」… グループごとに別の履歴レコードを作成\n"
                     + "「摘要のみ更新」… 1つの履歴レコードのまま摘要を変更",
                TextWrapping = TextWrapping.Wrap,
                FontSize = smallFontSize,
                Foreground = System.Windows.Media.Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 20)
            });

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var splitButton = new Button
            {
                Content = "別々の履歴に分割",
                Padding = new Thickness(16, 8, 16, 8),
                Margin = new Thickness(0, 0, 8, 0),
                FontSize = baseFontSize
            };
            splitButton.Click += (s, e) =>
            {
                selectedMode = SplitSaveMode.FullSplit;
                dialog.Close();
            };

            var summaryButton = new Button
            {
                Content = "摘要のみ更新",
                Padding = new Thickness(16, 8, 16, 8),
                Margin = new Thickness(0, 0, 8, 0),
                FontSize = baseFontSize
            };
            summaryButton.Click += (s, e) =>
            {
                selectedMode = SplitSaveMode.SummaryOnly;
                dialog.Close();
            };

            var cancelButton = new Button
            {
                Content = "キャンセル",
                Padding = new Thickness(16, 8, 16, 8),
                IsCancel = true,
                FontSize = baseFontSize
            };
            cancelButton.Click += (s, e) =>
            {
                selectedMode = SplitSaveMode.Cancel;
                dialog.Close();
            };

            buttonPanel.Children.Add(splitButton);
            buttonPanel.Children.Add(summaryButton);
            buttonPanel.Children.Add(cancelButton);
            panel.Children.Add(buttonPanel);

            dialog.Content = panel;
            dialog.ShowDialog();

            return selectedMode;
        }

        /// <summary>
        /// 閉じるボタンクリック
        /// </summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel?.HasChanges == true)
            {
                var result = MessageBox.Show(
                    "保存されていない変更があります。破棄してよろしいですか？",
                    "確認",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            Close();
        }
    }
}
