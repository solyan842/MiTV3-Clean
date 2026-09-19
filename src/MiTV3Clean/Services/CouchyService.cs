using System.Text.Json;
using System.Text.RegularExpressions;

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
        var packageDump = await SafeShellAsync(adb, "dumpsys package");
        var couchyDump = await SafeShellAsync(adb, $"dumpsys package {Package}");

        await SafeShellAsync(adb, "input keyevent 3");
        await Task.Delay(900);

        var windowDump = await SafeShellAsync(adb, "dumpsys window windows");
        var activityDump = await SafeShellAsync(adb, "dumpsys activity activities");

        var focusedWindow = ExtractFocusLine(windowDump);
        var focusedActivity = ExtractFocusLine(activityDump);
        var currentHomePackage = ExtractPackageFromFocus(focusedWindow) ?? ExtractPackageFromFocus(focusedActivity) ?? "không xác định";

        var homeContexts = ExtractContexts(packageDump, "android.intent.category.HOME", 6, 12);
        var couchyHomeRegistered = couchyDump.Contains("android.intent.category.HOME", StringComparison.OrdinalIgnoreCase) ||
                                   homeContexts.Any(x => x.Contains(Package, StringComparison.OrdinalIgnoreCase));

        var lines = new List<string>
        {
            "=== MiTV3 HOME DIAGNOSTIC v0.1.2 ===",
            "Couchy package: " + await SafeShellAsync(adb, $"pm list packages {Package}"),
            "Android SDK: " + await SafeShellAsync(adb, "getprop ro.build.version.sdk"),
            "Android release: " + await SafeShellAsync(adb, "getprop ro.build.version.release"),
            "Build: " + await SafeShellAsync(adb, "getprop ro.build.display.id"),
            "cmd binary: " + await SafeShellAsync(adb, "if [ -x /system/bin/cmd ]; then echo present; else echo missing; fi"),
            "",
            "HOME thực tế sau khi gửi KEYCODE_HOME:",
            "Window focus: " + focusedWindow,
            "Activity focus: " + focusedActivity,
            "Current HOME package: " + currentHomePackage,
            "",
            "Couchy có đăng ký HOME: " + (couchyHomeRegistered ? "YES" : "NO/CHƯA XÁC NHẬN"),
            "",
            "HOME candidates từ dumpsys package:"
        };

        if (homeContexts.Count == 0)
            lines.Add("Không tìm thấy chuỗi android.intent.category.HOME trong dumpsys package.");
        else
            lines.AddRange(homeContexts);

        lines.Add("");
        lines.Add("Couchy package detail:");
        lines.Add(couchyDump);

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
                await SafeShellAsync(adb, "input keyevent 3");
                await Task.Delay(700);
                var actual = await DetectCurrentHomePackageAsync(adb);
                if (actual.Contains(Package, StringComparison.OrdinalIgnoreCase))
                {
                    report.Add("HOME thực tế: " + actual);
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

        var forced = await SafeShellAsync(
            adb,
            $"am start -W -a android.intent.action.MAIN -c android.intent.category.HOME -p {Package}");
        report.Add("Test HOME chỉ trong package Couchy: " + forced);

        await SafeShellAsync(adb, "input keyevent 3");
        await Task.Delay(900);

        var currentHome = await DetectCurrentHomePackageAsync(adb);
        report.Add("HOME thực tế sau KEYCODE_HOME: " + currentHome);

        if (currentHome.Contains(Package, StringComparison.OrdinalIgnoreCase))
            return "Couchy hiện đã là HOME.\n" + string.Join("\n", report);

        return
            "Couchy đã cài và mở được nhưng HOME thực tế vẫn chưa chuyển sang Couchy.\n" +
            "Firmware Android 5.1.1 của MiTV3 không hỗ trợ set-home-activity hiện đại. " +
            "Bấm 'Chẩn đoán HOME' ở v0.1.2 để lấy đúng launcher đang giữ HOME; từ đó app có thể chuyển theo cơ chế legacy có rollback.\n\n" +
            string.Join("\n", report);
    }

    private static async Task<string> DetectCurrentHomePackageAsync(AdbService adb)
    {
        var windowDump = await SafeShellAsync(adb, "dumpsys window windows");
        var line = ExtractFocusLine(windowDump);
        var package = ExtractPackageFromFocus(line);
        if (!string.IsNullOrWhiteSpace(package))
            return package;

        var activityDump = await SafeShellAsync(adb, "dumpsys activity activities");
        line = ExtractFocusLine(activityDump);
        package = ExtractPackageFromFocus(line);
        return package ?? "không xác định";
    }

    private static string ExtractFocusLine(string dump)
    {
        var lines = dump.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var keys = new[] { "mCurrentFocus", "mFocusedApp", "mResumedActivity", "mFocusedActivity" };

        foreach (var key in keys)
        {
            var hit = lines.FirstOrDefault(x => x.Contains(key, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(hit))
                return hit.Trim();
        }

        return "không tìm thấy focus";
    }

    private static string? ExtractPackageFromFocus(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var match = Regex.Match(line, @"([A-Za-z0-9_.$]+)/[A-Za-z0-9_.$]+");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static List<string> ExtractContexts(string text, string needle, int before, int after)
    {
        var result = new List<string>();
        var lines = text.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(needle, StringComparison.OrdinalIgnoreCase))
                continue;

            var start = Math.Max(0, i - before);
            var end = Math.Min(lines.Length - 1, i + after);
            var block = string.Join("\n", lines[start..(end + 1)]).Trim();

            if (!string.IsNullOrWhiteSpace(block) && !result.Contains(block))
                result.Add(block);
        }

        return result;
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
