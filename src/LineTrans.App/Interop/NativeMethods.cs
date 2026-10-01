using System;
using System.Runtime.InteropServices;

namespace LineTrans.App.Interop;

/// <summary>
/// 本任务用到的全部 Win32 声明集中在这里（P/Invoke，无第三方依赖）。
///
/// 为什么不用第三方托盘库：本工程此前刻意保持「除 WindowsAppSDK 外零第三方 NuGet 依赖」，
/// 而全局热键（RegisterHotKey）本来就要求我们自己准备一个带消息循环的窗口，
/// 托盘图标（Shell_NotifyIcon 需要一个接收回调消息的 hwnd）正好复用同一个窗口过程，
/// 因此引入额外依赖换不来任何东西，反而给还原和离线构建增加风险。
///
/// 平台：本工程 <Platforms>x64</Platforms>，64 位指针宽度固定，GetWindowLongPtr 走 64 位分支。
/// </summary>
internal static class NativeMethods
{
    // ---------------------------------------------------------------
    // 窗口消息
    // ---------------------------------------------------------------

    internal const int WM_NULL = 0x0000;
    internal const int WM_DESTROY = 0x0002;
    internal const int WM_KEYDOWN = 0x0100;
    internal const int WM_SYSKEYDOWN = 0x0104;
    internal const int WM_CLOSE = 0x0010;
    internal const int WM_CONTEXTMENU = 0x007B;
    internal const int WM_LBUTTONUP = 0x0202;
    internal const int WM_LBUTTONDBLCLK = 0x0203;
    internal const int WM_RBUTTONUP = 0x0205;
    internal const int WM_HOTKEY = 0x0312;
    internal const int WM_APP = 0x8000;

    /// <summary>托盘图标回调消息（NOTIFYICONDATA.uCallbackMessage）。</summary>
    internal const int WM_TRAYICON = WM_APP + 1;

    /// <summary>本进程内部消息：请求托盘线程重新应用设置。</summary>
    internal const int WM_APPLY_SETTINGS = WM_APP + 2;

    // ---------------------------------------------------------------
    // 窗口样式 / 位置
    // ---------------------------------------------------------------

    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_NOACTIVATE = 0x08000000;
    internal const int SW_HIDE = 0;
    internal const int SW_SHOW = 5;
    internal const int SW_SHOWNOACTIVATE = 4;

    // ---------------------------------------------------------------
    // 热键修饰符
    // ---------------------------------------------------------------

    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_SHIFT = 0x0004;
    internal const uint MOD_WIN = 0x0008;
    internal const uint MOD_NOREPEAT = 0x4000;

    // ---------------------------------------------------------------
    // 输入
    // ---------------------------------------------------------------

    internal const uint INPUT_KEYBOARD = 1;
    internal const uint KEYEVENTF_KEYUP = 0x0002;
    internal const uint KEYEVENTF_UNICODE = 0x0004;
    internal const ushort VK_CONTROL = 0x11;
    internal const ushort VK_ESCAPE = 0x1B;
    internal const ushort VK_A = 0x41;
    internal const ushort VK_C = 0x43;

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    // ---------------------------------------------------------------
    // 剪贴板
    // ---------------------------------------------------------------

    /// <summary>
    /// 剪贴板序列号：剪贴板内容每次被改写都会自增。
    /// 用它判断「刚才那次 Ctrl+C 到底有没有真的复制到东西」，无需清空剪贴板，天然不破坏原内容。
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();

    // ---------------------------------------------------------------
    // 热键
    // ---------------------------------------------------------------

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---------------------------------------------------------------
    // 窗口 / 消息循环
    // ---------------------------------------------------------------

    internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowExW(
        int dwExStyle,
        string lpClassName,
        string? lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint RegisterWindowMessageW(string lpString);

    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_HIDEWINDOW = 0x0080;
    internal const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern short GetKeyState(int nVirtKey);

    // ---- Win32 剪贴板（自检用的「复制目标」窗口靠它把选中内容写进剪贴板） ----

    internal const uint CF_UNICODETEXT = 13;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    internal static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    internal static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        => IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    // ---------------------------------------------------------------
    // 窗口枚举（自检用）
    // ---------------------------------------------------------------

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassNameW(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowTextW(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool IsIconic(IntPtr hWnd);

    internal const uint WM_GETTEXT = 0x000D;
    internal const uint WM_GETTEXTLENGTH = 0x000E;
    internal const uint WM_SETTEXT = 0x000C;
    internal const uint EM_SETSEL = 0x00B1;
    internal const uint WS_CHILD = 0x40000000;
    internal const uint WS_VISIBLE = 0x10000000;
    internal const uint WS_TABSTOP = 0x00010000;
    internal const uint WS_BORDER = 0x00800000;
    internal const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    internal const uint ES_MULTILINE = 0x0004;
    internal const uint ES_AUTOVSCROLL = 0x0040;

    internal delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    /// <summary>跨进程取普通控件的文本（GetWindowText 对其他进程的控件无效，必须发 WM_GETTEXT）。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, [Out] System.Text.StringBuilder lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ---------------------------------------------------------------
    // 菜单
    // ---------------------------------------------------------------

    internal const uint MF_STRING = 0x00000000;
    internal const uint MF_SEPARATOR = 0x00000800;
    internal const uint MF_GRAYED = 0x00000001;
    internal const uint TPM_RIGHTBUTTON = 0x0002;
    internal const uint TPM_RETURNCMD = 0x0100;
    internal const uint TPM_NONOTIFY = 0x0080;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetMenuDefaultItem(IntPtr hMenu, uint uItem, uint fByPos);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int TrackPopupMenuEx(IntPtr hMenu, uint fuFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

    // ---------------------------------------------------------------
    // GDI（自绘托盘图标）
    // ---------------------------------------------------------------

    internal const int SM_CXSMICON = 49;
    internal const int SM_CYSMICON = 50;
    internal const uint DIB_RGB_COLORS = 0;
    internal const int BI_RGB = 0;
    internal const int FW_BOLD = 700;
    internal const int DEFAULT_CHARSET = 1;
    internal const int OUT_TT_PRECIS = 4;
    internal const int CLIP_DEFAULT_PRECIS = 0;
    internal const int CLEARTYPE_QUALITY = 5;
    internal const int DEFAULT_PITCH = 0;
    internal const int TRANSPARENT = 1;
    internal const uint DT_CENTER = 0x0001;
    internal const uint DT_VCENTER = 0x0004;
    internal const uint DT_SINGLELINE = 0x0020;
    internal const int NULL_BRUSH = 5;
    internal const int NULL_PEN = 8;
    internal const int IDI_APPLICATION = 32512;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public RECT(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreatePen(int fnPenStyle, int nWidth, uint color);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr GetStockObject(int i);

    [DllImport("gdi32.dll")]
    internal static extern bool RoundRect(IntPtr hdc, int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")]
    internal static extern bool FillRect(IntPtr hdc, ref RECT lprc, IntPtr hbr);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, IntPtr lpvBits);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateFontW(
        int nHeight,
        int nWidth,
        int nEscapement,
        int nOrientation,
        int fnWeight,
        uint fdwItalic,
        uint fdwUnderline,
        uint fdwStrikeOut,
        uint fdwCharSet,
        uint fdwOutputPrecision,
        uint fdwClipPrecision,
        uint fdwQuality,
        uint fdwPitchAndFamily,
        string lpszFace);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int DrawTextW(IntPtr hdc, string lpchText, int cchText, ref RECT lprc, uint format);

    [DllImport("user32.dll")]
    internal static extern int SetTextColor(IntPtr hdc, uint color);

    [DllImport("gdi32.dll")]
    internal static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    /// <summary>从磁盘上的 .ico / .exe / .dll 里加载图标（配 <see cref="LR_LOADFROMFILE"/>）。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr LoadImageW(IntPtr hInstance, string lpszName, uint uType, int cx, int cy, uint fuLoad);

    internal const uint IMAGE_ICON = 1;
    internal const uint LR_LOADFROMFILE = 0x00000010;

    // ---------------------------------------------------------------
    // 托盘图标
    // ---------------------------------------------------------------

    internal const int NIM_ADD = 0x00000000;
    internal const int NIM_MODIFY = 0x00000001;
    internal const int NIM_DELETE = 0x00000002;
    internal const int NIM_SETVERSION = 0x00000004;

    internal const uint NIF_MESSAGE = 0x00000001;
    internal const uint NIF_ICON = 0x00000002;
    internal const uint NIF_TIP = 0x00000004;
    internal const uint NIF_INFO = 0x00000010;

    internal const uint NIIF_INFO = 0x00000001;

    /// <summary>Shell_NotifyIcon 的第三个参数：NOTIFYICON_VERSION_4 需要 guidItem 支持，这里用默认版本。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string? szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string? szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATA lpData);

    // ---------------------------------------------------------------
    // 屏幕取像素 / 截图 + 鼠标输入（--uiprobe 原生外观取证用）
    //
    // 为什么必须抓屏而不是用 RenderTargetBitmap：
    //   Mica / 亚克力是 DWM 在窗口【后面】合成出来的，XAML 自己的位图渲染拿不到它。
    //   只有从桌面 DC 上 BitBlt 才是用户真正看到的那一帧，取到的像素才能证明毛玻璃可见。
    // ---------------------------------------------------------------

    internal const int SRCCOPY = 0x00CC0020;
    internal const int SM_CXSCREEN = 0;
    internal const int SM_CYSCREEN = 1;

    internal const uint MOUSEEVENTF_MOVE = 0x0001;
    internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    internal const uint MOUSEEVENTF_LEFTUP = 0x0004;
    internal const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    internal const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    internal static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height,
        IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    internal static extern bool SetCursorPos(int x, int y);

    /// <summary>窗口客户区左上角换算成屏幕坐标。XAML 元素的 (0,0) 对应的是客户区原点，不是窗口矩形原点。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    /// <summary>把鼠标事件投进系统输入队列（自检里用来做一次真实的分隔条拖动）。</summary>
    internal static uint SendMouse(uint flags)
    {
        var input = new INPUT
        {
            type = 0, // INPUT_MOUSE
            u = new INPUTUNION { mi = new MOUSEINPUT { dx = 0, dy = 0, mouseData = 0, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero } },
        };
        return SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// 把鼠标移到屏幕绝对坐标（用 MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE 注入，
    /// 而不是 SetCursorPos）：脚本拖拽必须产生真正的鼠标移动消息，控件才收得到 PointerMoved。
    /// </summary>
    internal static uint SendMouseMove(int x, int y)
    {
        int screenWidth = Math.Max(1, GetSystemMetrics(SM_CXSCREEN));
        int screenHeight = Math.Max(1, GetSystemMetrics(SM_CYSCREEN));

        var input = new INPUT
        {
            type = 0,
            u = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dx = (int)Math.Round(x * 65535.0 / (screenWidth - 1)),
                    dy = (int)Math.Round(y * 65535.0 / (screenHeight - 1)),
                    mouseData = 0,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };

        return SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>mouse_event 版本的绝对坐标移动。</summary>
    internal static void MoveMouseLegacy(int x, int y)
    {
        int screenWidth = Math.Max(1, GetSystemMetrics(SM_CXSCREEN));
        int screenHeight = Math.Max(1, GetSystemMetrics(SM_CYSCREEN));
        mouse_event(
            MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
            (int)Math.Round(x * 65535.0 / (screenWidth - 1)),
            (int)Math.Round(y * 65535.0 / (screenHeight - 1)),
            0,
            IntPtr.Zero);
    }

    /// <summary>
    /// 老接口 mouse_event。跟 SendInput 是两条独立的注入路径：
    /// 实测这台机器上从 UI 线程用 SendInput 投递的鼠标事件没有进到 WinUI 的指针栈
    ///（SendInput 返回 1，但控件的 PointerPressed 不触发），mouse_event 则可以 ——
    /// 外部脚本用它点「翻译」再点两次「下一句」，文档里的 lastIndex 真的从 0 变成了 2。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);

    /// <summary>把 Win32 错误码翻译成中文，用于「热键注册失败」这类必须可见的提示。</summary>
    internal static string DescribeError(int error)
    {
        return error switch
        {
            0 => "成功",
            1409 => "热键已被其他程序占用（ERROR_HOTKEY_ALREADY_REGISTERED）",
            5 => "拒绝访问（可能被系统或其他程序独占）",
            87 => "参数不正确",
            _ => "Win32 错误码 " + error,
        };
    }
}
