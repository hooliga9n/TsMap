using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using TsMap;

namespace TsMap.Canvas
{
    public partial class PaletteEditorForm : Form
    {
        private readonly MapPaletteSettings _paletteSettings;
        private readonly Settings _appSettings;
        private readonly Action _onPaletteChanged;

        public PaletteEditorForm(Settings appSettings, Action onPaletteChanged)
        {
            InitializeComponent();

            _appSettings = appSettings;
            _paletteSettings = appSettings?.MapColor ?? new MapPaletteSettings();
            _onPaletteChanged = onPaletteChanged;

            BuildUI();
        }

        private void BuildUI()
        {
            Text = "Palette Editor";
            Size = new Size(380, 480);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            TableLayoutPanel panel = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 370,
                Padding = new Padding(10),
                AutoScroll = true,
                ColumnCount = 3
            };

            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45F));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 45F));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55F));

            PropertyInfo[] props = typeof(MapPaletteSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            int row = 0;

            foreach (var prop in props)
            {
                if (prop.PropertyType != typeof(string)) continue;

                string propName = prop.Name;
                string currentValue = prop.GetValue(_paletteSettings) as string ?? "#000000";

                Label lbl = new Label
                {
                    Text = propName,
                    AutoSize = true,
                    Anchor = AnchorStyles.Left
                };

                Button btnColor = new Button
                {
                    Width = 35,
                    Height = 24,
                    BackColor = MapPaletteSettings.ParseHex(currentValue, Color.Black),
                    FlatStyle = FlatStyle.Flat,
                    Anchor = AnchorStyles.None
                };

                TextBox txtHex = new TextBox
                {
                    Text = currentValue,
                    Anchor = AnchorStyles.Left | AnchorStyles.Right
                };

                btnColor.Click += (s, e) =>
                {
                    using (ColorDialog cd = new ColorDialog())
                    {
                        cd.Color = btnColor.BackColor;
                        if (cd.ShowDialog() == DialogResult.OK)
                        {
                            btnColor.BackColor = cd.Color;
                            string hex = MapPaletteSettings.ColorToHex(cd.Color);
                            txtHex.Text = hex;
                            prop.SetValue(_paletteSettings, hex);
                            _onPaletteChanged?.Invoke();
                        }
                    }
                };

                txtHex.TextChanged += (s, e) =>
                {
                    string text = txtHex.Text.Trim();
                    if (text.StartsWith("#") && (text.Length == 7 || text.Length == 4))
                    {
                        Color parsed = MapPaletteSettings.ParseHex(text, Color.Empty);
                        if (parsed != Color.Empty)
                        {
                            btnColor.BackColor = parsed;
                            prop.SetValue(_paletteSettings, text);
                            _onPaletteChanged?.Invoke();
                        }
                    }
                };

                panel.Controls.Add(lbl, 0, row);
                panel.Controls.Add(btnColor, 1, row);
                panel.Controls.Add(txtHex, 2, row);
                row++;
            }

            Button btnSave = new Button
            {
                Text = "Save & Apply",
                Dock = DockStyle.Bottom,
                Height = 40
            };

            btnSave.Click += (s, e) =>
            {
                if (_appSettings != null)
                {
                    JsonHelper.SaveSettings(_appSettings);
                }
                _onPaletteChanged?.Invoke();
                Close();
            };

            Controls.Add(panel);
            Controls.Add(btnSave);
        }
    }
}