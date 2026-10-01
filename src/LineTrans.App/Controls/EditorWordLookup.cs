using System;

using LineTrans.App.Interop;
using LineTrans.App.Services;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

using Windows.Foundation;

namespace LineTrans.App.Controls;

/// <summary>取词的触发方式（日志与取证用）。</summary>
public enum WordLookupTrigger
{
    /// <summary>鼠标停在单词上。</summary>
    Hover,

    /// <summary>在单词上单击（不含拖动）。</summary>
    Click,
}

/// <summary>
/// 给一个 <see cref="RichEditBox"/> 装上「悬浮取词 / 单击取词 / 划词翻译」三种交互。
///
/// <para><b>为什么是 RichEditBox</b>：WinUI3 的 <c>TextBox</c> 没有
/// <c>GetCharacterIndexFromPoint</c>（那是 UWP 的），按坐标取字符只能用 RichEditBox 的
/// <c>ITextDocument.GetRangeFromPoint(Point, PointOptions.ClientCoordinates)</c>；
/// 反向的「字符 → 屏幕坐标」用 <c>ITextRange.GetPoint(...)</c>，正好用来定浮层锚点。
/// 这两条在本机 WindowsAppSDK 1.8 上都实测可用（见 --lookupprobe 报告）。</para>
///
/// <para><b>为什么处理器全部用 AddHandler(handledEventsToo: true) 注册</b>：
/// --lookupprobe 实测（2026-10-01）：WinUI3 的文本控件内部输入栈会在事件冒泡到控件自身
/// 之前把它标成 Handled。当时 <c>DoubleTapped="OnEditorDoubleTapped"</c> 这种 XAML 写法
/// 等价于 handledEventsToo: false，一次都没被调到（同一个控件上用 handledEventsToo: true
/// 挂的计数器却数到了 DoubleTapped=1），这就是「划词查义完全不能用」的根因。
/// 这里一律用 handledEventsToo: true，杜绝同一个坑。</para>
///
/// <para><b>局限</b>：CJK 没有词边界，悬浮/单击只能给出「从指针所在字开始的有限个字」，
/// 本地 ECDICT 词库是英→中，中文串通常查不到（会走 AI 兜底或提示未收录）；
/// 划词翻译因为直接拿选区文字，不受这条限制。</para>
/// </summary>
public sealed class EditorWordLookup
{
    /// <summary>
    /// 悬浮取词的延时（毫秒）。必须留一小段：不给延时的悬浮会在用户正常阅读、
    /// 拖选、移动鼠标穿过程序时一路狂弹浮层。400ms 是「手指划过不会触发、停一下才出」的手感。
    /// </summary>
    public const int HoverDelayMs = 400;

    /// <summary>按下之后位移超过这个距离（DIP）就算拖选，不再按「单击取词」处理。</summary>
    private const double DragSlopDip = 4.0;

    /// <summary>两次命中测试之间的最小位移（DIP）：PointerMoved 触发极密，不节流会一直砸 COM。</summary>
    private const double HitTestStepDip = 2.0;

    /// <summary>CJK 串一次最多取几个字（本地词库是英→中，取太长没有意义）。</summary>
    private const int MaxCjkWord = 4;

    private readonly RichEditBox _box;
    private readonly DispatcherQueueTimer _hoverTimer;

    // 注册到控件上的回调全部留一份引用：退出 / 换页时要能一个一个摘干净。
    // （AddHandler 传匿名委托就再也 RemoveHandler 不掉了，这里必须存字段。）
    private readonly PointerEventHandler _onMoved;
    private readonly PointerEventHandler _onExited;
    private readonly PointerEventHandler _onPressed;
    private readonly PointerEventHandler _onReleased;
    private readonly PointerEventHandler _onCaptureLost;
    private readonly PointerEventHandler _onWheel;
    private readonly RoutedEventHandler _onTextChanged;

    private bool _detached;

    private string _hoverWord = string.Empty;
    private int _hoverIndex = -1;

    private bool _pressed;
    private bool _downSeen;
    private Point _pressPoint;
    private bool _dragged;

    private Point _lastHitTest = new Point(double.NaN, double.NaN);
    private bool _pointerInside;

