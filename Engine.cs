using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using MaxMind.Db;

record struct ConnKey(string Proto, IPAddress Local, int LocalPort, IPAddress Remote, int RemotePort);

// In/Out: bir önceki Snapshot'tan bu yana geçen baytlar
record ConnSample(ConnKey Key, int Pid, string App, long In, long Out);

record Rule(long Up = 0, long Down = 0, bool Block = false);

record Caps(bool Up, bool Down, bool Block);

interface ICapture : IDisposable
{
    Caps Caps { get; }
    List<ConnSample> Snapshot();
    string? HostName(IPAddress ip);
    void Apply(IReadOnlyDictionary<string, Rule> rules); // hata olursa fırlatır
}

sealed class NullCapture : ICapture
{
    public Caps Caps { get; } = new(false, false, false);
    public List<ConnSample> Snapshot() => [];
    public string? HostName(IPAddress ip) => null;
    public void Apply(IReadOnlyDictionary<string, Rule> rules) { }
    public void Dispose() { }
}

sealed class AppInfo
{
    public required string Id, Name;
    public long In, Out, TotIn, TotOut;
    public readonly HashSet<int> Pids = [];
    public bool IconSent;
}

sealed class Engine : IDisposable
{
    readonly object gate = new(), applyGate = new();
    readonly ICapture cap;
    readonly string? error;
    readonly Geo geo = new(Path.Combine(AppContext.BaseDirectory, "data"));
    readonly History history;
    readonly string rulesFile;
    readonly Dictionary<string, Rule> rules;
    readonly Dictionary<string, AppInfo> apps = [];
    readonly Dictionary<ConnKey, long[]> connTotals = [];
    long lastTick = Stopwatch.GetTimestamp();
    DateTime lastSave = DateTime.UtcNow;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Engine(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        history = new History(Path.Combine(dataDir, "history.json"));
        rulesFile = Path.Combine(dataDir, "rules.json");
        rules = Store.Load<Dictionary<string, Rule>>(rulesFile) ?? [];
        try { cap = Platform.CreateCapture(); }
        catch (Exception e) { cap = new NullCapture(); error = e.Message; }
        foreach (var id in rules.Keys) App(id); // kuralı olan uygulama trafik yokken de listede kalsın
        if (rules.Count > 0) ApplyAsync(null);
    }

    AppInfo App(string id)
    {
        if (!apps.TryGetValue(id, out var a)) apps[id] = a = new AppInfo { Id = id, Name = Platform.DisplayName(id) };
        return a;
    }

    public string Tick()
    {
        var samples = cap.Snapshot();
        lock (gate)
        {
            var now = Stopwatch.GetTimestamp();
            var secs = Math.Max(0.2, Stopwatch.GetElapsedTime(lastTick, now).TotalSeconds);
            lastTick = now;
            long Rate(long bytes) => (long)(bytes / secs);

            foreach (var a in apps.Values) { a.In = a.Out = 0; a.Pids.Clear(); }
            var conns = new Dictionary<string, List<object>>();
            var live = new HashSet<ConnKey>();
            foreach (var s in samples)
            {
                var a = App(s.App);
                a.In += s.In; a.Out += s.Out; a.TotIn += s.In; a.TotOut += s.Out;
                if (s.Pid > 0) a.Pids.Add(s.Pid);
                history.Add(a.Id, a.Name, s.In, s.Out);

                if (!connTotals.TryGetValue(s.Key, out var tot)) connTotals[s.Key] = tot = new long[2];
                tot[0] += s.In; tot[1] += s.Out;
                live.Add(s.Key);

                var g = geo.Lookup(s.Key.Remote);
                if (!conns.TryGetValue(a.Id, out var list)) conns[a.Id] = list = [];
                list.Add(new
                {
                    proto = s.Key.Proto,
                    local = $"{s.Key.Local}:{s.Key.LocalPort}",
                    remote = s.Key.Remote.ToString(),
                    port = s.Key.RemotePort,
                    host = cap.HostName(s.Key.Remote),
                    cc = g.Cc, country = g.Country, org = g.Org,
                    down = Rate(s.In), up = Rate(s.Out),
                    totDown = tot[0], totUp = tot[1],
                });
            }
            foreach (var k in connTotals.Keys.Where(k => !live.Contains(k)).ToList()) connTotals.Remove(k);

            if (DateTime.UtcNow - lastSave > TimeSpan.FromMinutes(1)) { history.Save(); lastSave = DateTime.UtcNow; }

            var icons = new Dictionary<string, string>();
            foreach (var a in apps.Values.Where(a => !a.IconSent))
            {
                a.IconSent = true;
                if (Platform.Icon(a.Id) is { } icon) icons[a.Id] = icon;
            }

            return JsonSerializer.Serialize(new
            {
                type = "tick",
                down = Rate(apps.Values.Sum(a => a.In)),
                up = Rate(apps.Values.Sum(a => a.Out)),
                caps = cap.Caps,
                error,
                icons,
                apps = apps.Values.Select(a => new
                {
                    id = a.Id, name = a.Name, exe = Path.GetFileName(a.Id),
                    pids = a.Pids,
                    down = Rate(a.In), up = Rate(a.Out), totDown = a.TotIn, totUp = a.TotOut,
                    rule = rules.GetValueOrDefault(a.Id),
                    conns = conns.GetValueOrDefault(a.Id) ?? [],
                }),
            }, Json);
        }
    }

