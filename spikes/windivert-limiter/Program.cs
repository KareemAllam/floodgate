using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace FloodgateSpike;

internal static class Program
{
    private static int Main(string[] args)
    {
        Options? options = Options.Parse(args);
        if (options is null)
        {
            return 64;
        }
        return new Spike(options).Run();
    }
}

internal sealed record Options(string Process, int LimitKBps, int Seconds, int CrashAfter, double MaxQueueSeconds)
{
    private const string Usage =
        "Usage: FloodgateSpike [--process <name>] [--limit-kbps <n>] [--seconds <n>] [--crash-after <n>] [--queue-seconds <x>]\n" +
        "  --process        process name to limit, without .exe (default: msedge)\n" +
        "  --limit-kbps     download limit in KB/s, base 1024 (default: 500)\n" +
        "  --seconds        stop by itself after n seconds (default: run until Ctrl+C)\n" +
        "  --crash-after    kill this program abruptly after n seconds, to test what happens to traffic (default: off)\n" +
        "  --queue-seconds  longest a packet may be held before it is dropped (default: 1.0)";

    public static Options? Parse(string[] args)
    {
        string process = "msedge";
        int limit = 500;
        int seconds = 0;
        int crash = 0;
        double queue = 1.0;

        for (int i = 0; i < args.Length; i++)
        {
            string name = args[i];
            string? value = i + 1 < args.Length ? args[i + 1] : null;
            bool ok = value is not null;
            switch (name)
            {
                case "--process":
                    if (ok) { process = value!; i++; } else { ok = false; }
                    break;
                case "--limit-kbps":
                    ok = ok && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) && limit >= 1;
                    i++;
                    break;
                case "--seconds":
                    ok = ok && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds) && seconds >= 0;
                    i++;
                    break;
                case "--crash-after":
                    ok = ok && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out crash) && crash >= 0;
                    i++;
                    break;
                case "--queue-seconds":
                    ok = ok && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out queue) && queue > 0;
                    i++;
                    break;
                default:
                    ok = false;
                    break;
            }
            if (!ok)
            {
                Console.WriteLine($"Problem with argument '{name}'.");
                Console.WriteLine(Usage);
                return null;
            }
        }
        return new Options(process, limit, seconds, crash, queue);
    }
}

/// <summary>
/// Throwaway spike. Maps TCP flows to process IDs with the WinDivert FLOW layer, then intercepts inbound
/// TCP packets on the NETWORK layer and paces the ones owned by the target application to the limit.
/// Everything else is re-injected immediately.
/// Known simplifications: TCP only, download only, IPv4 and IPv6 matched by port pair only, flows that
/// began before the program started are not matched.
/// </summary>
internal sealed unsafe class Spike
{
    private sealed class Held
    {
        public byte[] Data = Array.Empty<byte>();
        public int Len;
        public WinDivertNative.Address Addr;
        public long ReleaseAt;
    }

    private readonly Options _o;
    private readonly Pacer _pacer;
    private readonly ConcurrentDictionary<uint, uint> _flows = new();
    private readonly ConcurrentQueue<Held> _queue = new();
    private readonly List<(double Arrived, double Released, double Cpu)> _samples = new();
    private volatile HashSet<uint> _targets = new();
    private volatile bool _stop;
    private IntPtr _flowHandle = IntPtr.Zero;
    private IntPtr _netHandle = IntPtr.Zero;

    private long _arrivedBytes;
    private long _releasedBytes;
    private long _passBytes;
    private long _drops;
    private long _unmatched;
    private long _queuedBytes;

    public Spike(Options options)
    {
        _o = options;
        _pacer = new Pacer(options.LimitKBps * 1024L, options.MaxQueueSeconds);
    }

    public int Run()
    {
        bool admin = IsAdmin();
        Console.WriteLine("Floodgate spike: WinDivert per-application download limiter");
        Console.WriteLine($"OS: {RuntimeInformation.OSDescription} | process arch: {RuntimeInformation.ProcessArchitecture} | admin: {admin}");
        Console.WriteLine($"Target process: {_o.Process} | download limit: {_o.LimitKBps} KB/s | queue cap: {_o.MaxQueueSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s");

        if (!admin)
        {
            Console.WriteLine("RESULT: not started. Run this PowerShell window as Administrator.");
            return 3;
        }
        string dir = AppContext.BaseDirectory;
        if (!File.Exists(Path.Combine(dir, "WinDivert.dll")) || !File.Exists(Path.Combine(dir, "WinDivert64.sys")))
        {
            Console.WriteLine("RESULT: not started. WinDivert.dll / WinDivert64.sys missing next to the program. Run setup.ps1 first.");
            return 4;
        }

        WinDivertNative.timeBeginPeriod(1);
        try
        {
            return RunCore();
        }
        finally
        {
            WinDivertNative.timeEndPeriod(1);
        }
    }

