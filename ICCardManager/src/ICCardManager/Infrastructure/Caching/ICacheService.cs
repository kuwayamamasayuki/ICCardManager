namespace ICCardManager.Infrastructure.Caching;

/// <summary>
/// キャッシュサービスインターフェース
/// </summary>
public interface ICacheService
{
    /// <summary>
    /// キャッシュからデータを取得、なければファクトリで生成してキャッシュ
    /// </summary>
    /// <typeparam name="T">データ型</typeparam>
    /// <param name="key">キャッシュキー</param>
    /// <param name="factory">データ生成ファクトリ</param>
    /// <param name="absoluteExpiration">絶対有効期限</param>
    /// <returns>キャッシュされたデータまたは新規生成されたデータ</returns>
    Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan absoluteExpiration);

    /// <summary>
    /// キャッシュからデータを取得
    /// </summary>
    /// <typeparam name="T">データ型</typeparam>
    /// <param name="key">キャッシュキー</param>
    /// <returns>キャッシュされたデータ（存在しない場合はdefault）</returns>
    T? Get<T>(string key);

    /// <summary>
    /// キャッシュにデータを設定
    /// </summary>
    /// <typeparam name="T">データ型</typeparam>
    /// <param name="key">キャッシュキー</param>
    /// <param name="value">キャッシュするデータ</param>
    /// <param name="absoluteExpiration">絶対有効期限</param>
    void Set<T>(string key, T value, TimeSpan absoluteExpiration);

    /// <summary>
    /// 指定したキーのキャッシュを無効化
    /// </summary>
    /// <param name="key">キャッシュキー</param>
    void Invalidate(string key);

    /// <summary>
    /// プレフィックスに一致するすべてのキャッシュを無効化
    /// </summary>
    /// <param name="prefix">キャッシュキープレフィックス</param>
    void InvalidateByPrefix(string prefix);

    /// <summary>
    /// 全キャッシュをクリア
    /// </summary>
    void Clear();
}
