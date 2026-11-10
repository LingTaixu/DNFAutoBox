using System;
using System.IO;
using System.Xml.Serialization;

namespace AutoTools
{
    public sealed class AppSettings
    {
        public int VirtualKey = 0x31;
        public int Mode = (int)MacroMode.SeriaBox;
        public int ToggleHotkey = 0x77;
        public int StopHotkey = 0x78;
        public int PauseHotkey = 0x79;
        public string ProcessName = "";
        public bool MousePointConfigured;
        public int MouseClientX;
        public int MouseClientY;
        public int MouseCapturedWidth;
        public int MouseCapturedHeight;
        public int MouseExpectedWidth = 1280;
        public int MouseExpectedHeight = 720;
        public int MouseCaptureHotkey = 0x76;
        public int MouseLoopGapMilliseconds = MouseOptions.DefaultLoopGapMilliseconds;
        public string MousePointProcessName = "";
        public bool ExtraEnterForChat;
        public bool LoopForever = true;
        public int LoopCount = 1;

        public static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoTools", "settings.xml"); }
        }
        public static AppSettings Load()
        {
            try
            {
                string source = FilePath;
                if (!File.Exists(source)) source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoBox", "settings.xml");
                if (File.Exists(source))
                    using (var stream = File.OpenRead(source))
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
