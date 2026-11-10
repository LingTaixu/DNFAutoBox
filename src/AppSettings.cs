using System;
using System.IO;
using System.Xml.Serialization;

namespace AutoBox
{
    public sealed class AppSettings
    {
        public int VirtualKey = 0x31;
        public int Mode = (int)MacroMode.SeriaBox;
        public int ToggleHotkey = 0x77;
        public int StopHotkey = 0x78;
        public int PauseHotkey = 0x79;
        public string ProcessName = "";

        public static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoBox", "settings.xml"); }
        }
        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    using (var stream = File.OpenRead(FilePath))
                        return (AppSettings)new XmlSerializer(typeof(AppSettings)).Deserialize(stream);
            }
            catch (InvalidOperationException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return new AppSettings();
        }
        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            string temporary = FilePath + ".tmp";
            using (var stream = File.Create(temporary)) new XmlSerializer(typeof(AppSettings)).Serialize(stream, this);
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
            else File.Move(temporary, FilePath);
        }
    }
}