    private string _textCache = string.Empty;
    private bool _textDirty = true;

    private Point _lastPointerPoint;
    private readonly List<string> _trace = new List<string>();

    /// <summary>取证用：最近若干次指针事件的原始过程（--lookupprobe 直接打印）。</summary>
    public IReadOnlyList<string> Trace => _trace;

    /// <summary>取证用：各类事件的计数。</summary>
    public int MovedCount { get; private set; }
    public int PressedCount { get; private set; }
    public int ReleasedCount { get; private set; }
    public int ExitedCount { get; private set; }
    public int CaptureLostCount { get; private set; }
    public int HoverFiredCount { get; private set; }
    public int WordFiredCount { get; private set; }
    public int SelectionFiredCount { get; private set; }

    /// <summary>取证用：最近一次 ITextDocument 几何调用失败的原因（成功时是空串）。</summary>
    public static string LastGeometryError { get; private set; } = string.Empty;

    private void Log(string message)
    {
        _trace.Add(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message);
        if (_trace.Count > 240) _trace.RemoveAt(0);
    }

    public EditorWordLookup(RichEditBox box)
    {
        _box = box ?? throw new ArgumentNullException(nameof(box));

        _onMoved = OnPointerMoved;
        _onExited = OnPointerExited;
        _onPressed = OnPointerPressed;
        _onReleased = OnPointerReleased;
        _onCaptureLost = OnPointerCaptureLost;
        _onWheel = OnPointerWheelChanged;
        _onTextChanged = (_, __) => _textDirty = true;

        _box.AddHandler(UIElement.PointerMovedEvent, _onMoved, true);
        _box.AddHandler(UIElement.PointerExitedEvent, _onExited, true);
        _box.AddHandler(UIElement.PointerPressedEvent, _onPressed, true);
        _box.AddHandler(UIElement.PointerReleasedEvent, _onReleased, true);
        _box.AddHandler(UIElement.PointerCaptureLostEvent, _onCaptureLost, true);
        _box.AddHandler(UIElement.PointerWheelChangedEvent, _onWheel, true);

        // 文本变了，命中测试用的缓存就得作废
        _box.TextChanged += _onTextChanged;

        _hoverTimer = _box.DispatcherQueue.CreateTimer();
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(HoverDelayMs);
        _hoverTimer.IsRepeating = false;
        _hoverTimer.Tick += OnHoverTick;
    }

    /// <summary>悬浮取词是否开启（每次触发时现读，改设置立即生效）。</summary>
    public Func<bool> HoverEnabled { get; set; } = () => true;

    /// <summary>单击取词是否开启。</summary>
    public Func<bool> ClickEnabled { get; set; } = () => true;

    /// <summary>划词翻译是否开启。</summary>
    public Func<bool> SelectionEnabled { get; set; } = () => true;

    /// <summary>
    /// 承载本控件的窗口句柄。用来把系统光标位置换算成控件坐标，
    /// 从而回答「指针现在到底还在不在这个框里」—— XAML 的 PointerExited 回答不了这个问题
    /// （指针移到子元素上也会触发，而且它的坐标是「框内最后位置」而不是真实光标位置）。
    /// </summary>
    public Func<IntPtr>? WindowHandleProvider { get; set; }

    /// <summary>要在某个单词上显示释义。<c>Point</c> 是锚点（相对本控件客户区）。</summary>
    public event Action<string, Point, WordLookupTrigger>? WordRequested;

    /// <summary>选中了一段文字要翻译。<c>Point</c> 是锚点（相对本控件客户区）。</summary>
    public event Action<string, Point>? SelectionRequested;

    /// <summary>指针离开控件（或滚轮滚动）：宿主据此考虑收起浮层。</summary>
    public event Action? PointerLeft;

    /// <summary>指针又回到控件里（宿主据此取消「准备收起浮层」的计时）。</summary>
    public event Action? PointerEntered;

    /// <summary>当前控件的纯文本（缓存；RichEditBox 的 GetText 对长文本不便宜）。</summary>
    public string Text
    {
        get
        {
            if (_textDirty)
            {
                _textCache = DocumentText(_box);
                _textDirty = false;
            }
            return _textCache;
        }
    }

