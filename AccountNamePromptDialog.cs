namespace CodexHomeManager;

internal sealed class AccountNamePromptDialog : Form
{
    private readonly TextBox _txtAccountName = new();
    private readonly Button _btnOk = new();
    private readonly Button _btnCancel = new();

    public AccountNamePromptDialog(
        string title,
        string promptText,
        string confirmButtonText,
        string? initialValue = null,
        string? hintText = null)
    {
        AccountName = initialValue?.Trim() ?? string.Empty;
        InitializeDialog(title, promptText, confirmButtonText, hintText);
    }

    public string AccountName
    {
        get => _txtAccountName.Text.Trim();
        private set => _txtAccountName.Text = value;
    }

    private void InitializeDialog(string title, string promptText, string confirmButtonText, string? hintText)
    {
        SuspendLayout();

        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(540, 180);
        MinimumSize = new Size(540, 180);

        var rootLayout = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            RowCount = 4
        };
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var lblPrompt = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8),
            Text = promptText
        };

        _txtAccountName.Dock = DockStyle.Top;
        _txtAccountName.Margin = new Padding(0, 0, 0, 8);

        var lblHint = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8),
            ForeColor = SystemColors.GrayText,
            Text = string.IsNullOrWhiteSpace(hintText)
                ? "账号名中的非法文件名字符会自动替换为 _。"
                : hintText
        };

        var footerLayout = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Padding(0, 10, 0, 0),
            WrapContents = false
        };

        _btnCancel.AutoSize = true;
        _btnCancel.Text = "取消";
        _btnCancel.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        _btnOk.AutoSize = true;
        _btnOk.Text = confirmButtonText;
        _btnOk.Click += btnOk_Click;

        footerLayout.Controls.Add(_btnCancel);
        footerLayout.Controls.Add(_btnOk);

        rootLayout.Controls.Add(lblPrompt, 0, 0);
        rootLayout.Controls.Add(_txtAccountName, 0, 1);
        rootLayout.Controls.Add(lblHint, 0, 2);
        rootLayout.Controls.Add(footerLayout, 0, 3);

        Controls.Add(rootLayout);
        AcceptButton = _btnOk;
        CancelButton = _btnCancel;

        Shown += (_, _) =>
        {
            _txtAccountName.Focus();
            _txtAccountName.SelectAll();
        };

        ResumeLayout(false);
    }

    private void btnOk_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AccountName))
        {
            MessageBox.Show(this, "请输入账号名称。", "缺少账号名称", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _txtAccountName.Focus();
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
