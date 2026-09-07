// taskbar-mirror: lets the Zebar bar mirror the native Windows taskbar.
//
//   taskbar-mirror.exe watch       Runs until stdin closes. Every 600ms
//                                  prints one JSON line: [{ name, appId,
//                                  windows, active, icon }] in taskbar
//                                  order.
//   taskbar-mirror.exe press <0-9> Sends Win+N (Win+0 = 10th button), so a
//                                  click in the bar does exactly what the
//                                  taskbar/Win+N does: activate, minimize if
//                                  already active, cycle a group's windows.
//
// Why these mechanisms:
// - The Windows 11 taskbar buttons live in an embedded XAML island. .NET's
//   UIA client can't see them walking down from Shell_TrayWnd, but can when
//   asked about the island's host window (DesktopWindowContentBridge)
//   directly. Buttons are Taskbar.TaskListButtonAutomationPeer elements whose
//   AutomationId is "Appid: <AppUserModelID>". Left-to-right order is the
//   order Win+1..9 counts in.
// - Those buttons expose no Invoke pattern, so they can't be pressed via UIA;
//   hence `press` sends Win+N instead.
// - Icons come from the shell item "shell:AppsFolder\<AppUserModelID>", the
//   same identity the taskbar uses, via IShellItemImageFactory.
//
// Build with build.ps1 (Windows' built-in .NET Framework compiler).
// Exit codes: 0 ok, 2 usage.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

