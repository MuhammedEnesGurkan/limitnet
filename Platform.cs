using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

static class Platform
{
    public static ICapture CreateCapture() =>
        OperatingSystem.IsWindows() ? new WindowsCapture()
        : OperatingSystem.IsMacOS() ? new NettopCapture()
        : new SsCapture();

    static readonly ConcurrentDictionary<string, string> Names = new();

    public static string DisplayName(string app) => Names.GetOrAdd(app, p =>
    {
        if (OperatingSystem.IsWindows() && Path.IsPathRooted(p))
        {
            try { if (FileVersionInfo.GetVersionInfo(p).FileDescription is { Length: > 0 } d) return d.Trim(); }
            catch (FileNotFoundException) { }
        }
        return Path.GetFileNameWithoutExtension(p) is { Length: > 0 } n ? n : p;
    });

    public static string? Icon(string app)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(app)) return null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(app);
            if (icon == null) return null;
            using var bmp = icon.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch (Exception e) when (e is ArgumentException or ExternalException or IOException) { return null; }
    }

    // ss/nettop: alanı ayır, adres:port veya adres.port çöz (köşeli parantez ve %arayüz temizlenir)
    public static (IPAddress Ip, int Port)? Endpoint(string s, char sep = ':')
    {
        var i = s.LastIndexOf(sep);
        if (i <= 0) return null;
        var host = s[..i].Trim('[', ']');
        if (host.IndexOf('%') is var pct and >= 0) host = host[..pct];
        if (!IPAddress.TryParse(host, out var ip) || !int.TryParse(s[(i + 1)..], out var port)) return null;
        return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip, port);
    }

    // birikimli sayaçlardan (ss, nettop) aralık farkı üret; ilk görülen bağlantının geçmiş baytları sayılmaz
    public static List<ConnSample> Diff(Dictionary<ConnKey, (long In, long Out)> prev, IEnumerable<(ConnKey Key, int Pid, string App, long In, long Out)> rows)
    {
        var next = new Dictionary<ConnKey, (long, long)>();
        var list = new List<ConnSample>();
        foreach (var r in rows)
        {
            next[r.Key] = (r.In, r.Out);
            var (pi, po) = prev.TryGetValue(r.Key, out var p) ? p : (r.In, r.Out);
            list.Add(new ConnSample(r.Key, r.Pid, r.App, Math.Max(0, r.In - pi), Math.Max(0, r.Out - po)));
        }
        prev.Clear();
        foreach (var (k, v) in next) prev[k] = v;
        return list;
    }

    public static string Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException((output + err.Result).Trim());
        return output;
    }
}

sealed class WindowsCapture : ICapture
{
    TraceEventSession kernel, dns;
    volatile bool disposed;
    readonly object gate = new();
    readonly Dictionary<ConnKey, Acc> conns = [];
    // ponytail: sınırsız IP→alan adı tablosu; uzun oturumlarda şişerse yaşa göre temizle
    readonly ConcurrentDictionary<IPAddress, string> hosts = new();
    readonly Dictionary<int, (string App, long At)> pids = [];
    volatile HashSet<IPAddress> localIps = LocalIps();
    long localAt = Environment.TickCount64;

    sealed class Acc { public int Pid; public long In, Out, Seen; }

    // indirme limiti çekirdek sürücüsü ister; şimdilik yok
    public Caps Caps { get; } = new(Up: true, Down: false, Block: true);

    public WindowsCapture()
    {
        kernel = Kernel();
        Pump("etw-net", () => kernel, () => kernel = Kernel());
        dns = Dns();
        Pump("etw-dns", () => dns, () => dns = Dns());

        // açılıştan önce çözülmüş adlar için DNS önbelleğini bir kez oku
        Task.Run(() =>
        {
            try
            {
                foreach (var line in Pwsh("Get-DnsClientCache | ForEach-Object { \"$($_.Data) $($_.Entry)\" }").Split('\n'))
                    if (line.Trim().Split(' ', 2) is [var ip, var name] && IPAddress.TryParse(ip, out var addr)) hosts.TryAdd(addr, name);
            }
            catch (InvalidOperationException) { } // önbellek okunamazsa adlar sonraki sorgularla gelir
        });
    }

