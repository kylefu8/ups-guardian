using System;
using System.IO;
using System.Xml.Serialization;

namespace UpsGuardian
{
    public sealed class UiPreferences
    {
        public string Language = "auto";
        public static UiPreferences Load(string path)
        {
            if (!File.Exists(path)) return new UiPreferences();
            try { using (var stream = File.OpenRead(path)) return (UiPreferences)new XmlSerializer(typeof(UiPreferences)).Deserialize(stream); }
            catch { return new UiPreferences(); }
        }
        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp";
            using (var stream = File.Create(temporary)) new XmlSerializer(typeof(UiPreferences)).Serialize(stream, this);
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak"); else File.Move(temporary, path);
        }
    }
}
