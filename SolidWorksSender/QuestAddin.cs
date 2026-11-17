using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;

namespace SolidWorksQuestSender;

/// SolidWorks add-in: a "Quest" tab (parts and assemblies) with Send to Quest / Send again / Settings, a status icon
/// in the notification area, and a background long poll so "Marvin, send this to the Quest" works from the headset.
[ComVisible(true)]
[Guid("6C1B7A52-3F0E-4E4B-9D0A-8723A11CE5E1")]
[ProgId("SolidWorksQuestSender.Addin")]
public class QuestAddin : ISwAddin
{
    const int GroupId = 8723;
    const string Title = "Quest Viewer";
    static readonly int[] DocTypes = { (int)swDocumentTypes_e.swDocPART, (int)swDocumentTypes_e.swDocASSEMBLY };

    ISldWorks sw;
    int cookie;
    CommandManager cmdMgr;
    Control ui;                 // created on SolidWorks' main thread; BeginInvoke gets back there
    NotifyIcon icon;
    Settings settings;
    CancellationTokenSource pollCts;
    int busy;

    // ---- ISwAddin ----

    public bool ConnectToSW(object ThisSW, int Cookie)
    {
        try
        {
            sw = (ISldWorks)ThisSW;
            cookie = Cookie;
            sw.SetAddinCallbackInfo2(0, this, cookie);
            settings = Settings.Load();

            ui = new Control();
            _ = ui.Handle;

            icon = new NotifyIcon { Icon = Icons.Tray(Icons.Idle), Text = "SolidWorks → Quest", Visible = true };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Send to Quest", null, (s, e) => OnSend());
            menu.Items.Add("Send again (even if unchanged)", null, (s, e) => OnSendAgain());
            menu.Items.Add("Settings…", null, (s, e) => OnSettings());
            menu.Items.Add("Open log", null, (s, e) => OpenLog());
            icon.ContextMenuStrip = menu;

            AddCommands();
            RestartPolling();
            WatchSettingsFile();
            Log.Write($"Add-in loaded in SolidWorks {sw.RevisionNumber()}");
            if (string.IsNullOrWhiteSpace(settings.PairCode))
                Balloon("Open Quest Viewer → Settings and enter the address and pairing code shown on the headset's keypad.", ToolTipIcon.Info);
            return true;
        }
        catch (Exception e)
        {
            Log.Write("ConnectToSW failed: " + e);
            return false;
        }
    }

    public bool DisconnectFromSW()
    {
        try
        {
            pollCts?.Cancel();
            settingsWatcher?.Dispose();
            RemoveCommands();
            if (icon != null) { icon.Visible = false; icon.Dispose(); icon = null; }
            ui?.Dispose();
        }
        catch (Exception e) { Log.Write("DisconnectFromSW: " + e.Message); }
        sw = null;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        return true;
    }

    // ---- Command callbacks (called by SolidWorks by name) ----

    public void OnSend() => Send(false, true);
    public void OnSendAgain() => Send(true, true);

    public void OnSettings()
    {
        using (var form = new SettingsForm(settings))
        {
            form.Saved += s =>
            {
                settings = s;
                Log.Write($"Settings saved: viewer {s.Viewer}");
                RestartPolling();
            };
            form.ShowDialog(new Owner(Process.GetCurrentProcess().MainWindowHandle));
        }
    }

    /// 1 = enabled: only for an open part or assembly.
    public int OnEnableSend()
    {
        try
        {
            var doc = sw?.ActiveDoc as ModelDoc2;
            if (doc == null) return 0;
            int t = doc.GetType();
            return t == (int)swDocumentTypes_e.swDocPART || t == (int)swDocumentTypes_e.swDocASSEMBLY ? 1 : 0;
        }
        catch (Exception) { return 0; }
    }

    public int OnEnableAlways() => 1;

    // ---- Automation hooks (swApp.GetAddInObject("SolidWorksQuestSender.Addin")), used for testing ----