    // Aynı adlı ETW oturumunu başka bir araç (ör. --probe) devralırsa Process() döner: oturumu yeniden kur.
    // ponytail: sonsuza dek 2 sn'de bir dener; iki araç oturumu sürekli kaparsa sayımda boşluklar olur
    void Pump(string name, Func<TraceEventSession> current, Action recreate) => new Thread(() =>
    {
        while (!disposed)
        {
            try { current().Source.Process(); }
            catch (Exception) { } // kapanmış oturum: aşağıda yeniden kurulur
            if (disposed) return;
            Thread.Sleep(2000);
            try { recreate(); }
            catch (Exception) { }
        }
    }) { IsBackground = true, Name = name }.Start();

    TraceEventSession Kernel()
    {
        // Kaynak İzleyicisi'nin kullandığı çekirdek TCP/IP olayları: süreç, adresler, portlar, bayt
        var s = new TraceEventSession(KernelTraceEventParser.KernelSessionName) { StopOnDispose = true };
        s.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
        var k = s.Source.Kernel;
        k.TcpIpSend += e => Add("TCP", e.ProcessID, e.saddr, e.sport, e.daddr, e.dport, e.size, false);
        k.TcpIpRecv += e => Add("TCP", e.ProcessID, e.saddr, e.sport, e.daddr, e.dport, e.size, true);
        k.TcpIpSendIPV6 += e => Add("TCP", e.ProcessID, e.saddr, e.sport, e.daddr, e.dport, e.size, false);
        k.TcpIpRecvIPV6 += e => Add("TCP", e.ProcessID, e.saddr, e.sport, e.daddr, e.dport, e.size, true);
        k.UdpIpSend += e => Add("UDP", e.ProcessID, e.saddr, e.sport, e.daddr, e.dport, e.size, false);
        k.UdpIpRecv += e => Add("UDP", e.ProcessID, e.saddr, e.sport, e.daddr, e.dport, e.size, true);
        k.UdpIpSendIPV6 += e => Add("UDP", e.ProcessID, e.saddr, e.sport, e.daddr, e.dport, e.size, false);
        k.UdpIpRecvIPV6 += e => Add("UDP", e.ProcessID, e.saddr, e.sport, e.daddr, e.dport, e.size, true);
        return s;
    }

    TraceEventSession Dns()
    {
        // alan adları: Windows DNS istemcisinin "sorgu tamamlandı" olayı (DoH açıkken de çalışır)
        var s = new TraceEventSession("Akis-DNS") { StopOnDispose = true };
        s.EnableProvider("Microsoft-Windows-DNS-Client");
        new RegisteredTraceEventParser(s.Source).All += e =>
        {
            if ((int)e.ID != 3008 || e.PayloadByName("QueryName") is not string name || e.PayloadByName("QueryResults") is not string results) return;
            foreach (var ip in DnsResultIps(results)) hosts[ip] = name;
        };
        return s;
    }

