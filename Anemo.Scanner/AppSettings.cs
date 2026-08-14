using System;
using System.IO;
using System.Text.Json;

namespace Anemo.Scanner
{
    public class AppSettings
    {
        public int DefaultPortFrom { get; set; } = 1;
        public int DefaultPortTo { get; set; } = 1024;

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Anemo.Scanner", "settings.json");

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
