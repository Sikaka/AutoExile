using System.Collections;
using System.Reflection;
using System.Text.Json;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Nodes;

namespace AutoExile.WebServer
{
    /// <summary>
    /// Reflection-based settings serialization/deserialization.
    /// Walks the BotSettings hierarchy and produces JSON-ready schema + values.
    /// </summary>
    public static class SettingsApi
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        /// <summary>Serialize the entire settings tree to a list of groups with schema + current values.</summary>
        public static List<SettingsGroup> Serialize(BotSettings settings)
        {
            var groups = new List<SettingsGroup>();

            // Top-level settings go into "General" group
            var general = new SettingsGroup { Name = "General", Path = "" };
            var settingsType = typeof(BotSettings);

            foreach (var prop in settingsType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.Name == "Enable") continue; // ExileCore internal
                var value = SafeGetValue(prop, settings);
                if (value == null) continue;

                var entry = TrySerializeNode(prop, value, ToCamelCase(prop.Name));
                if (entry != null)
                {
                    general.Settings.Add(entry);
                    continue;
                }

                // Check for nested submenu class
                if (IsSettingsClass(prop.PropertyType))
                {
                    var subGroup = SerializeGroup(prop.Name, ToCamelCase(prop.Name), value);
                    if (subGroup != null && (subGroup.Settings.Count > 0 || subGroup.Subgroups.Count > 0))
                        groups.Add(subGroup);
                }
            }

            if (general.Settings.Count > 0)
                groups.Insert(0, general);

