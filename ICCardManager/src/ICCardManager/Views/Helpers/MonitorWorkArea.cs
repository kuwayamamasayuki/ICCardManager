#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ICCardManager.Views.Helpers
{
    /// <summary>
    /// ウィンドウが表示されているモニターの作業領域（DIP 単位）を求める（Issue #2258）
    /// </summary>
    /// <remarks>
    /// <see cref="SystemParameters.WorkArea"/> はプライマリーモニターの作業領域しか表さない。メイン画面を
    /// サブモニターで使っているとき、その作業領域でダイアログの位置を寄せると、ダイアログだけがプライマリーへ
    /// 移って「開いたのに見えない」状態になる。そのウィンドウが載っているモニターの作業領域を使う。
    /// </remarks>
    internal static class MonitorWorkArea
    {
        private const uint MonitorDefaultToNearest = 2;

        /// <summary>
        /// <paramref name="window"/> が載っているモニターの作業領域。取れなければ null（呼び出し元は位置を変えない）
        /// </summary>
        /// <remarks>ウィンドウのハンドルと表示先（<see cref="PresentationSource"/>）ができた後（Loaded 以降）に呼ぶ</remarks>
        public static Rect? Of(Window window)
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            {
                return null;
            }

            // 物理ピクセル → DIP（Left / Top / Width と同じ単位）
            var target = PresentationSource.FromVisual(window)?.CompositionTarget;
            if (target == null)
            {
                return null;
            }

            var toDip = target.TransformFromDevice;
            var topLeft = toDip.Transform(new Point(info.Work.Left, info.Work.Top));
            var bottomRight = toDip.Transform(new Point(info.Work.Right, info.Work.Bottom));
            return new Rect(topLeft, bottomRight);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    }
}
