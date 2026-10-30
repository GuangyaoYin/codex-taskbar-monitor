using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace CodexTaskbarMonitor
{
    public sealed class AppSettings
    {
        public string SelectedGpuId { get; set; }
        public string CodexPath { get; set; }
        public int? ManualX { get; set; }
        public int? ManualY { get; set; }

        public static string DirectoryPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexTaskbarMonitor"); }
        }
        public static string FilePath { get { return Path.Combine(DirectoryPath, "settings.json"); } }
        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath)) return new JavaScriptSerializer().Deserialize<AppSettings>(File.ReadAllText(FilePath, Encoding.UTF8)) ?? new AppSettings();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            return new AppSettings();
        }
        public void Save()
        {
            Directory.CreateDirectory(DirectoryPath);
            string temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(this), new UTF8Encoding(false));
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
            else File.Move(temporary, FilePath);
        }
        public static bool StartupEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
                    return key != null && key.GetValue("CodexTaskbarMonitor") != null;
            }
            catch { return false; }
        }
        public static void SetStartup(bool enabled, string executable)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (enabled) key.SetValue("CodexTaskbarMonitor", "\"" + executable + "\"");
                else key.DeleteValue("CodexTaskbarMonitor", false);
            }
        }
        public static bool DarkTheme()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return key != null && Convert.ToInt32(key.GetValue("SystemUsesLightTheme", 1)) == 0;
            }
            catch { return false; }
        }
    }
}
