using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoExile.WebServer
{
    public class MacroAction
    {
        public string Key { get; set; } = ""; // key name, e.g. "F", "E"
        public int HoldMs { get; set; }
    }

    public class MacroDefinition
    {
        public string Name { get; set; } = "";
        public List<MacroAction> Sequence { get; set; } = new List<MacroAction>();
        public int BetweenDelayMs { get; set; } = 50;
        // Delay between macro iterations when repeating (ms)
        public int RepeatDelayMs { get; set; } = 1000;
        // Optional hotkey to toggle this macro (e.g. "F10", "G", "F1")
        public string Hotkey { get; set; } = "";
    }

    public static class MacroStore
    {
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        public static List<MacroDefinition> LoadMacros(ProfileManager pm)
        {
            try
            {
                var prof = pm.ActiveProfileName ?? "Default";
                var dir = pm.ProfilesDirectory;
                var path = Path.Combine(dir, prof + "-macros.json");
                if (!File.Exists(path)) return new List<MacroDefinition>();
                var json = File.ReadAllText(path);
                var macros = JsonSerializer.Deserialize<List<MacroDefinition>>(json, JsonOpts);
                return macros ?? new List<MacroDefinition>();
            }
            catch
            {
                return new List<MacroDefinition>();
            }
        }

        public static bool SaveMacros(ProfileManager pm, List<MacroDefinition> macros)
        {
            try
            {
                var prof = pm.ActiveProfileName ?? "Default";
                var dir = pm.ProfilesDirectory;
                var path = Path.Combine(dir, prof + "-macros.json");
                File.WriteAllText(path, JsonSerializer.Serialize(macros, JsonOpts));
                return true;
            }
            catch { return false; }
        }
    }
}
