using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;

static class Paths
{
    // servis tüm kullanıcılar adına sayar: veri kullanıcı profilinde değil, makine genelinde durur
    public static string DataDir =>
        OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Akis")
        : OperatingSystem.IsMacOS() ? "/Library/Application Support/Akis"
        : "/var/lib/akis";

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Akis");
}

// Servis ile pencere arasındaki kanal: satır başına bir JSON mesajı.
static class Ipc
{
    public static string Name => OperatingSystem.IsWindows() ? "akis" : "/var/run/akis.sock";
    static bool socketPrepared;

    public static NamedPipeServerStream CreateServer(string? owner)
    {
        if (OperatingSystem.IsWindows())
        {
            // yalnızca SYSTEM, yöneticiler ve servisi kuran kullanıcı bağlanabilir
            var rw = PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;
            var sec = new PipeSecurity();
            sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), rw, AccessControlType.Allow));
            if (owner != null) sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(owner), rw, AccessControlType.Allow));
            return NamedPipeServerStreamAcl.Create(Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, sec);
        }

        if (!socketPrepared) File.Delete(Name); // önceki çalıştırmadan kalan soket
        var pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        if (!socketPrepared)
        {
            // ponytail: soket yalnızca sudo'yu çalıştıran kullanıcıya açık; çok kullanıcılı makinede grup tabanlı izin gerekir
            File.SetUnixFileMode(Name, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if (Environment.GetEnvironmentVariable("SUDO_UID") is { } uid) Platform.Run("chown", uid, Name);
            socketPrepared = true;
        }
        return pipe;
    }

    public static bool Installed() =>
        OperatingSystem.IsWindows() ? ServiceController.GetServices().Any(s => s.ServiceName == Service.Name) : File.Exists(Name);

    public static bool Running() =>
        OperatingSystem.IsWindows() && ServiceController.GetServices().Any(s => s.ServiceName == Service.Name && s.Status == ServiceControllerStatus.Running);
}

sealed class Conn(PipeStream pipe) : IDisposable
{
    public readonly StreamReader Reader = new(pipe, new UTF8Encoding(false));
    readonly StreamWriter writer = new(pipe, new UTF8Encoding(false)) { AutoFlush = true };

    public void Send(string line)
    {
        lock (writer)
        {
            try { writer.WriteLine(line); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { } // karşı taraf kapandı; okuma döngüsü temizler
        }
    }

    public void Dispose() => pipe.Dispose();
}

// Servis süreci: Engine'i saniyede bir çalıştırır, bağlı pencerelere yayınlar, gelen komutları işler.
sealed class Host : IDisposable
{
    readonly Engine engine = new(Paths.DataDir);
    readonly List<Conn> clients = [];
    readonly CancellationTokenSource stop = new();
    readonly string? owner;

    public Host(string? owner)
    {
        this.owner = owner;
        new Thread(TickLoop) { IsBackground = true, Name = "tick" }.Start();
        new Thread(AcceptLoop) { IsBackground = true, Name = "ipc" }.Start();
    }

    void TickLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            string line;
            try { line = engine.Tick(); }
            catch (Exception e) { line = System.Text.Json.JsonSerializer.Serialize(new { type = "toast", text = "Güncelleme hatası: " + e.Message, ok = false }); }
            Conn[] list;
            lock (clients) list = [.. clients];
            // ponytail: donmuş bir pencere yayını bekletir; sayaçlar birikmeye devam ettiği için veri kaybolmaz
            foreach (var c in list) c.Send(line);
            stop.Token.WaitHandle.WaitOne(1000);
        }
    }

    void AcceptLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            var pipe = Ipc.CreateServer(owner);
            try { pipe.WaitForConnectionAsync(stop.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { pipe.Dispose(); return; }
            catch (IOException) { pipe.Dispose(); continue; }
            var c = new Conn(pipe);
            lock (clients) clients.Add(c);
            c.Send(engine.IconsJson()); // sonradan bağlanan pencere daha önce gönderilmiş simgeleri de alsın
            new Thread(() => Serve(c)) { IsBackground = true, Name = "ipc-client" }.Start();
        }
    }

    void Serve(Conn c)
    {
        try
        {
            while (c.Reader.ReadLine() is { } line)
                if (engine.Handle(line, c.Send) is { } reply) c.Send(reply);
        }
        catch (IOException) { }
        finally
        {
            lock (clients) clients.Remove(c);
            c.Dispose();
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        engine.Dispose(); // geçmişi diske yazar
    }
}

sealed class WinService : ServiceBase
{
    readonly string? owner;
    Host? host;

    public WinService(string? owner)
    {
        this.owner = owner;
        ServiceName = Service.Name;
        CanShutdown = true; // bilgisayar kapanırken de geçmiş kaydedilsin
    }

    protected override void OnStart(string[] args) => host = new Host(owner);
    protected override void OnStop() => host?.Dispose();
    protected override void OnShutdown() => host?.Dispose();
}

static class Service
{
    public const string Name = "Akis";

    // Program Files'a kopyala, servisi kaydet ve başlat. owner: pipe'a bağlanabilecek kullanıcının SID'i.
    public static void Install(string? owner)
    {
        var target = Paths.InstallDir;
        Stop();
        // SYSTEM olarak çalışan dosya yalnızca yöneticinin yazabildiği yerde durmalı
        var source = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, true);
            }

        Migrate();
        var bin = $"\"{Path.Combine(target, "Akis.exe")}\" --service" + (owner != null ? $" --owner {owner}" : "");
        if (Ipc.Installed()) Platform.Run("sc.exe", "config", Name, "binPath=", bin, "start=", "auto");
        else Platform.Run("sc.exe", "create", Name, "binPath=", bin, "start=", "auto", "DisplayName=", "Akış ağ servisi");
        Platform.Run("sc.exe", "description", Name, "Uygulama bazlı internet kullanımını sayar; Akış penceresi bu servise bağlanır.");
        using var sc = new ServiceController(Name);
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
    }

    public static void Uninstall()
    {
        Stop();
        if (Ipc.Installed()) Platform.Run("sc.exe", "delete", Name);
        WindowsCapture.ApplyRules(new Dictionary<string, Rule>()); // güvenlik duvarı ve QoS kurallarımızı kaldır
        // veriler (ProgramData\Akis) kullanıcıya ait: bilerek silinmiyor
    }

    static void Stop()
    {
        if (!Ipc.Installed()) return;
        using var sc = new ServiceController(Name);
        if (sc.Status == ServiceControllerStatus.Stopped) return;
        sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
    }

    // tek süreçli ilk sürümün kullanıcı profilindeki kayıtlarını makine geneline taşı
    static void Migrate()
    {
        var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Akis");
        Directory.CreateDirectory(Paths.DataDir);
        foreach (var name in new[] { "history.json", "rules.json" })
        {
            var from = Path.Combine(old, name);
            var to = Path.Combine(Paths.DataDir, name);
            if (File.Exists(from) && !File.Exists(to)) File.Copy(from, to);
        }
    }
}