    /// Exports the active document to a .glb file; returns a summary plus the mass/volume info.
    public string ExportToFile(string path)
    {
        try
        {
            var r = SolidWorksExporter.ExportActive(sw);
            File.WriteAllBytes(path, r.Glb);
            return r.Summary + "\n" + r.Timings + "\n" + SolidWorksExporter.DecodePartInfo(r.PartInfo);
        }
        catch (Exception e) { return "Error: " + e.Message; }
    }

    /// Development: loads a freshly built copy of this assembly (no SolidWorks restart) and runs its exporter
    /// in-process; returns its summary and timings.
    public string DevExport(string dllPath, string outPath)
    {
        try
        {
            var asm = System.Reflection.Assembly.Load(File.ReadAllBytes(dllPath));
            var exporter = asm.GetType("SolidWorksQuestSender.SolidWorksExporter");
            var r = exporter.GetMethod("ExportActive").Invoke(null, new object[] { sw });
            var t = r.GetType();
            File.WriteAllBytes(outPath, (byte[])t.GetField("Glb").GetValue(r));
            return t.GetProperty("Summary").GetValue(r) + "\n" + t.GetField("Timings").GetValue(r);
        }
        catch (System.Reflection.TargetInvocationException e) { return "Error: " + e.InnerException?.Message; }
        catch (Exception e) { return "Error: " + e.Message; }
    }

    /// Sends the active document and waits for the result.
    public string SendNow(bool force)
    {
        try
        {
            var r = SolidWorksExporter.ExportActive(sw);
            return Task.Run(() => Upload(r, force)).GetAwaiter().GetResult();
        }
        catch (Exception e) { return "Error: " + e.Message; }
    }

    // ---- Sending ----