    // "type: 5 x.l.google.com;::ffff:142.250.1.1;2a00:1450::e;" → IP'ler
    public static IEnumerable<IPAddress> DnsResultIps(string results) =>
        results.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => IPAddress.TryParse(p, out var ip) ? (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip) : null)
            .OfType<IPAddress>();

    void Add(string proto, int pid, IPAddress a, int ap, IPAddress b, int bp, int size, bool inbound)
    {
        // a = yerel uç olsun; olay yönü ne olursa olsun makinenin kendi adresine göre düzelt
        var locals = localIps;
        if (locals.Contains(b) && !locals.Contains(a)) (a, ap, b, bp) = (b, bp, a, ap);
        if (IPAddress.IsLoopback(b) || locals.Contains(b)) return; // makinenin kendi içindeki trafik
        var key = new ConnKey(proto, a, ap, b, bp);
        lock (gate)
        {
            if (!conns.TryGetValue(key, out var c)) conns[key] = c = new Acc();
            c.Pid = pid;
            c.Seen = Environment.TickCount64;
            if (inbound) c.In += size; else c.Out += size;
        }
    }

    public List<ConnSample> Snapshot()
    {
        var now = Environment.TickCount64;
        if (now - localAt > 30_000) { localIps = LocalIps(); localAt = now; }
        var raw = new List<(ConnKey Key, int Pid, long In, long Out)>();
        lock (gate)
        {
            foreach (var (key, c) in conns)
            {
                raw.Add((key, c.Pid, c.In, c.Out));
                c.In = c.Out = 0;
            }
            // 30 sn sessiz kalan bağlantı kapanmış sayılır
            foreach (var key in conns.Where(kv => now - kv.Value.Seen > 30_000).Select(kv => kv.Key).ToList()) conns.Remove(key);
        }
        return raw.Select(r => new ConnSample(r.Key, r.Pid, AppOf(r.Pid), r.In, r.Out)).ToList();
    }

    public string? HostName(IPAddress ip) => hosts.GetValueOrDefault(ip);

    string AppOf(int pid)
    {
        var now = Environment.TickCount64;
        if (pids.TryGetValue(pid, out var p) && now - p.At < 10_000) return p.App;
        var app = pid switch { 0 => "Bilinmeyen", 4 => "System", _ => ImagePath(pid) ?? $"PID {pid}" };
        pids[pid] = (app, now);
        return app;
    }

    static string? ImagePath(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == 0) return null;
        try
        {
            var buf = new char[1024];
            var len = buf.Length;
            return QueryFullProcessImageName(h, 0, buf, ref len) ? new string(buf, 0, len) : null;
        }
        finally { CloseHandle(h); }
    }

    static HashSet<IPAddress> LocalIps() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address)
            .ToHashSet();

    public void Apply(IReadOnlyDictionary<string, Rule> rules) => ApplyRules(rules);

    public static void ApplyRules(IReadOnlyDictionary<string, Rule> rules)
    {
        // her seferinde sıfırdan kur: bizim grubumuzdaki kural/politikaları sil, güncel listeyi yaz
        var sb = new StringBuilder("""
            Get-NetQosPolicy -ErrorAction SilentlyContinue | Where-Object Name -like 'Akis-*' | Remove-NetQosPolicy -Confirm:$false
            Remove-NetFirewallRule -Group 'Akis' -ErrorAction SilentlyContinue

            """);
        var n = 0;
        foreach (var (app, r) in rules)
        {
            if (!Path.IsPathRooted(app)) continue; // "System" gibi dosya yolu olmayan süreçlere kural yazılamaz
            var path = app.Replace("'", "''"); // tek tırnaklı PowerShell dizesi için kaçış
            var file = Path.GetFileName(app).Replace("'", "''");
            if (r.Up > 0)
                sb.AppendLine($"New-NetQosPolicy -Name 'Akis-{n++}' -AppPathNameMatchCondition '{path}' -ThrottleRateActionBitsPerSecond {r.Up * 8} -Confirm:$false | Out-Null");
            if (r.Block)
                foreach (var dir in new[] { "Inbound", "Outbound" })
                    sb.AppendLine($"New-NetFirewallRule -DisplayName 'Akış engeli: {file}' -Group 'Akis' -Direction {dir} -Action Block -Program '{path}' | Out-Null");
        }
        Pwsh(sb.ToString());
    }

    static string Pwsh(string body)
    {
        var script = $$"""
            $ProgressPreference = 'SilentlyContinue'
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [Text.Encoding]::UTF8
            try {
            {{body}}
            } catch { [Console]::Out.Write($_.Exception.Message); exit 1 }
            """;
        return Platform.Run("powershell.exe", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
    }

    public void Dispose()
    {
        disposed = true;
        kernel.Dispose();
        dns.Dispose();
    }

    [DllImport("kernel32", SetLastError = true)] static extern nint OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(nint h, int flags, char[] buf, ref int size);
    [DllImport("kernel32")] static extern bool CloseHandle(nint h);
}

// Linux: iproute2'deki ss, bağlantı başına bayt sayaçlarını süreçle birlikte verir.
// ponytail: yalnızca TCP; UDP/QUIC için eBPF gerekir, Linux limit aşamasında eklenecek
sealed class SsCapture : ICapture
{
    readonly Dictionary<ConnKey, (long, long)> prev = [];
    readonly Dictionary<int, (string App, long At)> exes = [];
    static readonly Regex Line = new(@"^\S+\s+\d+\s+\d+\s+(\S+)\s+(\S+)\s.*?users:\(\(""([^""]*)"",pid=(\d+)", RegexOptions.Compiled);
    static readonly Regex In = new(@"bytes_received:(\d+)", RegexOptions.Compiled), Out = new(@"bytes_sent:(\d+)", RegexOptions.Compiled);

