using System.Text.Json;
using System.Text.RegularExpressions;

namespace MiTV3Clean.Services;

public sealed class CouchyService
{
    private const string Package = "com.conreo.couchytv";
    private const string Component = "com.conreo.couchytv/.MainActivity";
    private const string XiaomiHomePackage = "com.mitv.tvhome";
    private const string XiaomiHomeComponent = "com.mitv.tvhome/.MainActivityUserMode";

    public async Task<string> DownloadLatestApkAsync(IProgress<string>? progress = null)
    {
        progress?.Report("Đang tìm bản Couchy Launcher mới nhất...");
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MiTV3Clean/0.1.3");

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
        var xiaomiDump = await SafeShellAsync(adb, $"dumpsys package {XiaomiHomePackage}");

        await SafeShellAsync(adb, "input keyevent 3");
        await Task.Delay(900);

        var windowDump = await SafeShellAsync(adb, "dumpsys window windows");
        var activityDump = await SafeShellAsync(adb, "dumpsys activity activities");

        var focusedWindow = ExtractFocusLine(windowDump);
        var focusedActivity = ExtractFocusLine(activityDump);
        var currentHomeComponent = ExtractComponentFromFocus(focusedWindow) ?? ExtractComponentFromFocus(focusedActivity) ?? "không xác định";
        var currentHomePackage = ExtractPackageFromComponent(currentHomeComponent) ?? "không xác định";

        var homeContexts = ExtractContexts(packageDump, "android.intent.category.HOME", 6, 12);

        var lines = new List<string>
        {
            "=== MiTV3 HOME DIAGNOSTIC v0.1.3 ===",
            "Couchy package: " + await SafeShellAsync(adb, $"pm list packages {Package}"),
            "Android SDK: " + await SafeShellAsync(adb, "getprop ro.build.version.sdk"),
            "Android release: " + await SafeShellAsync(adb, "getprop ro.build.version.release"),
            "Build: " + await SafeShellAsync(adb, "getprop ro.build.display.id"),
            "cmd binary: " + await SafeShellAsync(adb, "if [ -x /system/bin/cmd ]; then echo present; else echo missing; fi"),
            "",
            "HOME thực tế sau KEYCODE_HOME:",
            "Window focus: " + focusedWindow,
            "Activity focus: " + focusedActivity,
            "Current HOME component: " + currentHomeComponent,
            "Current HOME package: " + currentHomePackage,
            "",
            "Couchy explicit HOME test:",
            await SafeShellAsync(adb, $"am start -W -a android.intent.action.MAIN -c android.intent.category.HOME -p {Package}"),
            "",
            "HOME candidates từ dumpsys package:"
        };

        if (homeContexts.Count == 0)
            lines.Add("Firmware không in category HOME trong dumpsys package; dùng focus thực tế + explicit HOME test.");
        else
            lines.AddRange(homeContexts);

        lines.Add("");
        lines.Add("Couchy package detail:");
        lines.Add(couchyDump);
        lines.Add("");
        lines.Add("Xiaomi HOME package detail:");
        lines.Add(xiaomiDump);

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
        var cmdCheck = await SafeShellAsync(adb, "if [ -x /system/bin/cmd ]; then echo CMD_OK; else echo CMD_MISSING; fi");
        if (cmdCheck.Contains("CMD_OK", StringComparison.OrdinalIgnoreCase))
        {
            var output = await SafeShellAsync(adb, $"cmd package set-home-activity {Component}");
            await SafeShellAsync(adb, "input keyevent 3");
            await Task.Delay(700);
            var actual = await DetectCurrentHomeComponentAsync(adb);

            return actual.Contains(Package, StringComparison.OrdinalIgnoreCase)
                ? "Đã đặt Couchy làm HOME thành công.\nHOME thực tế: " + actual + "\n" + output
                : "set-home-activity đã chạy nhưng HOME thực tế chưa phải Couchy.\nHOME: " + actual + "\n" + output;
        }

        return await SwitchLegacyHomeAsync(adb);
    }