static class TaskbarMirror
{
    static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "watch") { Watch(); return 0; }
        if (args.Length == 1 && args[0] == "debug") { Debug(); return 0; }
        int n;
        if (args.Length == 2 && args[0] == "press" && int.TryParse(args[1], out n) && n >= 0 && n <= 9) { PressWinDigit(n); return 0; }
        Console.Error.WriteLine("usage: taskbar-mirror.exe watch | press <0-9>");
        return 2;
    }

    // ---------------------------------------------------------------- watch

    static void Watch()
    {
        // Exit when the bar (our parent) goes away: it holds our stdin open.
        var stdinClosed = new ManualResetEvent(false);
        new Thread(() => { try { while (Console.In.Read() != -1) { } } catch { } stdinClosed.Set(); }) { IsBackground = true }.Start();

        string lastActiveAppId = null;
        while (!stdinClosed.WaitOne(0))
        {
            try
            {
                var buttons = ReadTaskbarButtons();
                string active = ForegroundAppId(buttons);
                if (active != null) lastActiveAppId = active; // keep last real app while the bar itself has focus
                Console.Out.WriteLine(ToJson(buttons, lastActiveAppId));
                Console.Out.Flush();
            }
            catch (Exception e) { Console.Error.WriteLine("watch error: " + e.Message); }
            stdinClosed.WaitOne(600);
        }
    }

    static void Debug()
    {
        IntPtr bridge = FindTaskbarXamlHost();
        Console.Out.WriteLine("apartment=" + Thread.CurrentThread.GetApartmentState() + " bridge=" + bridge);
        if (bridge == IntPtr.Zero) return;
        var all = AutomationElement.FromHandle(bridge).FindAll(TreeScope.Descendants, Condition.TrueCondition);
        Console.Out.WriteLine("descendants=" + all.Count);
        foreach (AutomationElement e in all) Console.Out.WriteLine("  " + e.Current.ClassName + " | " + e.Current.AutomationId);
        var buttons = ReadTaskbarButtons();
        Console.Out.WriteLine("buttons=" + buttons.Count);
        IntPtr fg = GetForegroundWindow();
        uint pid;
        GetWindowThreadProcessId(fg, out pid);
        Console.Out.WriteLine("foreground hwnd=" + fg + " pid=" + pid + " exe=" + ProcessExeName(pid));
        Console.Out.WriteLine("  window AUMID  = " + (WindowAppUserModelId(fg) ?? "<none>"));
        Console.Out.WriteLine("  package AUMID = " + (PackagedAppUserModelId(pid) ?? "<none>"));
        Console.Out.WriteLine("  resolved      = " + (ForegroundAppId(buttons) ?? "<no match>"));
    }

    class Button { public string Name; public string AppId; public int Windows; public double Left; }

    static readonly Regex RunningSuffix = new Regex(@"^(.*) - (\d+) running windows?$", RegexOptions.Singleline);

    static List<Button> ReadTaskbarButtons()
    {
        var result = new List<Button>();
        IntPtr bridge = FindTaskbarXamlHost();
        if (bridge == IntPtr.Zero) return result;

        var host = AutomationElement.FromHandle(bridge);
        var condition = new PropertyCondition(AutomationElement.ClassNameProperty, "Taskbar.TaskListButtonAutomationPeer");
        foreach (AutomationElement element in host.FindAll(TreeScope.Descendants, condition))
        {
            var info = element.Current;
            string appId = info.AutomationId.StartsWith("Appid: ") ? info.AutomationId.Substring(7) : info.AutomationId;
            var match = RunningSuffix.Match(info.Name);
            result.Add(new Button
            {
                Name = match.Success ? match.Groups[1].Value : info.Name,
                Windows = match.Success ? int.Parse(match.Groups[2].Value) : 0,   // 0 = pinned, not running
                AppId = appId,
                Left = info.BoundingRectangle.Left,
            });
        }
        result.Sort((a, b) => a.Left.CompareTo(b.Left));
        return result;
    }

    // Which taskbar button the foreground window belongs to, resolved the way
    // the taskbar groups windows: (1) the window's own AppUserModelID
    // property; (2) for packaged apps (Claude, Windows Terminal), the
    // process's package AUMID; (3) the exe name at the end of an implicit
    // path-based app ID; (4) last resort, exe base name == button name.
    // Returns null for the bar itself or no match.
    static string ForegroundAppId(List<Button> buttons)
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        uint pid;
        GetWindowThreadProcessId(hwnd, out pid);
        string exe = ProcessExeName(pid);
        if (exe == null || exe.Equals("zebar.exe", StringComparison.OrdinalIgnoreCase)) return null;

        foreach (string aumid in new[] { WindowAppUserModelId(hwnd), PackagedAppUserModelId(pid) })
            if (aumid != null)
                foreach (var b in buttons)
                    if (b.AppId.Equals(aumid, StringComparison.OrdinalIgnoreCase)) return b.AppId;
        foreach (var b in buttons)
            if (b.AppId.EndsWith("\\" + exe, StringComparison.OrdinalIgnoreCase)) return b.AppId;
        string baseName = Path.GetFileNameWithoutExtension(exe);
        foreach (var b in buttons)
            if (b.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase)) return b.AppId;
        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder aumid);

    static string PackagedAppUserModelId(uint pid)
    {
        IntPtr handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            uint length = 512;
            var sb = new StringBuilder((int)length);
            return GetApplicationUserModelId(handle, ref length, sb) == 0 ? sb.ToString() : null; // non-zero = not packaged
        }
        finally { CloseHandle(handle); }
    }

    static string ToJson(List<Button> buttons, string activeAppId)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < buttons.Count; i++)
        {
            var b = buttons[i];
            string icon = IconDataUrl(b.AppId);
            if (i > 0) sb.Append(',');
            sb.Append("{\"name\":").Append(Quote(b.Name))
              .Append(",\"appId\":").Append(Quote(b.AppId))
              .Append(",\"windows\":").Append(b.Windows)
              .Append(",\"active\":").Append(activeAppId != null && b.AppId == activeAppId ? "true" : "false")
              .Append(",\"icon\":").Append(icon == null ? "null" : Quote(icon))
              .Append('}');
        }
        return sb.Append(']').ToString();
    }

    static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    // ---------------------------------------------------------------- icons

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory { [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr phbm); }

    [StructLayout(LayoutKind.Sequential)] struct NativeSize { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out BITMAP bmp);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;

    static string IconDataUrl(string appId)
    {
        IntPtr hbmp = IntPtr.Zero;
        try
        {
            Guid iid = typeof(IShellItemImageFactory).GUID;
            IShellItemImageFactory factory;
            SHCreateItemFromParsingName("shell:AppsFolder\\" + appId, IntPtr.Zero, ref iid, out factory);
            if (factory.GetImage(new NativeSize { cx = 32, cy = 32 }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbmp) != 0) return null;

            // The shell returns a 32bpp premultiplied-alpha DIB section.
            // Image.FromHbitmap would drop the alpha channel, so wrap the bits
            // directly (a positive height means bottom-up rows).
            BITMAP info;
            GetObject(hbmp, Marshal.SizeOf(typeof(BITMAP)), out info);
            if (info.bmBits == IntPtr.Zero || info.bmBitsPixel != 32) return null;
            int stride = info.bmWidthBytes;
            IntPtr scan0 = info.bmBits;
            if (info.bmHeight > 0) { scan0 = new IntPtr(scan0.ToInt64() + (long)(info.bmHeight - 1) * stride); stride = -stride; }
            int height = Math.Abs(info.bmHeight);
            using (var wrapped = new Bitmap(info.bmWidth, height, stride, PixelFormat.Format32bppPArgb, scan0))
            using (var copy = new Bitmap(info.bmWidth, height, PixelFormat.Format32bppArgb))
            using (var ms = new MemoryStream())
            {
                using (var g = Graphics.FromImage(copy)) g.DrawImageUnscaled(wrapped, 0, 0);
                copy.Save(ms, ImageFormat.Png);
                return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
            }
        }
        catch { return null; }
        finally { if (hbmp != IntPtr.Zero) DeleteObject(hbmp); }
    }

    // ------------------------------------------------------ foreground app id

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    struct PropVariant { public ushort vt; public ushort r1, r2, r3; public IntPtr p; public IntPtr p2; }

    [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pv);

    static string WindowAppUserModelId(IntPtr hwnd)
    {
        try
        {
            Guid iid = typeof(IPropertyStore).GUID;
            IPropertyStore store;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0 || store == null) return null;
            var key = new PropertyKey { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 }; // PKEY_AppUserModel_ID
            PropVariant value;
            if (store.GetValue(ref key, out value) != 0) return null;
            string id = value.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.p) : null;
            PropVariantClear(ref value);
            return string.IsNullOrEmpty(id) ? null : id;
        }
        catch { return null; }
    }

    static string ProcessExeName(uint pid)
    {
        IntPtr handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int len = sb.Capacity;
            return QueryFullProcessImageName(handle, 0, sb, ref len) ? Path.GetFileName(sb.ToString()) : null;
        }
        finally { CloseHandle(handle); }
    }

    // ---------------------------------------------------------------- press

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public KEYBDINPUT ki; public long padding; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);

    static void PressWinDigit(int digit)
    {
        const ushort VK_LWIN = 0x5B;
        ushort vk = (ushort)(0x30 + digit);
        var keys = new[] { Key(VK_LWIN, false), Key(vk, false), Key(vk, true), Key(VK_LWIN, true) };
        SendInput((uint)keys.Length, keys, Marshal.SizeOf(typeof(INPUT)));
    }

    static INPUT Key(ushort vk, bool up)
    {
        return new INPUT { type = 1 /* INPUT_KEYBOARD */, ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 2u /* KEYEVENTF_KEYUP */ : 0u } };
    }

    // ---------------------------------------------------------------- win32

    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);

    static IntPtr FindTaskbarXamlHost()
    {
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        return tray == IntPtr.Zero ? IntPtr.Zero : FindDescendant(tray, "Windows.UI.Composition.DesktopWindowContentBridge");
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    // The XAML island host isn't a direct child of Shell_TrayWnd, so search
    // all descendants (EnumChildWindows is recursive).
    static IntPtr FindDescendant(IntPtr parent, string className)
    {
        IntPtr found = IntPtr.Zero;
        var name = new StringBuilder(256);
        EnumChildWindows(parent, (h, l) =>
        {
            name.Clear();
            GetClassName(h, name, name.Capacity);
            if (name.ToString() == className) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
}
