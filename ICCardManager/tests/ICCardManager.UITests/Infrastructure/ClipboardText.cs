using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ICCardManager.UITests.Infrastructure
{
    /// <summary>
    /// クリップボードのテキストを読み書きする（Win32 API。Issue #2196）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// UI テストは利用者のデスクトップで走るため、クリップボードを書き換えるテストは<b>テストの前の内容を退避して戻す</b>
    /// （<see cref="Preserve"/>）。戻さないと、テストを流しただけで利用者がコピーしていた内容が消える。
    /// </para>
    /// <para>
    /// UITests は WinForms / WPF のクリップボード API を参照していない（STA スレッドも要る）ので、Win32 を直接呼ぶ。
    /// クリップボードは他のプロセスが開いている間は開けないため、少し待って数回やり直す。
    /// </para>
    /// </remarks>
    internal static class ClipboardText
    {
        private const uint CfUnicodeText = 13;
        private const uint GmemMoveable = 0x0002;

        /// <summary>テキストを読む。テキストが無ければ null。</summary>
        public static string? Read() =>
            WithClipboard(() =>
            {
                var handle = GetClipboardData(CfUnicodeText);
                if (handle == IntPtr.Zero)
                {
                    return null;
                }

                var pointer = GlobalLock(handle);
                try
                {
                    return pointer == IntPtr.Zero ? null : Marshal.PtrToStringUni(pointer);
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            });

        /// <summary>クリップボードを空にし、<paramref name="text"/> が null でなければそれを置く。</summary>
        public static void Write(string? text) =>
            WithClipboard<object?>(() =>
            {
                EmptyClipboard();
                if (text == null)
                {
                    return null;
                }

                var bytes = (text.Length + 1) * 2;
                var memory = GlobalAlloc(GmemMoveable, (UIntPtr)bytes);
                var pointer = GlobalLock(memory);
                try
                {
                    var chars = (text + "\0").ToCharArray();
                    Marshal.Copy(chars, 0, pointer, chars.Length);
                }
                finally
                {
                    GlobalUnlock(memory);
                }

                // 成功したら所有権はクリップボードへ移る（解放しない）
                SetClipboardData(CfUnicodeText, memory);
                return null;
            });

        /// <summary>いまのテキストを退避し、Dispose で戻す。</summary>
        public static IDisposable Preserve() => new Preserved(Read());

        private sealed class Preserved : IDisposable
        {
            private readonly string? _saved;

            public Preserved(string? saved) => _saved = saved;

            public void Dispose()
            {
                try
                {
                    Write(_saved);
                }
                catch
                {
                    // 戻せなくてもテストの結果は変えない（クリップボードを別のプロセスが握っている）
                }
            }
        }

        private static T WithClipboard<T>(Func<T> action)
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        return action();
                    }
                    finally
                    {
                        CloseClipboard();
                    }
                }

                Thread.Sleep(100);
            }

            throw new InvalidOperationException("クリップボードを開けませんでした（ほかのソフトが使用中の可能性があります）。");
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll")]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll")]
        private static extern IntPtr GetClipboardData(uint format);

        [DllImport("user32.dll")]
        private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr memory);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr memory);
    }
}
