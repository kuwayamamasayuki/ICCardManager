using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Models;

namespace ICCardManager.Dtos
{
/// <summary>
    /// 設定情報DTO
    /// ViewModelで使用する設定表示用オブジェクト
    /// </summary>
    public class SettingsDto
    {
        /// <summary>
        /// 残額警告閾値（円）
        /// </summary>
        public int WarningBalance { get; set; }

        /// <summary>
        /// バックアップ先フォルダパス
        /// </summary>
        public string BackupPath { get; set; } = string.Empty;

        /// <summary>
        /// 文字サイズ
        /// </summary>
        public FontSizeOption FontSize { get; set; }

        #region 表示用プロパティ

        /// <summary>
        /// 表示用: 残額警告閾値
        /// </summary>
        public string WarningBalanceDisplay => $"{WarningBalance:N0}円";

        /// <summary>
        /// 表示用: 文字サイズ
        /// </summary>
        public string FontSizeDisplay => FontSize switch
        {
            FontSizeOption.Small => "小",
            FontSizeOption.Medium => "中（標準）",
            FontSizeOption.Large => "大",
            FontSizeOption.ExtraLarge => "特大",
            _ => "中（標準）"
        };

        #endregion
    }
}