            return groups;
        }

        /// <summary>Return all settings as a flat key→entry map for the web UI.</summary>
        public static Dictionary<string, SettingEntry> SerializeFlat(BotSettings settings)
        {
            var flat = new Dictionary<string, SettingEntry>();
            var groups = Serialize(settings);
            foreach (var g in groups)
                FlattenGroup(g, flat);
            return flat;
        }

        private static void FlattenGroup(SettingsGroup group, Dictionary<string, SettingEntry> flat)
        {
            foreach (var s in group.Settings)
                flat[s.Key] = s;
            foreach (var sub in group.Subgroups)
                FlattenGroup(sub, flat);
        }

        /// <summary>Apply a single setting change by dotted path.</summary>
        public static (bool Success, string Error) Apply(BotSettings settings, string path, JsonElement value)
        {
            try
            {
                var parts = path.Split('.');
                object current = settings;

                // Navigate to the parent object
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    var prop = FindProperty(current.GetType(), parts[i]);
                    if (prop == null)
                        return (false, $"Property '{parts[i]}' not found at depth {i}");
                    current = prop.GetValue(current)!;
                }

                // Find the target property
                var targetProp = FindProperty(current.GetType(), parts[^1]);
                if (targetProp == null)
                    return (false, $"Property '{parts[^1]}' not found");

                var node = targetProp.GetValue(current);
                if (node == null)
                    return (false, $"Property '{parts[^1]}' is null");

                return SetNodeValue(node, value);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>Navigate the settings tree by dot-separated path and return the node object.</summary>
        public static object? FindNode(BotSettings settings, string path)
        {
            try
            {
                var parts = path.Split('.');
                object current = settings;
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    var prop = FindProperty(current.GetType(), parts[i]);
                    if (prop == null) return null;
                    current = prop.GetValue(current)!;
                }
                var targetProp = FindProperty(current.GetType(), parts[^1]);
                return targetProp?.GetValue(current);
            }
            catch { return null; }
        }

        // ================================================================
        // Serialization helpers
        // ================================================================

        private static SettingsGroup? SerializeGroup(string name, string path, object obj)
        {
            var group = new SettingsGroup
            {
                Name = FormatName(name),
                Path = path,
            };

            var type = obj.GetType();
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var value = SafeGetValue(prop, obj);
                if (value == null) continue;

                var entry = TrySerializeNode(prop, value, $"{path}.{ToCamelCase(prop.Name)}");
                if (entry != null)
                {
                    group.Settings.Add(entry);
                    continue;
                }

                // Nested submenu (e.g., SkillSlotConfig, TowerTypeSettings)
                if (IsSettingsClass(prop.PropertyType))
                {
                    var subPath = $"{path}.{ToCamelCase(prop.Name)}";
                    var sub = SerializeGroup(prop.Name, subPath, value);
                    if (sub != null && (sub.Settings.Count > 0 || sub.Subgroups.Count > 0))
                        group.Subgroups.Add(sub);
                }
            }

            return group;
        }

        private static SettingEntry? TrySerializeNode(PropertyInfo prop, object node, string path)
        {
            var (label, desc) = GetMenuInfo(prop);
            var nodeType = node.GetType();

            // ToggleNode
            if (node is ToggleNode toggle)
            {
                return new SettingEntry
                {
                    Key = path,
                    Label = label,
                    Description = desc,
                    Type = "toggle",
                    Value = toggle.Value,
                };
            }

            // RangeNode<int>
            if (nodeType.IsGenericType && nodeType.GetGenericTypeDefinition().Name.StartsWith("RangeNode"))
            {
                var genArg = nodeType.GetGenericArguments()[0];
                var valueProp = nodeType.GetProperty("Value");
                var minProp = nodeType.GetProperty("Min");
                var maxProp = nodeType.GetProperty("Max");

                if (valueProp != null)
                {
                    return new SettingEntry
                    {
                        Key = path,
                        Label = label,
                        Description = desc,
                        Type = genArg == typeof(float) || genArg == typeof(double) ? "range_float" : "range_int",
                        Value = valueProp.GetValue(node),
                        Min = minProp?.GetValue(node),
                        Max = maxProp?.GetValue(node),
                    };
                }
            }

            // ListNode
            if (node is ListNode listNode)
            {
                return new SettingEntry
                {
                    Key = path,
                    Label = label,
                    Description = desc,
                    Type = "list",
                    Value = listNode.Value,
                    Options = GetListNodeOptions(listNode),
                };
            }

            // TextNode
            if (node is TextNode textNode)
            {
                return new SettingEntry
                {
                    Key = path,
                    Label = label,
                    Description = desc,
                    Type = "text",
                    Value = textNode.Value ?? "",
                };
            }

            // HotkeyNode
            if (nodeType.Name.Contains("HotkeyNode"))
            {
                var valueProp = nodeType.GetProperty("Value");
                return new SettingEntry
                {
                    Key = path,
                    Label = label,
                    Description = desc,
                    Type = "hotkey",
                    Value = valueProp?.GetValue(node)?.ToString() ?? "",
                };
            }

            return null;
        }

        // ================================================================
        // Value setting
        // ================================================================

        private static (bool, string) SetNodeValue(object node, JsonElement value)
        {
            try
            {
                if (node is ToggleNode toggle)
                {
                    toggle.Value = value.GetBoolean();
                    return (true, "");
                }

                var nodeType = node.GetType();

                if (nodeType.IsGenericType && nodeType.GetGenericTypeDefinition().Name.StartsWith("RangeNode"))
                {
                    var genArg = nodeType.GetGenericArguments()[0];
                    var valueProp = nodeType.GetProperty("Value");
                    if (valueProp == null) return (false, "No Value property");

                    if (genArg == typeof(int))
                        valueProp.SetValue(node, value.GetInt32());
                    else if (genArg == typeof(float))
                        valueProp.SetValue(node, value.GetSingle());
                    else if (genArg == typeof(double))
                        valueProp.SetValue(node, value.GetDouble());
                    else
                        return (false, $"Unsupported RangeNode type: {genArg.Name}");

                    return (true, "");
                }

                if (node is ListNode listNode)
                {
                    listNode.Value = value.GetString() ?? "";
                    return (true, "");
                }

                if (node is TextNode textNode)
                {
                    textNode.Value = value.GetString() ?? "";
                    return (true, "");
                }

                // HotkeyNode — parse Keys enum from string
                if (nodeType.Name.Contains("HotkeyNode"))
                {
                    var keyStr = value.GetString() ?? "";
                    if (Enum.TryParse<System.Windows.Forms.Keys>(keyStr, true, out var parsedKey))
                    {
                        var valueProp2 = nodeType.GetProperty("Value");
                        valueProp2?.SetValue(node, parsedKey);
                        return (true, "");
                    }
                    return (false, $"Unknown key: '{keyStr}'");
                }

                return (false, $"Unsupported node type: {nodeType.Name}");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        // ================================================================
        // Reflection utilities
        // ================================================================

        private static PropertyInfo? FindProperty(Type type, string camelCaseName)
        {
            // Try exact match first
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (ToCamelCase(prop.Name) == camelCaseName)
                    return prop;
            }
            // Case-insensitive fallback
            return type.GetProperty(camelCaseName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        }

        private static (string Label, string? Description) GetMenuInfo(PropertyInfo prop)
        {
            var menuAttr = prop.GetCustomAttribute<MenuAttribute>();
            if (menuAttr != null)
            {
                // MenuAttribute stores name and tooltip — try known property names
                var attrType = menuAttr.GetType();
                var nameProp = attrType.GetProperty("MenuName")
                    ?? attrType.GetProperty("Name");
                var descProp = attrType.GetProperty("Tooltip")
                    ?? attrType.GetProperty("Description");

                var name = nameProp?.GetValue(menuAttr)?.ToString() ?? FormatName(prop.Name);
                var desc = descProp?.GetValue(menuAttr)?.ToString();
                return (name, desc);
            }

            return (FormatName(prop.Name), null);
        }

        private static bool IsSettingsClass(Type type)
        {
            if (!type.IsClass || type == typeof(string)) return false;
            // Check for Submenu attribute
            if (type.GetCustomAttribute<SubmenuAttribute>() != null) return true;
            // Also match nested classes within BotSettings
            if (type.IsNested && type.DeclaringType == typeof(BotSettings)) return true;
            return false;
        }

        private static List<string> GetListNodeOptions(ListNode node)
        {
            var type = node.GetType();

            // Try common property names
            foreach (var propName in new[] { "Values", "ListValues", "Options", "Items" })
            {
                var prop = type.GetProperty(propName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop != null)
                {
                    var val = prop.GetValue(node);
                    if (val is IEnumerable<string> strings)
                        return strings.ToList();
                    if (val is IList list && list.Count > 0)
                        return list.Cast<object>().Select(x => x?.ToString() ?? "").ToList();
                }
            }

            // Try private fields
            foreach (var fieldName in new[] { "_values", "_listValues", "values", "Values" })
            {
                var field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                {
                    var val = field.GetValue(node);
                    if (val is IEnumerable<string> strings)
                        return strings.ToList();
                    if (val is IList list && list.Count > 0)
                        return list.Cast<object>().Select(x => x?.ToString() ?? "").ToList();
                }
            }

            // Search ALL fields for List<string>
            foreach (var field in type.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
            {
                try
                {
                    var val = field.GetValue(node);
                    if (val is List<string> list && list.Count > 0)
                        return list;
                }
                catch { }
            }

            return new List<string>();
        }

        private static object? SafeGetValue(PropertyInfo prop, object obj)
        {
            try { return prop.GetValue(obj); }
            catch { return null; }
        }

        private static string ToCamelCase(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            return char.ToLowerInvariant(name[0]) + name[1..];
        }

        private static string FormatName(string propName)
        {
            // Insert spaces before capitals: "BlinkRange" → "Blink Range"
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < propName.Length; i++)
            {
                if (i > 0 && char.IsUpper(propName[i]) && !char.IsUpper(propName[i - 1]))
                    sb.Append(' ');
                sb.Append(propName[i]);
            }
            return sb.ToString();
        }
    }

    // ================================================================
    // DTOs
    // ================================================================

    public class SettingsGroup
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public List<SettingEntry> Settings { get; set; } = new();
        public List<SettingsGroup> Subgroups { get; set; } = new();
    }

    public class SettingEntry
    {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        public string? Description { get; set; }
        public string Type { get; set; } = "";
        public object? Value { get; set; }
        public object? Min { get; set; }
        public object? Max { get; set; }
        public List<string>? Options { get; set; }
        public bool ReadOnly { get; set; }
    }
}
