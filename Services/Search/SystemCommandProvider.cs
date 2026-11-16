using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace OpenTaskbar.Services.Search;

/// <summary>Built-in Windows and OpenTaskbar actions exposed in Spotlight search.</summary>
public static class SystemCommandProvider
{
    public sealed record CommandDefinition(
        string Key,
        string TurkishTitle,
        string EnglishTitle,
        string TurkishSubtitle,
        string EnglishSubtitle,
        string Keywords,
        string GeometryKey,
        Action<Action> ExecuteFactory);

    public sealed record Actions(
        Action OpenSettings,
        Action OpenWidgets,
        Action ShowDesktop);

    public static IReadOnlyList<(string Key, string Title, string Subtitle, SearchText Text, string GeometryKey, Func<bool> Execute)> Build(Actions actions)
    {
        var isEn = UiText.IsEnglish;
        var list = new List<(string, string, string, SearchText, string, Func<bool>)>();

        void Add(string key, string trTitle, string enTitle, string trSub, string enSub, string keywords, string geometryKey, Func<bool> execute)
        {
            var title = isEn ? enTitle : trTitle;
            var subtitle = isEn ? enSub : trSub;
            list.Add((key, title, subtitle, new SearchText($"{trTitle} {enTitle} {keywords}"), geometryKey, execute));
        }

        Add("cmd:opentaskbar-settings", "OpenTaskbar Ayarları", "OpenTaskbar Settings",
            "Dock, görünüm ve widget tercihleri", "Dock, appearance, and widget preferences",
            "ayarlar tercihler dock settings preferences", "IconSettings",
            () => { actions.OpenSettings(); return true; });

        Add("cmd:widgets", "Widget’lar ve Kontrol Merkezi", "Widgets & Control Center",
            "Saat, ses, medya, hava durumu, piyasalar ve sistem", "Clock, sound, media, weather, markets, and system",
            "widget widgets hava durumu weather borsa market haber news medya media", "IconWidgets",
            () => { actions.OpenWidgets(); return true; });

        Add("cmd:show-desktop", "Masaüstünü Göster", "Show Desktop",
            "Açık pencereleri küçült veya geri getir", "Minimize or restore open windows",
            "masaustu desktop minimize", "IconDesktop",
            () => { actions.ShowDesktop(); return true; });

        Add("cmd:win-settings", "Windows Ayarları", "Windows Settings",
            "Sistem ayarlarını aç", "Open system settings",
            "sistem system settings ayar", "IconSettings",
            () => LaunchUri("ms-settings:"));

        Add("cmd:sound", "Ses Ayarları", "Sound Settings",
            "Çıkış ve giriş ses aygıtları", "Output and input audio devices",
            "ses audio volume speaker mikrofon", "IconVolume",
            () => LaunchUri("ms-settings:sound"));

        Add("cmd:wifi", "Wi‑Fi ve Ağ Ayarları", "Wi‑Fi & Network Settings",
            "Kablosuz ağlar ve bağlantı durumu", "Wireless networks and connectivity",
            "wifi ağ ag internet network ethernet", "IconWifi",
            () => LaunchUri("ms-settings:network-wifi"));

        Add("cmd:bluetooth", "Bluetooth Aygıtları", "Bluetooth Devices",
            "Kablosuz aygıtları yönet", "Manage wireless devices",
            "bluetooth kulaklik mouse", "IconSystem",
            () => LaunchUri("ms-settings:bluetooth"));

        Add("cmd:display", "Ekran ve Parlaklık", "Display Settings",
            "Çözünürlük, ölçek ve çoklu ekran", "Resolution, scaling, and displays",
            "ekran monitor display brightness", "IconDesktop",
            () => LaunchUri("ms-settings:display"));

        Add("cmd:power", "Güç ve Pil Ayarları", "Power & Battery Settings",
            "Güç modu ve pil kullanımı", "Power mode and battery usage",
            "pil guc battery power sleep", "IconBattery",
            () => LaunchUri("ms-settings:powersleep"));

        Add("cmd:taskmgr", "Görev Yöneticisi", "Task Manager",
            "Çalışan süreçleri ve performansı izle", "Monitor running processes and performance",
            "gorev yoneticisi task manager cpu ram", "IconActivity",
            () => LaunchProcess("taskmgr.exe", null));

        Add("cmd:terminal", "Windows Terminal / PowerShell", "Windows Terminal / PowerShell",
            "Komut satırı oturumu başlat", "Start a command-line session",
            "terminal powershell cmd komut", "IconTerminal",
            () => LaunchProcess("wt.exe", null) || LaunchProcess("powershell.exe", null));

        Add("cmd:recycle", "Geri Dönüşüm Kutusu", "Recycle Bin",
            "Silinen dosyaları görüntüle", "View deleted files",
            "cop geri donusum recycle bin trash", "IconFolder",
            () => LaunchProcess("explorer.exe", "shell:RecycleBinFolder"));

        Add("cmd:lock", "Ekranı Kilitle", "Lock Screen",
            "Windows oturumunu kilitle", "Lock your Windows session",
            "kilitle lock screen win+l", "IconLock",
            () => { LockWorkStation(); return true; });

        Add("cmd:sleep", "Uyku Modu", "Sleep",
            "Bilgisayarı uyku durumuna geçir", "Put the computer to sleep",
            "uyku sleep suspend", "IconPower",
            () => LaunchProcess("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0"));

        Add("cmd:restart", "Yeniden Başlat", "Restart",
            "Bilgisayarı yeniden başlat", "Restart the computer",
            "yeniden baslat restart reboot", "IconPower",
            () => ConfirmAndRun("ConfirmRestart", "shutdown.exe", "/r /t 0"));

        Add("cmd:shutdown", "Bilgisayarı Kapat", "Shut Down",
            "Bilgisayarı güvenli biçimde kapat", "Shut down the computer",
            "bilgisayari kapat shutdown power off", "IconPower",
            () => ConfirmAndRun("ConfirmShutdown", "shutdown.exe", "/s /t 0"));

        Add("cmd:signout", "Oturumu Kapat", "Sign Out",
            "Geçerli kullanıcı oturumundan çık", "Sign out of the current session",
            "oturumu kapat sign out log off", "IconExit",
            () => ConfirmAndRun("ConfirmSignOut", "shutdown.exe", "/l"));

        return list;
    }

    public static bool LaunchUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool LaunchProcess(string fileName, string? arguments)
    {
        try
        {
            var info = new ProcessStartInfo(fileName) { UseShellExecute = true };
            if (!string.IsNullOrEmpty(arguments)) info.Arguments = arguments;
            Process.Start(info)?.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ConfirmAndRun(string messageKey, string fileName, string arguments)
    {
        if (MessageBox.Show(UiText.Get(messageKey), UiText.Get("ConfirmTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return false;
        }

        return LaunchProcess(fileName, arguments);
    }

    public static Geometry? ResolveGeometry(string key) =>
        Application.Current.TryFindResource(key) as Geometry;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();
}
