using System;
using System.Runtime.InteropServices;

namespace SharedMouse
{
    static class Native
    {
        // ---- low-level hooks ----

        public const int WH_KEYBOARD_LL = 13;
        public const int WH_MOUSE_LL = 14;

        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
        public const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;
        public const int WM_MOUSEWHEEL = 0x020A;
        public const int WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C;
        public const int WM_MOUSEHWHEEL = 0x020E;
        public const int WM_SETCURSOR = 0x0020;

        public const uint LLKHF_EXTENDED = 0x01, LLKHF_INJECTED = 0x10, LLKHF_UP = 0x80;
        public const uint LLMHF_INJECTED = 0x01;

        public delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X, Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode, scanCode, flags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData, flags, time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        // ---- input injection ----

        public const int INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;

        public const uint MOUSEEVENTF_MOVE = 0x0001;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
        public const uint MOUSEEVENTF_XDOWN = 0x0080, MOUSEEVENTF_XUP = 0x0100;
        public const uint MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000;
        public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;

        public const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_SCANCODE = 0x0008;

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx, dy, mouseData;
            public uint dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public int type;
            public InputUnion u;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        // ---- cursor & screens ----

        // ---- clipboard ----

        public const int WM_CLIPBOARDUPDATE = 0x031D;

        [DllImport("user32.dll", SetLastError = true)] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern IntPtr SetCursor(IntPtr hCursor);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);

        const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

        public const uint MONITOR_DEFAULTTONULL = 0, MONITOR_DEFAULTTOPRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor, rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        public static RECT VirtualScreen()
        {
            int x = GetSystemMetrics(SM_XVIRTUALSCREEN), y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            return new RECT { Left = x, Top = y, Right = x + GetSystemMetrics(SM_CXVIRTUALSCREEN), Bottom = y + GetSystemMetrics(SM_CYVIRTUALSCREEN) };
        }

        /// <summary>Bounds of the monitor containing (or nearest to) the point.</summary>
        public static RECT MonitorRect(POINT p, uint flags = MONITOR_DEFAULTTONEAREST)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(MonitorFromPoint(p, flags), ref mi);
            return mi.rcMonitor;
        }

        public static bool IsOnAnyMonitor(POINT p) => MonitorFromPoint(p, MONITOR_DEFAULTTONULL) != IntPtr.Zero;

        // ---- responsiveness ----

        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint uPeriod);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr hProcess, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version, ControlMask, StateMask;
        }

        const int ProcessPowerThrottling = 4;
        const uint THROTTLING_EXECUTION_SPEED = 0x1, THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;

        /// <summary>
        /// SharedMouse sits in the background but must react within a millisecond or two: ask for 1 ms timers, and opt out
        /// of Windows 11's background throttling (efficiency cores, coarse timers for windows that aren't visible).
        /// </summary>
        public static void PreferLowLatency()
        {
            timeBeginPeriod(1);
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = 1,
                ControlMask = THROTTLING_EXECUTION_SPEED | THROTTLING_IGNORE_TIMER_RESOLUTION,
                StateMask = 0,
            };
            int size = Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>();
            if (!SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, size))
            {
                state.ControlMask = THROTTLING_EXECUTION_SPEED; // Windows 10 doesn't know the timer flag
                SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, size);
            }
            try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.AboveNormal; }
            catch (System.ComponentModel.Win32Exception) { }
        }

        // ---- DPI: work in physical pixels so coordinates match between hooks, SetCursorPos and SendInput ----

        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        public static void MakeDpiAware()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(new IntPtr(-4))) // PER_MONITOR_AWARE_V2
                    return;
            }
            catch (EntryPointNotFoundException) { }
            SetProcessDPIAware();
        }

        // ---- Bluetooth (bthprops.cpl / ws2_32.dll) ----

        [StructLayout(LayoutKind.Sequential)]
        public struct BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            public int dwSize;
            public int fReturnAuthenticated, fReturnRemembered, fReturnUnknown, fReturnConnected, fIssueInquiry;
            public byte cTimeoutMultiplier;
            public IntPtr hRadio;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEMTIME
        {
            public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct BLUETOOTH_DEVICE_INFO
        {
            public int dwSize;
            public ulong Address;
            public uint ulClassofDevice;
            public int fConnected, fRemembered, fAuthenticated;
            public SYSTEMTIME stLastSeen, stLastUsed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
        }

        [DllImport("bthprops.cpl", SetLastError = true)]
        public static extern IntPtr BluetoothFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS pbtsp, ref BLUETOOTH_DEVICE_INFO pbtdi);

        [DllImport("bthprops.cpl", SetLastError = true)]
        public static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BLUETOOTH_DEVICE_INFO pbtdi);

        [DllImport("bthprops.cpl", SetLastError = true)]
        public static extern bool BluetoothFindDeviceClose(IntPtr hFind);

        public const int NS_BTH = 16;
        public const int RNRSERVICE_REGISTER = 0, RNRSERVICE_DELETE = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct WSAQUERYSET
        {
            public int dwSize;
            public IntPtr lpszServiceInstanceName;
            public IntPtr lpServiceClassId;
            public IntPtr lpVersion;
            public IntPtr lpszComment;
            public int dwNameSpace;
            public IntPtr lpNSProviderId;
            public IntPtr lpszContext;
            public int dwNumberOfProtocols;
            public IntPtr lpafpProtocols;
            public IntPtr lpszQueryString;
            public int dwNumberOfCsAddrs;
            public IntPtr lpcsaBuffer;
            public int dwOutputFlags;
            public IntPtr lpBlob;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SOCKET_ADDRESS
        {
            public IntPtr lpSockaddr;
            public int iSockaddrLength;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CSADDR_INFO
        {
            public SOCKET_ADDRESS LocalAddr, RemoteAddr;
            public int iSocketType, iProtocol;
        }

        [DllImport("ws2_32.dll", SetLastError = true)]
        public static extern int WSASetServiceW(ref WSAQUERYSET lpqsRegInfo, int essoperation, int dwControlFlags);
    }
}
