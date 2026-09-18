using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Anemo.Widget
{
    public class AppSettings
    {
        public bool StartMinimized { get; set; }

        // NetworkInterface.Id (a stable per-adapter GUID) rather than Name, so a rename
        // doesn't silently un-exclude an adapter the user picked, e.g. Tailscale.
        public List<string> ExcludedAdapterIds { get; set; } = new();

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Anemo.Widget", "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch
            {
                // Corrupt or unreadable settings file - fall back to defaults rather than crash.
            }
            return new AppSettings();
        }

        public void Save()
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
    }
}