    private int RunCore()
    {
        _flowHandle = WinDivertNative.Open("tcp", WinDivertNative.LayerFlow, 0,
            WinDivertNative.FlagSniff | WinDivertNative.FlagRecvOnly);
        if (_flowHandle == WinDivertNative.InvalidHandle)
        {
            int err = Marshal.GetLastWin32Error();
            Console.WriteLine($"RESULT: driver or flow-layer open FAILED. {WinDivertNative.Describe(err)}");
            return 10;
        }

        _netHandle = WinDivertNative.Open("inbound and tcp and !loopback", WinDivertNative.LayerNetwork, 0, 0);
        if (_netHandle == WinDivertNative.InvalidHandle)
        {
            int err = Marshal.GetLastWin32Error();
            Console.WriteLine($"RESULT: network-layer open FAILED. {WinDivertNative.Describe(err)}");
            WinDivertNative.Close(_flowHandle);
            return 11;
        }

        Console.WriteLine("RESULT: driver loaded and both WinDivert handles opened.");
        Console.WriteLine("Start the download in the target app NOW. Connections that began before this point are not matched.");
        Console.WriteLine("Press Ctrl+C to stop. Stopping closes the handles, so traffic should flow freely again.");
        Console.WriteLine();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Stop();
        };

        var threads = new List<Thread>
        {
            new(FlowLoop) { IsBackground = true, Name = "flow" },
            new(PidRefreshLoop) { IsBackground = true, Name = "pids" },
            new(CaptureLoop) { IsBackground = true, Name = "capture" },
            new(SendLoop) { IsBackground = true, Name = "send" },
            new(StatsLoop) { IsBackground = true, Name = "stats" },
        };
        foreach (Thread t in threads)
        {
            t.Start();
        }

        var clock = Stopwatch.StartNew();
        while (!_stop)
        {
            Thread.Sleep(100);
            double elapsed = clock.Elapsed.TotalSeconds;
            if (_o.Seconds > 0 && elapsed >= _o.Seconds)
            {
                Stop();
            }
            if (_o.CrashAfter > 0 && elapsed >= _o.CrashAfter)
            {
                Console.WriteLine("CRASH TEST: killing this program abruptly now. Watch whether the download speeds back up.");
                Console.Out.Flush();
                Process.GetCurrentProcess().Kill();
            }
        }

        foreach (Thread t in threads)
        {
            t.Join(2000);
        }
        WinDivertNative.Close(_netHandle);
        WinDivertNative.Close(_flowHandle);

