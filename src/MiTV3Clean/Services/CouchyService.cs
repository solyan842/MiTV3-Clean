using System.Text.Json;

namespace MiTV3Clean.Services;

public sealed class CouchyService
{
    private const string Package = "com.conreo.couchytv";
    private const string Component = "com.conreo.couchytv/.MainActivity";

    public async Task<string> DownloadLatestApkAsync(IProgress<string>? progress = null)
    {
        progress?.Report("Đang tìm bản Couchy Launcher mới nhất...");
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MiTV3Clean/0.1");

        var json = await http.GetStringAsync("https://api.github.com/repos/conreo/couchy-launcher/releases/latest");
        using var doc = JsonDocument.Parse(json);
        var assets = doc.RootElement.GetProperty("assets");

        string? url = null;
        string? name = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var candidate = asset.GetProperty("name").GetString();
            if (candidate?.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) == true)
            {
                name = candidate;
                url = asset.GetProperty("browser_download_url").GetString();
                break;
            }
        }

        if (url is null) throw new InvalidOperationException("Release Couchy mới nhất không có APK.");

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiTV3Clean", "downloads");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, name ?? "couchy.apk");
        await File.WriteAllBytesAsync(target, await http.GetByteArrayAsync(url));
        progress?.Report($"Đã tải {Path.GetFileName(target)}");
        return target;
    }

    public async Task<string> InstallAsync(AdbService adb, IProgress<string>? progress = null)
    {
        var apk = await DownloadLatestApkAsync(progress);
        progress?.Report("Đang cài Couchy Launcher...");
        var result = await adb.RunDeviceAsync($"install -r \"{apk}\"");
        if (!result.Contains("Success", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(result);

        await adb.RunShellAsync($"am start -n {Component}");
        return result;
    }

    public async Task<string> DiagnoseHomeAsync(AdbService adb)
    {
        var lines = new List<string>
        {
            "=== MiTV3 HOME DIAGNOSTIC ===",
            "Couchy package: " + await SafeShellAsync(adb, $"pm list packages {Package}"),
            "Android SDK: " + await SafeShellAsync(adb, "getprop ro.build.version.sdk"),
            "Android release: " + await SafeShellAsync(adb, "getprop ro.build.version.release"),
            "Build: " + await SafeShellAsync(adb, "getprop ro.build.display.id"),
            "cmd binary: " + await SafeShellAsync(adb, "if [ -x /system/bin/cmd ]; then echo present; else echo missing; fi"),
            "",
            "HOME resolve:",
            await ResolveHomeAsync(adb),
            "",
            "HOME candidates:"
        };

        var query = await SafeShellAsync(
            adb,
            "pm query-activities -a android.intent.action.MAIN -c android.intent.category.HOME");

        if (LooksLikeShellFailure(query) || string.IsNullOrWhiteSpace(query))
        {
            query = await SafeShellAsync(
                adb,
                "dumpsys package | grep -i -A 8 -B 2 'android.intent.category.HOME'");
        }
        lines.Add(query);

        lines.Add("");
        lines.Add("Couchy package detail:");
        lines.Add(await SafeShellAsync(adb, $"dumpsys package {Package}"));

        var text = string.Join("\n", lines);
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiTV3Clean", "diagnostics");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"home-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        await File.WriteAllTextAsync(path, text);
        return text + "\n\nĐã lưu: " + path;
    }

    public async Task<string> SetHomeAsync(AdbService adb)
    {
        var report = new List<string>();

        var installed = await adb.RunShellAsync($"pm list packages {Package}");
        if (!installed.Contains(Package, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Couchy Launcher chưa được cài trên TV.");

        try
        {
            await adb.RunShellAsync($"pm enable {Package}");
        }
        catch (Exception ex)
        {
            report.Add("Không thể gọi pm enable: " + ex.Message);
        }

        var cmdCheck = await SafeShellAsync(adb, "if [ -x /system/bin/cmd ]; then echo CMD_OK; else echo CMD_MISSING; fi");
        var hasCmd = cmdCheck.Contains("CMD_OK", StringComparison.OrdinalIgnoreCase);

        if (hasCmd)
        {
            var output = await SafeShellAsync(adb, $"cmd package set-home-activity {Component}");
            report.Add("set-home-activity: " + output);

            if (!LooksLikeShellFailure(output))
            {
                var resolved = await ResolveHomeAsync(adb);
                if (resolved.Contains(Package, StringComparison.OrdinalIgnoreCase))
                {
                    report.Add("HOME hiện tại: " + resolved);
                    return "Đã đặt Couchy làm HOME thành công.\n" + string.Join("\n", report);
                }
            }
        }
        else
        {
            report.Add("Firmware Android cũ: không có /system/bin/cmd.");
        }

        var launch = await SafeShellAsync(adb, $"am start -n {Component}");
        report.Add("Mở Couchy: " + launch);

        var homeCall = await SafeShellAsync(
            adb,
            "am start -a android.intent.action.MAIN -c android.intent.category.HOME");
        report.Add("Gọi HOME: " + homeCall);

        var currentHome = await ResolveHomeAsync(adb);
        report.Add("HOME Android đang resolve: " + currentHome);

        if (currentHome.Contains(Package, StringComparison.OrdinalIgnoreCase))
            return "Couchy hiện đã là HOME.\n" + string.Join("\n", report);

        return
            "Couchy đã cài và mở được, nhưng firmware MiTV3 chưa cho đặt HOME tự động.\n" +
            "Bấm 'Chẩn đoán HOME' để app tự thu thập launcher hiện tại và toàn bộ HOME candidates. " +
            "Không disable launcher Xiaomi trước khi biết chính xác package nào đang giữ HOME.\n\n" +
            string.Join("\n", report);
    }

    private static async Task<string> ResolveHomeAsync(AdbService adb)
    {
        var pm = await SafeShellAsync(
            adb,
            "pm resolve-activity -a android.intent.action.MAIN -c android.intent.category.HOME");

        if (!LooksLikeShellFailure(pm) && !string.IsNullOrWhiteSpace(pm))
            return pm.Trim();

        var dump = await SafeShellAsync(adb, "dumpsys package preferred-activities");
        if (!string.IsNullOrWhiteSpace(dump))
        {
            var lines = dump.Split('\n')
                .Select(x => x.Trim())
                .Where(x => x.Contains("HOME", StringComparison.OrdinalIgnoreCase) ||
                            x.Contains("couchytv", StringComparison.OrdinalIgnoreCase) ||
                            x.Contains("launcher", StringComparison.OrdinalIgnoreCase) ||
                            x.Contains("tvhome", StringComparison.OrdinalIgnoreCase))
                .Take(20);
            var summary = string.Join(" | ", lines);
            if (!string.IsNullOrWhiteSpace(summary))
                return summary;
        }

        return "Không xác định được bằng shell của firmware này.";
    }

    private static async Task<string> SafeShellAsync(AdbService adb, string command)
    {
        try
        {
            return (await adb.RunShellAsync(command)).Trim();
        }
        catch (Exception ex)
        {
            return ex.Message.Trim();
        }
    }

    private static bool LooksLikeShellFailure(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;

        var s = output.ToLowerInvariant();
        return s.Contains("not found") ||
               s.Contains("unknown command") ||
               s.Contains("unknown option") ||
               s.Contains("error:") ||
               s.Contains("exception") ||
               s.Contains("failure");
    }
}