    /// interactive: started by a click (asks before resolving lightweight components); false for voice, which
    /// resolves them without asking (the user is in the headset and can't answer a dialog on the PC).
    void Send(bool force, bool interactive)
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) return;
        SetState(Icons.Busy, "Sending to Quest…");
        ExportResult r;
        try
        {
            ResolveLightweight(ask: interactive);
            r = SolidWorksExporter.ExportActive(sw); // main thread; fast in-process
            Log.Write("Exported " + r.Summary + " | " + r.Timings);
        }
        catch (Exception e)
        {
            Done(null, e);
            return;
        }
        Task.Run(() => Upload(r, force)).ContinueWith(t =>
            ui.BeginInvoke(new Action(() => Done(t.IsFaulted ? null : t.Result, t.Exception?.GetBaseException()))));
    }

    /// Lightweight components have no geometry loaded, so they can't be sent. Resolve them first (asking if ask).
    void ResolveLightweight(bool ask)
    {
        if (!(sw.ActiveDoc is AssemblyDoc asm)) return;
        int count;
        try { count = asm.GetLightWeightComponentCount(); } catch (Exception) { return; }
        if (count <= 0) return;
        if (ask && sw.SendMsgToUser2(
                $"{count} component(s) are lightweight and can't be sent as they are.\n\nResolve them and include them in the model?",
                (int)swMessageBoxIcon_e.swMbQuestion, (int)swMessageBoxBtn_e.swMbYesNo) != (int)swMessageBoxResult_e.swMbHitYes)
            return;
        try
        {
            asm.ResolveAllLightWeightComponents(false);
            Log.Write($"Resolved {count} lightweight component(s)");
        }
        catch (Exception e) { Log.Write("Resolve failed: " + e.Message); }
    }

    async Task<string> Upload(ExportResult r, bool force)
    {
        var viewer = new ViewerClient(settings);
        string shown = await viewer.StatusAsync().ConfigureAwait(false);
        try
        {
            if (!force && shown != null && shown == r.Key)
            {
                await viewer.NotifyAsync("unchanged", r.Name).ConfigureAwait(false);
                return $"{r.Name}: already up to date";
            }
            await viewer.NotifyAsync("downloading", r.Name).ConfigureAwait(false);
            int lastPercent = -1;
            string summary = await viewer.SendModelAsync(r, (sent, total) =>
            {
                // Tray tooltip: "Sending DRIVE TRAIN 1: 45% (3.9 of 8.6 MB)", every 5%.
                int percent = (int)(100 * sent / Math.Max(1, total));
                if (percent / 5 == lastPercent / 5 || ui == null) return;
                lastPercent = percent;
                string tip = $"Sending {r.Name}: {percent}% ({sent / 1048576.0:0.0} of {total / 1048576.0:0.0} MB)";
                ui.BeginInvoke(new Action(() => SetState(Icons.Busy, tip)));
            }).ConfigureAwait(false);
            if (r.Lightweight > 0) summary += $" ({r.Lightweight} lightweight component(s) skipped; resolve them to include)";
            return summary;
        }
        catch (Exception e)
        {
            await viewer.NotifyAsync("error", e.Message).ConfigureAwait(false);
            throw;
        }
    }

    void Done(string message, Exception error)
    {
        Volatile.Write(ref busy, 0);
        if (error == null)
        {
            Log.Write("Sent: " + message);
            SetState(Icons.Ok, "Sent: " + message);
            Balloon("Sent: " + message, ToolTipIcon.Info);
        }
        else
        {
            Log.Write("Send failed: " + error.Message);
            SetState(Icons.Error, "Failed: " + error.Message);
            Balloon(error.Message, ToolTipIcon.Error);
            if (error is ViewerException ve && (ve.WrongCode || string.IsNullOrWhiteSpace(settings.PairCode))) OnSettings();
        }
    }

    FileSystemWatcher settingsWatcher;

    /// Editing settings.json by hand (e.g. a new viewer IP address) takes effect without restarting SolidWorks.
    void WatchSettingsFile()
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            settingsWatcher = new FileSystemWatcher(Settings.Folder, "settings.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler changed = (s, e) => ui.BeginInvoke(new Action(ReloadSettings));
            settingsWatcher.Changed += changed;
            settingsWatcher.Created += changed;
            settingsWatcher.Renamed += (s, e) => ui.BeginInvoke(new Action(ReloadSettings));
        }
        catch (Exception e) { Log.Write("Can't watch settings file: " + e.Message); }
    }

    void ReloadSettings()
    {
        Thread.Sleep(100); // editors often write in several steps
        var s = Settings.Load();
        if (s.Viewer == settings.Viewer && s.PairCode == settings.PairCode && s.ListenForVoice == settings.ListenForVoice) return;
        settings = s;
        Log.Write($"Settings file changed: viewer {s.Viewer}");
        RestartPolling();
    }

    /// Keeps a GET /commands open so the viewer's voice command can ask for a send.
    void RestartPolling()
    {
        pollCts?.Cancel();
        if (!settings.ListenForVoice || string.IsNullOrWhiteSpace(settings.PairCode))
        {
            Log.Write("Voice listening off (disabled or no pairing code)");
            return;
        }
        var cts = pollCts = new CancellationTokenSource();
        var viewer = new ViewerClient(settings);
        Log.Write($"Voice listening: polling {viewer.Address}");
        Task.Run(async () =>
        {
            string state = null;
            void Report(string s)
            {
                if (s == state) return;
                state = s;
                Log.Write($"Voice listening ({viewer.Address}): {s}");
            }
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var started = DateTime.UtcNow;
                    var wait = TimeSpan.FromSeconds(1);
                    try
                    {
                        var commands = await viewer.CommandsAsync(cts.Token).ConfigureAwait(false);
                        if (state != "connected")
                        {
                            try { await viewer.StatusAsync().ConfigureAwait(false); } catch (Exception) { } // which viewer build
                            Report("connected" + (Grouping.ViewerHandlesManyParts ? " (viewer handles many parts)" : ""));
                            state = "connected";
                        }
                        if (commands.Contains("send"))
                        {
                            Log.Write("Voice: send");
                            ui.BeginInvoke(new Action(() => Send(false, false)));
                        }
                    }
                    catch (ViewerException e)
                    {
                        Report(e.WrongCode ? "wrong pairing code" : "viewer not reachable");
                        wait = TimeSpan.FromSeconds(e.WrongCode ? 30 : 5);
                    }
                    catch (Exception) when (cts.IsCancellationRequested) { break; }
                    catch (Exception e)
                    {
                        Report("error: " + e.GetBaseException().Message);
                        wait = TimeSpan.FromSeconds(5);
                    }
                    var left = wait - (DateTime.UtcNow - started);
                    if (left > TimeSpan.Zero)
                    {
                        try { await Task.Delay(left, cts.Token).ConfigureAwait(false); } catch (Exception) { break; }
                    }
                }
                Log.Write($"Voice listening ({viewer.Address}): stopped");
            }
            catch (Exception e) { Log.Write("Voice listening crashed: " + e); }
        });
    }

    // ---- UI ----

    void SetState(Color color, string tip)
    {
        if (icon == null) return;
        var old = icon.Icon;
        icon.Icon = Icons.Tray(color);
        old?.Dispose();
        icon.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
    }

    void Balloon(string text, ToolTipIcon kind)
    {
        if (icon == null) return;
        icon.BalloonTipTitle = "SolidWorks → Quest";
        icon.BalloonTipText = text.Length > 250 ? text.Substring(0, 250) : text;
        icon.BalloonTipIcon = kind;
        icon.ShowBalloonTip(5000);
    }

    static void OpenLog()
    {
        if (!File.Exists(Log.FilePath)) Log.Write("Log started");
        Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
    }

    sealed class Owner : IWin32Window
    {
        public Owner(IntPtr h) { Handle = h; }
        public IntPtr Handle { get; }
    }

    void AddCommands()
    {
        cmdMgr = sw.GetCommandManager(cookie);
        int errors = 0;
        var group = cmdMgr.CreateCommandGroup2(GroupId, Title, "Send the model to the Quest viewer", "", -1, true, ref errors);
        group.IconList = Icons.Strips();
        group.MainIconList = Icons.MainIcons();

        int both = (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem);
        int send = group.AddCommandItem2("Send to Quest", -1, "Send the open part or assembly to the Quest viewer", "Send to Quest", 0, nameof(OnSend), nameof(OnEnableSend), 1, both);
        int again = group.AddCommandItem2("Send again", -1, "Send even if the model hasn't changed", "Send again", 1, nameof(OnSendAgain), nameof(OnEnableSend), 2, both);
        int setup = group.AddCommandItem2("Quest settings", -1, "Viewer address and pairing code", "Quest settings", 2, nameof(OnSettings), nameof(OnEnableAlways), 3, both);
        group.HasToolbar = true;
        group.HasMenu = true;
        group.Activate();

        var ids = new[] { group.get_CommandID(send), group.get_CommandID(again), group.get_CommandID(setup) };
        var text = Enumerable.Repeat((int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow, ids.Length).ToArray();
        foreach (int type in DocTypes)
        {
            var old = cmdMgr.GetCommandTab(type, Title);
            if (old != null) cmdMgr.RemoveCommandTab(old);
            var tab = cmdMgr.AddCommandTab(type, Title);
            tab?.AddCommandTabBox()?.AddCommands(ids, text);
        }
    }

    void RemoveCommands()
    {
        if (cmdMgr == null) return;
        foreach (int type in DocTypes)
        {
            var tab = cmdMgr.GetCommandTab(type, Title);
            if (tab != null) cmdMgr.RemoveCommandTab(tab);
        }
        cmdMgr.RemoveCommandGroup(GroupId);
        cmdMgr = null;
    }

    // ---- COM registration (RegAsm /codebase) ----

    [ComRegisterFunction]
    public static void Register(Type t)
    {
        using (var key = Registry.LocalMachine.CreateSubKey($@"SOFTWARE\SolidWorks\Addins\{{{t.GUID}}}"))
        {
            key.SetValue(null, 0);
            key.SetValue("Description", "Send parts and assemblies to the Quest mixed-reality viewer");
            key.SetValue("Title", Title);
        }
        using (var key = Registry.CurrentUser.CreateSubKey($@"Software\SolidWorks\AddInsStartup\{{{t.GUID}}}"))
            key.SetValue(null, 1); // load at startup
    }

    [ComUnregisterFunction]
    public static void Unregister(Type t)
    {
        try { Registry.LocalMachine.DeleteSubKeyTree($@"SOFTWARE\SolidWorks\Addins\{{{t.GUID}}}", false); } catch (Exception) { }
        try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\SolidWorks\AddInsStartup\{{{t.GUID}}}", false); } catch (Exception) { }
    }
}

