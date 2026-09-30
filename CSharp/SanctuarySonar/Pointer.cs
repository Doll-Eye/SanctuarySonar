// The mouse pointer: the one thing Sanctuary Sonar sends to the game, and only at the owner's
// request (28 Sep 2026: "no mouse in vicinity", so the click comes from here too). Moves the
// pointer to a screen point, and clicks. Nothing else is ever sent.
using System.Runtime.InteropServices;

namespace SanctuarySonar.Shell;

public static class Pointer
{
    public static bool moveTo(int screenX, int screenY) => SetCursorPos(screenX, screenY);

    public static (int x, int y) position()
    {
        GetCursorPos(out var p);
        return (p.x, p.y);
    }

    /// <summary>A left click where the pointer is.</summary>
    public static void click()
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_MOUSE; inputs[0].mi.dwFlags = MOUSEEVENTF_LEFTDOWN;
        inputs[1].type = INPUT_MOUSE; inputs[1].mi.dwFlags = MOUSEEVENTF_LEFTUP;
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Brings the game to the front so the pointer and the click land on it.</summary>
    public static bool bringToFront(nint window) => SetForegroundWindow(window);

    public static bool isInFront(nint window) => GetForegroundWindow() == window;

    const uint INPUT_MOUSE = 0, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint dwExtraInfo; }
    // INPUT's union is as large as MOUSEINPUT (its biggest member), so this layout matches: 40 bytes on x64.
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public MOUSEINPUT mi; }
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
}
