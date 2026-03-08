using System.ComponentModel;

namespace CodexHomeManager;

internal sealed class SharedStoreDefaultProfilesDialog : Form
{
    private readonly BindingList<SharedStoreDefaultProfileRow> _rows;
    private readonly DataGridView _grid = new();
    private readonly Button _btnUseCurrent = new();
    private readonly Button _btnAddRow = new();
    private readonly Button _btnRemoveRow = new();
    private readonly Button _btnOk = new();
    private readonly Button _btnCancel = new();
    private readonly string _currentSharedStoreHome;
    private readonly string _currentProfileName;

    public SharedStoreDefaultProfilesDialog(
        IDictionary<string, string> mappings,
        IEnumerable<string> availableProfileNames,
        string currentSharedStoreHome,
        string currentProfileName)
    {
        _currentSharedStoreHome = currentSharedStoreHome?.Trim() ?? string.Empty;
        _currentProfileName = currentProfileName?.Trim() ?? string.Empty;

        var orderedRows = mappings
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new SharedStoreDefaultProfileRow
            {
                SharedStoreHome = pair.Key,
                ProfileName = pair.Value
            })
            .ToList();

        _rows = new BindingList<SharedStoreDefaultProfileRow>(orderedRows);
        ResultMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        InitializeDialog(BuildAvailableProfilesHint(availableProfileNames));
    }

    public Dictionary<string, string> ResultMappings { get; private set; }

    private void InitializeDialog(string availableProfilesHint)
    {
        SuspendLayout();

        Text = "\u5171\u4eab\u4ed3\u9ed8\u8ba4\u8d26\u53f7\u6620\u5c04";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(820, 460);
        ClientSize = new Size(980, 560);

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

        var headerLayout = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8),
            RowCount = 2
        };
        headerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        headerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var lblHint = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            MaximumSize = new Size(920, 0),
            Text = "\u6bcf\u4e00\u884c\u5bf9\u5e94\u4e00\u4e2a\u5171\u4eab\u4ed3\u76ee\u5f55\u548c\u9ed8\u8ba4\u542f\u52a8\u8d26\u53f7\u3002\u53ef\u76f4\u63a5\u7f16\u8f91\u8868\u683c\uff0c\u4e5f\u53ef\u70b9\u51fb\u201c\u5199\u5165\u5f53\u524d\u5171\u4eab\u4ed3\u201d\u5feb\u901f\u65b0\u589e\u6216\u66f4\u65b0\u3002"
        };
        var lblProfilesHint = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(920, 0),
            Margin = new Padding(0, 6, 0, 0),
            Text = availableProfilesHint
        };
        headerLayout.Controls.Add(lblHint, 0, 0);
        headerLayout.Controls.Add(lblProfilesHint, 0, 1);

        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AutoGenerateColumns = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.DataSource = _rows;
        _grid.Dock = DockStyle.Fill;
        _grid.EditMode = DataGridViewEditMode.EditOnEnter;
        _grid.MultiSelect = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(SharedStoreDefaultProfileRow.SharedStoreHome),
            FillWeight = 66F,
            HeaderText = "\u5171\u4eab\u4ed3\u76ee\u5f55",
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(SharedStoreDefaultProfileRow.ProfileName),
            FillWeight = 34F,
            HeaderText = "\u9ed8\u8ba4\u542f\u52a8\u8d26\u53f7",
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        var footerLayout = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 8, 0, 0),
            RowCount = 1
        };
        footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var toolsLayout = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true
        };
        _btnUseCurrent.AutoSize = true;
        _btnUseCurrent.Text = "\u5199\u5165\u5f53\u524d\u5171\u4eab\u4ed3";
        _btnUseCurrent.Click += btnUseCurrent_Click;
        _btnAddRow.AutoSize = true;
        _btnAddRow.Text = "\u65b0\u589e\u7a7a\u767d\u884c";
        _btnAddRow.Click += btnAddRow_Click;
        _btnRemoveRow.AutoSize = true;
        _btnRemoveRow.Text = "\u5220\u9664\u9009\u4e2d\u884c";
        _btnRemoveRow.Click += btnRemoveRow_Click;
        toolsLayout.Controls.Add(_btnUseCurrent);
        toolsLayout.Controls.Add(_btnAddRow);
        toolsLayout.Controls.Add(_btnRemoveRow);

        var actionLayout = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
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
        _btnOk.Text = "\u786e\u5b9a";
        _btnOk.Click += btnOk_Click;
        actionLayout.Controls.Add(_btnCancel);
        actionLayout.Controls.Add(_btnOk);

        footerLayout.Controls.Add(toolsLayout, 0, 0);
        footerLayout.Controls.Add(actionLayout, 1, 0);

        rootLayout.Controls.Add(headerLayout, 0, 0);
        rootLayout.Controls.Add(_grid, 0, 1);
        rootLayout.Controls.Add(footerLayout, 0, 2);

        Controls.Add(rootLayout);
        AcceptButton = _btnOk;
        CancelButton = _btnCancel;

        ResumeLayout(false);
    }

    private void btnUseCurrent_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentSharedStoreHome))
        {
            MessageBox.Show(this, "\u8bf7\u5148\u5728\u4e3b\u7a97\u53e3\u586b\u5199\u5171\u4eab\u4ed3\u76ee\u5f55\u3002", "\u7f3a\u5c11\u5171\u4eab\u4ed3\u76ee\u5f55", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(_currentProfileName))
        {
            MessageBox.Show(this, "\u8bf7\u5148\u5728\u4e3b\u7a97\u53e3\u9009\u62e9\u6216\u8f93\u5165\u5f53\u524d\u8d26\u53f7\u540d\u79f0\u3002", "\u7f3a\u5c11\u9ed8\u8ba4\u8d26\u53f7", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var storeKey = NormalizeStoreKey(_currentSharedStoreHome);
        var existingRow = _rows.FirstOrDefault(row =>
            string.Equals(NormalizeStoreKey(row.SharedStoreHome), storeKey, StringComparison.OrdinalIgnoreCase));

        if (existingRow is not null)
        {
            existingRow.SharedStoreHome = _currentSharedStoreHome;
            existingRow.ProfileName = _currentProfileName;
            SelectRow(existingRow);
            return;
        }

        var newRow = new SharedStoreDefaultProfileRow
        {
            SharedStoreHome = _currentSharedStoreHome,
            ProfileName = _currentProfileName
        };
        _rows.Add(newRow);
        SelectRow(newRow);
    }

    private void btnAddRow_Click(object? sender, EventArgs e)
    {
        var row = new SharedStoreDefaultProfileRow();
        _rows.Add(row);
        SelectRow(row);
        _grid.CurrentCell = _grid.Rows[^1].Cells[0];
        _grid.BeginEdit(true);
    }

    private void btnRemoveRow_Click(object? sender, EventArgs e)
    {
        if (_grid.CurrentRow?.DataBoundItem is SharedStoreDefaultProfileRow row)
        {
            _rows.Remove(row);
        }
    }

    private void btnOk_Click(object? sender, EventArgs e)
    {
        _grid.EndEdit();

        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < _rows.Count; index++)
        {
            var row = _rows[index];
            var sharedStoreHome = row.SharedStoreHome.Trim();
            var profileName = row.ProfileName.Trim();

            if (string.IsNullOrWhiteSpace(sharedStoreHome) && string.IsNullOrWhiteSpace(profileName))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(sharedStoreHome))
            {
                MessageBox.Show(this, $"\u7b2c {index + 1} \u884c\u7f3a\u5c11\u5171\u4eab\u4ed3\u76ee\u5f55\u3002", "\u65e0\u6cd5\u4fdd\u5b58", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(profileName))
            {
                MessageBox.Show(this, $"\u7b2c {index + 1} \u884c\u7f3a\u5c11\u9ed8\u8ba4\u542f\u52a8\u8d26\u53f7\u3002", "\u65e0\u6cd5\u4fdd\u5b58", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var storeKey = NormalizeStoreKey(sharedStoreHome);
            if (string.IsNullOrWhiteSpace(storeKey))
            {
                MessageBox.Show(this, $"\u7b2c {index + 1} \u884c\u7684\u5171\u4eab\u4ed3\u76ee\u5f55\u65e0\u6548\u3002", "\u65e0\u6cd5\u4fdd\u5b58", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (mappings.ContainsKey(storeKey))
            {
                MessageBox.Show(this, $"\u7b2c {index + 1} \u884c\u7684\u5171\u4eab\u4ed3\u76ee\u5f55\u4e0e\u5176\u4ed6\u884c\u91cd\u590d\u3002", "\u65e0\u6cd5\u4fdd\u5b58", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            mappings[storeKey] = profileName;
        }

        ResultMappings = mappings;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void SelectRow(SharedStoreDefaultProfileRow row)
    {
        foreach (DataGridViewRow gridRow in _grid.Rows)
        {
            if (ReferenceEquals(gridRow.DataBoundItem, row))
            {
                gridRow.Selected = true;
                _grid.CurrentCell = gridRow.Cells[0];
                _grid.FirstDisplayedScrollingRowIndex = gridRow.Index;
                return;
            }
        }
    }

    private static string BuildAvailableProfilesHint(IEnumerable<string> availableProfileNames)
    {
        var profiles = availableProfileNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (profiles.Length == 0)
        {
            return "\u53ef\u7528\u8d26\u53f7\uff1a\u5f53\u524d\u672a\u53d1\u73b0\u914d\u7f6e\u6863\uff0c\u53ef\u624b\u52a8\u8f93\u5165\u540d\u79f0\u3002";
        }

        const int maxDisplayCount = 10;
        if (profiles.Length <= maxDisplayCount)
        {
            return $"\u53ef\u7528\u8d26\u53f7\uff1a{string.Join("\u3001", profiles)}";
        }

        var preview = string.Join("\u3001", profiles.Take(maxDisplayCount));
        return $"\u53ef\u7528\u8d26\u53f7\uff1a{preview} \u7b49 {profiles.Length} \u4e2a";
    }

    private static string NormalizeStoreKey(string? sharedStoreHome)
    {
        if (string.IsNullOrWhiteSpace(sharedStoreHome))
        {
            return string.Empty;
        }

        var trimmed = sharedStoreHome.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return trimmed;
        }
    }
}

internal sealed class SharedStoreDefaultProfileRow : INotifyPropertyChanged
{
    private string _sharedStoreHome = string.Empty;
    private string _profileName = string.Empty;

    public string SharedStoreHome
    {
        get => _sharedStoreHome;
        set
        {
            var normalizedValue = value?.Trim() ?? string.Empty;
            if (string.Equals(_sharedStoreHome, normalizedValue, StringComparison.Ordinal))
            {
                return;
            }

            _sharedStoreHome = normalizedValue;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SharedStoreHome)));
        }
    }

    public string ProfileName
    {
        get => _profileName;
        set
        {
            var normalizedValue = value?.Trim() ?? string.Empty;
            if (string.Equals(_profileName, normalizedValue, StringComparison.Ordinal))
            {
                return;
            }

            _profileName = normalizedValue;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProfileName)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}