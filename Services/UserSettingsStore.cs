using System;
using System.IO;
using System.Text.Json;

namespace ReviewG33k.Services;

public sealed class UserSettingsStore
{
    private const string ApplicationFolderName = "ReviewG33k";
    private const string SettingsFileName = "user-settings.json";
    private readonly string m_settingsPath;

    public UserSettingsStore()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        m_settingsPath = Path.Combine(localApplicationData, ApplicationFolderName, SettingsFileName);
    }

    public UserSettings Load()
    {
        try
        {
            if (!File.Exists(m_settingsPath))
                return new UserSettings();

            var json = File.ReadAllText(m_settingsPath);
            var settings = JsonSerializer.Deserialize<UserSettings>(json);
            return settings ?? new UserSettings();
        }
        catch
        {
            return new UserSettings();
        }
    }

    public void Save(UserSettings settings)
    {
        if (settings == null)
            return;

        var folder = Path.GetDirectoryName(m_settingsPath);
        if (!string.IsNullOrWhiteSpace(folder))
            Directory.CreateDirectory(folder);

        var json = JsonSerializer.Serialize(
            settings,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });

        File.WriteAllText(m_settingsPath, json);
    }
}

public sealed class UserSettings
{
    public string RepositoryRootPath { get; set; }
}
