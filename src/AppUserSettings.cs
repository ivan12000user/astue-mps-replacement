using System;
using System.IO;
using Newtonsoft.Json;

namespace AstueMpsReplacement
{
    public sealed class AppUserSettings
    {
        public string LastProjectPath { get; set; }

        private static string SettingsDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AstueMpsReplacement");

        private static string SettingsPath => Path.Combine(SettingsDirectory, "user-settings.json");

        public void Load(Action<string> warn = null)
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonConvert.DeserializeObject<AppUserSettings>(json);
                if (loaded != null)
                    LastProjectPath = loaded.LastProjectPath;
            }
            catch (Exception ex)
            {
                warn?.Invoke("Не удалось прочитать пользовательские настройки: " + ex.Message);
            }
        }

        public void Save(Action<string> warn = null)
        {
            try
            {
                Directory.CreateDirectory(SettingsDirectory);
                File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch (Exception ex)
            {
                warn?.Invoke("Не удалось сохранить пользовательские настройки: " + ex.Message);
            }
        }
    }
}
