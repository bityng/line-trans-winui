using System;

using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace LineTrans.App.Controls;

/// <summary>
/// 可拖拽分割条。WinUI3 没有内置 GridSplitter，这里手写一个：
/// 拖动时把「左侧面板占可用宽度的比例」通过 <see cref="RatioChanged"/> 报给宿主，
/// 由宿主去改 ColumnDefinition 的星号宽度。
///
/// 两个关键点：
///   1. 比例永远 clamp 在 [0.15, 0.85]，否则任一侧会变成 0 宽度（星号为 0 时控件会消失且再也拖不回来）；
///   2. 必须显式指定 <see cref="Host"/>，因为本控件被包在一层 Grid 里，
///      直接取 Parent 拿到的只是那层 10px 宽的容器，算出来的比例是错的。
/// </summary>
public sealed class DragSplitter : UserControl
{
    /// <summary>左侧最小占比。</summary>
    public const double MinRatio = 0.15;

    /// <summary>左侧最大占比。</summary>
    public const double MaxRatio = 0.85;

    private bool _dragging;
    private double _startPointerX;
    private double _startLeftWidth;
    private double _ratio = 0.5;

    /// <summary>左 pane 占比变化（0.15 ~ 0.85）。</summary>
    public event EventHandler<double>? RatioChanged;

    public DragSplitter()
    {
        Width = 10;

        // 透明背景 + 透明子元素，保证整条 10px 区域都能命中指针。
        Background = new SolidColorBrush(Colors.Transparent);
        Content = new Border { Background = new SolidColorBrush(Colors.Transparent) };

        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += OnPointerCaptureLost;
        DoubleTapped += OnDoubleTapped;
    }

    /// <summary>计算比例用的宿主容器（包含左右两列的 Grid）。</summary>
    public FrameworkElement? Host { get; set; }

    /// <summary>左面板占比。</summary>
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

    private FrameworkElement? ResolveHost() => Host ?? Parent as FrameworkElement;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var host = ResolveHost();
        if (host == null || host.ActualWidth <= 0) return;

        double available = host.ActualWidth - ActualWidth;
        if (available <= 1) return;

        _startPointerX = e.GetCurrentPoint(host).Position.X;
        _startLeftWidth = _ratio * available;
        _dragging = true;

        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        var host = ResolveHost();
        if (host == null) return;

        double available = host.ActualWidth - ActualWidth;
        if (available <= 1) return;

        double x = e.GetCurrentPoint(host).Position.X;
        double left = _startLeftWidth + (x - _startPointerX);
        double next = Clamp(left / available);

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
}
