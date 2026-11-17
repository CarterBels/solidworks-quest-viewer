using System;
using System.IO;
using System.Text;

namespace SolidWorksQuestSender;

public sealed class Settings
{
    public string Viewer = "127.0.0.1:8723";
    public string PairCode = "";
    public bool ListenForVoice = true;
    /// Triangles drawn in the headset, counting every instance; bigger models are simplified to fit.
    public int MaxTriangles = 1500000;

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SolidWorksQuestSender");
    static string FilePath => Path.Combine(Folder, "settings.json");

    public static Settings Load()
    {
        var s = new Settings();
        try
        {
            if (!File.Exists(FilePath)) return s;
            var root = Json.Parse(File.ReadAllText(FilePath));
            if (Json.Get(root, "viewer") is string v) s.Viewer = v;
            if (Json.Get(root, "pairCode") is string c) s.PairCode = c;
            if (Json.Get(root, "listenForVoice") is bool b) s.ListenForVoice = b;
            if (Json.Get(root, "maxTriangles") is double m && m >= 10000) s.MaxTriangles = (int)Math.Min(m, 50000000);
        }
        catch (Exception e) { Log.Write("Settings unreadable: " + e.Message); }
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath,
            "{\n  \"viewer\": " + Json.Str(Viewer) + ",\n  \"pairCode\": " + Json.Str(PairCode) +
            ",\n  \"listenForVoice\": " + (ListenForVoice ? "true" : "false") +
            ",\n  \"maxTriangles\": " + MaxTriangles + "\n}\n", Encoding.UTF8);
    }
}

public static class Log
{
    static readonly object gate = new object();
    public static string FilePath => Path.Combine(Settings.Folder, "log.txt");

    public static void Write(string line)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Settings.Folder);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1024 * 1024) fi.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
            }
        }
        catch (Exception) { }
    }
}