    /// <summary>取证用：最后一次指针位置（控件客户区坐标）。</summary>
    public Point LastPointerPoint => _lastPointerPoint;

    /// <summary>
    /// 松开与控件的全部绑定（停掉悬浮计时器、摘掉六个 AddHandler 回调与 TextChanged、
    /// 清空对外事件）。
    ///
    /// <para><b>为什么退出前必须做</b>：RichEditBox 的实体是原生控件（WinUIEdit.dll），
    /// 窗口销毁的过程中它还会反过来回调进 XAML。这时如果页面 / 本对象还挂在它的回调链上，
    /// 回调就会摸到正在拆掉的对象 —— 实测表现为退出期 Microsoft.UI.Xaml.dll 里
    /// 读空指针（0xC0000005）。摘干净之后原生控件再回调也没人接。</para>
    ///
    /// <para>可重复调用；调用之后再调用 <see cref="Detach"/> 是空操作。</para>
    /// </summary>
    public void Detach()
    {
        if (_detached) return;
        _detached = true;

        try
        {
            _hoverTimer.Stop();
            _hoverTimer.Tick -= OnHoverTick;

            _box.RemoveHandler(UIElement.PointerMovedEvent, _onMoved);
            _box.RemoveHandler(UIElement.PointerExitedEvent, _onExited);
            _box.RemoveHandler(UIElement.PointerPressedEvent, _onPressed);
            _box.RemoveHandler(UIElement.PointerReleasedEvent, _onReleased);
            _box.RemoveHandler(UIElement.PointerCaptureLostEvent, _onCaptureLost);
            _box.RemoveHandler(UIElement.PointerWheelChangedEvent, _onWheel);

            _box.TextChanged -= _onTextChanged;
        }
        catch (Exception ex)
        {
            AppServices.Log("解绑取词交互失败：" + ex.Message);
        }

        WordRequested = null;
        SelectionRequested = null;
        PointerLeft = null;
        PointerEntered = null;

        _pointerInside = false;
        _pressed = false;
        _downSeen = false;
        _hoverIndex = -1;
        _hoverWord = string.Empty;
    }

    /// <summary>是否已经解绑（退出流程会问一次，避免重复摘）。</summary>
    public bool IsDetached => _detached;

    /// <summary>取消挂起的悬浮计时（不碰浮层）。</summary>
    public void CancelHover()
    {
        _hoverTimer.Stop();
        _hoverIndex = -1;
        _hoverWord = string.Empty;
    }

    // ------------------------------------------------------------------
    // 指针
    // ------------------------------------------------------------------

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        Point point = e.GetCurrentPoint(_box).Position;
        if (!_pointerInside) PointerEntered?.Invoke();
        _pointerInside = true;
        _lastPointerPoint = point;
        MovedCount++;

        if (_pressed)
        {
            if (Distance(point, _pressPoint) > DragSlopDip) _dragged = true;
            Log("Move(" + P(point) + ") 按住中 dragged=" + _dragged);
            CancelHover();
            return;
        }

        if (!Inside(point)) { Log("Move(" + P(point) + ") 在控件外"); CancelHover(); return; }

        if (!double.IsNaN(_lastHitTest.X)
            && Math.Abs(point.X - _lastHitTest.X) < HitTestStepDip
            && Math.Abs(point.Y - _lastHitTest.Y) < HitTestStepDip)
        {
            Log("Move(" + P(point) + ") 被节流（上次命中点 " + P(_lastHitTest) + "）");
            return;
        }
        _lastHitTest = point;

        string word = WordAtPoint(_box, point, Text, out int index, out _);
        if (word.Length == 0 || index < 0)
        {
            Log("Move(" + P(point) + ") 取不到词；几何错误=" + LastGeometryError);
            CancelHover();
            return;
        }
        if (index == _hoverIndex)
        {
            Log("Move(" + P(point) + ") 还在同一个词 \"" + word + "\" 上，计时器不动");
            return;
        }

        _hoverIndex = index;
        _hoverWord = word;
        Log("Move(" + P(point) + ") 换到词 \"" + word + "\" 下标=" + index + "，悬浮开关=" + HoverEnabled());

        if (!HoverEnabled()) { Log("Move(" + P(point) + ") 取到 \"" + word + "\" 但悬浮开关已关"); return; }

