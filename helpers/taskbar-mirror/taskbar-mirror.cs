// taskbar-mirror: lets the Zebar bar mirror the native Windows taskbar.
//
//   taskbar-mirror.exe watch       Runs until stdin closes. Whenever the
//                                  taskbar's app buttons change, prints one
//                                  JSON line: [{ name, appId, windows,
//                                  active, icon }] in taskbar order.
//                                  Reads lines from stdin:
//                                    hide <hwnd>,<hwnd>,...   (or "hide")
//                                  the complete set of windows to keep OFF
//                                  the taskbar (the bar sends the windows on
//                                  komorebi workspaces that aren't displayed).
//                                  Windows dropped from the set, and all of
//                                  them when stdin closes, are put back.
//   taskbar-mirror.exe restore     Puts back every window a killed `watch`
//                                  left off the taskbar (from its state file).
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
// - Per-workspace taskbar: komorebi cloaks the windows of hidden workspaces,
//   but the taskbar still lists cloaked windows. ITaskbarList::DeleteTab
//   removes one window's entry (a group just shows one window fewer) and
//   AddTab puts it back; verified on this machine. Because the bar's icons
//   are read from the taskbar, they follow automatically, and Win+1..9
//   counts only the displayed workspace's apps.
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
    // STA: UIA against the taskbar's XAML island returns no elements from an
    // MTA thread (the C# default); PowerShell, which is STA, sees them.
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "watch") { Watch(); return 0; }
        if (args.Length == 1 && args[0] == "debug") { Debug(); return 0; }
        if (args.Length == 1 && args[0] == "restore") { new HiddenTabs().RestoreAll(); return 0; }
        int n;
        if (args.Length == 2 && args[0] == "press" && int.TryParse(args[1], out n) && n >= 0 && n <= 9) { PressWinDigit(n); return 0; }
        Console.Error.WriteLine("usage: taskbar-mirror.exe watch | press <0-9> | restore");
        return 2;
    }

    // ---------------------------------------------------------------- watch

    static void Watch()
    {
        // Exit when the bar (our parent) goes away: it holds our stdin open.
        // Only the latest "hide" line matters (each carries the full set).
        var stdinClosed = new ManualResetEvent(false);
        var hideArrived = new AutoResetEvent(false);
        string pendingHide = null;
        var pendingLock = new object();
        new Thread(() =>
        {
            try
            {
                // Not Console.In: it decodes with the ANSI codepage, which
                // turns a writer's UTF-8 BOM into junk characters that break
                // the first line. StreamReader detects and drops the BOM.
                var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false), true);
                string line;
                while ((line = stdin.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line != "hide" && !line.StartsWith("hide ")) continue;
                    lock (pendingLock) pendingHide = line.Substring(4).Trim();
                    hideArrived.Set();
                }
            }
            catch { }
            stdinClosed.Set();
        }) { IsBackground = true }.Start();

        var tabs = new HiddenTabs();
        var iconCache = new Dictionary<string, string>();
        string last = null;
        string lastActiveAppId = null;
        // What the bar last asked for. Honoured only while komorebi runs:
        // komorebi uncloaks every window when it stops, so they must all be
        // back on the taskbar then, even if the bar is still showing (and
        // sending) its last known state.
        var want = new HashSet<long>();
        bool komorebiUp = KomorebiRunning();
        DateTime lastCheck = DateTime.UtcNow;
        bool firstPass = true; // apply once at start: settles windows a killed predecessor left hidden
        try
        {
            while (!stdinClosed.WaitOne(0))
            {
                try
                {
                    string hide;
                    lock (pendingLock) { hide = pendingHide; pendingHide = null; }
                    bool dirty = firstPass;
                    firstPass = false;
                    if (hide != null) { want = ParseHwnds(hide); dirty = true; }
                    if ((DateTime.UtcNow - lastCheck).TotalSeconds >= 2)
                    {
                        lastCheck = DateTime.UtcNow;
                        bool up = KomorebiRunning();
                        if (up != komorebiUp) { komorebiUp = up; want = new HashSet<long>(); dirty = true; }
                        // The taskbar re-adds a deleted window's button on its
                        // own at times (e.g. Explorer restarting): re-delete.
                        else tabs.Reassert();
                    }
                    if (dirty)
                    {
                        tabs.Apply(komorebiUp ? want : new HashSet<long>());
                        Thread.Sleep(150); // the taskbar updates its buttons asynchronously
                    }

                    var buttons = ReadTaskbarButtons();
                    if (buttons != null) // null = unreadable (flyout open): keep the bar's last list
                    {
                        string active = ForegroundAppId(buttons);
                        if (active != null) lastActiveAppId = active; // keep last real app while the bar itself has focus
                        string json = ToJson(buttons, lastActiveAppId, iconCache);
                        if (json != last) { Console.Out.WriteLine(json); Console.Out.Flush(); last = json; }
                    }
                }
                catch (Exception e) { Console.Error.WriteLine("watch error: " + e.Message); }
                WaitHandle.WaitAny(new WaitHandle[] { stdinClosed, hideArrived }, 600);
            }
        }
        finally { tabs.RestoreAll(); }
    }

    static bool KomorebiRunning()
    {
        var found = System.Diagnostics.Process.GetProcessesByName("komorebi");
        foreach (var p in found) p.Dispose();
        return found.Length > 0;
    }

    static HashSet<long> ParseHwnds(string list)
    {
        var result = new HashSet<long>();
        foreach (string part in list.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            long h;
            if (long.TryParse(part, out h) && h > 0) result.Add(h);
        }
        return result;
    }

    // ------------------------------------------------------ hidden tabs

    [ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITaskbarList
    {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(IntPtr hwnd);
        [PreserveSig] int DeleteTab(IntPtr hwnd);
        [PreserveSig] int ActivateTab(IntPtr hwnd);
        [PreserveSig] int SetActiveAlt(IntPtr hwnd);
    }
    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")] class TaskbarListClass { }

    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);

    // The windows this process has taken off the taskbar. Saved to a file so
    // that if this process is killed (no chance to restore), the next
    // instance still knows to put them back. Each entry records the owning
    // process id too, so a window handle reused by another window after a
    // reboot or app restart is never touched.
    class HiddenTabs
    {
        static readonly string StatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "midnight-eclipse", "taskbar-hidden.txt");

        readonly ITaskbarList list;
        readonly Dictionary<long, uint> hidden = new Dictionary<long, uint>(); // hwnd -> pid when hidden

        public HiddenTabs()
        {
            list = (ITaskbarList)new TaskbarListClass();
            list.HrInit();
            try
            {
                if (File.Exists(StatePath))
                    foreach (string line in File.ReadAllLines(StatePath))
                    {
                        string[] p = line.Split(' ');
                        long h; uint pid;
                        if (p.Length == 2 && long.TryParse(p[0], out h) && uint.TryParse(p[1], out pid) && Alive(h, pid)) hidden[h] = pid;
                    }
            }
            catch (Exception e) { Console.Error.WriteLine("could not read " + StatePath + ": " + e.Message); }
        }

        static bool Alive(long hwnd, uint pid)
        {
            IntPtr h = new IntPtr(hwnd);
            uint owner;
            return IsWindow(h) && GetWindowThreadProcessId(h, out owner) != 0 && owner == pid;
        }

        public void Apply(HashSet<long> want)
        {
            foreach (long h in new List<long>(hidden.Keys))
                if (!want.Contains(h))
                {
                    if (Alive(h, hidden[h])) list.AddTab(new IntPtr(h));
                    hidden.Remove(h);
                }
            foreach (long h in want)
            {
                if (hidden.ContainsKey(h)) continue;
                IntPtr hwnd = new IntPtr(h);
                uint pid;
                if (!IsWindow(hwnd) || GetWindowThreadProcessId(hwnd, out pid) == 0) continue;
                list.DeleteTab(hwnd);
                hidden[h] = pid;
            }
            Save();
        }

        public void Reassert()
        {
            bool pruned = false;
            foreach (long h in new List<long>(hidden.Keys))
            {
                if (Alive(h, hidden[h])) list.DeleteTab(new IntPtr(h));
                else { hidden.Remove(h); pruned = true; }
            }
            if (pruned) Save();
        }

        public void RestoreAll()
        {
            try { Apply(new HashSet<long>()); }
            catch (Exception e) { Console.Error.WriteLine("restore error: " + e.Message); }
        }

        void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
                var lines = new List<string>();
                foreach (var kv in hidden) lines.Add(kv.Key + " " + kv.Value);
                File.WriteAllLines(StatePath, lines.ToArray());
            }
            catch (Exception e) { Console.Error.WriteLine("could not write " + StatePath + ": " + e.Message); }
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
        Console.Out.WriteLine(buttons == null ? "buttons=<unreadable>" : "buttons=" + buttons.Count);
        if (buttons == null) return;
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

    // Returns null when the taskbar can't be read right now, as opposed to an
    // empty list for a taskbar that really has no buttons (an empty workspace
    // with nothing pinned). While a Quick Settings / notification flyout is
    // open, the taskbar's whole UIA tree comes back empty, not even its
    // TaskbarFrame element (verified: 16 descendants -> 0 -> 16), and
    // Shell_TrayWnd drops out of EnumWindows/FindWindowEx. Reporting that as
    // "no buttons" made the bar's icons vanish whenever a flyout was open.
    static List<Button> ReadTaskbarButtons()
    {
        var result = new List<Button>();
        IntPtr bridge = FindTaskbarXamlHost();
        if (bridge == IntPtr.Zero) return null;

        var host = AutomationElement.FromHandle(bridge);
        var frame = new PropertyCondition(AutomationElement.ClassNameProperty, "Taskbar.TaskbarFrameAutomationPeer");
        if (host.FindFirst(TreeScope.Descendants, frame) == null) return null;

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

    static string ToJson(List<Button> buttons, string activeAppId, Dictionary<string, string> iconCache)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < buttons.Count; i++)
        {
            var b = buttons[i];
            string icon;
            if (!iconCache.TryGetValue(b.AppId, out icon)) { icon = IconDataUrl(b.AppId); iconCache[b.AppId] = icon; }
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
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);

    // Zebar's systray provider creates its own hidden "Shell_TrayWnd" window
    // (so apps send it their tray-icon messages), and FindWindow can return
    // that decoy. Pick the Shell_TrayWnd that actually hosts the taskbar's
    // XAML island.
    //
    // The result is cached and reused while that window exists: while a
    // Quick Settings / notification flyout is open, EnumWindows doesn't list
    // the real Shell_TrayWnd at all (verified: only Zebar's decoy comes
    // back), which made the bar's icons vanish whenever a flyout was open.
    static IntPtr cachedXamlHost = IntPtr.Zero;
    const string XamlHostClass = "Windows.UI.Composition.DesktopWindowContentBridge";

    static IntPtr FindTaskbarXamlHost()
    {
        // Still the same window? (Explorer restarting destroys it; checking
        // the class and that it still sits under a Shell_TrayWnd guards
        // against its handle being reused by another window.)
        if (cachedXamlHost != IntPtr.Zero && IsWindow(cachedXamlHost)
            && ClassOf(cachedXamlHost) == XamlHostClass
            && ClassOf(GetAncestor(cachedXamlHost, 2 /* GA_ROOT */)) == "Shell_TrayWnd")
            return cachedXamlHost;

        IntPtr host = IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            if (ClassOf(h) != "Shell_TrayWnd") return true;
            host = FindDescendant(h, XamlHostClass);
            return host == IntPtr.Zero;
        }, IntPtr.Zero);
        // Fallback for a process that starts while a flyout is open (no cache
        // yet): FindWindowEx walks the Shell_TrayWnd windows by class.
        for (IntPtr h = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
             host == IntPtr.Zero && h != IntPtr.Zero;
             h = FindWindowEx(IntPtr.Zero, h, "Shell_TrayWnd", null))
            host = FindDescendant(h, XamlHostClass);
        cachedXamlHost = host;
        return host;
    }

    static string ClassOf(IntPtr hwnd)
    {
        var name = new StringBuilder(256);
        GetClassName(hwnd, name, name.Capacity);
        return name.ToString();
    }
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    // The XAML island host isn't always a direct child of Shell_TrayWnd, so
    // search all descendants (EnumChildWindows is recursive).
    //
    // While a Quick Settings / notification flyout is open, EnumChildWindows
    // on the real Shell_TrayWnd skips the XAML host window itself but still
    // returns its child (Windows.UI.Input.InputSite.WindowClass), and
    // GetWindow/FindWindowEx see no children at all (verified on this
    // machine with the Wi-Fi flyout open). So also accept a descendant whose
    // parent has the class, and return that parent.
    static IntPtr FindDescendant(IntPtr parent, string className)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(parent, (h, l) =>
        {
            if (ClassOf(h) == className) { found = h; return false; }
            IntPtr up = GetAncestor(h, 1 /* GA_PARENT */);
            if (up != IntPtr.Zero && up != parent && ClassOf(up) == className) { found = up; return false; }
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
