namespace ICCardManager.Common;

/// <summary>
/// アプリケーションの状態
/// </summary>
public enum AppState
{
    /// <summary>職員証タッチ待ち</summary>
    WaitingForStaffCard,

    /// <summary>交通系ICカードタッチ待ち</summary>
    WaitingForIcCard,

    /// <summary>処理中</summary>
    Processing
}

/// <summary>
/// 交通系ICカードの種別
/// </summary>
public enum CardType
{
    /// <summary>Suica</summary>
    Suica,

    /// <summary>PASMO</summary>
    PASMO,

    /// <summary>ICOCA</summary>
    ICOCA,

    /// <summary>PiTaPa</summary>
    PiTaPa,

    /// <summary>nimoca</summary>
    Nimoca,

    /// <summary>SUGOCA</summary>
    SUGOCA,

    /// <summary>はやかけん</summary>
    Hayakaken,

    /// <summary>Kitaca</summary>
    Kitaca,

    /// <summary>TOICA</summary>
    TOICA,

    /// <summary>manaca</summary>
    Manaca,

    /// <summary>その他・不明</summary>
    Unknown
}

/// <summary>
/// 文字サイズ設定
/// </summary>
public enum FontSizeOption
{
    /// <summary>小</summary>
    Small,

    /// <summary>中</summary>
    Medium,

    /// <summary>大</summary>
    Large,

    /// <summary>特大</summary>
    ExtraLarge
}
