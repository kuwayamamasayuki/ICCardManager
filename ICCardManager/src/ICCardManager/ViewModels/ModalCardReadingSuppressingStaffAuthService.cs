#nullable enable
using System;
using System.Threading.Tasks;
using ICCardManager.Services;

namespace ICCardManager.ViewModels;

/// <summary>
/// 職員認証（認証ダイアログ）を求めている間、メイン画面のカード読み取りを抑制する <see cref="IStaffAuthService"/> の包み
/// （Issue #2484 / PR #2491 のレビュー 3 回目）。
/// </summary>
/// <remarks>
/// <para>
/// 認証ダイアログ（<c>StaffAuthDialog</c>）は自分でも抑制を取る（<c>CardReadingSource.Authentication</c>）が、取るのは
/// <c>Loaded</c> で、<c>ShowDialog()</c> の開始から <c>Loaded</c> までの入れ子のメッセージポンプの間はメイン画面が無抑制だった。
/// 「モーダル全般で止める」方針に合わせ、認証を求める呼び出しの全体をメイン画面の抑制で囲む。
/// </para>
/// <para>
/// 呼び出しごとに <c>using</c> を書かず、受け取った時点で包むのは <see cref="ModalCardReadingSuppressingNavigationService"/> と
/// 同じ理由（認証を求める操作を足すたびに書き忘れが起きる）。止めるのはメイン画面の処理だけで、認証ダイアログは
/// カードリーダーを直接購読しているので認証は従来どおり動く。
/// </para>
/// </remarks>
internal sealed class ModalCardReadingSuppressingStaffAuthService : IStaffAuthService
{
    private readonly IStaffAuthService _inner;
    private readonly Func<IDisposable> _beginSuppression;

    /// <param name="inner">実際に認証を求める <see cref="IStaffAuthService"/></param>
    /// <param name="beginSuppression">認証を求めている間だけ保持する抑制スコープを開く関数（呼び出しのたびに呼ぶ）</param>
    public ModalCardReadingSuppressingStaffAuthService(IStaffAuthService inner, Func<IDisposable> beginSuppression)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _beginSuppression = beginSuppression ?? throw new ArgumentNullException(nameof(beginSuppression));
    }

    /// <inheritdoc/>
    public async Task<StaffAuthResult?> RequestAuthenticationAsync(string operationDescription)
    {
        using (_beginSuppression())
        {
            return await _inner.RequestAuthenticationAsync(operationDescription);
        }
    }
}
