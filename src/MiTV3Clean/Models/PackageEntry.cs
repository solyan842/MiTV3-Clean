using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MiTV3Clean.Models;

public enum PackageRisk
{
    Protected = 0,
    SafeCandidate = 1,
    Review = 2,
    UserApp = 3
}

public sealed class PackageEntry : INotifyPropertyChanged
{
    private bool _selected;

    public bool Selected
    {
        get => _selected;
        set { _selected = value; OnPropertyChanged(); }
    }

    public string Name { get; set; } = "";
    public string ApkPath { get; set; } = "";
    public bool IsSystem { get; set; }
    public bool IsDisabled { get; set; }
    public PackageRisk Risk { get; set; }
    public string Group { get; set; } = "";
    public string Reason { get; set; } = "";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