/// Generated icons, so the add-in ships as a single DLL.
static class Icons
{
    public static readonly Color Idle = Color.FromArgb(0x37, 0x47, 0x4f);
    public static readonly Color Busy = Color.FromArgb(0xf9, 0xa8, 0x25);
    public static readonly Color Ok = Color.FromArgb(0x2e, 0x7d, 0x32);
    public static readonly Color Error = Color.FromArgb(0xc6, 0x28, 0x28);
    static readonly int[] Sizes = { 20, 32, 40, 64, 96, 128 };
    static readonly (string glyph, Color color)[] Commands =
    {
        ("Q", Color.FromArgb(0x15, 0x65, 0xc0)),
        ("↻", Color.FromArgb(0x00, 0x83, 0x8f)),
        ("⚙", Color.FromArgb(0x54, 0x6e, 0x7a)),
    };

    static string Folder
    {
        get
        {
            var f = Path.Combine(Settings.Folder, "icons");
            Directory.CreateDirectory(f);
            return f;
        }
    }

    public static string[] Strips() => Sizes.Select(s =>
    {
        var path = Path.Combine(Folder, $"strip{s}.png");
        using (var bmp = new Bitmap(s * Commands.Length, s))
        {
            using (var g = Graphics.FromImage(bmp))
                for (int i = 0; i < Commands.Length; i++) Draw(g, i * s, s, Commands[i].glyph, Commands[i].color);
            bmp.Save(path, ImageFormat.Png);
        }
        return path;
    }).ToArray();

