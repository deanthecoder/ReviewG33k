using DTC.Core.Settings;

namespace ReviewG33k.ViewModels;

public sealed class Settings : UserSettingsBase
{
    public static Settings Instance { get; } = new();

    protected override void ApplyDefaults()
    {
        AutoOpenSolutionFile = true;
        RepositoryRootPath = string.Empty;
    }

    public string RepositoryRootPath
    {
        get => Get<string>();
        set => Set(value);
    }

    public bool AutoOpenSolutionFile
    {
        get => Get<bool>();
        set => Set(value);
    }
}