    public async Task<string> SwitchLegacyHomeAsync(AdbService adb)
    {
        var report = new List<string>();

        var installed = await SafeShellAsync(adb, $"pm list packages {Package}");
        if (!installed.Contains(Package, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Couchy Launcher chưa được cài trên TV.");

        await SafeShellAsync(adb, $"pm enable {Package}");

        var explicitHome = await SafeShellAsync(
            adb,
            $"am start -W -a android.intent.action.MAIN -c android.intent.category.HOME -p {Package}");
        report.Add("Kiểm tra Couchy nhận HOME: " + explicitHome);

        if (LooksLikeShellFailure(explicitHome) ||
            explicitHome.Contains("unable to resolve", StringComparison.OrdinalIgnoreCase) ||
            explicitHome.Contains("no activities", StringComparison.OrdinalIgnoreCase))
        {
            return "DỪNG AN TOÀN: firmware không resolve được Couchy cho intent HOME. Không thay đổi launcher Xiaomi.\n" +
                   string.Join("\n", report);
        }

        await SafeShellAsync(adb, "input keyevent 3");
        await Task.Delay(800);
        var before = await DetectCurrentHomeComponentAsync(adb);
        report.Add("HOME trước khi chuyển: " + before);

        if (before.Contains(Package, StringComparison.OrdinalIgnoreCase))
            return "Couchy đã là HOME, không cần thay đổi.\n" + string.Join("\n", report);

        if (!before.StartsWith(XiaomiHomePackage + "/", StringComparison.OrdinalIgnoreCase))
        {
            return "DỪNG AN TOÀN: HOME hiện tại không phải launcher Xiaomi đã xác nhận. Không disable component nào.\n" +
                   string.Join("\n", report);
        }

        // Disable ONLY the currently focused Xiaomi HOME activity, never the whole package.
        var disable = await SafeShellAsync(adb, $"pm disable-user --user 0 {before}");
        report.Add("Disable activity HOME Xiaomi: " + disable);

        if (LooksLikeShellFailure(disable))
        {
            await SafeShellAsync(adb, $"pm enable {before}");
            return "Không disable được activity HOME Xiaomi; đã yêu cầu enable lại để đảm bảo an toàn.\n" +
                   string.Join("\n", report);
        }

        await SafeShellAsync(adb, "input keyevent 3");
        await Task.Delay(1200);
        var after = await DetectCurrentHomeComponentAsync(adb);
        report.Add("HOME sau khi chuyển: " + after);

        if (after.Contains(Package, StringComparison.OrdinalIgnoreCase))
        {
            return "CHUYỂN HOME LEGACY THÀNH CÔNG: Couchy đang nhận phím HOME.\n" +
                   "Chỉ activity HOME của Xiaomi bị disable; package com.mitv.tvhome vẫn còn nguyên để giảm rủi ro.\n" +
                   string.Join("\n", report);
        }

        // Automatic rollback.
        var rollback = await SafeShellAsync(adb, $"pm enable {before}");
        report.Add("ROLLBACK enable Xiaomi HOME: " + rollback);
        await SafeShellAsync(adb, "input keyevent 3");
        await Task.Delay(800);
        var restored = await DetectCurrentHomeComponentAsync(adb);
        report.Add("HOME sau rollback: " + restored);

        return "CHUYỂN HOME KHÔNG THÀNH CÔNG - ĐÃ ROLLBACK TỰ ĐỘNG. Launcher Xiaomi đã được bật lại.\n" +
               string.Join("\n", report);
    }

    public async Task<string> RestoreXiaomiHomeAsync(AdbService adb)
    {
        var report = new List<string>();

        var enableComponent = await SafeShellAsync(adb, $"pm enable {XiaomiHomeComponent}");
        report.Add("Enable Xiaomi HOME component: " + enableComponent);

        var enablePackage = await SafeShellAsync(adb, $"pm enable {XiaomiHomePackage}");
        report.Add("Enable Xiaomi HOME package: " + enablePackage);

        await SafeShellAsync(adb, "input keyevent 3");
        await Task.Delay(900);

        var actual = await DetectCurrentHomeComponentAsync(adb);
        report.Add("HOME thực tế: " + actual);

        return "Đã yêu cầu khôi phục launcher Xiaomi.\n" + string.Join("\n", report);
    }

    private static async Task<string> DetectCurrentHomeComponentAsync(AdbService adb)
    {
        var windowDump = await SafeShellAsync(adb, "dumpsys window windows");
        var line = ExtractFocusLine(windowDump);
        var component = ExtractComponentFromFocus(line);
        if (!string.IsNullOrWhiteSpace(component))
            return component;

        var activityDump = await SafeShellAsync(adb, "dumpsys activity activities");
        line = ExtractFocusLine(activityDump);
        component = ExtractComponentFromFocus(line);
        return component ?? "không xác định";
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

    private static string? ExtractComponentFromFocus(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var match = Regex.Match(line, @"([A-Za-z0-9_.$]+/[A-Za-z0-9_.$]+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ExtractPackageFromComponent(string component)
    {
        if (string.IsNullOrWhiteSpace(component))
            return null;

        var slash = component.IndexOf('/');
        return slash > 0 ? component[..slash] : null;
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
               s.Contains("failure") ||
               s.Contains("securityexception");
    }
}