    public Caps Caps { get; } = new(false, false, false);

    public SsCapture() => Query(); // ss yoksa açılışta hata ver

    static string Query() => Platform.Run("ss", "-H", "-O", "-t", "-i", "-n", "-p");

    public List<ConnSample> Snapshot() => Platform.Diff(prev, Parse(Query(), Exe));

    public static IEnumerable<(ConnKey, int, string, long, long)> Parse(string output, Func<int, string, string> app)
    {
        foreach (var line in output.Split('\n'))
        {
            var m = Line.Match(line);
            if (!m.Success || Platform.Endpoint(m.Groups[1].Value) is not { } l || Platform.Endpoint(m.Groups[2].Value) is not { } r) continue;
            var pid = int.Parse(m.Groups[4].Value);
            long Num(Regex re) => re.Match(line) is { Success: true } x ? long.Parse(x.Groups[1].Value) : 0;
            yield return (new ConnKey("TCP", l.Ip, l.Port, r.Ip, r.Port), pid, app(pid, m.Groups[3].Value), Num(In), Num(Out));
        }
    }

    string Exe(int pid, string name)
    {
        var now = Environment.TickCount64;
        if (exes.TryGetValue(pid, out var e) && now - e.At < 10_000) return e.App;
        string app;
        try { app = new FileInfo($"/proc/{pid}/exe").LinkTarget ?? name; }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { app = name; }
        exes[pid] = (app, now);
        return app;
    }

    public string? HostName(IPAddress ip) => null;
    public void Apply(IReadOnlyDictionary<string, Rule> rules) { }
    public void Dispose() { }
}

// macOS: nettop -L 1 tek örnek basar; süreç satırının altında o sürecin bağlantıları gelir.
// ponytail: limit/engelleme için Network Extension gerekir (Apple geliştirici hesabı), sonraki aşama
sealed class NettopCapture : ICapture
{
    readonly Dictionary<ConnKey, (long, long)> prev = [];

    public Caps Caps { get; } = new(false, false, false);

    public NettopCapture() => Query();

    static string Query() => Platform.Run("nettop", "-L", "1", "-x", "-n", "-J", "bytes_in,bytes_out");

    public List<ConnSample> Snapshot() => Platform.Diff(prev, Parse(Query()));

    public static IEnumerable<(ConnKey, int, string, long, long)> Parse(string output)
    {
        var lines = output.Split('\n');
        var header = lines[0].Split(',');
        int iIn = Array.IndexOf(header, "bytes_in"), iOut = Array.IndexOf(header, "bytes_out");
        if (iIn < 0 || iOut < 0) yield break;
        string app = ""; var pid = 0;
        foreach (var line in lines.Skip(1))
        {
            var f = line.Split(',');
            if (f.Length <= Math.Max(iIn, iOut) || f[1].Length == 0) continue;
            var name = f[1];
            if (name.Contains("<->"))
            {
                // "tcp4 192.168.1.2:52345<->17.57.146.20:5223"; IPv6'da port ayracı nokta olabilir
                var ends = name[(name.IndexOf(' ') + 1)..].Split("<->");
                if (app == "" || ends.Length != 2 || End(ends[0]) is not { } l || End(ends[1]) is not { } r) continue;
                if (!long.TryParse(f[iIn], out var bin) || !long.TryParse(f[iOut], out var bout)) continue;
                yield return (new ConnKey(name[..3].ToUpperInvariant(), l.Ip, l.Port, r.Ip, r.Port), pid, app, bin, bout);
            }
            else
            {
                // süreç satırı: "Google Chrome H.1234"
                var dot = name.LastIndexOf('.');
                (app, pid) = dot > 0 && int.TryParse(name[(dot + 1)..], out var p) ? (name[..dot], p) : (name, 0);
            }
        }
    }

    static (IPAddress Ip, int Port)? End(string s) => Platform.Endpoint(s, s.Count(c => c == ':') == 1 ? ':' : '.');

    public string? HostName(IPAddress ip) => null;
    public void Apply(IReadOnlyDictionary<string, Rule> rules) { }
    public void Dispose() { }
}
