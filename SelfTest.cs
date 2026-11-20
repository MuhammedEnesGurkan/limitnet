using System.Diagnostics;
using System.Net;

// `Akis --selftest`: yönetici gerektirmeyen mantığın tek kontrolü. Linux/macOS ayrıştırıcıları da burada sınanır.
static class SelfTest
{
    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "ok    " : "HATA  ") + what);
        if (!ok) Environment.ExitCode = 1;
    }

    public static void Run()
    {
        Check(Platform.Endpoint("192.168.1.5:44312") is { Port: 44312 } a && a.Ip.Equals(IPAddress.Parse("192.168.1.5")), "ipv4 uç nokta");
        Check(Platform.Endpoint("[2a00:1450:4001:82a::200e]:443") is { Port: 443 } b && b.Ip.Equals(IPAddress.Parse("2a00:1450:4001:82a::200e")), "ipv6 uç nokta");
        Check(Platform.Endpoint("[::ffff:10.0.0.2]:22")?.Ip.Equals(IPAddress.Parse("10.0.0.2")) == true, "ipv4-mapped → ipv4");
        Check(Platform.Endpoint("192.168.1.5%wlan0:5353")?.Port == 5353, "%arayüz temizlenir");
        Check(Platform.Endpoint("*:*") == null, "çözülemeyen uç atlanır");

        const string ss = """
            ESTAB 0      0      192.168.1.10:48562 140.82.112.26:443 users:(("firefox",pid=2201,fd=88)) cubic wscale:7,7 rto:204 rtt:3.2/1.1 bytes_sent:12345 bytes_acked:12346 bytes_received:67890 segs_out:40
            ESTAB 0      0      [2a02:ff0::5]:51000 [2a00:1450:4001::200e]:443 users:(("curl",pid=99,fd=3)) bytes_received:10
            TIME-WAIT 0  0      192.168.1.10:40000 1.1.1.1:443
            """;
        var rows = SsCapture.Parse(ss, (pid, name) => "/usr/bin/" + name).ToList();
        Check(rows.Count == 2, "ss: süreçsiz satır atlanır");
        Check(rows[0] is ({ RemotePort: 443, LocalPort: 48562 }, 2201, "/usr/bin/firefox", 67890, 12345), "ss: firefox satırı");
        Check(rows[1] is ({ Proto: "TCP" }, 99, _, 10, 0), "ss: ipv6 satırı, eksik sayaç 0");

        const string nettop = """
            time,,bytes_in,bytes_out,
            12:00:00.000000,Google Chrome H.1234,5000,700,
            12:00:00.000000,tcp4 192.168.1.2:52345<->142.250.187.110:443,4000,600,
            12:00:00.000000,tcp6 2a02:ff0::5.51000<->2a00:1450:4001::200e.443,1000,100,
            12:00:00.000000,launchd.1,0,0,
            """;
        var nt = NettopCapture.Parse(nettop).ToList();
        Check(nt.Count == 2, "nettop: iki bağlantı");
        Check(nt[0] is ({ RemotePort: 443, LocalPort: 52345 }, 1234, "Google Chrome H", 4000, 600), "nettop: ipv4 bağlantısı süreçe bağlanır");
        Check(nt[1].Item1.Remote.Equals(IPAddress.Parse("2a00:1450:4001::200e")), "nettop: ipv6 nokta ayracı");

        var key = new ConnKey("TCP", IPAddress.Loopback, 1, IPAddress.Loopback, 2);
        var prev = new Dictionary<ConnKey, (long, long)>();
        Check(Platform.Diff(prev, [(key, 1, "x", 1000, 50)])[0] is { In: 0, Out: 0 }, "fark: ilk görüşte geçmiş sayılmaz");
        Check(Platform.Diff(prev, [(key, 1, "x", 1500, 80)])[0] is { In: 500, Out: 30 }, "fark: sonraki aralık");

        var dns = WindowsCapture.DnsResultIps("type:  5 youtube-ui.l.google.com;::ffff:142.250.187.110;2a00:1450:4017:80e::200e;").ToList();
        Check(dns.Count == 2 && dns[0].Equals(IPAddress.Parse("142.250.187.110")), "dns sonuç ayrıştırma");

        var geo = new Geo(Path.Combine(AppContext.BaseDirectory, "data"));
        var g = geo.Lookup(IPAddress.Parse("8.8.8.8"));
        Console.WriteLine($"      8.8.8.8 → {g}");
        Check(g.Cc == "US" && g.Org.Contains("Google"), "geoip: ülke ve sağlayıcı");
        Check(geo.Lookup(IPAddress.Parse("192.168.1.1")).Country == "Yerel ağ", "geoip: yerel ağ");
        Check(Geo.IsPrivate(IPAddress.Parse("100.64.0.1")) && !Geo.IsPrivate(IPAddress.Parse("100.128.0.1")), "geoip: CGNAT aralığı");

        var dir = Directory.CreateTempSubdirectory("akis-test").FullName;
        var file = Path.Combine(dir, "history.json");
        var h = new History(file);
        h.Add("C:\\a.exe", "A", 100, 10);
        h.Add("C:\\a.exe", "A", 50, 5);
        h.Save();
        var q = System.Text.Json.JsonSerializer.Serialize(new History(file).Query(1));
        Check(q.Contains("\"down\":150") && q.Contains("\"up\":15"), "geçmiş: kaydet/yükle/topla");
        File.WriteAllText(file, "{bozuk");
        Check(Store.Load<History.Data>(file) == null && Directory.GetFiles(dir, "history.json.bozuk-*").Length == 1, "bozuk dosya kenara alınır");
        Directory.Delete(dir, true);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 50_000; i++) geo.Lookup(new IPAddress(i));
        Console.WriteLine($"      50k geoip sorgusu: {sw.ElapsedMilliseconds} ms");

        Console.WriteLine(Environment.ExitCode == 0 ? "selftest TAMAM" : "selftest BAŞARISIZ");
    }

    // `Akis --probe <dosya>` (yönetici): pencere açmadan gerçek yakalama + kural uygula/kaldır, sonucu dosyaya yazar
    public static void Probe(string file)
    {
        var log = new StringWriter();
        try
        {
            using var engine = new Engine(Directory.CreateTempSubdirectory("akis-probe").FullName);
            using var http = new HttpClient();
            for (var i = 0; i < 6; i++)
            {
                if (i == 1) http.GetAsync("https://www.google.com/").Wait(); // kendi trafiğimiz ve alan adımız görünmeli
                Thread.Sleep(1000);
                var tick = System.Text.Json.Nodes.JsonNode.Parse(engine.Tick())!;
                var apps = tick["apps"]!.AsArray();
                log.WriteLine($"tick {i}: ↓{tick["down"]} B/s ↑{tick["up"]} B/s, {apps.Count} uygulama, hata={tick["error"]}");
                if (i < 5) continue;
                foreach (var a in apps.OrderByDescending(a => (long)a!["totDown"]! + (long)a["totUp"]!).Take(10))
                {
                    log.WriteLine($"  {a!["name"]} [{a["id"]}] ↓{a["totDown"]} ↑{a["totUp"]} pid={a["pids"]!.ToJsonString()}");
                    foreach (var c in a["conns"]!.AsArray().Take(4))
                        log.WriteLine($"     {c!["proto"]} {c["local"]} → {c["remote"]}:{c["port"]} host={c["host"]} cc={c["cc"]} org={c["org"]} tot↓{c["totDown"]} tot↑{c["totUp"]}");
                }
            }

            var done = new ManualResetEventSlim();
            string? toast = null;
            void Rule(string json) { done.Reset(); engine.Handle(json, t => { toast = t; done.Set(); }); done.Wait(TimeSpan.FromMinutes(1)); }
            Rule("""{"type":"rule","id":"C:\\akis-probe\\yok.exe","up":102400,"down":0,"block":true}""");
            log.WriteLine($"kural ekle: {toast} → sistemde {Count()}");
            Rule("""{"type":"rule","id":"C:\\akis-probe\\yok.exe","up":0,"down":0,"block":false}""");
            log.WriteLine($"kural kaldır: {toast} → sistemde {Count()}");
        }
        catch (Exception e) { log.WriteLine("HATA " + e); }
        File.WriteAllText(file, log.ToString());
    }

    static string Count() => Platform.Run("powershell.exe", "-NoProfile", "-Command",
        "\"güvenlik duvarı=$(@(Get-NetFirewallRule -Group Akis -ErrorAction SilentlyContinue).Count) qos=$(@(Get-NetQosPolicy -ErrorAction SilentlyContinue | Where-Object Name -like 'Akis-*').Count)\"").Trim();
}
