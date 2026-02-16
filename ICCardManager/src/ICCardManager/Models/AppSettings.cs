using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
namespace ICCardManager.Models
{
/// <summary>
    /// アプリケーション設定モデル
    /// settingsテーブルのKVS形式を構造化して保持
    /// </summary>
    public class AppSettings
    {
        /// <summary>
        /// 残額警告閾値（円）
        /// </summary>
        public int WarningBalance { get; set; } = 10000;

        /// <summary>
        /// バックアップ先フォルダパス
        /// </summary>
        public string BackupPath { get; set; } = string.Empty;

        /// <summary>
        /// 文字サイズ
        /// </summary>
        public FontSizeOption FontSize { get; set; } = FontSizeOption.Medium;

        /// <summary>
        /// 最終VACUUM実行日
        /// </summary>
        public DateTime? LastVacuumDate { get; set; }

        /// <summary>
        /// メインウィンドウの位置・サイズ設定
        /// </summary>
        public WindowSettings MainWindowSettings { get; set; } = new();

        /// <summary>
        /// 音声モード
        /// </summary>
        public SoundMode SoundMode { get; set; } = SoundMode.Beep;

        /// <summary>
        /// トースト通知の表示位置
        /// </summary>
        public ToastPosition ToastPosition { get; set; } = ToastPosition.TopRight;

        /// <summary>
        /// 部署種別
        /// </summary>
        public DepartmentType DepartmentType { get; set; } = DepartmentType.MayorOffice;
    }

    /// <summary>
    /// ウィンドウの位置・サイズ設定
    /// </summary>
    public class WindowSettings
    {
        /// <summary>
        /// ウィンドウ左端のX座標
        /// </summary>
        public double? Left { get; set; }

        /// <summary>
        /// ウィンドウ上端のY座標
        /// </summary>
        public double? Top { get; set; }

        /// <summary>
        /// ウィンドウ幅
        /// </summary>
        public double? Width { get; set; }

        /// <summary>
        /// ウィンドウ高さ
        /// </summary>
        public double? Height { get; set; }

        /// <summary>
        /// 最大化状態かどうか
        /// </summary>
        public bool IsMaximized { get; set; }

        /// <summary>
        /// 有効な設定かどうか（一度でも保存されているか）
        /// </summary>
        public bool HasValidSettings => Left.HasValue && Top.HasValue && Width.HasValue && Height.HasValue;
    }

    /// <summary>
    /// 文字サイズオプション
    /// </summary>
    public enum FontSizeOption
    {
        /// <summary>
        /// 小
        /// </summary>
        Small,

        /// <summary>
        /// 中（デフォルト）
        /// </summary>
        Medium,

        /// <summary>
        /// 大
        /// </summary>
        Large,

        /// <summary>
        /// 特大
        /// </summary>
        ExtraLarge
    }

    /// <summary>
    /// 音声モードオプション
    /// </summary>
    public enum SoundMode
    {
        /// <summary>
        /// 効果音のみ（ピッ/ピピッ）
        /// </summary>
        Beep,

        /// <summary>
        /// 音声（男性）
        /// </summary>
        VoiceMale,

        /// <summary>
        /// 音声（女性）
        /// </summary>
        VoiceFemale,

        /// <summary>
        /// 無し
        /// </summary>
        None
    }

    /// <summary>
    /// 部署種別オプション
    /// </summary>
    public enum DepartmentType
    {
        /// <summary>
        /// 市長事務部局（チャージ摘要: 役務費によりチャージ）
        /// </summary>
        MayorOffice,

        /// <summary>
        /// 企業会計部局（チャージ摘要: 旅費によりチャージ）
        /// </summary>
        EnterpriseAccount
    }

    /// <summary>
    /// トースト通知の表示位置オプション
    /// </summary>
    public enum ToastPosition
    {
        /// <summary>
        /// 右上（デフォルト）
        /// </summary>
        TopRight,

        /// <summary>
        /// 左上
        /// </summary>
        TopLeft,

        /// <summary>
        /// 右下
        /// </summary>
        BottomRight,

        /// <summary>
        /// 左下
        /// </summary>
        BottomLeft
    }
}
