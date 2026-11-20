using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text.Json;
using Photino.NET;

static class Program
{
    [STAThread] // WebView2 bir COM bileşeni: ana iş parçacığı STA olmalı, yoksa pencere boş kalır
    static void Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = new CultureInfo("tr-TR");

        if (args.Contains("--selftest")) { SelfTest.Run(); return; }

        // ETW, güvenlik duvarı ve QoS yönetici ister: yetkisizsek UAC ile kendimizi yeniden başlatırız
        if (OperatingSystem.IsWindows() && !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", Arguments = string.Join(" ", args.Select(a => $"\"{a}\"")) };
            try { Process.Start(psi); }
            catch (System.ComponentModel.Win32Exception) { } // kullanıcı UAC'yi reddetti
            return;
        }

        if (args.Contains("--probe")) { SelfTest.Probe(args[^1]); return; }

        // çekirdek ETW oturumu sistem genelinde tek: yeni açılan örnek eskisini kapatır
        // ponytail: eski pencereyi öne getirmek daha kibar olurdu, gerekirse named pipe ile eklenir
        foreach (var old in Process.GetProcessesByName("Akis").Where(p => p.Id != Environment.ProcessId))
            try { old.Kill(); old.WaitForExit(3000); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }

        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Akis");
        using var engine = new Engine(dataDir);

        var window = new PhotinoWindow()
            .SetTitle("Akış")
            .SetUseOsDefaultSize(false)
            .SetSize(1320, 860)
            .SetMinSize(960, 620)
            .Center()
            .SetContextMenuEnabled(false)
            .SetDevToolsEnabled(args.Contains("--dev"));

        var started = false;
        window.RegisterWebMessageReceivedHandler((_, msg) =>
        {
            if (!started && msg.Contains("\"ready\""))
            {
                started = true;
                new Thread(() =>
                {
                    while (true)
                    {
                        // tek bir hatalı tur canlı güncellemeyi sessizce öldürmesin
                        try { window.SendWebMessage(engine.Tick()); }
                        catch (Exception e) { window.SendWebMessage(JsonSerializer.Serialize(new { type = "toast", text = "Güncelleme hatası: " + e.Message, ok = false })); }
                        Thread.Sleep(1000);
                    }
                }) { IsBackground = true }.Start();
                return;
            }
            var reply = engine.Handle(msg, window.SendWebMessage);
            if (reply != null) window.SendWebMessage(reply);
        });

        window.Load(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html"));
        window.WaitForClose();
    }
}
