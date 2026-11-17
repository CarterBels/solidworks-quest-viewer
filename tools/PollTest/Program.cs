using System;
using System.Diagnostics;
using System.Threading;
using SolidWorksQuestSender;

namespace SolidWorksQuestSender { public sealed class ExportResult { public string Name, Key, PartInfo; public byte[] Glb; public int Hidden; } }

static class Program
{
    static void Main()
    {
        var s = Settings.Load();
        Console.WriteLine($"viewer {s.Viewer}, code {s.PairCode}");
        var client = new ViewerClient(s);
        for (int i = 0; i < 3; i++)
        {
            var t = Stopwatch.StartNew();
            try
            {
                var c = client.CommandsAsync(CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine($"poll {i}: {t.Elapsed.TotalSeconds:0.0}s -> [{string.Join(",", c)}]");
            }
            catch (Exception e) { Console.WriteLine($"poll {i}: {t.Elapsed.TotalSeconds:0.0}s -> {e.GetType().Name}: {e.Message}"); }
        }
    }
}
