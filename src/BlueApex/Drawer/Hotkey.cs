using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using BlueApex.Desktop;

namespace BlueApex.Drawer;

/// <summary>A global hotkey such as "Ctrl+Shift+Space", delivered to a WPF window.</summary>
internal sealed class Hotkey : IDisposable
{
    private const int Id = 0xB1A;
    private readonly HwndSource _source;
    private readonly Action _pressed;

    public Hotkey(Window window, string spec, Action pressed)
    {
        _pressed = pressed;
        var handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("창 핸들이 없습니다.");
        _source.AddHook(Hook);

        var (modifiers, key) = Parse(spec);
        if (!NativeMethods.RegisterHotKey(handle, Id, modifiers | NativeMethods.MOD_NOREPEAT, key))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"단축키 {spec}을(를) 등록하지 못했습니다(다른 프로그램이 쓰고 있을 수 있음).");
    }

    private static (uint Modifiers, uint Key) Parse(string spec)
    {
        uint modifiers = 0;
        Key key = Key.None;
        foreach (var part in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= NativeMethods.MOD_CONTROL; break;
                case "shift": modifiers |= NativeMethods.MOD_SHIFT; break;
                case "alt": modifiers |= NativeMethods.MOD_ALT; break;
                case "win": modifiers |= NativeMethods.MOD_WIN; break;
                default: key = (Key)Enum.Parse(typeof(Key), part, ignoreCase: true); break;
            }
        }
        if (key == Key.None) throw new FormatException($"단축키에 키가 없습니다: {spec}");
        return (modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == Id)
        {
            _pressed();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        NativeMethods.UnregisterHotKey(_source.Handle, Id);
        _source.RemoveHook(Hook);
    }
}
