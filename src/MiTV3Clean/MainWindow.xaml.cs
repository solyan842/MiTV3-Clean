using MiTV3Clean.Models;
using MiTV3Clean.Services;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;

namespace MiTV3Clean;

public partial class MainWindow : Window
{
    public ObservableCollection<PackageEntry> Packages { get; } = new();

    private readonly AdbService _adb = new();
    private readonly CouchyService _couchy = new();
    private readonly Progress<string> _progress;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _progress = new Progress<string>(Log);
    }

    private void Log(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        LogBox.ScrollToEnd();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        await GuardAsync(async () =>
        {
            Log(await _adb.ConnectAsync(IpBox.Text.Trim(), _progress));
            DeviceText.Text = await _adb.GetDeviceSummaryAsync();
            Log("Kết nối TV thành công.");
        });
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        await GuardAsync(async () =>
        {
            Log("Đang đọc package thật trên TV...");
            var all = await _adb.RunShellAsync("pm list packages -f");
            var system = new HashSet<string>(ParseNames(await _adb.RunShellAsync("pm list packages -s")));
            var disabled = new HashSet<string>(ParseNames(await _adb.RunShellAsync("pm list packages -d")));

            Packages.Clear();
            foreach (var line in all.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var clean = line.Trim();
                if (!clean.StartsWith("package:")) continue;
                clean = clean[8..];
                var eq = clean.LastIndexOf('=');
                if (eq <= 0) continue;
                var path = clean[..eq];
                var name = clean[(eq + 1)..];
                Packages.Add(PackageClassifier.Classify(name, path, system.Contains(name), disabled.Contains(name)));
            }

            foreach (var item in Packages.OrderBy(x => x.Risk).ThenBy(x => x.Name).ToList())
            {
                Packages.Remove(item);
                Packages.Add(item);
            }

            await SaveSnapshotAsync();
            Log($"Đã quét {Packages.Count} package và lưu snapshot.");
        });
    }

    private void SelectCandidates_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in Packages)
            p.Selected = p.Risk == PackageRisk.SafeCandidate;
        Log("Đã chọn nhóm ỨNG VIÊN DỌN; vẫn cần xem tên package trước khi thao tác.");
    }

    private async void Disable_Click(object sender, RoutedEventArgs e)
    {
        await ApplySelectedAsync(async p =>
        {
            if (p.Risk == PackageRisk.Protected) return "BỎ QUA package lõi";
            var r = await _adb.RunShellAsync($"pm disable-user --user 0 {p.Name}");
            p.IsDisabled = true;
            return r;
        }, "Disable");
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        await ApplySelectedAsync(async p =>
        {
            string install = "";
            try { install = await _adb.RunShellAsync($"cmd package install-existing --user 0 {p.Name}"); }
            catch
            {
                try { install = await _adb.RunShellAsync($"pm install-existing --user 0 {p.Name}"); }
                catch { }
            }

            var enable = await _adb.RunShellAsync($"pm enable --user 0 {p.Name}");
            p.IsDisabled = false;
            return (install + "\n" + enable).Trim();
        }, "Restore");
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var selected = Packages.Where(x => x.Selected && x.Risk != PackageRisk.Protected).ToList();
        if (selected.Count == 0) { Log("Không có package hợp lệ được chọn."); return; }

        var answer = MessageBox.Show(
            $"Gỡ {selected.Count} package khỏi user 0?\n\nKhuyến nghị chỉ làm sau khi đã Disable + reboot và TV hoạt động bình thường.",
            "MiTV3 Clean", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        await SaveSnapshotAsync();
        await ApplySelectedAsync(
            p => _adb.RunShellAsync($"pm uninstall -k --user 0 {p.Name}"),
            "Uninstall user 0");
    }

    private async void InstallCouchy_Click(object sender, RoutedEventArgs e)
        => await GuardAsync(async () => Log(await _couchy.InstallAsync(_adb, _progress)));

    private async void SetCouchyHome_Click(object sender, RoutedEventArgs e)
        => await GuardAsync(async () => Log(await _couchy.SetHomeAsync(_adb)));

    private async void DiagnoseHome_Click(object sender, RoutedEventArgs e)
        => await GuardAsync(async () => Log(await _couchy.DiagnoseHomeAsync(_adb)));

    private async void RestoreXiaomiHome_Click(object sender, RoutedEventArgs e)
        => await GuardAsync(async () => Log(await _couchy.RestoreXiaomiHomeAsync(_adb)));

    private async Task ApplySelectedAsync(Func<PackageEntry, Task<string>> action, string title)
    {
        await GuardAsync(async () =>
        {
            await SaveSnapshotAsync();
            foreach (var p in Packages.Where(x => x.Selected).ToList())
            {
                if (p.Risk == PackageRisk.Protected)
                {
                    Log($"{title}: {p.Name} -> BỊ KHÓA");
                    continue;
                }

                try { Log($"{title}: {p.Name} -> {await action(p)}"); }
                catch (Exception ex) { Log($"{title}: {p.Name} -> LỖI: {ex.Message}"); }
            }
        });
    }

    private async Task SaveSnapshotAsync()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiTV3Clean", "snapshots");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"snapshot-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        var data = Packages.Select(x => new
        {
            x.Name, x.ApkPath, x.IsSystem, x.IsDisabled, x.Group, x.Reason
        });
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static IEnumerable<string> ParseNames(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                 .Select(x => x.Trim())
                 .Where(x => x.StartsWith("package:"))
                 .Select(x => x[8..].Split('=')[^1]);

    private async Task GuardAsync(Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex)
        {
            Log("LỖI: " + ex.Message);
            MessageBox.Show(ex.Message, "MiTV3 Clean", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
