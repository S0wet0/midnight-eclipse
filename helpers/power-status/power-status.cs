// power-status: live power state for the Zebar bar, straight from Windows.
//
//   power-status.exe watch   Runs until stdin closes. Prints one JSON line
//                            at start and whenever anything changes:
//                            {"hasBattery":true,"acOnline":true,
//                             "charging":true,"percent":44,
//                             "energySaver":false}
//
// Why: Zebar's battery provider refreshes only every 60s by default and
// breaks when the charger is plugged or unplugged (see index.html), so
// plug/unplug took a minute or more to show. GetSystemPowerStatus reflects
// the charger immediately and also carries the energy saver flag, which
// Zebar doesn't report at all. It's a cheap call, so polling it every 500ms
// is simpler than a power-broadcast window and still feels instant.
//
// Fields: acOnline = charger connected (ACLineStatus 1); charging = Windows
// reports the battery charging (BatteryFlag bit 8); percent = charge 0-100,
// or null when unknown (255); hasBattery = false when BatteryFlag says there
// is no system battery (128) or the state is unknown (255); energySaver =
// SystemStatusFlag 1.
//
// Build with build.ps1 (Windows' built-in .NET Framework compiler, C# 5).
// Exit codes: 0 ok, 2 usage.

using System;
using System.Runtime.InteropServices;
using System.Threading;

static class PowerStatus
{
    static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "watch") { Watch(); return 0; }
        Console.Error.WriteLine("usage: power-status.exe watch");
        return 2;
    }

    static void Watch()
    {
        // Exit when the bar (our parent) goes away: it holds our stdin open.
        var stdinClosed = new ManualResetEvent(false);
        new Thread(() => { try { while (Console.In.Read() != -1) { } } catch { } stdinClosed.Set(); }) { IsBackground = true }.Start();
        var quit = ClaimSingleInstance("MidnightEclipse.power-status");

        string last = null;
        while (!stdinClosed.WaitOne(0) && !quit.WaitOne(0))
        {
            SYSTEM_POWER_STATUS s;
            if (GetSystemPowerStatus(out s))
            {
                bool hasBattery = s.BatteryFlag != 255 && (s.BatteryFlag & 128) == 0;
                string json = "{\"hasBattery\":" + Bool(hasBattery)
                    + ",\"acOnline\":" + Bool(s.ACLineStatus == 1)
                    + ",\"charging\":" + Bool(s.BatteryFlag != 255 && (s.BatteryFlag & 8) != 0)
                    + ",\"percent\":" + (s.BatteryLifePercent == 255 ? "null" : s.BatteryLifePercent.ToString())
                    + ",\"energySaver\":" + Bool(s.SystemStatusFlag == 1) + "}";
                if (json != last) { Console.Out.WriteLine(json); Console.Out.Flush(); last = json; }
            }
            else Console.Error.WriteLine("GetSystemPowerStatus failed: " + Marshal.GetLastWin32Error());
            WaitHandle.WaitAny(new WaitHandle[] { stdinClosed, quit }, 500);
        }
    }

    // One watcher per session. A newer instance means the bar was reopened or
    // reloaded without Zebar restarting, and Zebar doesn't stop the processes
    // a closed widget started; the newer one asks the older to quit.
    static Mutex instanceMutex; // held for the process's lifetime
    static EventWaitHandle ClaimSingleInstance(string name)
    {
        var quit = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\" + name + ".quit");
        instanceMutex = new Mutex(false, @"Local\" + name + ".instance");
        bool owned;
        try { owned = instanceMutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            quit.Set();
            try { owned = instanceMutex.WaitOne(10000); } catch (AbandonedMutexException) { owned = true; }
            if (!owned) Console.Error.WriteLine("an older instance didn't exit; continuing anyway");
        }
        quit.Reset();
        return quit;
    }

    static string Bool(bool b) { return b ? "true" : "false"; }

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
