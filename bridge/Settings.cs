using System;
using System.IO;

namespace WiiRemoteBridge
{
    // Small settings remembered next to the app.
    internal static class Settings
    {
        public static volatile bool FlipLeftRight;
        public static volatile bool FlipUpDown;

        static string FilePath => Path.Combine(AppContext.BaseDirectory, "WiiRemoteBridge-settings.txt");

        public static void Load()
        {
            try
            {
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var parts = line.Split('=');
                    if (parts.Length != 2) continue;
                    bool on = parts[1].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                    if (parts[0].Trim() == "FlipLeftRight") FlipLeftRight = on;
                    if (parts[0].Trim() == "FlipUpDown") FlipUpDown = on;
                }
            }
            catch { }
        }

        public static void Save()
        {
            try { File.WriteAllText(FilePath, "FlipLeftRight=" + FlipLeftRight + Environment.NewLine + "FlipUpDown=" + FlipUpDown + Environment.NewLine); }
            catch { }
        }
    }
}
