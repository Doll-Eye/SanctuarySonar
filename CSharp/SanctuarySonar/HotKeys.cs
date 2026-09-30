// Global hot keys: Control-Shift-Alt + a key, registered on their own thread with a message
// loop (WM_HOTKEY goes to the registering thread). Actions are queued to the worker.
using System.Runtime.InteropServices;

namespace SanctuarySonar.Shell;

public sealed class HotKeys
{
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;
    readonly List<(string name, uint vk, Action action)> keys = new();
    readonly Action<Action> post;

    public HotKeys(Action<Action> postToWorker) { post = postToWorker; }

    public void register(string name, char key, Action action) => keys.Add((name, (uint)char.ToUpperInvariant(key), action));
    public void register(string name, uint virtualKey, Action action) => keys.Add((name, virtualKey, action));

    public void start()
    {
        var thread = new Thread(loop) { IsBackground = true, Name = "hotkeys" };
        thread.Start();
    }

    void loop()
    {
        for (int i = 0; i < keys.Count; i++)
        {
            if (!RegisterHotKey(0, i + 1, MOD_CONTROL | MOD_SHIFT | MOD_ALT | MOD_NOREPEAT, keys[i].vk))
                Log.log($"Hot key {keys[i].name} could not be registered (in use elsewhere?)");
        }
        Log.log("Hot keys: Control-Shift-Alt + " + string.Join(", ", keys.Select(k => $"{(k.vk == 0xBB ? "=" : k.vk == 0xBD ? "-" : ((char)k.vk).ToString())} {k.name}")));
        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message == 0x0312 /* WM_HOTKEY */)
            {
                int id = (int)msg.wParam - 1;
                if (id >= 0 && id < keys.Count) { var k = keys[id]; Log.log($"Hot key: {k.name}"); post(k.action); }
            }
            TranslateMessage(ref msg); DispatchMessage(ref msg);
        }
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, nint hWnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern nint DispatchMessage(ref MSG msg);
    [StructLayout(LayoutKind.Sequential)] struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public int px, py; }
}
