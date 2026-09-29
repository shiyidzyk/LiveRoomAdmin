using System;
using System.Windows;

namespace LiveRoomAdmin.Services
{
    public static class LocService
    {
        public static readonly (string Key, string Name)[] Languages =
        {
            ("zh", "简体中文"),
            ("en", "English")
        };

        public static string Current
        {
            get
            {
                var saved = SettingsService.Get("lang", "");
                if (saved == "en" || saved == "zh") return saved;
                var installer = ReadInstallerLang();
                return installer == "en" ? "en" : "zh";
            }
        }

        public static bool IsEn => Current == "en";

        private static string ReadInstallerLang()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\LiveRoomAdmin");
                return key?.GetValue("Language", "") as string ?? "";
            }
            catch { return ""; }
        }

        public static string T(string zh, string en) => IsEn ? en : zh;

        public static void Init() => Apply(Current, false);

        public static void Apply(string lang, bool save = true)
        {
            if (lang != "en") lang = "zh";
            if (save) SettingsService.Set("lang", lang);
            var uri = new Uri("pack://application:,,,/Strings/" + (lang == "en" ? "en" : "zh") + ".xaml");
            var next = new ResourceDictionary { Source = uri };
            var mds = Application.Current.Resources.MergedDictionaries;
            for (int i = mds.Count - 1; i >= 0; i--)
            {
                try { if (mds[i].Contains("S_MARKER")) mds.RemoveAt(i); }
                catch { }
            }
            mds.Add(next);
        }
    }
}
