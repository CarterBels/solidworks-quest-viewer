using System;
using System.Drawing;
using System.Windows.Forms;

namespace SolidWorksQuestSender;

/// Viewer address + pairing code, as shown on the viewer's keypad display.
public sealed class SettingsForm : Form
{
    public event Action<Settings> Saved;
    readonly TextBox address = new() { Width = 220 };
    readonly TextBox code = new() { Width = 120, CharacterCasing = CharacterCasing.Upper, MaxLength = 12 };
    readonly CheckBox voice = new() { Text = "Listen for voice \"send\" from the headset", AutoSize = true };
    readonly Label result = new() { AutoSize = false, Width = 360, Height = 48 };

    public SettingsForm(Settings s)
    {
        Text = "SolidWorks Quest Sender – Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        Font = new Font("Segoe UI", 9.5f);

        maxTriangles = s.MaxTriangles; // edited in settings.json only
        address.Text = s.Viewer;
        code.Text = s.PairCode;
        voice.Checked = s.ListenForVoice;

        var save = new Button { Text = "Save", AutoSize = true };
        var test = new Button { Text = "Test connection", AutoSize = true };
        save.Click += (_, _) => { var n = Current(); n.Save(); Saved?.Invoke(n); result.ForeColor = Color.DarkGreen; result.Text = "Saved."; };
        test.Click += async (_, _) =>
        {
            result.ForeColor = SystemColors.ControlText;
            result.Text = "Testing…";
            try
            {
                var key = await new ViewerClient(Current()).StatusAsync();
                result.ForeColor = Color.DarkGreen;
                result.Text = "Connected to the viewer." + (key != null ? " It is showing a model." : "");
            }
            catch (Exception e) { result.ForeColor = Color.DarkRed; result.Text = e.Message; }
        };
        AcceptButton = save;

        var help = new Label
        {
            AutoSize = false, Width = 360, Height = 64,
            Text = "Start the viewer on the Quest. Its keypad display shows an address and a pairing code; enter both here.\n"
                 + "For the PC (Quest Link) version the address is 127.0.0.1:8723.",
        };

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        grid.Controls.Add(help, 0, 0); grid.SetColumnSpan(help, 2);
        grid.Controls.Add(new Label { Text = "Viewer address", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        grid.Controls.Add(address, 1, 1);
        grid.Controls.Add(new Label { Text = "Pairing code", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        grid.Controls.Add(code, 1, 2);
        grid.Controls.Add(voice, 0, 3); grid.SetColumnSpan(voice, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        buttons.Controls.Add(save); buttons.Controls.Add(test);
        grid.Controls.Add(buttons, 0, 4); grid.SetColumnSpan(buttons, 2);
        grid.Controls.Add(result, 0, 5); grid.SetColumnSpan(result, 2);
        Controls.Add(grid);
    }

    Settings Current() => new()
    {
        Viewer = string.IsNullOrWhiteSpace(address.Text) ? "127.0.0.1:8723" : address.Text.Trim(),
        PairCode = code.Text.Trim(),
        ListenForVoice = voice.Checked,
        MaxTriangles = maxTriangles,
    };
    readonly int maxTriangles;
}
