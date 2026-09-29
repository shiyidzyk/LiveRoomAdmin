using System;
using System.Collections.Generic;
using System.IO;

namespace LiveRoomAdmin.Services
{
    public static class SettingsService
    {
        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiveRoomAdmin");
        private static readonly string FilePath = Path.Combine(Dir, "settings.json");
        private static Dictionary<string, string> _cache;

        private static Dictionary<string, string> Load()
        {
            if (_cache != null) return _cache;
            _cache = new Dictionary<string, string>();
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var doc = System.Text.Json.JsonDocument.Parse(json);
                    foreach (var p in doc.RootElement.EnumerateObject())
                        _cache[p.Name] = p.Value.GetString();
                }
            }
            catch { }
            return _cache;
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var json = System.Text.Json.JsonSerializer.Serialize(_cache);
                File.WriteAllText(FilePath, json);
            }
            catch { }
        }

        public static string Get(string key, string def)
        {
            var d = Load();
            return d.TryGetValue(key, out var v) ? v : def;
        }

        public static void Set(string key, string value)
        {
            var d = Load();
            d[key] = value;
            Save();
        }
    }
}
