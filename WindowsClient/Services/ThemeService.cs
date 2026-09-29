using System;
using System.Windows;

namespace LiveRoomAdmin.Services
{
    public static class ThemeService
    {
        public static readonly (string Key, string Name)[] Themes =
        {
            ("default", "原版"),
            ("miuix", "Miuix"),
            ("material", "Material")
        };

        public static string Current => SettingsService.Get("theme", "default");

        public static string DisplayName(string key)
        {
            foreach (var t in Themes) if (t.Key == key) return t.Name;
            return "原版";
        }

        public static void Init() => Apply(Current, false);

        public static void Apply(string theme, bool save = true)
        {
            if (string.IsNullOrEmpty(theme)) theme = "default";
            bool known = false;
            foreach (var t in Themes) if (t.Key == theme) { known = true; break; }
            if (!known) theme = "default";
            if (save) SettingsService.Set("theme", theme);
            var cap = theme.Substring(0, 1).ToUpper() + theme.Substring(1);
            var uri = new Uri("pack://application:,,,/Themes/" + cap + ".xaml");
            var next = new ResourceDictionary { Source = uri };
            var mds = Application.Current.Resources.MergedDictionaries;
            for (int i = mds.Count - 1; i >= 0; i--)
            {
                try { if (mds[i].Contains("ThemeName")) mds.RemoveAt(i); }
                catch { }
            }
            mds.Add(next);
        }
    }
}
