using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Silverbud.Core;

public sealed class AppSettings
{
    public static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Silverbud");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public string? LastDeviceId { get; set; }
    public bool AutoConnect { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool LowBatteryAlert { get; set; } = true;
    public int[] CustomEq { get; set; } = new int[9];

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                if (settings.CustomEq is not { Length: 9 })
                    settings.CustomEq = new int[9];
                return settings;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Settings unreadable, using defaults: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Write($"Settings not saved: {ex.Message}");
        }
    }
}

/// <summary>Per-user "start with Windows" entry (HKCU Run key), launching straight to the tray.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Silverbud";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
                key.SetValue(Name, $"\"{Environment.ProcessPath}\" --tray");
            else
                key.DeleteValue(Name, false);
        }
    }
}

public static class Log
{
    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(AppSettings.Folder, "silverbud.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.Folder);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1_000_000)
                    info.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never break the app
        }
    }
}