        _hoverTimer.Stop();
        _hoverTimer.Start();
    }

    private void OnHoverTick(object? sender, object e)
    {
        _hoverTimer.Stop();

        if (_pressed || _hoverWord.Length == 0 || _hoverIndex < 0)
        {
            Log("悬浮计时到点但放弃：pressed=" + _pressed + " word=\"" + _hoverWord + "\" index=" + _hoverIndex);
            return;
        }
        if (!HoverEnabled()) { Log("悬浮计时到点但开关已关"); return; }
        if (!_pointerInside && !CursorInsideBox()) { Log("悬浮计时到点但指针已经离开控件"); return; }   // 计时期间已经移开就不要再弹

        HoverFiredCount++;
        WordFiredCount++;
        Point anchor = AnchorFor(_hoverIndex);
        Log("悬浮到点：词=\"" + _hoverWord + "\" 下标=" + _hoverIndex
            + " 锚点=" + P(anchor) + " 几何错误=" + LastGeometryError);
        WordRequested?.Invoke(_hoverWord, anchor, WordLookupTrigger.Hover);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerInside) PointerEntered?.Invoke();
        _pointerInside = true;
        _pressed = true;
        _downSeen = true;
        _dragged = false;
        _pressPoint = e.GetCurrentPoint(_box).Position;
        _lastPointerPoint = _pressPoint;
        PressedCount++;
        Log("按下(" + P(_pressPoint) + ")");
        CancelHover();
        // 刻意不设 e.Handled：控件自己要用这次按下定位插入符 / 开始选择
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        Point point = e.GetCurrentPoint(_box).Position;
        // 注意：判断「这次抬起是不是配得上一次按下」只能看 _downSeen。
        // _pressed 会被 PointerCaptureLost 清掉（文本控件按下后很快会丢一次捕获），
        // 用它当判据的话每次抬起都会被当成「没有对应的按下」，单击与划词就全哑了。
        bool wasPressed = _downSeen;
        _pressed = false;
        _downSeen = false;

        bool dragged = _dragged || Distance(point, _pressPoint) > DragSlopDip;
        _dragged = false;
        CancelHover();
        ReleasedCount++;
        Log("抬起(" + P(point) + ") wasPressed=" + wasPressed + " dragged=" + dragged
            + " clickEnabled=" + ClickEnabled() + " selectionEnabled=" + SelectionEnabled());

        if (!wasPressed) return;

        // 1) 拖选：翻译选中的内容
        if (dragged)
        {
            string selected = SelectedText(_box);
            Log("  拖选内容=\"" + LookupTextShorten(selected) + "\" 长度=" + selected.Length);
            if (selected.Trim().Length > 0 && SelectionEnabled())
            {
                int start = SelectionStart(_box);
                SelectionFiredCount++;
                Point anchor = AnchorFor(start);
                Log("  划词到点：锚点=" + P(anchor) + " 几何错误=" + LastGeometryError);
                SelectionRequested?.Invoke(selected.Trim(), anchor);
            }
            return;
        }

        // 2) 单击（没有位移）：查指针下面的那个词
        if (!ClickEnabled()) return;

        string word = WordAtPoint(_box, point, Text, out int index, out _);
        Log("  单击取词=\"" + word + "\" 下标=" + index + " 几何错误=" + LastGeometryError);
        if (word.Length == 0 || index < 0) return;
        WordFiredCount++;
        WordRequested?.Invoke(word, AnchorFor(index), WordLookupTrigger.Click);
    }

    /// <summary>
    /// 注意：XAML 的 PointerExited 不是「离开这个控件的可见范围」，
    /// 指针从父元素移到它的子元素上也会触发（「指针直接覆盖的元素变了」），
    /// 而且事件参数里的坐标是「框内最后位置」，不是真实光标位置（实测）。
    /// RichEditBox 内部全是子元素，所以这里必须另取真实光标位置来判断：
    /// 还在框里就当没发生，否则悬浮计时每次刚起来就被自己取消掉。
    /// </summary>
    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        ExitedCount++;

        if (CursorInsideBox())
        {
            Log("PointerExited 但真实光标还在框内（指针进了子元素），忽略");
            return;
        }

        Log("真的离开控件");
        _pointerInside = false;
        _lastHitTest = new Point(double.NaN, double.NaN);   // 再进来时一定要重新命中测试
        CancelHover();
        PointerLeft?.Invoke();
    }

    /// <summary>取系统光标的真实位置，换算成控件坐标，判断它是否还在框内。</summary>
    private bool CursorInsideBox()
    {
        try
        {
            IntPtr hwnd = WindowHandleProvider?.Invoke() ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero) return true;
            if (_box.XamlRoot?.Content is not UIElement root) return true;
            if (!NativeMethods.GetCursorPos(out NativeMethods.POINT screen)) return true;

            var origin = new NativeMethods.POINT { X = 0, Y = 0 };
            if (!NativeMethods.ClientToScreen(hwnd, ref origin)) return true;

            double scale = ScaleOf(hwnd);
            if (scale <= 0) scale = 1.0;

            double x = (screen.X - origin.X) / scale;
            double y = (screen.Y - origin.Y) / scale;

            Rect bounds = _box.TransformToVisual(root)
                .TransformBounds(new Rect(0, 0, _box.ActualWidth, _box.ActualHeight));

            return x >= bounds.X && y >= bounds.Y && x <= bounds.X + bounds.Width && y <= bounds.Y + bounds.Height;
        }
        catch
        {
            return true;   // 拿不准就当作还在里面，宁可多弹一次也不要误伤悬浮
        }
    }

    private static double ScaleOf(IntPtr hwnd)
    {
        try
        {
            uint dpi = NativeMethods.GetDpiForWindow(hwnd);
            return dpi == 0 ? 1.0 : dpi / 96.0;
        }
        catch
        {
            return 1.0;
        }
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        CaptureLostCount++;
        Log("丢失指针捕获（_pressed 归零，_downSeen 保留=" + _downSeen + "）");
        _pressed = false;
        _dragged = false;
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        _lastHitTest = new Point(double.NaN, double.NaN);
        CancelHover();
        PointerLeft?.Invoke();
    }

    private bool Inside(Point point) =>
        point.X >= 0 && point.Y >= 0 && point.X <= _box.ActualWidth && point.Y <= _box.ActualHeight;

    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // ------------------------------------------------------------------
    // 文本 / 坐标（ITextDocument API）
    // ------------------------------------------------------------------

    /// <summary>读控件的纯文本。RichEditBox 的 GetText 末尾一定带一个段落结束符，去掉它。</summary>
    public static string DocumentText(RichEditBox box)
    {
        try
        {
            box.Document.GetText(TextGetOptions.NoHidden, out string text);
            text ??= string.Empty;
            return text.EndsWith("\r", StringComparison.Ordinal) ? text.Substring(0, text.Length - 1) : text;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 写控件的纯文本（替换全文）。
    /// 注意：只读的 RichEditBox 上 <c>ITextDocument.SetText</c> 会抛
    /// UnauthorizedAccessException（实测），所以这里先把 IsReadOnly 摘掉、写完再戴回去。
    /// 整个过程在 UI 线程上同步完成，用户没有机会在中途输入。
    /// </summary>
    public static void SetDocumentText(RichEditBox box, string? text)
    {
        bool readOnly = box.IsReadOnly;
        try
        {
            if (readOnly) box.IsReadOnly = false;
            box.Document.SetText(TextSetOptions.None, text ?? string.Empty);
        }
        finally
        {
            if (readOnly) box.IsReadOnly = true;
        }
    }

    /// <summary>当前选区的起始字符下标（没有选区时是插入符位置）。</summary>
    public static int SelectionStart(RichEditBox box)
    {
        try
        {
            ITextRange selection = box.Document.Selection;
            return selection.StartPosition;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>当前选中的文字（没有选中就是空串）。</summary>
    public static string SelectedText(RichEditBox box)
    {
        try
        {
            // 显式用 ITextRange：RichEditTextSelection 同时实现了 ITextSelection 与 ITextRange，
            // 直接写 var 取值在某些投影下会有二义性。
            ITextRange selection = box.Document.Selection;
            if (selection.EndPosition <= selection.StartPosition) return string.Empty;
            return selection.Text ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 按坐标取词：<c>GetRangeFromPoint</c> 拿到最近字符的下标，再按词边界扩成完整的词。
    /// </summary>
    public static string WordAtPoint(RichEditBox box, Point point, string text, out int start, out int length)
    {
        start = -1;
        length = 0;

        try
        {
            ITextRange range = box.Document.GetRangeFromPoint(point, PointOptions.ClientCoordinates);
            LastGeometryError = string.Empty;
            return ExtractWord(text, range.StartPosition, out start, out length);
        }
        catch (Exception ex)
        {
            LastGeometryError = "GetRangeFromPoint 失败：" + ex.GetType().Name + " " + ex.Message;
            return string.Empty;
        }
    }

    /// <summary>字符 → 控件客户区坐标（浮层锚点）。失败时返回 false 并把原因记进 <see cref="LastGeometryError"/>。</summary>
    public static bool TryPointOfChar(RichEditBox box, int index, out Point point)
    {
        point = new Point(0, 0);
        if (index < 0)
        {
            LastGeometryError = "下标为负";
            return false;
        }

        try
        {
            ITextRange range = box.Document.GetRange(index, index);
            range.GetPoint(
                HorizontalCharacterAlignment.Left,
                VerticalCharacterAlignment.Top,
                PointOptions.ClientCoordinates,
                out point);
            LastGeometryError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            LastGeometryError = "GetPoint 失败：" + ex.GetType().Name + " " + ex.Message;
            return false;
        }
    }

    /// <summary>字符 → 控件客户区坐标；拿不到就退回 (0,0)（调用方一般该用 TryPointOfChar）。</summary>
    public static Point PointOfChar(RichEditBox box, int index) =>
        TryPointOfChar(box, index, out Point point) ? point : new Point(0, 0);

    /// <summary>浮层锚点：优先用「这个词自己的左上角」，拿不到就退回指针位置（永远给得出一个合理锚点）。</summary>
    private Point AnchorFor(int index)
    {
        if (TryPointOfChar(_box, index, out Point point)) return point;
        Log("  锚点退回指针位置（" + LastGeometryError + "）");
        return _lastPointerPoint;
    }

    /// <summary>
    /// 从 <paramref name="position"/> 这个字符扩出一个「词」。
    /// 拉丁词按字母/数字/连字符/撇号扩；CJK 没有词边界，从指针所在字起最多取 <see cref="MaxCjkWord"/> 个字。
    /// </summary>
    public static string ExtractWord(string? text, int position, out int start, out int length)
    {
        start = -1;
        length = 0;

        if (string.IsNullOrEmpty(text) || position < 0 || position >= text.Length) return string.Empty;

        int probe = position;
        // GetRangeFromPoint 命中的常常是「离指针最近的字符边界」：落在词尾右侧时会指到下一个位置，
        // 这时候往回看一格才是用户真正指着的词。
        if (!IsWordChar(text[probe]) && probe > 0 && IsWordChar(text[probe - 1])) probe--;

        if (IsCjk(text[probe]))
        {
            start = probe;
            int end = probe;
            while (end + 1 < text.Length && IsCjk(text[end + 1]) && end + 1 - probe < MaxCjkWord - 1) end++;
            length = end - start + 1;
            return text.Substring(start, length);
        }

        if (!IsLatinWordChar(text[probe])) return string.Empty;

        int from = probe;
        while (from > 0 && IsLatinWordChar(text[from - 1])) from--;
        int to = probe;
        while (to + 1 < text.Length && IsLatinWordChar(text[to + 1])) to++;

        start = from;
        length = to - from + 1;
        return text.Substring(start, length);
    }

    private static string P(Point point) => "(" + point.X.ToString("0.#") + "," + point.Y.ToString("0.#") + ")";

    private static string LookupTextShorten(string value)
    {
        string flat = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length <= 40 ? flat : flat.Substring(0, 40) + "…";
    }

    private static bool IsLatinWordChar(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '\'' || c == '-' || c == '_';

    private static bool IsCjk(char c) =>
        (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0xF900 && c <= 0xFAFF);

    private static bool IsWordChar(char c) => IsLatinWordChar(c) || IsCjk(c);
}
