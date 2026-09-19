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

    public async Task<string> SetHomeAsync(AdbService adb)
    {
        // Newer Android: direct assignment.
        try
        {
            var output = await adb.RunShellAsync($"cmd package set-home-activity {Component}");
            if (!output.Contains("Error", StringComparison.OrdinalIgnoreCase) &&
                !output.Contains("Unknown", StringComparison.OrdinalIgnoreCase))
                return "Đã yêu cầu Android đặt Couchy làm HOME.\n" + output;
        }
        catch { }

        // Android 5/6 fallback: launch Couchy, then HOME. System may show resolver once.
        await adb.RunShellAsync($"pm enable {Package}");
        await adb.RunShellAsync($"am start -n {Component}");
        var fallback = await adb.RunShellAsync("am start -a android.intent.action.MAIN -c android.intent.category.HOME");
        return "Firmware không hỗ trợ set-home-activity. Đã gọi HOME theo cơ chế Android cũ; nếu TV hiện hộp chọn launcher, chọn Couchy và Always.\n" + fallback;
    }
}
