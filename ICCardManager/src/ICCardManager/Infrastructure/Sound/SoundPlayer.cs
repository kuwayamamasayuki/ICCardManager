using System.IO;
using System.Media;

namespace ICCardManager.Infrastructure.Sound;

/// <summary>
/// 効果音再生サービス
/// </summary>
public class SoundPlayer : ISoundPlayer
{
    private readonly Dictionary<SoundType, string> _soundFiles;
    private readonly Dictionary<SoundType, System.Media.SoundPlayer?> _players;
    private bool _disposed;

    /// <summary>
    /// 効果音ファイルのベースパス
    /// </summary>
    private string SoundsBasePath => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "Resources", "Sounds");

    public bool IsEnabled { get; set; } = true;

    public SoundPlayer()
    {
        _soundFiles = new Dictionary<SoundType, string>
        {
            { SoundType.Lend, "lend.wav" },
            { SoundType.Return, "return.wav" },
            { SoundType.Error, "error.wav" },
            { SoundType.Warning, "warning.wav" }
        };

        _players = new Dictionary<SoundType, System.Media.SoundPlayer?>();

        // プレイヤーを事前にロード
        LoadPlayers();
    }

    /// <summary>
    /// 効果音プレイヤーをロード
    /// </summary>
    private void LoadPlayers()
    {
        foreach (var (soundType, fileName) in _soundFiles)
        {
            var filePath = Path.Combine(SoundsBasePath, fileName);
            if (File.Exists(filePath))
            {
                try
                {
                    var player = new System.Media.SoundPlayer(filePath);
                    player.Load();
                    _players[soundType] = player;
                }
                catch
                {
                    _players[soundType] = null;
                }
            }
            else
            {
                _players[soundType] = null;
            }
        }
    }

    /// <inheritdoc/>
    public void Play(SoundType soundType)
    {
        if (!IsEnabled)
        {
            return;
        }

        if (_players.TryGetValue(soundType, out var player) && player != null)
        {
            try
            {
                player.Play();
            }
            catch
            {
                // 再生失敗時はシステムビープで代替
                PlaySystemBeep(soundType);
            }
        }
        else
        {
            // ファイルがない場合はシステムビープで代替
            PlaySystemBeep(soundType);
        }
    }

    /// <inheritdoc/>
    public Task PlayAsync(SoundType soundType)
    {
        return Task.Run(() => Play(soundType));
    }

    /// <summary>
    /// システムビープ音を再生（フォールバック用）
    /// </summary>
    private void PlaySystemBeep(SoundType soundType)
    {
        try
        {
            switch (soundType)
            {
                case SoundType.Lend:
                    // 貸出：短い高音
                    Console.Beep(1000, 100);
                    break;

                case SoundType.Return:
                    // 返却：短い高音×2
                    Console.Beep(1000, 100);
                    Thread.Sleep(50);
                    Console.Beep(1000, 100);
                    break;

                case SoundType.Error:
                    // エラー：長い低音
                    Console.Beep(500, 500);
                    break;

                case SoundType.Warning:
                    // 警告：中音
                    Console.Beep(750, 200);
                    break;
            }
        }
        catch
        {
            // Console.Beepが使えない環境では無視
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                foreach (var player in _players.Values)
                {
                    player?.Dispose();
                }
                _players.Clear();
            }
            _disposed = true;
        }
    }
}
