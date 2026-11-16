#nullable enable
using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Models;
using ICCardManager.Services;

namespace ICCardManager.ViewModels
{
    /// <summary>
    /// 職員管理画面の職員証の認証の保持（Issue #2494）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 職員の登録・変更・復元は、この画面で最初に認証が要ったときだけ職員証の認証を求め、認証した職員を画面が閉じるまで
    /// 保持する。2 回目以降は認証ダイアログを出さず、保持している職員の操作として操作ログへ記録する。
    /// </para>
    /// <para>
    /// <b>保持するのは「誰か」だけで、操作者セッションではない</b>。セッションは操作ごとに
    /// <see cref="StaffAuthResult.ReissueForNextOperation"/> で作り直した結果をその場で <c>Enter()</c> して開始し、
    /// 操作が終わったら破棄する。操作と操作の間にセッションを残すと、この画面の外の認証の無い操作が拾う（Issue #2260）。
    /// </para>
    /// <para>
    /// 画面単位で保持すると、認証した職員が席を離れた後の別の職員の操作が、認証した職員の名前で記録され得る（#2260 と
    /// 同じ取り違えを、この画面を開いている間に限って許す）。そのため次の 4 つで保持を短くする。
    /// </para>
    /// <list type="number">
    /// <item><description>画面に「認証中: 氏名」を常に表示する（<see cref="HeldAuthenticationText"/>）</description></item>
    /// <item><description>「認証を解除」で破棄できる（<see cref="ReleaseHeldAuthenticationCommand"/>）</description></item>
    /// <item><description>
    /// 無操作が <see cref="HeldAuthenticationIdleTimeoutSeconds"/> 秒続いたら破棄する（最後の操作の時刻からの経過時間で判定）。無操作とは、この画面へのキー入力・
    /// マウスのボタン・ホイールの操作（View が <see cref="NotifyUserActivity"/> を呼ぶ）と、保持した認証を使った操作が
    /// 無いこと
    /// </description></item>
    /// <item><description>画面を閉じたら破棄する（<see cref="Cleanup"/>）</description></item>
    /// </list>
    /// <para>
    /// 保持を使う前に、保持している職員が削除されていない同じ IDm の職員として登録されているかを DB で確かめ、
    /// 登録されていなければ（他のパソコンでの削除・職員証の差し替えを含む）破棄して認証を求め直す。
    /// 削除と職員証の差し替えはこの保持を使わず、変えもしない（確認の後に毎回認証を求める）。
    /// </para>
    /// </remarks>
    public partial class StaffManageViewModel
    {
        /// <summary>
        /// 無操作のまま保持した認証を破棄するまでの秒数（Issue #2494。5 分。設定にはしない）
        /// </summary>
        internal const int HeldAuthenticationIdleTimeoutSeconds = 300;

        /// <summary>認証を保持していないときの表示</summary>
        internal const string NoHeldAuthenticationText =
            "職員証の認証: なし（登録・変更・復元を保存するときに、登録済みの職員の職員証で認証してください）";

        /// <summary>
        /// 認証を保持しているときの表示を組み立てる
        /// </summary>
        /// <param name="staffName">保持している職員の氏名</param>
        internal static string BuildHeldAuthenticationText(string staffName)
            => $"認証中: {staffName}（この画面での登録・変更・復元は、{staffName} の操作として記録します。" +
               $"操作が無いまま{HeldAuthenticationIdleTimeoutSeconds / 60}分たつか、画面を閉じると解除します）";

        /// <summary>
        /// 保持している認証（最初の操作で受け取った結果。<c>Enter()</c> はせず、次の操作の結果を作るためだけに使う）
        /// </summary>
        private StaffAuthResult? _heldAuthentication;

        /// <summary>無操作の時間を確かめるタイマー（初めて保持するときに作る。1 秒ごと）</summary>
        private ITimer? _heldAuthenticationTimer;

        /// <summary>
        /// 最後に操作があった時刻（<see cref="_clock"/> の時刻）
        /// </summary>
        /// <remarks>
        /// 無操作の時間は Tick の回数ではなく、この時刻からの経過時間で判定する。PC がスリープから復帰しても
        /// <c>DispatcherTimer</c> は取りこぼした Tick を再生しないので、回数で数えると実時間で 5 分を超えても保持が残る
        /// （レビュー 1 回目で検出）。
        /// </remarks>
        private DateTime _heldAuthenticationLastActivityAt;

