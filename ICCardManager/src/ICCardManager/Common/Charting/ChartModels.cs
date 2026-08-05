using System;

namespace ICCardManager.Common.Charting
{
    /// <summary>
    /// グラフの描画領域（軸ラベル等を除いた、実際にデータを描く矩形）を表す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 管理者ダッシュボード（Issue #1692）のグラフは外部ライブラリを使わず WPF の
    /// <c>Canvas</c> 上へ自前描画する。インターネット非接続環境向けの自己完結型ビルドであり、
    /// チャートライブラリの NuGet 追加は配布サイズと THIRD_PARTY_LICENSES 同期の負担を伴うため。
    /// </para>
    /// <para>
    /// 「データ → 座標」の変換を <see cref="ChartScale"/> / <see cref="ChartGeometryCalculator"/> の
    /// 純粋関数へ切り出すことで、UI を起動せずに単体テストで描画の正しさを固定できる。
    /// View 側は算出済みの座標を <c>ItemsControl</c> + <c>Canvas</c> へ流し込むだけに留める。
    /// </para>
    /// </remarks>
    public class ChartPlotArea
    {
        public ChartPlotArea(double left, double top, double width, double height)
        {
            Left = left;
            Top = top;
            Width = width;
            Height = height;
        }

        /// <summary>描画領域の左端 X 座標（ピクセル）</summary>
        public double Left { get; }

        /// <summary>描画領域の上端 Y 座標（ピクセル）</summary>
        public double Top { get; }

        /// <summary>描画領域の幅（ピクセル）</summary>
        public double Width { get; }

        /// <summary>描画領域の高さ（ピクセル）</summary>
        public double Height { get; }

        /// <summary>描画領域の右端 X 座標（ピクセル）</summary>
        public double Right => Left + Width;

        /// <summary>描画領域の下端 Y 座標（ピクセル）</summary>
        public double Bottom => Top + Height;

        /// <summary>
        /// 描画可能な領域かどうか。幅または高さが 0 以下の場合は描画できない。
        /// </summary>
        /// <remarks>
        /// ウィンドウを極端に縮めた場合や初期レイアウト前に 0 が入ることがあるため、
        /// 各計算メソッドはこれを見て空の結果を返す（例外を投げない）。
        /// </remarks>
        public bool IsValid => Width > 0 && Height > 0;
    }

    /// <summary>
    /// 数値軸のスケール（下限・上限・目盛り間隔）。
    /// </summary>
    public class AxisScale
    {
        public AxisScale(double min, double max, double tickInterval, int tickCount)
        {
            Min = min;
            Max = max;
            TickInterval = tickInterval;
            TickCount = tickCount;
        }

        /// <summary>軸の下限値（切りの良い値に丸められている）</summary>
        public double Min { get; }

        /// <summary>軸の上限値（切りの良い値に丸められている）</summary>
        public double Max { get; }

        /// <summary>目盛りの間隔</summary>
        public double TickInterval { get; }

        /// <summary>目盛りの本数（両端を含む）</summary>
        public int TickCount { get; }
    }

    /// <summary>
    /// 折れ線グラフの 1 点（ピクセル座標つき）。
    /// </summary>
    public class ChartPoint
    {
        public ChartPoint(int categoryIndex, double x, double y, double value, double markerSize)
        {
            CategoryIndex = categoryIndex;
            X = x;
            Y = y;
            Value = value;
            MarkerSize = markerSize;
        }

        /// <summary>元データ列における位置（月別なら何か月目か）</summary>
        public int CategoryIndex { get; }

        /// <summary>点の X 座標（ピクセル）</summary>
        public double X { get; }

        /// <summary>点の Y 座標（ピクセル）</summary>
        public double Y { get; }

        /// <summary>元の値</summary>
        public double Value { get; }

        /// <summary>マーカー（丸）の直径</summary>
        public double MarkerSize { get; }

        /// <summary>マーカーを中心揃えで配置するための <c>Canvas.Left</c> 値</summary>
        public double MarkerLeft => X - (MarkerSize / 2.0);

        /// <summary>マーカーを中心揃えで配置するための <c>Canvas.Top</c> 値</summary>
        public double MarkerTop => Y - (MarkerSize / 2.0);
    }

    /// <summary>
    /// 棒グラフの 1 本（ピクセル座標つき）。縦棒・横棒・積み上げのいずれでも使う。
    /// </summary>
    public class ChartBar
    {
        public ChartBar(int categoryIndex, int seriesIndex, double left, double top, double width, double height, double value, string brushKey)
        {
            CategoryIndex = categoryIndex;
            SeriesIndex = seriesIndex;
            Left = left;
            Top = top;
            Width = width;
            Height = height;
            Value = value;
            BrushKey = brushKey;
        }

        /// <summary>元データ列における位置（月別なら何か月目か、カード別なら何枚目か）</summary>
        public int CategoryIndex { get; }

        /// <summary>系列の番号（積み上げ棒で職員ごとに色を変えるために使う）</summary>
        public int SeriesIndex { get; }

        /// <summary><c>Canvas.Left</c> 値（ピクセル）</summary>
        public double Left { get; }

        /// <summary><c>Canvas.Top</c> 値（ピクセル）</summary>
        public double Top { get; }

        /// <summary>棒の幅（ピクセル）</summary>
        public double Width { get; }

        /// <summary>棒の高さ（ピクセル）</summary>
        public double Height { get; }

        /// <summary>元の値</summary>
        public double Value { get; }

        /// <summary>
        /// 塗り色として使うリソースキー名。
        /// </summary>
        /// <remarks>
        /// 色値リテラルを持たせず、キー名を <c>ResourceKeyToBrushConverter</c> 経由で
        /// <c>AccessibilityStyles.xaml</c> のブラシへ解決する（Issue #1392、#1461 の方針）。
        /// </remarks>
        public string BrushKey { get; }
    }

    /// <summary>
    /// 軸の目盛り 1 個（ピクセル座標つき）。
    /// </summary>
    public class ChartAxisTick
    {
        /// <summary>ラベルを配置する箱の幅（中央揃えの基準）</summary>
        /// <remarks>
        /// 文字サイズが 4 段階で変わるため実測幅では中央を決められない。固定幅の箱に
        /// <c>TextAlignment="Center"</c> で流し込み、箱ごと目盛りの中心へ寄せる。
        /// 幅は「特大（20pt）で "2026/05" が収まる」ことを基準にしている。
        /// </remarks>
        public const double LabelBoxWidth = 76;

        /// <summary>ラベルを配置する箱の高さ（中央揃えの基準）</summary>
        public const double LabelBoxHeight = 22;

        public ChartAxisTick(double value, double position, string label)
        {
            Value = value;
            Position = position;
            Label = label;
        }

        /// <summary>X 軸ラベルを目盛りの中心へ寄せるための <c>Canvas.Left</c> 値</summary>
        public double LabelLeftCentered => Position - (LabelBoxWidth / 2.0);

        /// <summary>Y 軸ラベルを目盛りの中心へ寄せるための <c>Canvas.Top</c> 値</summary>
        public double LabelTopCentered => Position - (LabelBoxHeight / 2.0);

        /// <summary>目盛りが表す値（カテゴリ軸では元データ列における位置）</summary>
        public double Value { get; }

        /// <summary>目盛りのピクセル座標（Y 軸なら Top、X 軸なら Left として使う）</summary>
        public double Position { get; }

        /// <summary>目盛りの表示ラベル</summary>
        public string Label { get; }
    }
}
