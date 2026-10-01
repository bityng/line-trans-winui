using System;

using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace LineTrans.App.Controls;

/// <summary>分割方向：决定分隔条是竖的还是横的、拖动是沿 X 还是沿 Y。</summary>
public enum SplitDirection
{
    /// <summary>左右分栏：竖分隔条，沿 X 拖动（第一栏在左）。</summary>
    Columns,

    /// <summary>上下分栏：横分隔条，沿 Y 拖动（第一栏在上）。</summary>
    Rows,
}

/// <summary>
/// 可拖拽分割条。WinUI3 没有内置 GridSplitter，这里手写一个：
/// 拖动时把「第一栏占可用宽/高的比例」通过 <see cref="RatioChanged"/> 报给宿主，
/// 由宿主去改 ColumnDefinition / RowDefinition 的星号尺寸。
///
/// 两个关键点：
///   1. 比例永远 clamp 在 [0.15, 0.85]（<see cref="MinRatio"/> / <see cref="MaxRatio"/>），
///      否则任一侧会变成 0 宽度（星号为 0 时控件会消失且再也拖不回来）；
///   2. 必须显式指定 <see cref="Host"/>，因为本控件被包在一层容器里，
///      直接取 Parent 拿到的只是那层 10px 宽的容器，算出来的比例是错的。
///
/// 2026-10：加上 <see cref="Direction"/>，同一支控件同时支持左右式与上下式两种布局。
/// </summary>
public sealed class DragSplitter : UserControl
{
    /// <summary>第一栏最小占比。</summary>
    public const double MinRatio = 0.15;

    /// <summary>第一栏最大占比。</summary>
    public const double MaxRatio = 0.85;

    /// <summary>分隔条自身的厚度（DIP）。</summary>
    public const double Thickness = 10;

    /// <summary>
    /// 两种方向的系统光标。
    ///
    /// 必须【长期持有】这份引用：InputSystemCursor 背后是 native 资源，
    /// 如果只在 ApplyOrientation 里临时 Create 一个、赋给 ProtectedCursor 之后就没别的引用了，
    /// GC 一收，XAML 在布局 / 退出阶段再碰这个游标就是访问已释放内存
    /// （实测现象：进程退出时 Microsoft.UI.Xaml.dll 里 0xC0000005，且只在某些时序下复现）。
    /// 两种方向各缓存一份，顺带也省掉了每次切布局都新建对象。
    /// </summary>
    private static readonly InputSystemCursor ColumnsCursor =
        InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

    private static readonly InputSystemCursor RowsCursor =
        InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);

    private bool _dragging;
    private double _startPointer;
    private double _startFirst;
    private double _ratio = 0.5;
    private SplitDirection _direction = SplitDirection.Columns;

    /// <summary>第一栏占比变化（0.15 ~ 0.85）。</summary>
    public event EventHandler<double>? RatioChanged;

    public DragSplitter()
    {
        // 透明背景 + 透明子元素，保证整条区域都能命中指针。
        Background = new SolidColorBrush(Colors.Transparent);
        Content = new Border { Background = new SolidColorBrush(Colors.Transparent) };

        ApplyOrientation();

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += OnPointerCaptureLost;
        DoubleTapped += OnDoubleTapped;
    }

    /// <summary>计算比例用的宿主容器（包含两栏定义的那个 Grid）。</summary>
    public FrameworkElement? Host { get; set; }

    /// <summary>分割方向：左右式用 <see cref="SplitDirection.Columns"/>，上下式用 <see cref="SplitDirection.Rows"/>。</summary>
    public SplitDirection Direction
    {
        get => _direction;
        set
        {
            if (_direction == value) return;
            _direction = value;
            ApplyOrientation();
        }
    }

    /// <summary>第一栏占比。</summary>
    public double Ratio
    {
        get => _ratio;
        set => _ratio = Clamp(value);
    }

    /// <summary>clamp 到合法区间。</summary>
    public static double Clamp(double value)
    {
        if (double.IsNaN(value)) return 0.5;
        return Math.Min(MaxRatio, Math.Max(MinRatio, value));
    }

    /// <summary>按当前方向摆放自己：竖条撑满高度 / 横条撑满宽度，并换光标。</summary>
    private void ApplyOrientation()
    {
        bool columns = _direction == SplitDirection.Columns;

        Width = columns ? Thickness : double.NaN;
        Height = columns ? double.NaN : Thickness;
        HorizontalAlignment = columns ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        VerticalAlignment = columns ? VerticalAlignment.Stretch : VerticalAlignment.Center;

        ProtectedCursor = columns ? ColumnsCursor : RowsCursor;
    }

    private FrameworkElement? ResolveHost() => Host ?? Parent as FrameworkElement;

    private bool IsColumns => _direction == SplitDirection.Columns;

    /// <summary>沿拖动方向可用长度（宿主尺寸减去分隔条自己占的那一段）。</summary>
    private double AvailableLength(FrameworkElement host)
    {
        double total = IsColumns ? host.ActualWidth : host.ActualHeight;
        double own = IsColumns ? ActualWidth : ActualHeight;
        return total - own;
    }

    private static double PointerPosition(FrameworkElement host, PointerRoutedEventArgs e, bool columns)
    {
        var position = e.GetCurrentPoint(host).Position;
        return columns ? position.X : position.Y;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var host = ResolveHost();
        if (host == null) return;

        double available = AvailableLength(host);
        if (available <= 1) return;

        _startPointer = PointerPosition(host, e, IsColumns);
        _startFirst = _ratio * available;
        _dragging = true;

        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        var host = ResolveHost();
        if (host == null) return;

        double available = AvailableLength(host);
        if (available <= 1) return;

        double current = PointerPosition(host, e, IsColumns);
        double first = _startFirst + (current - _startPointer);
        double next = Clamp(first / available);

        if (Math.Abs(next - _ratio) < 0.0005) return;

        _ratio = next;
        RatioChanged?.Invoke(this, next);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _ratio = 0.5;
        RatioChanged?.Invoke(this, _ratio);
        e.Handled = true;
    }

    /// <summary>
    /// 按与真实拖动完全相同的算法移动一次分隔条（自检 / 截图取证用）。
    /// 真实拖动走 PointerMoved，这里只是把「指针位移」直接喂给同一段计算。
    /// </summary>
    public double SimulateDrag(double deltaPixels)
    {
        var host = ResolveHost();
        if (host == null) return _ratio;

        double available = AvailableLength(host);
        if (available <= 1) return _ratio;

        double next = Clamp((_ratio * available + deltaPixels) / available);
        if (Math.Abs(next - _ratio) < 0.0005) return _ratio;

        _ratio = next;
        RatioChanged?.Invoke(this, next);
        return _ratio;
    }
}
