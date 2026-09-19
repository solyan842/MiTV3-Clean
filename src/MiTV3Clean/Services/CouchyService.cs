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
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MiTV3Clean/0.1.7");

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
            "=== MiTV3 HOME DIAGNOSTIC v0.1.7 ===",
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

        var couchyStatus = await GetPackageInstallStatusAsync(adb, Package);
        report.Add("Trạng thái Couchy: " + couchyStatus.Detail);
        if (!couchyStatus.Installed)
            throw new InvalidOperationException("Không xác nhận được Couchy Launcher trên TV.\n" + couchyStatus.Detail);

        // Verify Couchy really advertises a HOME activity before touching preferences.
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
            return "DỪNG AN TOÀN: HOME hiện tại không phải launcher Xiaomi đã xác nhận. Không thay đổi preferred HOME.\n" +
                   string.Join("\n", report);
        }

        // IMPORTANT: do not pm disable/disable-user Xiaomi HOME here.
        // MiTV3 runs the Xiaomi launcher as uid 1000 while adb shell is uid 2000,
        // so changing the system component state is rejected with SecurityException.
        report.Add("Không disable Xiaomi HOME: firmware chặn shell UID 2000 đổi component hệ thống UID 1000.");

        // On legacy Android, the safe non-root path is to clear preferred HOME
        // associations and ask PackageManager to resolve a generic HOME intent.
        // This may present the launcher chooser on the TV, where the user selects
        // Couchy and confirms Always/Luôn luôn once.
        var clearXiaomi = await SafeShellAsync(
            adb,
            $"pm clear-package-preferred-activities {XiaomiHomePackage}");
        report.Add("Clear preferred Xiaomi HOME: " + clearXiaomi);

        var clearCouchy = await SafeShellAsync(
            adb,
            $"pm clear-package-preferred-activities {Package}");
        report.Add("Clear preferred Couchy HOME: " + clearCouchy);

        var clearFailed = LooksLikeShellFailure(clearXiaomi) && LooksLikeShellFailure(clearCouchy);
        if (clearFailed)
        {
            // We still launch Couchy explicitly so the launcher itself is usable,
            // but we do not claim that the HOME default was changed.
            var launch = await SafeShellAsync(adb, $"am start -W -n {Component}");
            report.Add("Mở Couchy trực tiếp: " + launch);

            return "Firmware không cho ADB thay preferred HOME. Couchy đã được mở nhưng chưa thể đặt mặc định tự động.\n" +
                   "Không cần root và không disable launcher Xiaomi. Nếu TV có mục chọn launcher mặc định, hãy chọn Couchy tại đó.\n" +
                   string.Join("\n", report);
        }

        var resolver = await SafeShellAsync(
            adb,
            "am start -W -a android.intent.action.MAIN -c android.intent.category.HOME");
        report.Add("Mở HOME chooser: " + resolver);

        await Task.Delay(900);
        var chooserFocus = await DetectCurrentHomeComponentAsync(adb);
        report.Add("Focus sau khi gọi HOME chooser: " + chooserFocus);

        if (chooserFocus.Contains(Package, StringComparison.OrdinalIgnoreCase))
        {
            return "CHUYỂN HOME THÀNH CÔNG: Couchy đang nhận phím HOME.\n" +
                   string.Join("\n", report);
        }

        if (LooksLikeResolver(chooserFocus) || !chooserFocus.StartsWith(XiaomiHomePackage + "/", StringComparison.OrdinalIgnoreCase))
        {
            return "ĐÃ MỞ BỘ CHỌN HOME TRÊN TV. Hãy chọn Couchy Launcher và chọn Always/Luôn luôn một lần.\n" +
                   "App không disable launcher Xiaomi và không cần root.\n" +
                   string.Join("\n", report);
        }

        // Some Xiaomi builds immediately restore their own HOME even after preferred
        // activities are cleared. In that case we stop here instead of escalating.
        var launchCouchy = await SafeShellAsync(adb, $"am start -W -n {Component}");
        report.Add("Fallback mở Couchy trực tiếp: " + launchCouchy);

        return "Firmware Xiaomi đã tự nhận lại HOME sau khi clear preferred. Couchy vẫn mở được trực tiếp, nhưng ADB shell không có quyền ép launcher mặc định trên firmware này.\n" +
               "Không thực hiện disable package/component để tránh SecurityException hoặc boot-loop.\n" +
               string.Join("\n", report);
    }

    public async Task<string> SwitchHomeByUninstallUser0Async(AdbService adb, IProgress<string>? progress = null)
    {
        var report = new List<string>();

        var couchyStatus = await GetPackageInstallStatusAsync(adb, Package);
        report.Add("Trạng thái Couchy: " + couchyStatus.Detail);
        if (!couchyStatus.Installed)
            throw new InvalidOperationException("Không xác nhận được Couchy Launcher trên TV.\n" + couchyStatus.Detail);

        var explicitHome = await SafeShellAsync(
            adb,
            $"am start -W -a android.intent.action.MAIN -c android.intent.category.HOME -p {Package}");
        report.Add("Kiểm tra Couchy nhận HOME: " + explicitHome);

        if (LooksLikeShellFailure(explicitHome) ||
            explicitHome.Contains("unable to resolve", StringComparison.OrdinalIgnoreCase) ||
            explicitHome.Contains("no activities", StringComparison.OrdinalIgnoreCase))
        {
            return "DỪNG AN TOÀN: Couchy chưa resolve được intent HOME. Không gỡ TVHome.\n" +
                   string.Join("\n", report);
        }

        await SafeShellAsync(adb, "input keyevent 3");
        await Task.Delay(700);
        var before = await DetectCurrentHomeComponentAsync(adb);
        report.Add("HOME trước khi chuyển: " + before);

        if (!before.StartsWith(XiaomiHomePackage + "/", StringComparison.OrdinalIgnoreCase))
        {
            return "DỪNG AN TOÀN: HOME hiện tại không phải com.mitv.tvhome. Không thực hiện uninstall user 0.\n" +
                   string.Join("\n", report);
        }

        var pathOutput = await SafeShellAsync(adb, $"pm path {XiaomiHomePackage}");
        var apkPath = pathOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .FirstOrDefault(x => x.StartsWith("package:", StringComparison.OrdinalIgnoreCase));

        if (apkPath is null)
            return "DỪNG AN TOÀN: không lấy được đường dẫn APK gốc của Xiaomi TVHome.\n" + string.Join("\n", report);

        apkPath = apkPath["package:".Length..].Trim();
        report.Add("APK hệ thống TVHome: " + apkPath);

        var backupDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiTV3Clean", "backup");
        Directory.CreateDirectory(backupDir);
        var backupApk = Path.Combine(backupDir, $"com.mitv.tvhome-{DateTime.Now:yyyyMMdd-HHmmss}.apk");

        progress?.Report("Đang sao lưu APK Xiaomi TVHome về máy tính...");
        var pull = await adb.RunDeviceAsync($"pull \"{apkPath}\" \"{backupApk}\"");
        report.Add("Backup TVHome: " + pull);

        if (!File.Exists(backupApk) || new FileInfo(backupApk).Length < 1024)
        {
            return "DỪNG AN TOÀN: backup APK TVHome thất bại hoặc file không hợp lệ. Không uninstall.\n" +
                   string.Join("\n", report);
        }

        var backupMeta = Path.ChangeExtension(backupApk, ".txt");
        await File.WriteAllTextAsync(
            backupMeta,
            $"Package={XiaomiHomePackage}\nComponent={XiaomiHomeComponent}\nDeviceApkPath={apkPath}\nBackupApk={backupApk}\nCreated={DateTime.Now:O}\n");

        progress?.Report("Backup hoàn tất. Đang gỡ TVHome khỏi user 0...");
        var uninstall = await SafeShellAsync(adb, $"pm uninstall -k --user 0 {XiaomiHomePackage}");
        report.Add("Uninstall user 0 TVHome: " + uninstall);

        if (!uninstall.Contains("Success", StringComparison.OrdinalIgnoreCase))
        {
            return "Firmware không cho uninstall TVHome khỏi user 0. TV không bị thay đổi.\n" +
                   "Backup APK đã được lưu tại: " + backupApk + "\n" +
                   string.Join("\n", report);
        }

        await SafeShellAsync(adb, $"am start -W -a android.intent.action.MAIN -c android.intent.category.HOME -p {Package}");
        await SafeShellAsync(adb, "input keyevent 3");
        await Task.Delay(1200);

        var after = await DetectCurrentHomeComponentAsync(adb);
        report.Add("HOME sau uninstall user 0: " + after);

        if (after.Contains(Package, StringComparison.OrdinalIgnoreCase))
        {
            return "CHUYỂN HOME THÀNH CÔNG: Couchy đang nhận phím HOME.\n" +
                   "TVHome chỉ bị gỡ khỏi user 0; APK hệ thống đã được backup trên PC tại:\n" + backupApk + "\n" +
                   string.Join("\n", report);
        }

        progress?.Report("Couchy chưa thành HOME. Đang rollback TVHome từ APK backup...");
        var reinstall = await adb.RunDeviceAsync($"install -r -d \"{backupApk}\"");
        report.Add("Rollback adb install TVHome: " + reinstall);

        await SafeShellAsync(adb, $"am start -W -n {XiaomiHomeComponent}");
        await SafeShellAsync(adb, "input keyevent 3");
        await Task.Delay(1000);

        var restored = await DetectCurrentHomeComponentAsync(adb);
        report.Add("HOME sau rollback: " + restored);

        if (restored.StartsWith(XiaomiHomePackage + "/", StringComparison.OrdinalIgnoreCase))
        {
            return "CHUYỂN HOME KHÔNG THÀNH CÔNG - ĐÃ ROLLBACK TVHOME THÀNH CÔNG.\n" +
                   "Backup vẫn được giữ tại: " + backupApk + "\n" +
                   string.Join("\n", report);
        }

        return "CẢNH BÁO: uninstall user 0 đã chạy nhưng rollback tự động chưa xác nhận TVHome trở lại.\n" +
               "KHÔNG reboot TV. Giữ ADB kết nối và dùng file backup sau để phục hồi thủ công:\n" + backupApk + "\n" +
               string.Join("\n", report);
    }

    public async Task<string> EmergencyRecoverXiaomiHomeAsync(AdbService adb, IProgress<string>? progress = null)
    {
        var report = new List<string>();

        var backupDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiTV3Clean", "backup");

        var latestBackup = Directory.Exists(backupDir)
            ? Directory.GetFiles(backupDir, "com.mitv.tvhome-*.apk")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;

        var path = await SafeShellAsync(adb, $"pm path {XiaomiHomePackage}");
        report.Add("pm path TVHome: " + (string.IsNullOrWhiteSpace(path) ? "(rỗng)" : path));

        var installExisting = await SafeShellAsync(adb, $"pm install-existing --user 0 {XiaomiHomePackage}");
        report.Add("pm install-existing: " + installExisting);

        if (latestBackup is not null)
        {
            progress?.Report("Đang phục hồi TVHome từ APK backup trên PC...");
            var reinstall = await adb.RunDeviceAsync($"install -r -d \"{latestBackup}\"");
            report.Add("adb install backup: " + reinstall);
            report.Add("Backup dùng để phục hồi: " + latestBackup);
        }
        else
        {
            report.Add("Không tìm thấy APK backup TVHome trên PC.");
        }

        var start = await SafeShellAsync(adb, $"am start -W -n {XiaomiHomeComponent}");
        report.Add("Mở Xiaomi HOME trực tiếp: " + start);

        await SafeShellAsync(adb, "input keyevent 3");
        await Task.Delay(1200);

        var actual = await DetectCurrentHomeComponentAsync(adb);
        report.Add("HOME thực tế sau phục hồi: " + actual);

        if (actual.StartsWith(XiaomiHomePackage + "/", StringComparison.OrdinalIgnoreCase))
            return "PHỤC HỒI HOME XIAOMI THÀNH CÔNG.\n" + string.Join("\n", report);

        return "CHƯA XÁC NHẬN PHỤC HỒI XONG. KHÔNG REBOOT TV. Giữ ADB kết nối và gửi log này.\n" +
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

    private static bool LooksLikeResolver(string component)
    {
        if (string.IsNullOrWhiteSpace(component)) return false;

        var s = component.ToLowerInvariant();
        return s.Contains("resolveractivity") ||
               s.Contains("chooseractivity") ||
               s.Contains("android/com.android.internal.app") ||
               s.Contains("com.android.settings");
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
