using MiTV3Clean.Models;

namespace MiTV3Clean.Services;

public static class PackageClassifier
{
    private static readonly string[] ProtectedExact =
    {
        "android",
        "com.android.systemui",
        "com.android.settings",
        "com.android.providers.settings",
        "com.android.providers.media",
        "com.android.providers.downloads",
        "com.android.packageinstaller",
        "com.android.inputmethod.latin",
        "com.android.bluetooth",
        "com.android.networkstack",
        "com.google.android.webview",
        "com.android.webview",
        "com.mitv.videoplayer"
    };

    private static readonly string[] ProtectedContains =
    {
        "inputmethod", "settings", "systemui", "packageinstaller",
        "bluetooth", "wifi", "network", "ethernet", "media.codec",
        "mediaprovider", "permissioncontroller", "documentsui",
        "tvinput", "hdmi", "cec", "remote", "audio", "surfaceflinger"
    };

    // High-confidence MiTV/MIUI China bloat candidates.
    // The app still uses Disable-first; these are not deleted automatically.
    private static readonly HashSet<string> SafeCandidateExact = new(StringComparer.OrdinalIgnoreCase)
    {
        // Ads / telemetry / statistics
        "com.xiaomi.mitv.advertise",
        "com.miui.systemAdSolution",
        "com.miui.analytics",
        "com.miui.tv.analytics",
        "com.xiaomi.statistic",
        "com.xiaomi.mitv.osstatistic",
        "mitv.service",

        // Store / commerce / payment
        "com.xiaomi.mitv.shop",
        "com.xiaomi.mitv.payment",
        "com.xiaomi.mitv.pay",
        "com.mipay.wallet.tv",
        "com.xiaomi.mitv.appstore",
        "com.mitv.appstore.component.land",

        // Games
        "com.xiaomi.mibox.gamecenter",
        "com.xiaomi.gamecenter.sdk.service.mibox",

        // China content / recommendations
        "com.duokan.videodaily",
        "com.xm.webcontent",
        "com.xiaomi.tv.gallery",
        "com.mitv.gallery",

        // Optional Xiaomi ecosystem services
        "com.xiaomi.smarthome.tv",
        "com.xiaomi.mitv.handbook",
        "com.xiaomi.mitv.calendar",
        "com.xiaomi.tweather",
        "com.xiaomi.mimusic2",
        "com.xiaomi.mitv.karaoke.service",
        "com.xiaomi.mitv.tvpush.tvpushservice",
        "com.xiaomi.screenrecorder",
        "com.miui.screenrecorder",
        "com.sogou.speech.offlineservice"
    };

    // Additional token matches for variants seen across MiTV firmwares.
    private static readonly string[] SafeCandidateTokens =
    {
        ".advertise", ".advert", ".analytics", ".statistic", ".osstatistic",
        ".videodaily", ".gamecenter", ".payment", ".wallet.tv",
        ".mitv.shop", ".mitv.handbook", ".mitv.calendar",
        ".tvpush.tvpushservice", ".smarthome.tv", ".karaoke.service",
        "systemadsolution"
    };

    // These may be removable depending on the user's setup, but should not be
    // auto-selected because they can affect casting, updates, account login, etc.
    private static readonly HashSet<string> ReviewExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "com.xiaomi.account",
        "com.xiaomi.account.auth",
        "com.xiaomi.mitv.upgrade",
        "com.xiaomi.tv.appupgrade",
        "com.xiaomi.mitv.tvmanager",
        "com.duokan.airkan.tvbox",
        "com.mitv.milinkservice",
        "com.xiaomi.miplay",
        "com.droidlogic",
        "com.mitv.tvhome",
        "com.mitv.tvhome.michannel",
        "com.mi.umifrontend"
    };

    public static PackageEntry Classify(string package, string path, bool isSystem, bool disabled)
    {
        var lower = package.ToLowerInvariant();

        if (ProtectedExact.Contains(package, StringComparer.OrdinalIgnoreCase) ||
            ProtectedContains.Any(x => lower.Contains(x)))
        {
            return New(package, path, isSystem, disabled, PackageRisk.Protected,
                "GIỮ - LÕI", "Thành phần hệ thống/TV quan trọng. MiTV3 Clean khóa thao tác dọn.");
        }

        if (!isSystem)
        {
            return New(package, path, false, disabled, PackageRisk.UserApp,
                "APP NGƯỜI DÙNG", "Ứng dụng cài thêm; không tự động coi là rác.");
        }

        if (SafeCandidateExact.Contains(package) ||
            SafeCandidateTokens.Any(x => lower.Contains(x.ToLowerInvariant())))
        {
            return New(package, path, true, disabled, PackageRisk.SafeCandidate,
                "ỨNG VIÊN DỌN",
                "Khớp danh sách MiTV/MIUI nội địa thường được debloat. MiTV3 Clean vẫn khuyến nghị Disable trước, reboot kiểm tra rồi mới gỡ user 0.");
        }

        if (ReviewExact.Contains(package))
        {
            return New(package, path, true, disabled, PackageRisk.Review,
                "CÓ THỂ DỌN - XEM KỸ",
                "Có thể không cần nếu bỏ hệ sinh thái Xiaomi, nhưng có thể ảnh hưởng launcher/casting/update/account hoặc phần cứng TV.");
        }

        if (lower.StartsWith("com.xiaomi.") || lower.StartsWith("com.mitv.") ||
            lower.StartsWith("com.duokan.") || lower.StartsWith("com.miui.") ||
            lower.StartsWith("com.mipay."))
        {
            return New(package, path, true, disabled, PackageRisk.Review,
                "XIAOMI - XEM KỸ", "Package Xiaomi/MIUI chưa đủ dữ liệu để tự động chọn dọn.");
        }

        return New(package, path, isSystem, disabled, PackageRisk.Review,
            "CHƯA PHÂN LOẠI", "Chưa có quy tắc đáng tin cậy; cần kiểm tra trước khi thay đổi.");
    }

    private static PackageEntry New(string n, string p, bool s, bool d, PackageRisk r, string g, string why)
        => new() { Name = n, ApkPath = p, IsSystem = s, IsDisabled = d, Risk = r, Group = g, Reason = why };
}
