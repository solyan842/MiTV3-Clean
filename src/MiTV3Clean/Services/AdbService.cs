using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace MiTV3Clean.Services;

public sealed class AdbService
{
    private readonly string _toolsDir;
    private string? _serial;

    public AdbService()
    {
        _toolsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiTV3Clean", "platform-tools");
    }

    public string AdbPath => Path.Combine(_toolsDir, "adb.exe");
    public string? Serial => _serial;

    public async Task EnsureAdbAsync(IProgress<string>? progress = null)
    {
        if (File.Exists(AdbPath)) return;

        progress?.Report("Đang tải Android Platform Tools chính thức từ Google...");
        Directory.CreateDirectory(Path.GetDirectoryName(_toolsDir)!);
        var zipPath = Path.Combine(Path.GetTempPath(), "platform-tools-latest-windows.zip");

        using var http = new HttpClient();
        var bytes = await http.GetByteArrayAsync("https://dl.google.com/android/repository/platform-tools-latest-windows.zip");
        await File.WriteAllBytesAsync(zipPath, bytes);

        var temp = Path.Combine(Path.GetTempPath(), "MiTV3Clean-platform-tools");
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        ZipFile.ExtractToDirectory(zipPath, temp);

        if (Directory.Exists(_toolsDir)) Directory.Delete(_toolsDir, true);
        Directory.Move(Path.Combine(temp, "platform-tools"), _toolsDir);
        progress?.Report("ADB đã sẵn sàng.");
    }

    public async Task<string> ConnectAsync(string host, IProgress<string>? progress = null)
    {
        await EnsureAdbAsync(progress);
        if (!host.Contains(':')) host += ":5555";
        await RunRawAsync("start-server");
        var output = await RunRawAsync($"connect {Quote(host)}");
        _serial = host;
        return output;
    }

    public async Task<string> RunShellAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(_serial))
            throw new InvalidOperationException("Chưa kết nối TV.");
        return await RunRawAsync($"-s {Quote(_serial)} shell {command}");
    }

    public async Task<string> RunDeviceAsync(string args)
    {
        if (string.IsNullOrWhiteSpace(_serial))
            throw new InvalidOperationException("Chưa kết nối TV.");
        return await RunRawAsync($"-s {Quote(_serial)} {args}");
    }

    public async Task<string> GetDeviceSummaryAsync()
    {
        var model = (await RunShellAsync("getprop ro.product.model")).Trim();
        var android = (await RunShellAsync("getprop ro.build.version.release")).Trim();
        var sdk = (await RunShellAsync("getprop ro.build.version.sdk")).Trim();
        var build = (await RunShellAsync("getprop ro.build.display.id")).Trim();
        var abi = (await RunShellAsync("getprop ro.product.cpu.abi")).Trim();
        return $"{model} · Android {android} (SDK {sdk}) · {abi}\nBuild: {build}";
    }

    public async Task<string> RunRawAsync(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = AdbPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Không chạy được adb.exe");
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        var all = (stdout + (string.IsNullOrWhiteSpace(stderr) ? "" : "\n" + stderr)).Trim();
        if (p.ExitCode != 0) throw new InvalidOperationException(all);
        return all;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