    public string? Handle(string msg, Action<string> send)
    {
        var m = JsonNode.Parse(msg);
        switch ((string?)m?["type"])
        {
            case "history":
                var days = Math.Clamp((int?)m["days"] ?? 7, 1, 366);
                lock (gate) return JsonSerializer.Serialize(new { type = "history", data = history.Query(days) }, Json);

            case "rule":
                var id = (string?)m["id"];
                if (string.IsNullOrEmpty(id)) return null;
                // arayüzden gelen değerler: negatif/absürt hızları kırp (1 TB/s üst sınır)
                long Clamp(string k) => Math.Clamp((long?)m[k] ?? 0, 0, 1L << 40);
                var rule = new Rule(Clamp("up"), Clamp("down"), (bool?)m["block"] ?? false);
                lock (gate)
                {
                    if (rule == new Rule()) rules.Remove(id); else rules[id] = rule;
                    App(id);
                    Store.Save(rulesFile, rules);
                }
                ApplyAsync(send);
                return null;
        }
        return null;
    }

    void ApplyAsync(Action<string>? send) => Task.Run(() =>
    {
        string text; bool ok;
        lock (applyGate) // aynı anda iki uygulama betiği çalışıp birbirini ezmesin
        {
            Dictionary<string, Rule> snap;
            lock (gate) snap = new(rules);
            try { cap.Apply(snap); text = "Kural uygulandı"; ok = true; }
            catch (Exception e) { text = "Kural uygulanamadı: " + e.Message; ok = false; }
        }
        send?.Invoke(JsonSerializer.Serialize(new { type = "toast", text, ok }, Json));
    });

    public void Dispose()
    {
        lock (gate) history.Save();
        cap.Dispose();
    }
}

record GeoInfo(string Cc, string Country, string Org);

sealed class Geo
{
    readonly Reader? country, asn;
    // ponytail: sınırsız önbellek, masaüstü oturumu için yeterli; büyürse LRU
    readonly ConcurrentDictionary<IPAddress, GeoInfo> cache = new();

    public Geo(string dir)
    {
        country = Open(dir, "dbip-country-lite-*.mmdb");
        asn = Open(dir, "dbip-asn-lite-*.mmdb");
    }

    // dosya adında ay var (2026-11): en yenisini al
    static Reader? Open(string dir, string pattern) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, pattern).Order().Select(f => new Reader(f)).LastOrDefault() : null;

    public GeoInfo Lookup(IPAddress ip) => cache.GetOrAdd(ip, Resolve);

    GeoInfo Resolve(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IsPrivate(ip)) return new("", "Yerel ağ", "");
        var c = country?.Find<Dictionary<string, object>>(ip)?.GetValueOrDefault("country") as Dictionary<string, object>;
        var cc = c?.GetValueOrDefault("iso_code") as string ?? "";
        var org = asn?.Find<Dictionary<string, object>>(ip)?.GetValueOrDefault("autonomous_system_organization") as string ?? "";
        return new(cc, "", org); // Türkçe ülke adını arayüz Intl.DisplayNames ile üretir
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6Multicast) return true;
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] is 0 or 10 or >= 224
            || (b[0] == 172 && b[1] is >= 16 and < 32)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] is >= 64 and < 128);
    }
}

sealed class History(string file)
{
    public sealed class Data
    {
        public Dictionary<string, Dictionary<string, long[]>> Days { get; set; } = [];
        public Dictionary<string, string> Names { get; set; } = [];
    }

    readonly Data d = Store.Load<Data>(file) ?? new();

    public void Add(string app, string name, long i, long o)
    {
        if (i == 0 && o == 0) return;
        var day = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!d.Days.TryGetValue(day, out var apps)) d.Days[day] = apps = [];
        if (!apps.TryGetValue(app, out var v)) apps[app] = v = new long[2];
        v[0] += i; v[1] += o;
        d.Names[app] = name;
    }

    public void Save()
    {
        var cutoff = DateTime.Now.AddDays(-400).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var k in d.Days.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList()) d.Days.Remove(k);
        Store.Save(file, d);
    }

    public object Query(int days)
    {
        var first = DateTime.Now.Date.AddDays(1 - days);
        var range = Enumerable.Range(0, days).Select(k => first.AddDays(k).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList();
        var perDay = range.Select(day => d.Days.GetValueOrDefault(day) ?? []).ToList();
        var all = d.Days.Values.SelectMany(a => a.Values).ToList();
        return new
        {
            since = d.Days.Keys.Order().FirstOrDefault(),
            allDown = all.Sum(v => v[0]),
            allUp = all.Sum(v => v[1]),
            days = range.Zip(perDay, (day, a) => new { date = day, down = a.Values.Sum(v => v[0]), up = a.Values.Sum(v => v[1]) }),
            apps = perDay.SelectMany(a => a)
                .GroupBy(kv => kv.Key)
                .Select(g => new { id = g.Key, name = d.Names.GetValueOrDefault(g.Key, g.Key), down = g.Sum(kv => kv.Value[0]), up = g.Sum(kv => kv.Value[1]) })
                .OrderByDescending(x => x.down + x.up),
        };
    }
}

static class Store
{
    public static T? Load<T>(string file)
    {
        if (!File.Exists(file)) return default;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(file)); }
        catch (JsonException)
        {
            // bozuk dosyayı ezmeden kenara al, kullanıcının verisi kaybolmasın
            File.Move(file, file + ".bozuk-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
            return default;
        }
    }

    public static void Save<T>(string file, T value)
    {
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value));
        File.Move(tmp, file, true);
    }
}
