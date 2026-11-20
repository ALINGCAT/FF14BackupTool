using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace FF14BackupTool.Core
{
    public static class ThemeManager
    {
        public const string SystemTheme = "system";
        public const string LightTheme = "light";
        public const string DarkTheme = "dark";

        private static bool _watchingSystemTheme;
        private static string _activePreference = SystemTheme;

        public static event EventHandler ThemeChanged;

        public static string ActivePreference { get { return _activePreference; } }

        public static void ApplySavedTheme()
        {
            ApplyTheme(SettingsStore.Current.Settings.Theme, false);
            if (_watchingSystemTheme) return;
            _watchingSystemTheme = true;
            SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        }

        public static void SetThemePreference(string preference)
        {
            string normalized = Normalize(preference);
            SettingsStore.Current.Settings.Theme = normalized;
            SettingsStore.Current.Save();
            ApplyTheme(normalized, true);
        }

        public static int ToIndex(string preference)
        {
            switch (Normalize(preference))
            {
                case LightTheme: return 1;
                case DarkTheme: return 2;
                default: return 0;
            }
        }

        public static string FromIndex(int index)
        {
            switch (index)
            {
                case 1: return LightTheme;
                case 2: return DarkTheme;
                default: return SystemTheme;
            }
        }

        public static string Normalize(string preference)
        {
            if (string.Equals(preference, LightTheme, StringComparison.OrdinalIgnoreCase)) return LightTheme;
            if (string.Equals(preference, DarkTheme, StringComparison.OrdinalIgnoreCase)) return DarkTheme;
            return SystemTheme;
        }

        private static void ApplyTheme(string preference, bool notify)
        {
            _activePreference = Normalize(preference);
            bool dark = string.Equals(_activePreference, DarkTheme, StringComparison.OrdinalIgnoreCase)
                || (string.Equals(_activePreference, SystemTheme, StringComparison.OrdinalIgnoreCase) && !IsSystemLight());
            var colors = dark ? DarkColors() : LightColors();
            foreach (var pair in colors)
            {
                var brush = Application.Current.Resources[pair.Key] as SolidColorBrush;
                if (brush == null) continue;
                var replacement = brush.IsFrozen ? brush.Clone() : brush;
                replacement.Color = (Color)pair.Value;
                Application.Current.Resources[pair.Key] = replacement;
            }
            if (notify)
            {
                var handler = ThemeChanged;
                if (handler != null) handler(null, EventArgs.Empty);
            }
        }

        private static bool IsSystemLight()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object value = key == null ? null : key.GetValue("AppsUseLightTheme");
                    return value == null || Convert.ToInt32(value) != 0;
                }
            }
            catch { return true; }
        }

        private static void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (!string.Equals(_activePreference, SystemTheme, StringComparison.OrdinalIgnoreCase)) return;
            if (Application.Current == null || Application.Current.Dispatcher == null) return;
            Application.Current.Dispatcher.BeginInvoke(new Action(() => ApplyTheme(SystemTheme, true)));
        }

        private static Dictionary<string, object> LightColors()
        {
            return new Dictionary<string, object>
            {
                { "AppBackgroundBrush", ColorFrom("#F7F8FC") }, { "HeaderBackgroundBrush", ColorFrom("#FFFFFF") },
                { "CardBackgroundBrush", ColorFrom("#FFFFFF") }, { "ListBackgroundBrush", ColorFrom("#FBFCFF") },
                { "PrimaryTextBrush", ColorFrom("#172033") }, { "SecondaryTextBrush", ColorFrom("#5E687A") },
                { "MutedTextBrush", ColorFrom("#7B8495") }, { "AccentBrush", ColorFrom("#3658D4") },
                { "AccentHoverBrush", ColorFrom("#2946B5") }, { "AccentSoftBrush", ColorFrom("#EEF2FF") },
                { "BorderBrush", ColorFrom("#E5E9F1") }, { "ListSeparatorBrush", ColorFrom("#EEF1F6") },
                { "InputBackgroundBrush", ColorFrom("#FCFDFF") }, { "ButtonSecondaryBackgroundBrush", ColorFrom("#F1F4FA") },
                { "ButtonSecondaryBorderBrush", ColorFrom("#E1E6EF") }, { "CheckBoxBorderBrush", ColorFrom("#C7D0DC") },
                { "DisabledButtonBackgroundBrush", ColorFrom("#EEF1F6") }, { "DisabledButtonForegroundBrush", ColorFrom("#8A94A6") },
                { "ProgressTrackBrush", ColorFrom("#E8EDF5") }, { "TabNavBackgroundBrush", ColorFrom("#F0F3FA") },
                { "TabSelectedBackgroundBrush", ColorFrom("#FFFFFF") }, { "TabHoverBackgroundBrush", ColorFrom("#E8EDFC") },
                { "WarningBrush", ColorFrom("#A45A08") }, { "WarningSoftBrush", ColorFrom("#FFF8EA") },
                { "InfoBorderBrush", ColorFrom("#C9DBFF") }, { "WarningBorderBrush", ColorFrom("#F5D28A") }
            };
        }

        private static Dictionary<string, object> DarkColors()
        {
            return new Dictionary<string, object>
            {
                { "AppBackgroundBrush", ColorFrom("#151923") }, { "HeaderBackgroundBrush", ColorFrom("#1B2030") },
                { "CardBackgroundBrush", ColorFrom("#202637") }, { "ListBackgroundBrush", ColorFrom("#1A2030") },
                { "PrimaryTextBrush", ColorFrom("#F4F6FB") }, { "SecondaryTextBrush", ColorFrom("#C2CADB") },
                { "MutedTextBrush", ColorFrom("#98A4BA") }, { "AccentBrush", ColorFrom("#8298FF") },
                { "AccentHoverBrush", ColorFrom("#A2B2FF") }, { "AccentSoftBrush", ColorFrom("#252E50") },
                { "BorderBrush", ColorFrom("#353D52") }, { "ListSeparatorBrush", ColorFrom("#30384B") },
                { "InputBackgroundBrush", ColorFrom("#171C29") }, { "ButtonSecondaryBackgroundBrush", ColorFrom("#30384B") },
                { "ButtonSecondaryBorderBrush", ColorFrom("#48536C") }, { "CheckBoxBorderBrush", ColorFrom("#68748E") },
                { "DisabledButtonBackgroundBrush", ColorFrom("#2A3040") }, { "DisabledButtonForegroundBrush", ColorFrom("#AAB4C8") },
                { "ProgressTrackBrush", ColorFrom("#30384B") }, { "TabNavBackgroundBrush", ColorFrom("#1C2231") },
                { "TabSelectedBackgroundBrush", ColorFrom("#303A62") }, { "TabHoverBackgroundBrush", ColorFrom("#27304A") },
                { "WarningBrush", ColorFrom("#F5C26B") }, { "WarningSoftBrush", ColorFrom("#3D3020") },
                { "InfoBorderBrush", ColorFrom("#465B99") }, { "WarningBorderBrush", ColorFrom("#80612C") }
            };
        }

        private static Color ColorFrom(string value)
        {
            return (Color)ColorConverter.ConvertFromString(value);
        }
    }
}