    public static string[] MainIcons() => Sizes.Select(s =>
    {
        var path = Path.Combine(Folder, $"main{s}.png");
        using (var bmp = new Bitmap(s, s))
        {
            using (var g = Graphics.FromImage(bmp)) Draw(g, 0, s, "Q", Commands[0].color);
            bmp.Save(path, ImageFormat.Png);
        }
        return path;
    }).ToArray();

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    public static Icon Tray(Color color)
    {
        using (var bmp = new Bitmap(32, 32))
        {
            using (var g = Graphics.FromImage(bmp)) Draw(g, 0, 32, "Q", color);
            IntPtr h = bmp.GetHicon();
            var icon = (Icon)Icon.FromHandle(h).Clone();
            DestroyIcon(h);
            return icon;
        }
    }

    static void Draw(Graphics g, int x, int s, string glyph, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        float m = s * 0.04f, r = s * 0.28f, w = s - 2 * m;
        using (var path = new GraphicsPath())
        {
            path.AddArc(x + m, m, r, r, 180, 90);
            path.AddArc(x + m + w - r, m, r, r, 270, 90);
            path.AddArc(x + m + w - r, m + w - r, r, r, 0, 90);
            path.AddArc(x + m, m + w - r, r, r, 90, 90);
            path.CloseFigure();
            using (var brush = new SolidBrush(color)) g.FillPath(brush, path);
        }
        using (var font = new Font(glyph == "Q" ? "Segoe UI" : "Segoe UI Symbol", s * 0.55f, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(glyph, font, Brushes.White, new RectangleF(x, s * 0.02f, s, s), sf);
    }
}
