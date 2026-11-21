using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Nodes;
using Photino.NET;

static class Program
{
    [STAThread] // WebView2 bir COM bileşeni: ana iş parçacığı STA olmalı, yoksa pencere boş kalır
    static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = new CultureInfo("tr-TR");
        string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

        if (args.Contains("--selftest")) { SelfTest.Run(); return Environment.ExitCode; }

        // pencere yetkisiz çalışır; servis, kurulum ve probe yönetici ister
        string[] privileged = ["--service", "--install", "--uninstall", "--probe"];
        if (args.Any(privileged.Contains) && OperatingSystem.IsWindows() && !IsAdmin())
            return Elevated(args);

        if (args.Contains("--probe")) { SelfTest.Probe(args[^1]); return 0; }
        if (args.Contains("--install") || args.Contains("--uninstall"))
        {
            Directory.CreateDirectory(Paths.DataDir);
            var errorFile = Path.Combine(Paths.DataDir, "kurulum-hatasi.txt");
            File.Delete(errorFile);
            try
            {
                if (args.Contains("--install")) Service.Install(Arg("--owner"));
                else Service.Uninstall();
                return 0;
            }
            catch (Exception e)
            {
                // yükseltilmiş süreç çıktısını pencereye aktaramaz: hatayı dosyaya bırak
                Directory.CreateDirectory(Paths.DataDir);
                File.WriteAllText(errorFile, e.Message);
                return 1;
            }
        }

        if (args.Contains("--service"))
        {
            if (OperatingSystem.IsWindows() && !Environment.UserInteractive) { ServiceBase.Run(new WinService(Arg("--owner"))); return 0; }
            // konsolda (geliştirme) veya Linux/macOS'ta: sudo akis --service
            using var host = new Host(Arg("--owner"));
            var exit = new ManualResetEventSlim();
            using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; exit.Set(); });
            using var intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; exit.Set(); });
            exit.Wait();
            return 0;
        }

        RunWindow(args);
        return 0;
    }

    static bool IsAdmin() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    // aynı argümanlarla UAC üzerinden yeniden başlat ve bitmesini bekle
    static int Elevated(string[] args)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", Arguments = string.Join(" ", args.Select(a => $"\"{a}\"")) };
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception) { return 1223; } // ERROR_CANCELLED: kullanıcı UAC'yi reddetti
    }

    static void RunWindow(string[] args)
    {
        var window = new PhotinoWindow()
            .SetTitle("Akış")
            .SetUseOsDefaultSize(false)
            .SetSize(1320, 860)
            .SetMinSize(960, 620)
            .Center()
            .SetContextMenuEnabled(false)
            .SetDevToolsEnabled(args.Contains("--dev"))
            // Program Files'tan çalışırken WebView2 verisi yazılabilir bir yerde dursun
            .SetTemporaryFilesPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Akis", "webview"));

        var gate = new object();
        Conn? client = null;
        var started = false;

        void Status(string state, string? detail = null) => window.SendWebMessage(JsonSerializer.Serialize(new
        {
            type = "service", state, detail,
            installed = Ipc.Installed(),
            canInstall = OperatingSystem.IsWindows(),
        }));

        // servise bağlı kal; servis yeniden başlarsa kendiliğinden tekrar bağlan
        void ConnectLoop()
        {
            while (true)
            {
                var pipe = new NamedPipeClientStream(".", Ipc.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
                try { pipe.Connect(1500); }
                catch (Exception e) when (e is System.TimeoutException or IOException or UnauthorizedAccessException)
                {
                    pipe.Dispose();
                    Status(Ipc.Running() ? "starting" : "missing", e.Message);
                    Thread.Sleep(2000);
                    continue;
                }
                var c = new Conn(pipe);
                lock (gate) client = c;
                Status("connected");
                try { while (c.Reader.ReadLine() is { } line) window.SendWebMessage(line); }
                catch (IOException) { }
                lock (gate) client = null;
                c.Dispose();
                Status("lost");
                Thread.Sleep(1000);
            }
        }

        void Install()
        {
            Status("installing");
            var sid = WindowsIdentity.GetCurrent().User!.Value;
            var code = Elevated(["--install", "--owner", sid]);
            var error = Path.Combine(Paths.DataDir, "kurulum-hatasi.txt");
            var text = code switch
            {
                0 => "Servis kuruldu ve başlatıldı",
                1223 => "Kurulum iptal edildi",
                _ => "Servis kurulamadı: " + (File.Exists(error) ? File.ReadAllText(error) : $"çıkış kodu {code}"),
            };
            window.SendWebMessage(JsonSerializer.Serialize(new { type = "toast", text, ok = code == 0 }));
        }

        window.RegisterWebMessageReceivedHandler((_, msg) =>
        {
            switch ((string?)JsonNode.Parse(msg)?["type"])
            {
                case "ready":
                    if (started) return;
                    started = true;
                    new Thread(ConnectLoop) { IsBackground = true, Name = "ipc" }.Start();
                    return;
                case "install":
                    Task.Run(Install);
                    return;
                default:
                    lock (gate) client?.Send(msg);
                    return;
            }
        });

        window.Load(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html"));
        window.WaitForClose();
    }
}
