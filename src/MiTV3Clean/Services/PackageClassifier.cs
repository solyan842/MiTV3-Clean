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
        "com.android.webview"
    };

    private static readonly string[] ProtectedContains =
    {
        "inputmethod", "settings", "systemui", "packageinstaller",
        "bluetooth", "wifi", "network", "ethernet", "media.codec",
        "mediaprovider", "permissioncontroller", "documentsui",
        "tvinput", "hdmi", "cec", "remote", "audio", "surfaceflinger"
    };

    // Conservative list: these are merely candidates for review/disable.
    private static readonly string[] ChinaCandidateTokens =
    {
        "mitv.advert", "mitv.analytics", "mitv.stat", "mitv.shop",
        "mitv.payment", "mitv.mishop", "mitv.user", "mitv.account",
        "duokan", "xiaomi.market", "xiaomi.game", "xiaomi.mipicks",
        "miui.analytics", "miui.systemAdSolution", "miui.msa",
        "xiaomi.jr", "xiaomi.vip", "xiaomi.smarthome"
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

        if (ChinaCandidateTokens.Any(x => lower.Contains(x.ToLowerInvariant())))
        {
            return New(package, path, true, disabled, PackageRisk.SafeCandidate,
                "ỨNG VIÊN DỌN", "Khớp nhóm quảng cáo/analytics/market/dịch vụ nội địa. Nên Disable thử trước.");
        }

        if (lower.StartsWith("com.xiaomi.") || lower.StartsWith("com.mitv.") ||
            lower.StartsWith("com.duokan.") || lower.StartsWith("com.miui."))
        {
            return New(package, path, true, disabled, PackageRisk.Review,
                "XIAOMI - XEM KỸ", "Package Xiaomi/MIUI chưa đủ dữ liệu để kết luận an toàn.");
        }

        return New(package, path, isSystem, disabled, PackageRisk.Review,
            "CHƯA PHÂN LOẠI", "Chưa có quy tắc đáng tin cậy; cần kiểm tra trước khi thay đổi.");
    }

    private static PackageEntry New(string n, string p, bool s, bool d, PackageRisk r, string g, string why)
        => new() { Name = n, ApkPath = p, IsSystem = s, IsDisabled = d, Risk = r, Group = g, Reason = why };
}