        /// <summary>
        /// 画面を閉じた（<see cref="Cleanup"/> を呼んだ）か。閉じた後は保持も認証の要求もしない
        /// </summary>
        /// <remarks>
        /// 保持している職員の確認（DB の読み取り）を待つ間に画面を閉じると、確認から戻った流れが認証ダイアログへ進み、
        /// 認証すると閉じた画面のタイマーを動かし直していた（レビュー 1 回目で検出）。
        /// </remarks>
        private bool _isHeldAuthenticationClosed;

        /// <summary>
        /// 保持している職員の氏名（保持していなければ null）
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsAuthenticationHeld))]
        [NotifyPropertyChangedFor(nameof(HeldAuthenticationText))]
        [NotifyCanExecuteChangedFor(nameof(ReleaseHeldAuthenticationCommand))]
        private string? _heldOperatorName;

        /// <summary>職員証の認証を保持しているか</summary>
        public bool IsAuthenticationHeld => HeldOperatorName != null;

        /// <summary>画面に常に表示する認証の状態</summary>
        public string HeldAuthenticationText =>
            HeldOperatorName is { } name ? BuildHeldAuthenticationText(name) : NoHeldAuthenticationText;

        /// <summary>
        /// 保持している認証を、次の 1 操作のための認証結果として取り出す（保持していない・使えないなら null）
        /// </summary>
        /// <remarks>
        /// <para>
        /// 返した結果は呼び出し側がその場で <c>Enter()</c> して <c>using</c> で受ける（静的検査）。
        /// </para>
        /// <para>
        /// 保持している職員が削除されていない同じ IDm の職員として登録されているかを確かめ（キャッシュを通さない読み取り）、
        /// 登録されていなければ保持を破棄して null を返す（呼び出し側は認証を求め直す）。確認の間は処理中にする
        /// （処理中の外で DB を待つと、その間に取り消したフォームの内容が書き込まれ得る。Issue #2263）。
        /// </para>
        /// </remarks>
        private async Task<StaffAuthResult?> TakeHeldAuthenticationAsync()
        {
            var held = _heldAuthentication;
            if (held == null || _isHeldAuthenticationClosed)
            {
                return null;
            }

            // 使う前に期限を確かめる。スリープ復帰直後は Tick がまだ来ておらず、期限の切れた保持が残っている
            if (ReleaseHeldAuthenticationIfIdleExpired())
            {
                return null;
            }

            Staff? current;
            using (BeginBusy("確認中..."))
            {
                current = await _staffRepository.GetByIdmAsync(held.Idm);
            }

            // 確かめている間に「認証を解除」・無操作・画面を閉じたことで破棄された、または別の認証に置き換わったなら使わない
            if (_isHeldAuthenticationClosed || !ReferenceEquals(_heldAuthentication, held))
            {
                return null;
            }

            if (current == null)
            {
                // 削除された・職員証が差し替えられた（他のパソコンの操作を含む）。認証を求め直す
                ReleaseHeldAuthentication();
                return null;
            }

            // 確かめている間に期限が来ていたら使わない（延長しない）
            if (ReleaseHeldAuthenticationIfIdleExpired())
            {
                return null;
            }

            var name = !string.IsNullOrWhiteSpace(current.Name) ? current.Name : held.StaffName;
            HeldOperatorName = name;
            ResetHeldAuthenticationIdleCountdown();
            return held.ReissueForNextOperation(name);
        }

        /// <summary>画面を閉じた後か（閉じた後は認証ダイアログへ進まない）</summary>
        private bool IsHeldAuthenticationClosed => _isHeldAuthenticationClosed;

        /// <summary>
        /// 保持している認証があれば、認証ダイアログを出さずにその職員の操作として <paramref name="write"/> を行う
        /// </summary>
        /// <returns>保持している認証で行ったら true。保持していない・使えないなら false（何もしていない）</returns>
        /// <remarks>
        /// セッションはこのメソッドの流れで開始し、<paramref name="write"/> はその内側で実行するため、書き込みの操作ログには
        /// 保持している職員が記録される。メソッドを抜けるとセッションは終わる。
        /// </remarks>
        private async Task<bool> TryRunWithHeldAuthenticationAsync(Func<Task> write)
        {
            using var heldAuthResult = (await TakeHeldAuthenticationAsync())?.Enter();
            if (heldAuthResult == null)
            {
                return false;
            }

            try
            {
                await write();
            }
            finally
            {
                // 操作の終わりも「操作があった」とみなす（書き込みに時間がかかっても、その直後に破棄しない）
                ResetHeldAuthenticationIdleCountdown();
            }

            return true;
        }

        /// <summary>
        /// 認証した職員を、画面が閉じるまで（無操作・「認証を解除」で破棄されるまで）保持する
        /// </summary>
        private void HoldAuthentication(StaffAuthResult authResult)
        {
            if (_isHeldAuthenticationClosed)
            {
                // 閉じた画面では保持しない（タイマーを動かし直すと、外したハンドラーのまま画面ごと残る）
                return;
            }

            _heldAuthentication = authResult;
            HeldOperatorName = authResult.StaffName;
            // 新しく保持するので、期限の判定を経ずに今から数える（前の保持の期限は関係ない）
            _heldAuthenticationLastActivityAt = _clock.Now;

            var timer = _heldAuthenticationTimer ??= CreateHeldAuthenticationTimer();
            timer.Stop();
            timer.Start();
        }

        /// <summary>
        /// 保持している職員証の認証を破棄する（「認証を解除」。次の登録・変更・復元では認証を求める）
        /// </summary>
        [RelayCommand(CanExecute = nameof(IsAuthenticationHeld))]
        public void ReleaseHeldAuthentication()
        {
            _heldAuthentication = null;
            _heldAuthenticationTimer?.Stop();
            HeldOperatorName = null;
        }

        /// <summary>
        /// この画面への操作（キー入力・マウスのボタン・ホイール）があったことを知らせる。無操作の時間を数え直す
        /// </summary>
        public void NotifyUserActivity()
        {
            ResetHeldAuthenticationIdleCountdown();
        }

        /// <summary>
        /// 操作があった時刻を記録して、無操作の時間を数え直す（保持していなければ何もしない）
        /// </summary>
        /// <remarks>
        /// 数え直す前に期限を確かめ、切れていれば延長せずに破棄する。スリープ復帰の直後（Tick が来る前）に別の職員が
        /// キーを押すと、切れているはずの保持が延びて、その職員の操作が認証した職員の名前で記録されていた
        /// （fail-open。自動のセキュリティレビューで検出）。
        /// </remarks>
        private void ResetHeldAuthenticationIdleCountdown()
        {
            if (_heldAuthentication == null || ReleaseHeldAuthenticationIfIdleExpired())
            {
                return;
            }

            _heldAuthenticationLastActivityAt = _clock.Now;
        }

        /// <summary>
        /// 無操作の期限が切れていれば保持を破棄する（期限の判定はここ 1 か所）
        /// </summary>
        /// <returns>破棄した（期限が切れていた）なら true。保持していない・期限内なら false</returns>
        /// <remarks>
        /// 最後の操作の時刻からの経過時間が <see cref="HeldAuthenticationIdleTimeoutSeconds"/> 秒以上なら期限切れ。
        /// <b>経過時間が負（時計が戻った）ときも期限切れとして扱う</b> — 経過を確かめられないまま保持を延ばさない
        /// （手動の時刻合わせ・時刻同期で戻った場合は、認証し直せば済む安全側へ倒す）。
        /// 判定は Tick・操作の記録・保持の使用のすべてで、延長や使用の前に行う。
        /// </remarks>
        private bool ReleaseHeldAuthenticationIfIdleExpired()
        {
            if (_heldAuthentication == null)
            {
                return false;
            }

            var idle = _clock.Now - _heldAuthenticationLastActivityAt;
            if (idle >= TimeSpan.Zero && idle < TimeSpan.FromSeconds(HeldAuthenticationIdleTimeoutSeconds))
            {
                return false;
            }

            ReleaseHeldAuthentication();
            return true;
        }

        private ITimer CreateHeldAuthenticationTimer()
        {
            var timer = _timerFactory.Create();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += OnHeldAuthenticationTimerTick;
            return timer;
        }

        private void OnHeldAuthenticationTimerTick(object? sender, EventArgs e)
        {
            if (_heldAuthentication == null)
            {
                _heldAuthenticationTimer?.Stop();
                return;
            }

            // 経過時間で判定する（スリープからの復帰で Tick が飛んでも、最初の Tick で破棄する）
            ReleaseHeldAuthenticationIfIdleExpired();
        }

        /// <summary>
        /// 画面を閉じるときに保持を破棄し、タイマーを止める（<see cref="Cleanup"/> から呼ぶ）
        /// </summary>
        private void CleanupHeldAuthentication()
        {
            _isHeldAuthenticationClosed = true;
            ReleaseHeldAuthentication();
            if (_heldAuthenticationTimer != null)
            {
                _heldAuthenticationTimer.Stop();
                _heldAuthenticationTimer.Tick -= OnHeldAuthenticationTimerTick;
            }
        }
    }
}
