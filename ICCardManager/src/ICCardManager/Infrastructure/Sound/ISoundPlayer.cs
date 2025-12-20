using ICCardManager.Models;

namespace ICCardManager.Infrastructure.Sound;

/// <summary>
/// 効果音種別
/// </summary>
public enum SoundType
{
    /// <summary>
    /// 貸出時（ピッ）
    /// </summary>
    Lend,

    /// <summary>
    /// 返却時（ピピッ）
    /// </summary>
    Return,

    /// <summary>
    /// エラー時（ピー）
    /// </summary>
    Error,

    /// <summary>
    /// 警告時
    /// </summary>
    Warning
}

/// <summary>
/// 効果音再生インターフェース
/// </summary>
public interface ISoundPlayer : IDisposable
{
    /// <summary>
    /// 効果音を再生
    /// </summary>
    /// <param name="soundType">効果音種別</param>
    void Play(SoundType soundType);

    /// <summary>
    /// 効果音を非同期で再生
    /// </summary>
    /// <param name="soundType">効果音種別</param>
    Task PlayAsync(SoundType soundType);

    /// <summary>
    /// 音声を有効にするかどうか
    /// </summary>
    bool IsEnabled { get; set; }

    /// <summary>
    /// 音声モード
    /// </summary>
    SoundMode SoundMode { get; set; }
}
