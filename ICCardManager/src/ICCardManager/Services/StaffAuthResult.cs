#nullable enable
using System;
using System.Threading;

namespace ICCardManager.Services
{
    /// <summary>
    /// 職員認証の結果。認証した 1 操作の間だけ有効な操作者セッションのスコープを兼ねる。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #2260: 認証を要求する画面は、この結果を <see cref="Enter"/> してから <c>using</c> で受けて操作を行う。
    /// <see cref="Enter"/> がその画面の非同期の流れで操作者セッション（<see cref="ICurrentOperatorContext"/>）を開始し、
    /// 破棄でセッションが終わって以後の操作ログは GUI 操作として記録される。成功・失敗・キャンセル・例外のいずれでも
    /// 操作の完了時に破棄されるよう、必ず次の形で書くこと（静的検査 <c>StaffAuthScopeConventionTests</c>）。
    /// </para>
    /// <code>
    /// using var authResult = (await _staffAuthService.RequestAuthenticationAsync("職員の削除"))?.Enter();
    /// if (authResult == null)
    /// {
    ///     return;
    /// }
    /// // ここからメソッドを抜けるまで（この流れの中）の書き込みが、認証した職員の操作として記録される
    /// </code>
    /// <para>
    /// セッションの開始を認証サービスの内側ではなく <see cref="Enter"/> に置くのは、セッションが非同期の流れ
    /// （<see cref="System.Threading.AsyncLocal{T}"/>）に結び付いており、<c>async</c> メソッドの内側で開始した値は
    /// 呼び出し元へ戻らないため。<see cref="Enter"/> は同期メソッドなので、呼び出した画面のメソッドの流れに残る。
    /// </para>
    /// <para>
    /// 旧実装は認証の成功時にセッションを開始するだけで終える経路が無く、5 分間残ったセッションを
    /// 認証の無い操作（カードの編集・払い戻し・明細の保存など）が拾っていた。
    /// </para>
    /// </remarks>
    public sealed class StaffAuthResult : IDisposable
    {
        private readonly ICurrentOperatorContext? _operatorContext;
        private IDisposable? _session;
        private bool _entered;

        /// <summary>
        /// 操作者セッションを伴わない結果を作る（テストの代役など）。<see cref="Enter"/> は何もしない。
        /// </summary>
        public StaffAuthResult()
        {
        }

        /// <summary>
        /// 認証した職員の結果を作る。セッションは <see cref="Enter"/> で開始し、破棄で終了する。
        /// </summary>
        internal StaffAuthResult(string idm, string staffName, ICurrentOperatorContext operatorContext)
        {
            Idm = idm;
            StaffName = staffName;
            _operatorContext = operatorContext ?? throw new ArgumentNullException(nameof(operatorContext));
        }

        /// <summary>
        /// 認証された職員のIDm
        /// </summary>
        public string Idm { get; set; } = string.Empty;

        /// <summary>
        /// 認証された職員の氏名
        /// </summary>
        public string StaffName { get; set; } = string.Empty;

        /// <summary>
        /// 呼び出した非同期の流れで、認証した職員の操作者セッションを開始する（2 回目以降は何もしない）。
        /// </summary>
        /// <returns>この結果自身（<c>using var r = (await …)?.Enter();</c> と書けるように）。</returns>
        /// <remarks>
        /// 認証を要求した画面のメソッドの中で呼ぶこと。<c>async</c> のヘルパーの内側で呼ぶと、
        /// そのヘルパーから戻った画面のメソッドにはセッションが見えない。
        /// </remarks>
        public StaffAuthResult Enter()
        {
            if (_entered)
            {
                return this;
            }

            _entered = true;
            if (_operatorContext != null)
            {
                _session = _operatorContext.BeginSession(Idm, StaffName);
            }

            return this;
        }

        /// <summary>
        /// 同じ職員の、次の 1 操作のための認証結果を作る（Issue #2494）。操作者セッションはまだ開始しない。
        /// </summary>
        /// <param name="staffName">記録する氏名（保持している間に氏名が変わっていれば、確かめ直した現在の氏名）</param>
        /// <returns>
        /// 新しい認証結果。この結果と同じく、その場で <see cref="Enter"/> して <c>using</c> で受けること
        /// （静的検査 <c>StaffAuthScopeConventionTests</c>）。
        /// </returns>
        /// <remarks>
        /// <para>
        /// 職員管理画面（F2）は、最初に認証した職員を画面が閉じるまで保持し、2 回目以降の登録・変更・復元をその職員の
        /// 操作として記録する。この結果自身は最初の操作の終わりに破棄され（<see cref="Enter"/> は 1 回しか効かない）、
        /// 操作と操作の間に操作者セッションを残さない（Issue #2260）。次の操作は、ここで作る新しい結果で
        /// その操作の間だけセッションを開始する。
        /// </para>
        /// <para>
        /// 認証の有効期限は持たない。保持をいつ破棄するか（無操作・「認証を解除」・画面を閉じる・職員が無効になった）は
        /// 保持している画面が決める。
        /// </para>
        /// </remarks>
        internal StaffAuthResult ReissueForNextOperation(string staffName)
        {
            if (_operatorContext == null)
            {
                return new StaffAuthResult { Idm = Idm, StaffName = staffName };
            }

            return new StaffAuthResult(Idm, staffName, _operatorContext);
        }

        /// <summary>
        /// 認証した操作の終了。操作者セッションを終える（2 回目以降の呼び出しは何もしない）。
        /// </summary>
        public void Dispose()
        {
            Interlocked.Exchange(ref _session, null)?.Dispose();
        }
    }
}
