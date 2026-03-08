using System.Text.Json;
using CodexHomeManager.Models;

namespace CodexHomeManager;

internal sealed class ProfileContentEditorDialog : Form
{
    private readonly TextBox _txtAuthJson = new();
    private readonly TextBox _txtConfigToml = new();
    private readonly Button _btnOk = new();
    private readonly Button _btnCancel = new();

    public ProfileContentEditorDialog(ManagedProfileContent content)
    {
        ProfileName = content.Name;
        AuthJson = content.AuthJson;
        ConfigToml = content.ConfigToml;

        InitializeDialog(content);
    }

    public string ProfileName { get; }

    public string AuthJson
    {
        get => _txtAuthJson.Text;
        private set => _txtAuthJson.Text = value;
    }

    public string ConfigToml
    {
        get => _txtConfigToml.Text;
        private set => _txtConfigToml.Text = value;
    }

    private void InitializeDialog(ManagedProfileContent content)
    {
        SuspendLayout();

        Text = "\u7f16\u8f91\u8d26\u53f7\u5185\u5bb9";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(900, 620);
        ClientSize = new Size(1080, 760);

        var rootLayout = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            RowCount = 3
        };
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8),
            MaximumSize = new Size(1020, 0),
            Text = $"\u8d26\u53f7\uff1a{content.Name}    \u63d0\u4f9b\u65b9\uff1a{DisplayText(content.ModelProvider)}    Revision\uff1a{content.Revision}"
        };

        var editorsLayout = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            RowCount = 4
        };
        editorsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editorsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        editorsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editorsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

        var lblAuthJson = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 6),
            Text = "auth.json"
        };
        _txtAuthJson.AcceptsReturn = true;
        _txtAuthJson.AcceptsTab = true;
        _txtAuthJson.Dock = DockStyle.Fill;
        _txtAuthJson.Multiline = true;
        _txtAuthJson.ScrollBars = ScrollBars.Both;
        _txtAuthJson.WordWrap = false;
        _txtAuthJson.Font = new Font("Consolas", 10F, FontStyle.Regular, GraphicsUnit.Point);

        var lblConfigToml = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 10, 0, 6),
            Text = "config.toml"
        };
        _txtConfigToml.AcceptsReturn = true;
        _txtConfigToml.AcceptsTab = true;
        _txtConfigToml.Dock = DockStyle.Fill;
        _txtConfigToml.Multiline = true;
        _txtConfigToml.ScrollBars = ScrollBars.Both;
        _txtConfigToml.WordWrap = false;
        _txtConfigToml.Font = new Font("Consolas", 10F, FontStyle.Regular, GraphicsUnit.Point);

        editorsLayout.Controls.Add(lblAuthJson, 0, 0);
        editorsLayout.Controls.Add(_txtAuthJson, 0, 1);
        editorsLayout.Controls.Add(lblConfigToml, 0, 2);
        editorsLayout.Controls.Add(_txtConfigToml, 0, 3);

        var footerLayout = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Padding(0, 10, 0, 0),
            WrapContents = false
        };

        _btnCancel.AutoSize = true;
        _btnCancel.Text = "\u53d6\u6d88";
        _btnCancel.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        _btnOk.AutoSize = true;
        _btnOk.Text = "\u4fdd\u5b58";
        _btnOk.Click += btnOk_Click;

        footerLayout.Controls.Add(_btnCancel);
        footerLayout.Controls.Add(_btnOk);

        rootLayout.Controls.Add(header, 0, 0);
        rootLayout.Controls.Add(editorsLayout, 0, 1);
        rootLayout.Controls.Add(footerLayout, 0, 2);

        Controls.Add(rootLayout);
        AcceptButton = _btnOk;
        CancelButton = _btnCancel;

        ResumeLayout(false);
    }

    private void btnOk_Click(object? sender, EventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_txtAuthJson.Text))
        {
            try
            {
                JsonDocument.Parse(_txtAuthJson.Text);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "auth.json", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private static string DisplayText(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "\u672a\u8bbe\u7f6e"
            : value.Trim();
    }
}
