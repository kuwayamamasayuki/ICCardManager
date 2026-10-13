#nullable enable
using System;
using ICCardManager.ViewModels;

namespace ICCardManager.Services
{
    /// <summary>
    /// <see cref="IReportOutputFolderStore"/> の実装。
    /// <c>%PROGRAMDATA%\ICCardManager\report_output_config.txt</c> を読み書きする（Issue #2266）
    /// </summary>
    /// <remarks>
    /// 読み書きの形式（BOM による文字コード判定・一時ファイル経由の置換）は、インストーラーと共有する
    /// ほかの設定ファイルと同じ <see cref="SettingsViewModel"/> の共通処理に委ねる。
    /// </remarks>
    public sealed class ReportOutputFolderStore : IReportOutputFolderStore
    {
        private readonly string _configFilePath;

        /// <summary>本番の置き場所（<c>AppDataPaths.RootDirectory</c> 配下）を使う</summary>
        public ReportOutputFolderStore()
            : this(SettingsViewModel.GetReportOutputConfigPath())
        {
        }

        /// <summary>置き場所を指定する（テスト用）</summary>
        internal ReportOutputFolderStore(string configFilePath)
        {
            _configFilePath = configFilePath ?? throw new ArgumentNullException(nameof(configFilePath));
        }

        /// <inheritdoc/>
        public string Load() => SettingsViewModel.LoadConfigFile(_configFilePath);

        /// <inheritdoc/>
        public void Save(string folder) => SettingsViewModel.SaveConfigFile(_configFilePath, folder ?? string.Empty);
    }
}
