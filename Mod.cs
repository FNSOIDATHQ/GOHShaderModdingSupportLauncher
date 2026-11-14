using System.ComponentModel;

namespace GOHShaderModdingSupportLauncher;

public sealed class Mod : INotifyPropertyChanged
{
    private string originalName;
    private string? alias;
    public string name
    {
        get => originalName;
        set
        {
            if (originalName == value) return;
            originalName = value;
            // A different original name must be looked up again, without carrying an old alias.
            alias = null;
            PropertyChanged?.Invoke(this, new(nameof(name)));
            NotifyDisplay();
        }
    }
    public string DisplayName => alias ?? name;
    public bool HasAlias => alias is not null;
    public string type { get; set; }
    public bool hasShader { get; set; }
    public string path;
    public string folderName;
    public bool hasLoad;

    public Mod(string n, string t, string p, string fn, bool hS)
    {
        originalName = n;
        type = t;
        path = p;
        folderName = fn;
        hasShader = hS;
    }

    internal void SetAlias(string? value)
    {
        if (alias == value) return;
        alias = value;
        NotifyDisplay();
    }

    private void NotifyDisplay()
    {
        PropertyChanged?.Invoke(this, new(nameof(DisplayName)));
        PropertyChanged?.Invoke(this, new(nameof(HasAlias)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