        PrintSummary();
        return 0;
    }

    private void Stop()
    {
        if (_stop)
        {
            return;
        }
        _stop = true;
        if (_flowHandle != IntPtr.Zero) { WinDivertNative.Shutdown(_flowHandle, WinDivertNative.ShutdownBoth); }
        if (_netHandle != IntPtr.Zero) { WinDivertNative.Shutdown(_netHandle, WinDivertNative.ShutdownBoth); }
    }

    // Maps (local port, remote port) -> process ID using flow established/deleted events.
    private void FlowLoop()
    {
        int errors = 0;
        while (!_stop)
        {
            WinDivertNative.Address addr;
            if (!WinDivertNative.Recv(_flowHandle, null, 0, null, &addr))
            {
                if (_stop) { break; }
                int e = Marshal.GetLastWin32Error();
                if (++errors > 200)
                {
                    Console.WriteLine($"flow: giving up after repeated errors. {WinDivertNative.Describe(e)}");
                    Stop();
                    break;
                }
                Thread.Sleep(5);
                continue;
            }
            if (addr.Layer != WinDivertNative.LayerFlow) { continue; }
            uint key = Key(addr.LocalPort, addr.RemotePort);
            if (addr.Event == WinDivertNative.EventFlowEstablished)
            {
                _flows[key] = addr.ProcessId;
            }
            else if (addr.Event == WinDivertNative.EventFlowDeleted)
            {
                _flows.TryRemove(key, out _);
            }
        }
    }

    // Every second, find the process IDs of the target application (all its processes share one limit).
    private void PidRefreshLoop()
    {
        string name = _o.Process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? _o.Process[..^4]
            : _o.Process;
        int lastCount = -1;
        while (!_stop)
        {
            var set = new HashSet<uint>();
            foreach (Process p in Process.GetProcessesByName(name))
            {
                set.Add((uint)p.Id);
                p.Dispose();
            }
            _targets = set;
            if (set.Count != lastCount)
            {
                lastCount = set.Count;
                Console.WriteLine(set.Count == 0
                    ? $"[info] No running process named '{name}'. Start it, or check the name in Task Manager (Details tab, without .exe)."
                    : $"[info] Found {set.Count} running '{name}' process(es); they share one limit.");
            }
            for (int i = 0; i < 10 && !_stop; i++)
            {
                Thread.Sleep(100);
            }
        }
    }

    // Receives every inbound TCP packet. Target packets are queued for paced release, the rest go straight back.
    private void CaptureLoop()
    {
        byte[] buffer = new byte[65535];
        int errors = 0;
        fixed (byte* p = buffer)
        {
            while (!_stop)
            {
                WinDivertNative.Address addr;
                uint len;
                if (!WinDivertNative.Recv(_netHandle, p, (uint)buffer.Length, &len, &addr))
                {
                    if (_stop) { break; }
                    int e = Marshal.GetLastWin32Error();
                    if (++errors > 200)
                    {
                        Console.WriteLine($"capture: giving up after repeated errors. {WinDivertNative.Describe(e)}");
                        Stop();
                        break;
                    }
                    Thread.Sleep(5);
                    continue;
                }

                bool shape = false;
                if (TryGetTcpPorts(p, len, out ushort srcPort, out ushort dstPort))
                {
                    // Inbound packet: destination is our local port, source is the remote port.
                    if (_flows.TryGetValue(Key(dstPort, srcPort), out uint pid))
                    {
                        shape = _targets.Contains(pid);
                    }
                    else
                    {
                        Interlocked.Increment(ref _unmatched);
                    }
                }

                if (shape)
                {
                    Interlocked.Add(ref _arrivedBytes, len);
                    if (_pacer.TryReserve((int)len, out long releaseAt))
                    {
                        var held = new Held { Data = new byte[len], Len = (int)len, Addr = addr, ReleaseAt = releaseAt };
                        Buffer.BlockCopy(buffer, 0, held.Data, 0, (int)len);
                        Interlocked.Add(ref _queuedBytes, len);
                        _queue.Enqueue(held);
                    }
                    else
                    {
                        Interlocked.Increment(ref _drops);
                    }
                }
                else
                {
                    uint sent;
                    WinDivertNative.Send(_netHandle, p, len, &sent, &addr);
                    Interlocked.Add(ref _passBytes, len);
                }
            }
        }
    }

    // Releases queued packets at their scheduled time.
    private void SendLoop()
    {
        while (!_stop)
        {
            if (!_queue.TryPeek(out Held? head))
            {
                Thread.Sleep(1);
                continue;
            }
            long now = Stopwatch.GetTimestamp();
            if (head.ReleaseAt > now)
            {
                double waitMs = (head.ReleaseAt - now) * 1000.0 / Stopwatch.Frequency;
                if (waitMs >= 1.5) { Thread.Sleep(1); } else { Thread.Yield(); }
                continue;
            }
            if (!_queue.TryDequeue(out Held? item))
            {
                continue;
            }
            WinDivertNative.Address addr = item.Addr;
            uint sent;
            fixed (byte* d = item.Data)
            {
                WinDivertNative.Send(_netHandle, d, (uint)item.Len, &sent, &addr);
            }
            Interlocked.Add(ref _releasedBytes, item.Len);
            Interlocked.Add(ref _queuedBytes, -item.Len);
        }
    }

    private void StatsLoop()
    {
        using Process self = Process.GetCurrentProcess();
        string csvPath = Path.Combine(AppContext.BaseDirectory, $"spike-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        File.WriteAllText(csvPath, "second,arrived_KBps,released_KBps,other_KBps,queue_KB,drops_total,unmatched_pkts_total,cpu_percent_one_core\n");
        Console.WriteLine("sec | arrived KB/s | released KB/s | other KB/s | queue KB | drops | unmatched pkts | CPU % (one core)");

        long prevArrived = 0, prevReleased = 0, prevPass = 0;
        TimeSpan prevCpu = self.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        double lastTick = 0;
        int second = 0;

        while (!_stop)
        {
            Thread.Sleep(1000);
            if (_stop) { break; }
            double now = clock.Elapsed.TotalSeconds;
            double dt = now - lastTick;
            lastTick = now;
            second++;

            long arrived = Interlocked.Read(ref _arrivedBytes);
            long released = Interlocked.Read(ref _releasedBytes);
            long pass = Interlocked.Read(ref _passBytes);
            self.Refresh();
            TimeSpan cpuNow = self.TotalProcessorTime;

            double arrivedKb = (arrived - prevArrived) / 1024.0 / dt;
            double releasedKb = (released - prevReleased) / 1024.0 / dt;
            double passKb = (pass - prevPass) / 1024.0 / dt;
            double cpu = (cpuNow - prevCpu).TotalSeconds / dt * 100.0;
            double queueKb = Interlocked.Read(ref _queuedBytes) / 1024.0;
            long drops = Interlocked.Read(ref _drops);
            long unmatched = Interlocked.Read(ref _unmatched);
            prevArrived = arrived; prevReleased = released; prevPass = pass; prevCpu = cpuNow;

            _samples.Add((arrivedKb, releasedKb, cpu));
            Console.WriteLine(FormattableString.Invariant(
                $"{second,3} | {arrivedKb,12:0.0} | {releasedKb,13:0.0} | {passKb,10:0.0} | {queueKb,8:0.0} | {drops,5} | {unmatched,14} | {cpu,8:0.0}"));
            File.AppendAllText(csvPath, FormattableString.Invariant(
                $"{second},{arrivedKb:0.0},{releasedKb:0.0},{passKb:0.0},{queueKb:0.0},{drops},{unmatched},{cpu:0.0}\n"));
        }
        Console.WriteLine($"[info] Per-second data saved to {csvPath}");
    }

    private void PrintSummary()
    {
        Console.WriteLine();
        // Ignore the first 3 busy seconds (ramp-up) when judging accuracy.
        var busy = _samples.Where(s => s.Arrived > 1.0).Skip(3).ToList();
        Console.WriteLine("=== SUMMARY ===");
        Console.WriteLine($"Seconds recorded: {_samples.Count}, busy seconds after ramp-up: {busy.Count}");
        if (busy.Count > 0)
        {
            double avg = busy.Average(s => s.Released);
            double min = busy.Min(s => s.Released);
            double max = busy.Max(s => s.Released);
            double cpu = _samples.Average(s => s.Cpu);
            Console.WriteLine(FormattableString.Invariant(
                $"RESULT: limit {_o.LimitKBps} KB/s | released avg {avg:0.0} KB/s ({avg / _o.LimitKBps * 100.0:0}% of limit), min {min:0.0}, max {max:0.0}"));
            Console.WriteLine(FormattableString.Invariant($"RESULT: average CPU {cpu:0.0}% of one core (includes timing busy-wait)"));
        }
        else
        {
            Console.WriteLine("RESULT: no target traffic was shaped. Check the process name, and start the download AFTER this program started.");
        }
        Console.WriteLine($"Packets dropped by the queue cap: {Interlocked.Read(ref _drops)}; unmatched packets: {Interlocked.Read(ref _unmatched)}");
    }

    private static uint Key(ushort localPort, ushort remotePort) => ((uint)localPort << 16) | remotePort;

    private static bool IsAdmin()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // Reads TCP source and destination ports from a raw IPv4 or IPv6 packet (no extension headers).
    private static bool TryGetTcpPorts(byte* p, uint len, out ushort srcPort, out ushort dstPort)
    {
        srcPort = 0;
        dstPort = 0;
        if (len < 20) { return false; }
        int version = p[0] >> 4;
        int offset;
        if (version == 4)
        {
            if (p[9] != 6) { return false; }
            offset = (p[0] & 0x0F) * 4;
        }
        else if (version == 6)
        {
            if (len < 40 || p[6] != 6) { return false; }
            offset = 40;
        }
        else
        {
            return false;
        }
        if (len < offset + 4) { return false; }
        srcPort = (ushort)((p[offset] << 8) | p[offset + 1]);
        dstPort = (ushort)((p[offset + 2] << 8) | p[offset + 3]);
        return true;
    }
}
