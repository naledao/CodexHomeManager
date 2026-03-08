using System.ComponentModel;
using CodexHomeManager.Models;
using CodexHomeManager.Services;

namespace CodexHomeManager;

public partial class Form1 : Form
{
    private readonly CodexManager _manager = new();
    private readonly ProfileStore _profileStore = new();
    private readonly AppSettingsStore _settingsStore = new();
    private readonly BindingList<SessionRecord> _sessions = [];
    private readonly List<FileSystemWatcher> _configWatchers = [];
    private readonly System.Windows.Forms.Timer _autoSyncTimer = new();
    private readonly SemaphoreSlim _runtimeSyncLock = new(1, 1);
    private readonly ToolTip _toolTip = new();

    private IReadOnlyList<ProviderProfile> _profiles = [];
    private bool _suppressUiUpdates;
    private bool _autoSyncPending;
    private bool _autoSyncInProgress;
    private DateTime _lastConfigChangeUtc = DateTime.MinValue;
    private DateTime _ignoreWatchEventsUntilUtc = DateTime.MinValue;
    private string? _pendingConfigHome;
    private string _pendingConfigFile = string.Empty;
    private string _currentSessionScope = "\u6e90\u4f1a\u8bdd";
    private string _defaultLaunchProfileName = string.Empty;
    private Dictionary<string, string> _sharedStoreDefaultLaunchProfiles = new(StringComparer.OrdinalIgnoreCase);

    public Form1()
    {
        InitializeComponent();
        ApplyApplicationIcon();
        ConfigureGrid();
        ConfigureUserFriendlyLayout();
        sessionsGrid.DataSource = _sessions;
        WireSettingsPersistence();
        InitializeAutomation();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplySavedPaths();
        RefreshStatuses();
        UpdateSessionSplitRatio();
        Log("\u5c31\u7eea\u3002");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveCurrentPaths();
        DisposeConfigWatchers();
        _autoSyncTimer.Stop();
        base.OnFormClosing(e);
    }

    private void ApplyApplicationIcon()
    {
        try
        {
            using var extractedIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (extractedIcon is not null)
            {
                Icon = (Icon)extractedIcon.Clone();
            }
        }
        catch
        {
            // Best effort. The executable icon still applies even if runtime extraction fails.
        }
    }

    private void ConfigureUserFriendlyLayout()
    {
        SuspendLayout();
        try
        {
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = Color.FromArgb(242, 245, 249);
            AutoScroll = true;
            rootLayout.BackColor = BackColor;
            rootLayout.AutoScroll = true;
            rootLayout.Padding = new Padding(10);
            rootLayout.RowStyles[0].SizeType = SizeType.AutoSize;
            rootLayout.RowStyles[1].SizeType = SizeType.AutoSize;
            rootLayout.RowStyles[2].SizeType = SizeType.Percent;
            rootLayout.RowStyles[2].Height = 100F;

            grpPaths.Text = "第一步：目录设置";
            grpActions.Text = "第二步：操作中心";
            grpSessions.Text = "会话列表";
            grpDetails.Text = "会话详情";
            grpLog.Text = "操作日志";

            ConfigurePathSection();
            ConfigureActionSection();
            ConfigureSessionArea();
            ConfigureToolTips();
            rootLayout.PerformLayout();
            PerformLayout();
        }
        finally
        {
            ResumeLayout(false);
        }
    }

    private void ConfigurePathSection()
    {
        grpPaths.BackColor = Color.White;
        grpPaths.ForeColor = Color.FromArgb(30, 41, 59);
        grpPaths.AutoSize = true;
        grpPaths.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        grpPaths.MinimumSize = new Size(0, 0);
        pathsLayout.Padding = new Padding(6, 6, 6, 4);
        pathsLayout.AutoSize = true;
        pathsLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;

        lblStateHome.Text = "会话来源目录";
        lblAuthHome.Text = "当前账号目录";
        lblProfilesRoot.Text = "账号库存目录";
        lblSharedStoreHome.Text = "共享仓目录";
        lblTargetHome.Text = "运行目录";
        btnBrowseState.Text = "...";
        btnBrowseAuth.Text = "...";
        btnBrowseProfilesRoot.Text = "...";
        btnBrowseShared.Text = "...";
        btnBrowseTarget.Text = "...";
        btnBrowseAppExe.Text = "...";

        foreach (var rowIndex in Enumerable.Range(0, pathsLayout.RowStyles.Count))
        {
            pathsLayout.RowStyles[rowIndex].SizeType = SizeType.AutoSize;
        }

        if (pathsLayout.ColumnStyles.Count >= 3)
        {
            pathsLayout.ColumnStyles[0].SizeType = SizeType.AutoSize;
            pathsLayout.ColumnStyles[1].SizeType = SizeType.Percent;
            pathsLayout.ColumnStyles[1].Width = 100F;
            pathsLayout.ColumnStyles[2].SizeType = SizeType.AutoSize;
        }

        foreach (var textBox in new[] { txtStateHome, txtAuthHome, txtProfilesRoot, txtSharedStoreHome, txtTargetHome, txtAppExe })
        {
            textBox.BorderStyle = BorderStyle.FixedSingle;
            textBox.Margin = new Padding(0, 2, 0, 4);
            textBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        }

        txtAuthHome.ReadOnly = true;
        txtAuthHome.BackColor = Color.FromArgb(246, 248, 252);

        foreach (var button in new[] { btnBrowseState, btnBrowseAuth, btnBrowseProfilesRoot, btnBrowseShared, btnBrowseTarget, btnBrowseAppExe })
        {
            var textSize = TextRenderer.MeasureText(
                button.Text,
                button.Font,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            var buttonWidth = Math.Max(44, textSize.Width + 16);

            button.AutoSize = false;
            button.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            button.Size = new Size(buttonWidth, 36);
            button.MinimumSize = new Size(buttonWidth, 36);
            button.Margin = new Padding(8, 1, 0, 1);
            button.Padding = new Padding(0);
            button.TextAlign = ContentAlignment.MiddleCenter;
            button.Anchor = AnchorStyles.Left;
            button.UseMnemonic = false;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            button.BackColor = Color.White;
        }

        if (pathsLayout.Controls.Find("lblPathsHint", false).Length == 0)
        {
            var lblPathsHint = new Label
            {
                Name = "lblPathsHint",
                AutoSize = true,
                ForeColor = Color.FromArgb(100, 116, 139),
                Margin = new Padding(0, 8, 0, 0),
                MaximumSize = new Size(1320, 0),
                Text = "会话来源目录用于读取旧会话；共享仓目录用于统一存放导入后的会话；运行目录是启动 Codex 时真正使用的工作目录。"
            };

            pathsLayout.RowCount = 7;
            pathsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            pathsLayout.Controls.Add(lblPathsHint, 0, 6);
            pathsLayout.SetColumnSpan(lblPathsHint, 3);
        }
    }

    private void ConfigureActionSection()
    {
        grpActions.SuspendLayout();
        try
        {
            grpActions.BackColor = Color.White;
            grpActions.ForeColor = Color.FromArgb(30, 41, 59);
            grpActions.AutoSize = true;
            grpActions.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            grpActions.MinimumSize = new Size(0, 0);
            grpActions.Controls.Clear();

            btnUseDefaults.Text = "恢复推荐路径";
            btnLoadSessions.Text = "读取来源会话";
            btnLoadSharedSessions.Text = "读取共享会话";
            btnPrepareHome.Text = "初始化共享仓";
            btnImportSelected.Text = "导入选中会话";
            btnRepairTargetHome.Text = "同步运行目录";
            btnSaveProfile.Text = "保存当前账号";
            btnSetDefaultLaunchProfile.Text = "设为默认启动";
            btnApplyProfile.Text = "切换账号";
            btnImportProfile.Text = "导入账号";
            btnExportProfile.Text = "导出账号";
            btnLaunchDefaultProfile.Text = "启动默认账号";
            btnManageSharedStoreDefaults.Text = "默认账号映射";
            btnSwitchProfileAndLaunch.Text = "切换并启动";
            btnLaunchApp.Text = "同步并启动";
            chkOverwriteTarget.Text = "覆盖运行目录";
            chkRefreshUpdatedAt.Text = "刷新 updated_at";
            chkAddWorkspaceHint.Text = "写入工作区提示";
            chkAutoSyncConfigChanges.Text = "自动同步配置变更";

            cmbProfiles.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            cmbProfiles.AutoCompleteSource = AutoCompleteSource.ListItems;
            cmbProfiles.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            cmbProfiles.Margin = new Padding(0, 0, 8, 0);
            cmbProfiles.MinimumSize = new Size(220, 32);

            var actionsHost = new TableLayoutPanel
            {
                Name = "actionsHost",
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(0, 4, 0, 0),
                BackColor = Color.White,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            actionsHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            actionsHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            actionsHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            actionsHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var quickFlow = CreateWrappedFlow(
                btnLoadSessions,
                btnLoadSharedSessions,
                btnPrepareHome,
                btnImportSelected,
                btnRepairTargetHome,
                btnSwitchProfileAndLaunch,
                btnLaunchApp);

            var quickGroup = CreateSectionGroup(
                "常用流程",
                "推荐顺序：先选账号，再读取会话，导入后同步运行目录，最后启动 Codex。",
                quickFlow);
            quickGroup.Margin = new Padding(0, 0, 0, 10);

            var accountContent = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Margin = Padding.Empty,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            accountContent.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            accountContent.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            accountContent.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var accountHeader = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                Margin = Padding.Empty,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            accountHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48F));
            accountHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            accountHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126F));
            lblProfileName.Margin = new Padding(0, 9, 8, 0);
            lblProfileName.Text = "账号";
            btnRefreshProfiles.Text = "刷新列表";
            accountHeader.Controls.Add(lblProfileName, 0, 0);
            accountHeader.Controls.Add(cmbProfiles, 1, 0);
            accountHeader.Controls.Add(btnRefreshProfiles, 2, 0);

            var accountFlowPrimary = CreateWrappedFlow(
                btnEditProfileContents,
                btnSaveProfile,
                btnImportProfile,
                btnExportProfile,
                btnSetDefaultLaunchProfile,
                btnLaunchDefaultProfile,
                btnManageSharedStoreDefaults,
                btnApplyProfile);

            var accountFlowSecondary = CreateWrappedFlow(
                btnCreateEmptyProfile,
                btnRenameProfile,
                btnDeleteProfile,
                btnUseDefaults,
                btnCloseApp);

            accountContent.Controls.Add(accountHeader, 0, 0);
            accountContent.Controls.Add(accountFlowPrimary, 0, 1);
            accountContent.Controls.Add(accountFlowSecondary, 0, 2);

            var accountGroup = CreateSectionGroup(
                "账号管理",
                "这里处理账号选择、编辑、导入导出，以及默认启动账号设置。",
                accountContent);
            accountGroup.Margin = new Padding(0, 0, 0, 10);

            var workspaceContent = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 1,
                Margin = Padding.Empty,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            workspaceContent.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var optionsGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            optionsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            optionsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            optionsGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            optionsGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var optionControls = new[] { chkOverwriteTarget, chkRefreshUpdatedAt, chkAddWorkspaceHint, chkAutoSyncConfigChanges };
            for (var index = 0; index < optionControls.Length; index++)
            {
                var checkBox = optionControls[index];
                checkBox.Margin = new Padding(0, 0, 0, 8);
                optionsGrid.Controls.Add(checkBox, index % 2, index / 2);
            }

            workspaceContent.Controls.Add(optionsGrid, 0, 0);

            var workspaceGroup = CreateSectionGroup(
                "同步选项",
                "控制导入、运行目录覆盖和配置自动同步的行为。",
                workspaceContent);
            workspaceGroup.Margin = new Padding(0, 0, 10, 0);

            var statusContent = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 3,
                Margin = Padding.Empty,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            statusContent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            statusContent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            statusContent.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            statusContent.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            statusContent.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var statusLabels = new[] { lblStatus, lblAppStatus, lblProviderStatus, lblDefaultProfileStatus, lblWatchStatus };
            for (var index = 0; index < statusLabels.Length; index++)
            {
                var label = statusLabels[index];
                label.AutoSize = true;
                label.Margin = new Padding(0, 0, 8, 8);
                label.MaximumSize = new Size(320, 0);
                label.Padding = new Padding(10, 8, 10, 8);
                label.BorderStyle = BorderStyle.FixedSingle;
                label.BackColor = Color.FromArgb(248, 250, 252);
                statusContent.Controls.Add(label, index % 2, index / 2);
            }

            var statusGroup = CreateSectionGroup(
                "当前状态",
                "这些状态会随共享仓、账号和程序运行情况自动刷新。",
                statusContent);
            statusGroup.Margin = Padding.Empty;

            var bottomHost = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            bottomHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44F));
            bottomHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56F));
            bottomHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            bottomHost.Controls.Add(workspaceGroup, 0, 0);
            bottomHost.Controls.Add(statusGroup, 1, 0);

            actionsHost.Controls.Add(quickGroup, 0, 0);
            actionsHost.Controls.Add(accountGroup, 0, 1);
            actionsHost.Controls.Add(bottomHost, 0, 2);

            grpActions.Controls.Add(actionsHost);

            StyleButton(btnLoadSessions, isPrimary: true);
            StyleButton(btnLoadSharedSessions, isPrimary: false);
            StyleButton(btnPrepareHome, isPrimary: false, minWidth: 120);
            StyleButton(btnImportSelected, isPrimary: true);
            StyleButton(btnRepairTargetHome, isPrimary: false);
            StyleButton(btnSwitchProfileAndLaunch, isPrimary: true, minWidth: 120);
            StyleButton(btnLaunchApp, isPrimary: true, minWidth: 120);
            StyleButton(btnRefreshProfiles, isPrimary: false, minWidth: 104);
            StyleButton(btnEditProfileContents, isPrimary: false);
            StyleButton(btnSaveProfile, isPrimary: false);
            StyleButton(btnImportProfile, isPrimary: false);
            StyleButton(btnExportProfile, isPrimary: false);
            StyleButton(btnSetDefaultLaunchProfile, isPrimary: false, minWidth: 120);
            StyleButton(btnLaunchDefaultProfile, isPrimary: false, minWidth: 120);
            StyleButton(btnManageSharedStoreDefaults, isPrimary: false, minWidth: 132);
            StyleButton(btnApplyProfile, isPrimary: false);
            StyleButton(btnCreateEmptyProfile, isPrimary: false);
            StyleButton(btnRenameProfile, isPrimary: false);
            StyleButton(btnDeleteProfile, isPrimary: false, isDanger: true);
            StyleButton(btnUseDefaults, isPrimary: false, minWidth: 116);
            StyleButton(btnCloseApp, isPrimary: false, isDanger: true, minWidth: 116);
        }
        finally
        {
            grpActions.ResumeLayout(false);
        }
    }

    private void ConfigureSessionArea()
    {
        grpSessions.BackColor = Color.White;
        grpDetails.BackColor = Color.White;
        grpLog.BackColor = Color.White;
        mainSplit.BackColor = Color.FromArgb(226, 232, 240);
        mainSplit.FixedPanel = FixedPanel.None;
        mainSplit.Panel1MinSize = 560;
        mainSplit.Panel2MinSize = 280;
        mainSplit.SplitterWidth = 8;
        mainSplit.Resize -= mainSplit_Resize;
        mainSplit.Resize += mainSplit_Resize;
        UpdateSessionSplitRatio();

        lblSessionCount.Font = new Font(Font, FontStyle.Bold);
        lblSessionCount.Padding = new Padding(0, 2, 0, 4);

        sessionsGrid.BorderStyle = BorderStyle.FixedSingle;
        sessionsGrid.EnableHeadersVisualStyles = false;
        sessionsGrid.GridColor = Color.FromArgb(226, 232, 240);
        sessionsGrid.RowTemplate.Height = 34;
        sessionsGrid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        sessionsGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(15, 118, 110);
        sessionsGrid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        sessionsGrid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(15, 118, 110);
        sessionsGrid.ColumnHeadersDefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
        sessionsGrid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(14, 116, 144);
        sessionsGrid.DefaultCellStyle.SelectionForeColor = Color.White;
        sessionsGrid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 252);

        grpDetails.Height = 176;
        detailsLayout.RowStyles[1].Height = 44F;
        txtSelectedTitle.Multiline = true;
        txtSelectedTitle.ScrollBars = ScrollBars.Vertical;
        txtSelectedPath.ScrollBars = ScrollBars.Both;
        txtSelectedPath.WordWrap = false;

        var monoFont = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);
        txtSelectedId.Font = monoFont;
        txtSelectedPath.Font = monoFont;

        foreach (var textBox in new[] { txtSelectedId, txtSelectedTitle, txtSelectedCwd, txtSelectedPath, txtLog })
        {
            textBox.BorderStyle = BorderStyle.FixedSingle;
            textBox.BackColor = Color.FromArgb(250, 250, 251);
        }

        txtLog.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
    }

    private void mainSplit_Resize(object? sender, EventArgs e)
    {
        UpdateSessionSplitRatio();
    }

    private void UpdateSessionSplitRatio()
    {
        var availableWidth = mainSplit.ClientSize.Width - mainSplit.SplitterWidth;
        if (availableWidth <= 0)
        {
            return;
        }

        var desiredLeftWidth = (int)Math.Round(availableWidth * (2D / 3D));
        var minLeftWidth = Math.Max(320, mainSplit.Panel1MinSize);
        var minRightWidth = Math.Max(240, mainSplit.Panel2MinSize);
        var maxLeftWidth = availableWidth - minRightWidth;
        if (maxLeftWidth <= minLeftWidth)
        {
            desiredLeftWidth = Math.Max(1, availableWidth - minRightWidth);
        }
        else
        {
            desiredLeftWidth = Math.Max(minLeftWidth, Math.Min(desiredLeftWidth, maxLeftWidth));
        }

        if (desiredLeftWidth > 0 && desiredLeftWidth < availableWidth)
        {
            mainSplit.SplitterDistance = desiredLeftWidth;
        }
    }

    private void ConfigureToolTips()
    {
        _toolTip.AutoPopDelay = 12000;
        _toolTip.InitialDelay = 400;
        _toolTip.ReshowDelay = 200;
        _toolTip.ShowAlways = true;

        _toolTip.SetToolTip(txtStateHome, "旧会话来源目录。读取“来源会话”时，会从这里扫描 sessions、history.jsonl 和 session_index.jsonl。");
        _toolTip.SetToolTip(txtAuthHome, "当前账号的临时落地目录。通常由你选中的账号自动生成，不需要手工维护。");
        _toolTip.SetToolTip(txtSharedStoreHome, "共享仓目录。导入后的会话会统一存到这里。");
        _toolTip.SetToolTip(txtTargetHome, "运行目录。启动 Codex 时，真正作为 CODEX_HOME 使用的是这里。");
        _toolTip.SetToolTip(btnBrowseState, "选择会话来源目录");
        _toolTip.SetToolTip(btnBrowseAuth, "选择当前账号目录");
        _toolTip.SetToolTip(btnBrowseProfilesRoot, "选择账号库存目录");
        _toolTip.SetToolTip(btnBrowseShared, "选择共享仓目录");
        _toolTip.SetToolTip(btnBrowseTarget, "选择运行目录");
        _toolTip.SetToolTip(btnBrowseAppExe, "选择 Codex 程序");
        _toolTip.SetToolTip(btnImportSelected, "把当前选中的来源会话导入到共享仓。批量导入时，不会再自动同步运行目录。");
        _toolTip.SetToolTip(btnSwitchProfileAndLaunch, "先切换到当前账号，再把共享仓同步到运行目录，最后启动 Codex。");
        _toolTip.SetToolTip(btnLaunchApp, "按当前路径设置直接同步并启动 Codex。适合共享仓内容已经准备好的情况。");
        _toolTip.SetToolTip(btnManageSharedStoreDefaults, "给不同共享仓设置各自的默认启动账号。");
        _toolTip.SetToolTip(chkAutoSyncConfigChanges, "监控 auth.json / config.toml 的变化，必要时自动把变更写回账号库并同步到运行目录。");
    }

    private GroupBox CreateSectionGroup(string title, string description, Control content)
    {
        var group = new GroupBox
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            Margin = new Padding(0, 0, 10, 10),
            Text = title,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(30, 41, 59),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var descriptionLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(100, 116, 139),
            Margin = new Padding(0, 0, 0, 8),
            MaximumSize = new Size(960, 0),
            Text = description
        };

        layout.Controls.Add(descriptionLabel, 0, 0);
        layout.Controls.Add(content, 0, 1);
        group.Controls.Add(layout);
        return group;
    }

    private FlowLayoutPanel CreateWrappedFlow(params Control[] controls)
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoScroll = true,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };

        foreach (var control in controls)
        {
            if (control is not null)
            {
                flow.Controls.Add(control);
            }
        }

        return flow;
    }

    private void StyleButton(Button button, bool isPrimary, bool isDanger = false, int minWidth = 104)
    {
        var measuredText = TextRenderer.MeasureText(
            button.Text,
            button.Font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        var targetWidth = Math.Max(minWidth, measuredText.Width + 34);
        var targetHeight = Math.Max(38, measuredText.Height + 16);

        button.AutoSize = false;
        button.MinimumSize = new Size(minWidth, 38);
        button.Size = new Size(targetWidth, targetHeight);
        button.Margin = new Padding(0, 0, 8, 8);
        button.Padding = new Padding(12, 5, 12, 5);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.Cursor = Cursors.Hand;
        button.UseVisualStyleBackColor = false;

        if (isDanger)
        {
            button.BackColor = Color.FromArgb(127, 29, 29);
            button.ForeColor = Color.White;
            button.FlatAppearance.BorderColor = button.BackColor;
            return;
        }

        if (isPrimary)
        {
            button.BackColor = Color.FromArgb(15, 118, 110);
            button.ForeColor = Color.White;
            button.FlatAppearance.BorderColor = button.BackColor;
            return;
        }

        button.BackColor = Color.White;
        button.ForeColor = Color.FromArgb(30, 41, 59);
        button.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
        button.FlatAppearance.BorderSize = 1;
    }

    private void ConfigureGrid()
    {
        sessionsGrid.AutoGenerateColumns = false;
        sessionsGrid.Columns.Clear();
        sessionsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(SessionRecord.Title),
            HeaderText = "\u6807\u9898",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 34
        });
        sessionsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(SessionRecord.Id),
            HeaderText = "\u4f1a\u8bdd ID",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 22
        });
        sessionsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(SessionRecord.ModelProvider),
            HeaderText = "\u63d0\u4f9b\u65b9",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 10
        });
        sessionsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(SessionRecord.Cwd),
            HeaderText = "\u5de5\u4f5c\u76ee\u5f55",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 24
        });
        sessionsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(SessionRecord.UpdatedAt),
            HeaderText = "\u66f4\u65b0\u65f6\u95f4",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 10,
            DefaultCellStyle = new DataGridViewCellStyle { Format = "yyyy-MM-dd HH:mm:ss" }
        });
    }

    private void WireSettingsPersistence()
    {
        txtStateHome.TextChanged += PathInputChanged;
        txtAuthHome.TextChanged += PathInputChanged;
        txtProfilesRoot.TextChanged += PathInputChanged;
        txtSharedStoreHome.TextChanged += PathInputChanged;
        txtTargetHome.TextChanged += PathInputChanged;
        txtAppExe.TextChanged += PathInputChanged;
        cmbProfiles.TextChanged += ProfileSelectionChanged;
        cmbProfiles.SelectedIndexChanged += ProfileSelectionChanged;
        chkAutoSyncConfigChanges.CheckedChanged += AutoSyncConfigChangesChanged;
    }

    private void InitializeAutomation()
    {
        _autoSyncTimer.Interval = 1500;
        _autoSyncTimer.Tick += autoSyncTimer_Tick;
        _autoSyncTimer.Start();
    }

    private void ApplySavedPaths()
    {
        var settings = _settingsStore.Load();
        var legacySharedStoreDefaults = NormalizeSharedStoreDefaultLaunchProfiles(settings?.SharedStoreDefaultLaunchProfiles);

        _suppressUiUpdates = true;
        try
        {
            txtStateHome.Text = FirstNonBlank(settings?.StateHome, _manager.DefaultCodexHome);
            txtAuthHome.Text = FirstNonBlank(settings?.AuthHome, _manager.DefaultCodexHome);
            txtProfilesRoot.Text = FirstNonBlank(settings?.ProfilesRoot, _profileStore.DefaultProfilesRoot);
            txtSharedStoreHome.Text = FirstNonBlank(settings?.SharedStoreHome, _manager.DefaultSharedStoreHome);
            txtTargetHome.Text = FirstNonBlank(settings?.TargetHome, GetDefaultTargetHome());
            txtAppExe.Text = FirstNonBlank(settings?.AppExePath, string.Empty);
            RefreshCodexAppExecutablePath(logChange: false, forceLatestWindowsAppsPath: true);
            _defaultLaunchProfileName = settings?.DefaultLaunchProfile?.Trim() ?? string.Empty;
            _sharedStoreDefaultLaunchProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            chkAutoSyncConfigChanges.Checked = settings?.AutoSyncConfigChanges ?? true;
            cmbProfiles.Text = settings?.SelectedProfile?.Trim() ?? string.Empty;
        }
        finally
        {
            _suppressUiUpdates = false;
        }

        RefreshProfileList(selectName: cmbProfiles.Text.Trim(), preserveTypedSelection: false);
        _sharedStoreDefaultLaunchProfiles = _profileStore.MigrateSharedStoreDefaultLaunchProfiles(legacySharedStoreDefaults);
        if (string.IsNullOrWhiteSpace(cmbProfiles.Text))
        {
            cmbProfiles.Text = FirstNonBlank(settings?.SelectedProfile, GetCurrentDefaultLaunchProfileName());
        }

        RefreshConfigWatchers();
        SaveCurrentPaths();
    }

    private void PathInputChanged(object? sender, EventArgs e)
    {
        if (_suppressUiUpdates)
        {
            return;
        }

        var controlName = (sender as Control)?.Name;
        if (string.Equals(controlName, nameof(txtSharedStoreHome), StringComparison.Ordinal))
        {
            ApplyCurrentSharedStoreDefaultProfileSelection(logChange: true);
        }

        SaveCurrentPaths();

        if (string.Equals(controlName, nameof(txtProfilesRoot), StringComparison.Ordinal))
        {
            RefreshProfileList(selectName: cmbProfiles.Text.Trim(), preserveTypedSelection: true);
        }

        if (controlName is nameof(txtAuthHome) or nameof(txtTargetHome) or nameof(txtProfilesRoot))
        {
            RefreshConfigWatchers();
        }

        RefreshStatuses();
    }

    private void ProfileSelectionChanged(object? sender, EventArgs e)
    {
        if (_suppressUiUpdates)
        {
            return;
        }

        SaveCurrentPaths();
    }

    private bool ApplyCurrentSharedStoreDefaultProfileSelection(bool logChange)
    {
        if (!TryGetCurrentSharedStoreDefaultLaunchProfileName(out var profileName))
        {
            return false;
        }

        var trimmedProfileName = profileName.Trim();
        if (string.IsNullOrWhiteSpace(trimmedProfileName) ||
            string.Equals(cmbProfiles.Text.Trim(), trimmedProfileName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _suppressUiUpdates = true;
        try
        {
            cmbProfiles.Text = trimmedProfileName;
        }
        finally
        {
            _suppressUiUpdates = false;
        }

        if (logChange)
        {
            Log($"\u5df2\u6309\u5171\u4eab\u4ed3\u9ed8\u8ba4\u6620\u5c04\u5207\u6362\u5230\u8d26\u53f7\uff1a{trimmedProfileName}");
        }

        return true;
    }

    private void AutoSyncConfigChangesChanged(object? sender, EventArgs e)
    {
        if (_suppressUiUpdates)
        {
            return;
        }

        SaveCurrentPaths();
        RefreshConfigWatchers();
        RefreshStatuses();
    }

    private void SaveCurrentPaths()
    {
        if (_suppressUiUpdates)
        {
            return;
        }

        try
        {
            _profileStore.SaveSharedStoreDefaultLaunchProfiles(_sharedStoreDefaultLaunchProfiles);
        }
        catch
        {
            // Best effort persistence. UI settings should still save even if SQLite persistence fails.
        }

        _settingsStore.Save(new AppPathSettings
        {
            StateHome = txtStateHome.Text.Trim(),
            AuthHome = txtAuthHome.Text.Trim(),
            ProfilesRoot = txtProfilesRoot.Text.Trim(),
            SelectedProfile = cmbProfiles.Text.Trim(),
            DefaultLaunchProfile = _defaultLaunchProfileName,
            SharedStoreDefaultLaunchProfiles = new Dictionary<string, string>(_sharedStoreDefaultLaunchProfiles, StringComparer.OrdinalIgnoreCase),
            SharedStoreHome = txtSharedStoreHome.Text.Trim(),
            TargetHome = txtTargetHome.Text.Trim(),
            AppExePath = txtAppExe.Text.Trim(),
            AutoSyncConfigChanges = chkAutoSyncConfigChanges.Checked
        });
    }

    private async void btnLoadSessions_Click(object sender, EventArgs e)
    {
        await LoadSessionsIntoGridAsync(txtStateHome.Text.Trim(), "\u6e90\u4f1a\u8bdd");
    }

    private async void btnLoadSharedSessions_Click(object sender, EventArgs e)
    {
        await LoadSessionsIntoGridAsync(txtSharedStoreHome.Text.Trim(), "\u5171\u4eab\u4ed3\u4f1a\u8bdd");
    }

    private async Task LoadSessionsIntoGridAsync(string home, string scopeLabel)
    {
        var sessions = await RunUiActionAsync($"\u6b63\u5728\u52a0\u8f7d {scopeLabel}", () => _manager.LoadSessions(home));
        if (sessions is null)
        {
            return;
        }

        _currentSessionScope = scopeLabel;
        ReplaceSessions(sessions);
        UpdateSessionCountLabel();
        Log($"\u5df2\u4ece {home} \u52a0\u8f7d {_sessions.Count} \u6761{scopeLabel}\u3002");
    }

    private async void btnPrepareHome_Click(object sender, EventArgs e)
    {
        var authHome = NullIfWhiteSpace(txtAuthHome.Text);
        var sharedStoreHome = txtSharedStoreHome.Text.Trim();
        var runtimeHome = txtTargetHome.Text.Trim();
        var overwriteRuntimeConfig = chkOverwriteTarget.Checked;

        var success = await RunUiActionAsync("\u6b63\u5728\u51c6\u5907\u5171\u4eab\u5e03\u5c40", () =>
        {
            _manager.PrepareSharedWorkspace(authHome, sharedStoreHome, runtimeHome, overwriteRuntimeConfig);
            return true;
        });

        if (success == true)
        {
            Log($"\u5df2\u51c6\u5907\u5171\u4eab\u4ed3\uff1a{sharedStoreHome}");
            Log($"\u5df2\u51c6\u5907\u8fd0\u884c\u76ee\u5f55\uff1a{runtimeHome}");
        }
    }

    private async void btnImportSelected_Click(object sender, EventArgs e)
    {
        if (sessionsGrid.CurrentRow?.DataBoundItem is not SessionRecord selected)
        {
            MessageBox.Show(this, "\u8bf7\u5148\u9009\u62e9\u4e00\u6761\u4f1a\u8bdd\u3002", "\u672a\u9009\u62e9\u4f1a\u8bdd", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var sourceHome = txtStateHome.Text.Trim();
        var sharedStoreHome = txtSharedStoreHome.Text.Trim();
        var refreshUpdatedAt = chkRefreshUpdatedAt.Checked;
        var addWorkspaceHint = chkAddWorkspaceHint.Checked;

        var importedSession = await RunUiActionAsync($"\u6b63\u5728\u5bfc\u5165\u4f1a\u8bdd {selected.Id}", () =>
            _manager.ImportSessionToSharedStoreOnly(sourceHome, sharedStoreHome, selected.Id, refreshUpdatedAt, addWorkspaceHint));

        if (importedSession is not null)
        {
            Log($"\u5df2\u5bfc\u5165\u5230\u5171\u4eab\u4ed3\uff1a{importedSession.Id}");
            Log($"\u5171\u4eab\u4ed3\u63d0\u4f9b\u65b9\uff1a{DisplayProvider(importedSession.ModelProvider)}");
            Log("\u6279\u91cf\u5bfc\u5165\u65f6\u4e0d\u518d\u81ea\u52a8\u540c\u6b65\u8fd0\u884c\u76ee\u5f55\u3002\u5168\u90e8\u5bfc\u5165\u5b8c\u6210\u540e\uff0c\u518d\u70b9\u201c\u540c\u6b65\u8fd0\u884c\u76ee\u5f55\u201d\u6216\u201c\u5207\u6362\u5e76\u542f\u52a8\u201d\u3002");
        }
    }
    private async void btnRepairTargetHome_Click(object sender, EventArgs e)
    {
        var sharedStoreHome = txtSharedStoreHome.Text.Trim();
        var authHome = NullIfWhiteSpace(txtAuthHome.Text);
        var runtimeHome = txtTargetHome.Text.Trim();
        var overwriteRuntimeConfig = chkOverwriteTarget.Checked;

        var result = await RunSerializedRuntimeUiActionAsync("\u6b63\u5728\u540c\u6b65\u8fd0\u884c\u76ee\u5f55", () =>
            _manager.SyncRuntimeHome(sharedStoreHome, authHome, runtimeHome, overwriteRuntimeConfig));

        if (result is not null)
        {
            Log($"\u8fd0\u884c\u76ee\u5f55\u5df2\u540c\u6b65\uff1a{runtimeHome}");
            Log($"\u8fd0\u884c\u63d0\u4f9b\u65b9\uff1a{DisplayProvider(result.EffectiveProvider)}");
            Log($"\u8fd0\u884c\u4f1a\u8bdd\u6570\uff1a{result.SessionCount}");
        }
    }

    private void btnRefreshProfiles_Click(object sender, EventArgs e)
    {
        var selected = cmbProfiles.Text.Trim();
        RefreshProfileList(selectName: selected, preserveTypedSelection: true);
        Log($"\u5df2\u5237\u65b0\u914d\u7f6e\u6863\u5217\u8868\u3002\u6570\u91cf={_profiles.Count}");
    }

    private async void btnSaveProfile_Click(object sender, EventArgs e)
    {
        var profilesRoot = txtProfilesRoot.Text.Trim();
        var profileName = cmbProfiles.Text.Trim();
        var sourceHome = ResolveProfileSourceHome();
        if (string.IsNullOrWhiteSpace(profileName))
        {
            MessageBox.Show(this, "\u8bf7\u5148\u8f93\u5165\u914d\u7f6e\u6863\u540d\u79f0\u3002", "\u9700\u8981\u914d\u7f6e\u6863\u540d\u79f0", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var profile = await RunUiActionAsync($"\u6b63\u5728\u4fdd\u5b58\u914d\u7f6e\u6863 {profileName}", () =>
            _profileStore.SaveProfile(profilesRoot, profileName, sourceHome, overwrite: true));

        if (profile is not null)
        {
            RefreshProfileList(selectName: profile.Name, preserveTypedSelection: false);
            Log($"\u5df2\u4fdd\u5b58\u914d\u7f6e\u6863\uff1a{profile.Name}");
            Log($"\u914d\u7f6e\u6863\u63d0\u4f9b\u65b9\uff1a{DisplayProvider(profile.ModelProvider)}");
        }
    }

    private async void btnCreateEmptyProfile_Click(object sender, EventArgs e)
    {
        using var dialog = new AccountNamePromptDialog(
            "新建空账号",
            "请输入新账号名称。",
            "创建",
            cmbProfiles.Text.Trim(),
            "新账号内容只保存在 SQLite 数据库中；切换、导出或启动时才会落地到目录。");
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var profile = await RunUiActionAsync($"正在新建空账号 {dialog.AccountName}", () =>
            _profileStore.CreateEmptyProfile(dialog.AccountName));
        if (profile is null)
        {
            return;
        }

        RefreshProfileList(selectName: profile.Name, preserveTypedSelection: false);
        SetActiveProfileSelection(profile);
        Log($"已新建空账号：{profile.Name}");
    }

    private async void btnRenameProfile_Click(object sender, EventArgs e)
    {
        var currentProfileName = cmbProfiles.Text.Trim();
        if (string.IsNullOrWhiteSpace(currentProfileName))
        {
            MessageBox.Show(this, "请先选择或输入要重命名的账号。", "缺少账号名称", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new AccountNamePromptDialog(
            "重命名账号",
            "请输入新的账号名称。",
            "重命名",
            currentProfileName,
            $"当前账号：{currentProfileName}");
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (string.Equals(currentProfileName, dialog.AccountName, StringComparison.Ordinal))
        {
            return;
        }

        var renamedProfile = await RunUiActionAsync($"正在重命名账号 {currentProfileName}", () =>
            _profileStore.RenameProfile(currentProfileName, dialog.AccountName));
        if (renamedProfile is null)
        {
            return;
        }

        if (string.Equals(_defaultLaunchProfileName, currentProfileName, StringComparison.OrdinalIgnoreCase))
        {
            _defaultLaunchProfileName = renamedProfile.Name;
        }

        _sharedStoreDefaultLaunchProfiles = _profileStore.LoadSharedStoreDefaultLaunchProfiles();
        RefreshProfileList(selectName: renamedProfile.Name, preserveTypedSelection: false);
        SetActiveProfileSelection(renamedProfile);
        SaveCurrentPaths();
        Log($"已重命名账号：{currentProfileName} -> {renamedProfile.Name}");
    }

    private async void btnDeleteProfile_Click(object sender, EventArgs e)
    {
        var profileName = cmbProfiles.Text.Trim();
        if (string.IsNullOrWhiteSpace(profileName))
        {
            MessageBox.Show(this, "请先选择或输入要删除的账号。", "缺少账号名称", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirmResult = MessageBox.Show(
            this,
            $"确定要删除账号“{profileName}”吗？{Environment.NewLine}{Environment.NewLine}删除后将从 SQLite 数据库中移除该账号，并清理对应的临时落地目录。",
            "确认删除账号",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmResult != DialogResult.OK)
        {
            return;
        }

        var existingProfile = await LoadProfileByNameAsync(profileName, $"正在检查账号 {profileName}");
        if (existingProfile is null)
        {
            return;
        }

        var clearActiveHome = SamePath(txtAuthHome.Text, existingProfile.DirectoryPath);
        var deleted = await RunUiActionAsync($"正在删除账号 {profileName}", () =>
        {
            _profileStore.DeleteProfile(profileName);
            return true;
        });
        if (deleted != true)
        {
            return;
        }

        if (string.Equals(_defaultLaunchProfileName, profileName, StringComparison.OrdinalIgnoreCase))
        {
            _defaultLaunchProfileName = string.Empty;
        }

        _sharedStoreDefaultLaunchProfiles = _profileStore.LoadSharedStoreDefaultLaunchProfiles();
        RefreshProfileList(selectName: string.Empty, preserveTypedSelection: false);
        if (clearActiveHome)
        {
            ClearActiveProfileSelection();
        }

        Log($"已删除账号：{profileName}");
    }

    private async void btnSetDefaultLaunchProfile_Click(object sender, EventArgs e)
    {
        var profile = await LoadSelectedProfileAsync();
        if (profile is null)
        {
            return;
        }

        SetCurrentDefaultLaunchProfileName(profile.Name);
        SaveCurrentPaths();
        RefreshStatuses();
        Log($"\u5df2\u8bbe\u7f6e\u9ed8\u8ba4\u542f\u52a8\u8d26\u53f7\uff1a{profile.Name}");
    }

    private async void btnLaunchDefaultProfile_Click(object sender, EventArgs e)
    {
        var profile = await LoadDefaultLaunchProfileAsync();
        if (profile is null)
        {
            return;
        }

        await ApplyProfileAsync(profile, launchAfterSync: true);
    }

    private async void btnEditProfileContents_Click(object sender, EventArgs e)
    {
        var profileName = cmbProfiles.Text.Trim();
        if (string.IsNullOrWhiteSpace(profileName))
        {
            MessageBox.Show(this, "\u8bf7\u5148\u5728\u4e0b\u62c9\u6846\u8f93\u5165\u6216\u9009\u62e9\u8d26\u53f7\u540d\u79f0\u3002", "\u7f3a\u5c11\u8d26\u53f7\u540d\u79f0", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var content = await RunUiActionAsync($"\u6b63\u5728\u52a0\u8f7d\u8d26\u53f7\u5185\u5bb9 {profileName}", () =>
            _profileStore.GetOrCreateProfileContent(profileName));
        if (content is null)
        {
            return;
        }

        using var dialog = new ProfileContentEditorDialog(content);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var savedContent = await RunUiActionAsync($"\u6b63\u5728\u4fdd\u5b58\u8d26\u53f7\u5185\u5bb9 {profileName}", () =>
            _profileStore.SaveProfileContent(profileName, dialog.AuthJson, dialog.ConfigToml));
        if (savedContent is null)
        {
            return;
        }

        RefreshProfileList(selectName: savedContent.Name, preserveTypedSelection: false);

        var materializedProfile = await RunUiActionAsync($"\u6b63\u5728\u5237\u65b0\u8d26\u53f7\u843d\u5730\u76ee\u5f55 {savedContent.Name}", () =>
            _profileStore.GetProfile(txtProfilesRoot.Text.Trim(), savedContent.Name));
        if (materializedProfile is not null)
        {
            SetActiveProfileSelection(materializedProfile);
        }

        Log($"\u5df2\u4fdd\u5b58\u8d26\u53f7\u5185\u5bb9\uff1a{savedContent.Name}");
        Log($"\u8d26\u53f7\u63d0\u4f9b\u65b9\uff1a{DisplayProvider(savedContent.ModelProvider)}");
    }

    private void btnManageSharedStoreDefaults_Click(object sender, EventArgs e)
    {
        using var dialog = new SharedStoreDefaultProfilesDialog(
            _sharedStoreDefaultLaunchProfiles,
            _profiles.Select(profile => profile.Name),
            txtSharedStoreHome.Text.Trim(),
            cmbProfiles.Text.Trim());

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _sharedStoreDefaultLaunchProfiles = new Dictionary<string, string>(dialog.ResultMappings, StringComparer.OrdinalIgnoreCase);
        ApplyCurrentSharedStoreDefaultProfileSelection(logChange: false);
        SaveCurrentPaths();
        RefreshStatuses();
        Log($"\u5df2\u66f4\u65b0\u5171\u4eab\u4ed3\u9ed8\u8ba4\u542f\u52a8\u8d26\u53f7\u6620\u5c04\uff0c\u5171 {_sharedStoreDefaultLaunchProfiles.Count} \u6761\u3002");
    }

    private async void btnImportProfile_Click(object sender, EventArgs e)
    {
        var importDirectory = SelectFolder(
            "\u9009\u62e9\u8981\u5bfc\u5165\u7684\u914d\u7f6e\u6863\u76ee\u5f55",
            txtProfilesRoot.Text,
            allowCreate: false);
        if (string.IsNullOrWhiteSpace(importDirectory))
        {
            return;
        }

        var typedName = cmbProfiles.Text.Trim();
        var profileName = string.IsNullOrWhiteSpace(typedName)
            ? Path.GetFileName(Path.GetFullPath(importDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : typedName;

        var profile = await RunUiActionAsync($"\u6b63\u5728\u5bfc\u5165\u914d\u7f6e\u6863 {profileName}", () =>
            _profileStore.ImportProfile(txtProfilesRoot.Text.Trim(), importDirectory, profileName, overwrite: true));

        if (profile is not null)
        {
            RefreshProfileList(selectName: profile.Name, preserveTypedSelection: false);
            Log($"\u5df2\u5bfc\u5165\u914d\u7f6e\u6863\uff1a{profile.Name}");
            Log($"\u5bfc\u5165\u6765\u6e90\uff1a{importDirectory}");
        }
    }

    private async void btnExportProfile_Click(object sender, EventArgs e)
    {
        var profile = await LoadSelectedProfileAsync();
        if (profile is null)
        {
            return;
        }

        var exportRoot = SelectFolder(
            "\u9009\u62e9\u5bfc\u51fa\u76ee\u6807\u76ee\u5f55",
            txtProfilesRoot.Text,
            allowCreate: true);
        if (string.IsNullOrWhiteSpace(exportRoot))
        {
            return;
        }

        var exportDirectory = await RunUiActionAsync($"\u6b63\u5728\u5bfc\u51fa\u914d\u7f6e\u6863 {profile.Name}", () =>
            _profileStore.ExportProfile(txtProfilesRoot.Text.Trim(), profile.Name, exportRoot, overwrite: true));

        if (!string.IsNullOrWhiteSpace(exportDirectory))
        {
            Log($"\u5df2\u5bfc\u51fa\u914d\u7f6e\u6863\uff1a{exportDirectory}");
        }
    }

    private async void btnApplyProfile_Click(object sender, EventArgs e)
    {
        var profile = await LoadSelectedProfileAsync();
        if (profile is null)
        {
            return;
        }

        await ApplyProfileAsync(profile, launchAfterSync: false);
    }

    private async void btnSwitchProfileAndLaunch_Click(object sender, EventArgs e)
    {
        var profile = await LoadSelectedProfileAsync();
        if (profile is null)
        {
            return;
        }

        await ApplyProfileAsync(profile, launchAfterSync: true);
    }

    private async Task<ProviderProfile?> LoadSelectedProfileAsync()
    {
        var profileName = cmbProfiles.Text.Trim();
        if (string.IsNullOrWhiteSpace(profileName))
        {
            MessageBox.Show(this, "\u8bf7\u5148\u9009\u62e9\u6216\u8f93\u5165\u914d\u7f6e\u6863\u3002", "\u9700\u8981\u914d\u7f6e\u6863", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }

        return await LoadProfileByNameAsync(profileName, $"\u6b63\u5728\u52a0\u8f7d\u914d\u7f6e\u6863 {profileName}");
    }

    private async Task<ProviderProfile?> LoadDefaultLaunchProfileAsync()
    {
        var defaultProfileName = GetCurrentDefaultLaunchProfileName();
        if (string.IsNullOrWhiteSpace(defaultProfileName))
        {
            MessageBox.Show(this, "\u8bf7\u5148\u4e3a\u5f53\u524d\u5171\u4eab\u4ed3\u8bbe\u7f6e\u9ed8\u8ba4\u542f\u52a8\u8d26\u53f7\u3002", "\u672a\u8bbe\u7f6e\u9ed8\u8ba4\u8d26\u53f7", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }

        return await LoadProfileByNameAsync(defaultProfileName, $"\u6b63\u5728\u52a0\u8f7d\u9ed8\u8ba4\u542f\u52a8\u8d26\u53f7 {defaultProfileName}");
    }

    private async Task<ProviderProfile?> LoadProfileByNameAsync(string profileName, string actionName)
    {
        var profilesRoot = txtProfilesRoot.Text.Trim();
        return await RunUiActionAsync(actionName, () =>
            _profileStore.GetProfile(profilesRoot, profileName));
    }

    private void SetActiveProfileSelection(ProviderProfile profile)
    {
        _ignoreWatchEventsUntilUtc = DateTime.UtcNow.AddSeconds(2);
        _suppressUiUpdates = true;
        try
        {
            txtAuthHome.Text = profile.DirectoryPath;
            cmbProfiles.Text = profile.Name;
        }
        finally
        {
            _suppressUiUpdates = false;
        }

        SaveCurrentPaths();
        RefreshConfigWatchers();
        RefreshStatuses();
    }

    private void ClearActiveProfileSelection()
    {
        _ignoreWatchEventsUntilUtc = DateTime.UtcNow.AddSeconds(2);
        _suppressUiUpdates = true;
        try
        {
            txtAuthHome.Text = string.Empty;
            cmbProfiles.Text = string.Empty;
        }
        finally
        {
            _suppressUiUpdates = false;
        }

        SaveCurrentPaths();
        RefreshConfigWatchers();
        RefreshStatuses();
    }

    private async Task ApplyProfileAsync(ProviderProfile profile, bool launchAfterSync)
    {
        SetActiveProfileSelection(profile);

        var sharedStoreHome = txtSharedStoreHome.Text.Trim();
        var runtimeHome = txtTargetHome.Text.Trim();
        if (Directory.Exists(sharedStoreHome))
        {
            if (launchAfterSync)
            {
                var appExe = RefreshCodexAppExecutablePath(logChange: true, forceLatestWindowsAppsPath: true);
                var result = await RunSerializedRuntimeUiActionAsync($"\u6b63\u5728\u5207\u6362\u8d26\u53f7\u5e76\u542f\u52a8 {profile.Name}", () =>
                    _manager.SyncAndLaunchCodexApp(sharedStoreHome, profile.DirectoryPath, runtimeHome, appExe));

                if (result is not null)
                {
                    Log($"\u5df2\u5207\u6362\u8d26\u53f7\u5e76\u542f\u52a8\uff1a{profile.Name}");
                    Log($"\u8fd0\u884c\u63d0\u4f9b\u65b9\uff1a{DisplayProvider(result.EffectiveProvider)}");
                    Log($"\u8fd0\u884c\u4f1a\u8bdd\u6570\uff1a{result.SessionCount}");
                }
            }
            else
            {
                var result = await RunSerializedRuntimeUiActionAsync($"\u6b63\u5728\u5e94\u7528\u914d\u7f6e\u6863 {profile.Name}", () =>
                    _manager.SyncRuntimeHome(sharedStoreHome, profile.DirectoryPath, runtimeHome, overwriteRuntimeConfig: true));

                if (result is not null)
                {
                    Log($"\u5df2\u5207\u6362\u8d26\u53f7\uff1a{profile.Name}");
                    Log($"\u8fd0\u884c\u63d0\u4f9b\u65b9\uff1a{DisplayProvider(result.EffectiveProvider)}");
                }
            }

            return;
        }

        if (!launchAfterSync)
        {
            Log($"\u914d\u7f6e\u6e90\u5df2\u5207\u6362\u4e3a\u914d\u7f6e\u6863\uff1a{profile.Name}");
            return;
        }

        var appExePath = RefreshCodexAppExecutablePath(logChange: true, forceLatestWindowsAppsPath: true);
        SuppressWatchTriggeredAutoSync();
        var launched = await RunSerializedRuntimeUiActionAsync($"\u6b63\u5728\u5207\u6362\u8d26\u53f7\u5e76\u542f\u52a8 {profile.Name}", () =>
        {
            Directory.CreateDirectory(runtimeHome);
            MirrorConfigFiles(profile.DirectoryPath, runtimeHome);
            _manager.LaunchCodexApp(runtimeHome, appExePath);
            return true;
        });

        if (launched == true)
        {
            Log($"\u5df2\u5207\u6362\u8d26\u53f7\u5e76\u542f\u52a8\uff1a{profile.Name}");
            Log("\u672a\u68c0\u6d4b\u5230\u5171\u4eab\u4ed3\uff0c\u4ec5\u66f4\u65b0\u4e86\u8fd0\u884c\u76ee\u5f55\u914d\u7f6e\u3002");
        }
    }

    private async void btnCloseApp_Click(object sender, EventArgs e)
    {
        var count = await RunUiActionAsync("\u6b63\u5728\u5173\u95ed Codex", () => (int?)_manager.CloseRunningCodexApp());
        if (count is not null)
        {
            Log($"\u5df2\u5173\u95ed {count} \u4e2a Codex \u8fdb\u7a0b\u3002");
        }
    }

    private async void btnLaunchApp_Click(object sender, EventArgs e)
    {
        var sharedStoreHome = txtSharedStoreHome.Text.Trim();
        var authHome = NullIfWhiteSpace(txtAuthHome.Text);
        var runtimeHome = txtTargetHome.Text.Trim();
        var appExe = RefreshCodexAppExecutablePath(logChange: true, forceLatestWindowsAppsPath: true);

        var result = await RunSerializedRuntimeUiActionAsync("\u6b63\u5728\u540c\u6b65\u5e76\u542f\u52a8 Codex", () =>
            _manager.SyncAndLaunchCodexApp(sharedStoreHome, authHome, runtimeHome, appExe));

        if (result is not null)
        {
            Log($"\u5df2\u4f7f\u7528\u8fd0\u884c\u76ee\u5f55\u542f\u52a8 Codex\uff1a{runtimeHome}");
            Log($"\u8fd0\u884c\u63d0\u4f9b\u65b9\uff1a{DisplayProvider(result.EffectiveProvider)}");
            Log($"\u8fd0\u884c\u4f1a\u8bdd\u6570\uff1a{result.SessionCount}");
        }
    }

    private void btnUseDefaults_Click(object sender, EventArgs e)
    {
        _suppressUiUpdates = true;
        try
        {
            txtStateHome.Text = _manager.DefaultCodexHome;
            txtProfilesRoot.Text = _profileStore.DefaultProfilesRoot;
            txtSharedStoreHome.Text = _manager.DefaultSharedStoreHome;

            if (string.IsNullOrWhiteSpace(txtAuthHome.Text))
            {
                txtAuthHome.Text = _manager.DefaultCodexHome;
            }

            if (string.IsNullOrWhiteSpace(txtTargetHome.Text))
            {
                txtTargetHome.Text = GetDefaultTargetHome();
            }

            txtAppExe.Text = _manager.FindCodexAppExecutable() ?? string.Empty;
            RefreshCodexAppExecutablePath(logChange: false, forceLatestWindowsAppsPath: true);
            if (cmbProfiles.Items.Count > 0 && string.IsNullOrWhiteSpace(cmbProfiles.Text))
            {
                var defaultProfileName = GetCurrentDefaultLaunchProfileName();
                cmbProfiles.Text = !string.IsNullOrWhiteSpace(defaultProfileName)
                    ? defaultProfileName
                    : cmbProfiles.Items[0]?.ToString() ?? string.Empty;
            }
        }
        finally
        {
            _suppressUiUpdates = false;
        }

        SaveCurrentPaths();
        RefreshProfileList(selectName: cmbProfiles.Text.Trim(), preserveTypedSelection: true);
        RefreshConfigWatchers();
        RefreshStatuses();
        Log("\u5df2\u586b\u5145\u9ed8\u8ba4\u503c\u3002");
    }

    private void btnBrowseState_Click(object sender, EventArgs e)
    {
        BrowseFolder(txtStateHome);
    }

    private void btnBrowseAuth_Click(object sender, EventArgs e)
    {
        BrowseFolder(txtAuthHome, allowCreate: true);
    }

    private void btnBrowseProfilesRoot_Click(object sender, EventArgs e)
    {
        BrowseFolder(txtProfilesRoot, allowCreate: true);
    }

    private void btnBrowseShared_Click(object sender, EventArgs e)
    {
        BrowseFolder(txtSharedStoreHome, allowCreate: true);
    }

    private void btnBrowseTarget_Click(object sender, EventArgs e)
    {
        BrowseFolder(txtTargetHome, allowCreate: true);
    }

    private void btnBrowseAppExe_Click(object sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "\u53ef\u6267\u884c\u6587\u4ef6 (*.exe)|*.exe|\u6240\u6709\u6587\u4ef6 (*.*)|*.*",
            CheckFileExists = true,
            FileName = txtAppExe.Text
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            txtAppExe.Text = dialog.FileName;
        }
    }

    private void sessionsGrid_SelectionChanged(object sender, EventArgs e)
    {
        if (sessionsGrid.CurrentRow?.DataBoundItem is not SessionRecord session)
        {
            txtSelectedId.Clear();
            txtSelectedTitle.Clear();
            txtSelectedCwd.Clear();
            txtSelectedPath.Clear();
            return;
        }

        txtSelectedId.Text = session.Id;
        txtSelectedTitle.Text = session.Title;
        txtSelectedCwd.Text = session.Cwd;
        txtSelectedPath.Text = session.SessionPath;
    }

    private void RefreshProfileList(string? selectName, bool preserveTypedSelection)
    {
        var profilesRoot = txtProfilesRoot.Text.Trim();
        var desiredSelection = !string.IsNullOrWhiteSpace(selectName)
            ? selectName.Trim()
            : preserveTypedSelection ? cmbProfiles.Text.Trim() : string.Empty;

        _profiles = _profileStore.ListProfiles(profilesRoot);

        _suppressUiUpdates = true;
        try
        {
            cmbProfiles.BeginUpdate();
            cmbProfiles.Items.Clear();
            foreach (var profile in _profiles)
            {
                cmbProfiles.Items.Add(profile.Name);
            }

            cmbProfiles.Text = desiredSelection;
        }
        finally
        {
            cmbProfiles.EndUpdate();
            _suppressUiUpdates = false;
        }

        SaveCurrentPaths();
        RefreshStatuses();
    }

    private void RefreshConfigWatchers()
    {
        DisposeConfigWatchers();

        if (!chkAutoSyncConfigChanges.Checked)
        {
            lblWatchStatus.Text = "\u76d1\u63a7\uff1a\u5173\u95ed";
            return;
        }

        var runtimeHome = NullIfWhiteSpace(txtTargetHome.Text);
        var authHome = NullIfWhiteSpace(txtAuthHome.Text);
        var watcherCount = 0;

        watcherCount += RegisterConfigWatcher(runtimeHome, "runtime");
        if (!SamePath(runtimeHome, authHome))
        {
            watcherCount += RegisterConfigWatcher(authHome, "source");
        }

        lblWatchStatus.Text = watcherCount == 0
            ? "\u76d1\u63a7\uff1a\u672a\u5c31\u7eea"
            : $"\u76d1\u63a7\uff1a\u5df2\u5f00\u542f ({watcherCount})";
    }

    private int RegisterConfigWatcher(string? home, string originLabel)
    {
        if (string.IsNullOrWhiteSpace(home) || !Directory.Exists(home))
        {
            return 0;
        }

        var watcher = new FileSystemWatcher(home)
        {
            Filter = "*.*",
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size
        };

        FileSystemEventHandler onChange = (_, args) => HandleConfigFileChanged(home, originLabel, args.Name);
        RenamedEventHandler onRename = (_, args) =>
        {
            HandleConfigFileChanged(home, originLabel, args.OldName);
            HandleConfigFileChanged(home, originLabel, args.Name);
        };
        watcher.Changed += onChange;
        watcher.Created += onChange;
        watcher.Deleted += onChange;
        watcher.Renamed += onRename;
        watcher.EnableRaisingEvents = true;
        _configWatchers.Add(watcher);
        return 1;
    }

    private void DisposeConfigWatchers()
    {
        foreach (var watcher in _configWatchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _configWatchers.Clear();
    }

    private void HandleConfigFileChanged(string watchedHome, string originLabel, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        if (!string.Equals(fileName, "auth.json", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fileName, "config.toml", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (DateTime.UtcNow < _ignoreWatchEventsUntilUtc)
        {
            return;
        }

        if (!IsHandleCreated || IsDisposed)
        {
            return;
        }

        BeginInvoke(new Action(() => QueueAutoSync(watchedHome, $"{originLabel}/{fileName}")));
    }

    private void QueueAutoSync(string configHome, string fileDescription)
    {
        if (!chkAutoSyncConfigChanges.Checked)
        {
            return;
        }

        _pendingConfigHome = configHome;
        _pendingConfigFile = fileDescription;
        _autoSyncPending = true;
        _lastConfigChangeUtc = DateTime.UtcNow;
        lblWatchStatus.Text = $"\u76d1\u63a7\uff1a\u5df2\u68c0\u6d4b {fileDescription}";
        Log($"\u5df2\u68c0\u6d4b\u5230\u914d\u7f6e\u53d8\u66f4\uff1a{fileDescription}\uff0c\u5df2\u52a0\u5165\u81ea\u52a8\u540c\u6b65\u3002");
    }

    private async void autoSyncTimer_Tick(object? sender, EventArgs e)
    {
        if (!_autoSyncPending || _autoSyncInProgress)
        {
            return;
        }

        if (DateTime.UtcNow - _lastConfigChangeUtc < TimeSpan.FromMilliseconds(900))
        {
            return;
        }

        var sharedStoreHome = NullIfWhiteSpace(txtSharedStoreHome.Text);
        var runtimeHome = NullIfWhiteSpace(txtTargetHome.Text);
        if (_manager.IsCodexAppRunning())
        {
            lblWatchStatus.Text = "\u76d1\u63a7\uff1a\u7b49\u5f85 Codex \u9000\u51fa";
            return;
        }

        _autoSyncPending = false;
        _autoSyncInProgress = true;
        lblWatchStatus.Text = "\u76d1\u63a7\uff1a\u81ea\u52a8\u540c\u6b65\u4e2d";

        try
        {
            var syncSourceHome = ResolveSyncSourceHomeForWatch();
            var persistedProfile = await Task.Run(() => PersistCurrentSelectedProfileFromHome(syncSourceHome));
            if (persistedProfile is not null)
            {
                RefreshProfileList(selectName: persistedProfile.Name, preserveTypedSelection: false);
                Log($"\u5df2\u5c06\u914d\u7f6e\u53d8\u66f4\u5199\u56de\u8d26\u53f7\u5e93\uff1a{persistedProfile.Name}");
            }

            if (string.IsNullOrWhiteSpace(sharedStoreHome) || string.IsNullOrWhiteSpace(runtimeHome) || !Directory.Exists(sharedStoreHome))
            {
                lblWatchStatus.Text = persistedProfile is null
                    ? "\u76d1\u63a7\uff1a\u5171\u4eab\u4ed3\u4e0d\u5b58\u5728"
                    : "\u76d1\u63a7\uff1a\u5df2\u5199\u56de\u8d26\u53f7\u5e93";
                return;
            }

            var result = await ExecuteSerializedRuntimeWorkAsync(
                () => _manager.SyncRuntimeHome(sharedStoreHome, syncSourceHome, runtimeHome, overwriteRuntimeConfig: true),
                suppressWatchEvents: false);
            lblWatchStatus.Text = $"\u76d1\u63a7\uff1a\u5df2\u540c\u6b65 ({DisplayProvider(result.EffectiveProvider)})";
            Log($"\u81ea\u52a8\u540c\u6b65\u5b8c\u6210\u3002\u63d0\u4f9b\u65b9={DisplayProvider(result.EffectiveProvider)}");
        }
        catch (Exception ex)
        {
            lblWatchStatus.Text = "\u76d1\u63a7\uff1a\u81ea\u52a8\u540c\u6b65\u5931\u8d25";
            Log($"\u81ea\u52a8\u540c\u6b65\u5931\u8d25\uff1a{ex.Message}");
        }
        finally
        {
            _autoSyncInProgress = false;
            RefreshStatuses();
        }
    }

    private string ResolveSyncSourceHomeForWatch()
    {
        var runtimeHome = txtTargetHome.Text.Trim();
        var authHome = NullIfWhiteSpace(txtAuthHome.Text);

        if (!string.IsNullOrWhiteSpace(_pendingConfigHome)
            && SamePath(_pendingConfigHome, runtimeHome)
            && !string.IsNullOrWhiteSpace(authHome)
            && !SamePath(authHome, runtimeHome))
        {
            _ignoreWatchEventsUntilUtc = DateTime.UtcNow.AddSeconds(2);
            MirrorConfigFiles(runtimeHome, authHome);
            return authHome;
        }

        if (!string.IsNullOrWhiteSpace(_pendingConfigHome) && Directory.Exists(_pendingConfigHome))
        {
            return _pendingConfigHome;
        }

        if (!string.IsNullOrWhiteSpace(authHome) && Directory.Exists(authHome))
        {
            return authHome;
        }

        return runtimeHome;
    }

    private ProviderProfile? PersistCurrentSelectedProfileFromHome(string sourceHome)
    {
        var profileName = cmbProfiles.Text.Trim();
        if (string.IsNullOrWhiteSpace(profileName)
            || string.IsNullOrWhiteSpace(sourceHome)
            || !Directory.Exists(sourceHome))
        {
            return null;
        }

        return _profileStore.UpdateProfileFromHome(profileName, sourceHome);
    }

    private void MirrorConfigFiles(string sourceHome, string targetHome)
    {
        SyncConfigFile(sourceHome, targetHome, "auth.json");
        SyncConfigFile(sourceHome, targetHome, "config.toml");
    }

    private static void SyncConfigFile(string sourceHome, string targetHome, string fileName)
    {
        var source = Path.Combine(sourceHome, fileName);
        var target = Path.Combine(targetHome, fileName);
        if (!File.Exists(source))
        {
            if (File.Exists(target))
            {
                File.Delete(target);
            }

            return;
        }

        Directory.CreateDirectory(targetHome);
        File.Copy(source, target, overwrite: true);
    }

    private string ResolveProfileSourceHome()
    {
        var authHome = NullIfWhiteSpace(txtAuthHome.Text);
        if (!string.IsNullOrWhiteSpace(authHome) && Directory.Exists(authHome))
        {
            return authHome;
        }

        return txtTargetHome.Text.Trim();
    }

    private string? SelectFolder(string description, string? initialDirectory, bool allowCreate)
    {
        using var dialog = new FolderBrowserDialog
        {
            UseDescriptionForTitle = true,
            Description = description,
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ShowNewFolderButton = allowCreate
        };

        return dialog.ShowDialog(this) == DialogResult.OK
            ? dialog.SelectedPath
            : null;
    }

    private void BrowseFolder(TextBox target, bool allowCreate = false)
    {
        var selectedPath = SelectFolder(
            allowCreate ? "\u9009\u62e9\u6216\u521b\u5efa\u6587\u4ef6\u5939" : "\u9009\u62e9\u6587\u4ef6\u5939",
            target.Text,
            allowCreate);
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            target.Text = selectedPath;
        }
    }

    private string? RefreshCodexAppExecutablePath(bool logChange, bool forceLatestWindowsAppsPath)
    {
        var configuredPath = NullIfWhiteSpace(txtAppExe.Text);
        var discoveredPath = _manager.FindCodexAppExecutable();
        var hasConfiguredPath = !string.IsNullOrWhiteSpace(configuredPath);
        var configuredExists = hasConfiguredPath && File.Exists(configuredPath);
        var discoveredExists = !string.IsNullOrWhiteSpace(discoveredPath) && File.Exists(discoveredPath);
        var shouldReplaceConfiguredPath = false;

        if (discoveredExists)
        {
            if (!configuredExists)
            {
                shouldReplaceConfiguredPath = true;
            }
            else if (forceLatestWindowsAppsPath && IsManagedWindowsAppsCodexPath(configuredPath))
            {
                shouldReplaceConfiguredPath = !string.Equals(configuredPath, discoveredPath, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (!shouldReplaceConfiguredPath)
        {
            return configuredExists ? configuredPath : discoveredExists ? discoveredPath : null;
        }

        var previousSuppressUiUpdates = _suppressUiUpdates;
        _suppressUiUpdates = true;
        try
        {
            txtAppExe.Text = discoveredPath!;
        }
        finally
        {
            _suppressUiUpdates = previousSuppressUiUpdates;
        }

        SaveCurrentPaths();
        if (logChange)
        {
            Log($"已自动更新 Codex 程序路径：{discoveredPath}");
        }

        return discoveredPath;
    }

    private static bool IsManagedWindowsAppsCodexPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return path.Contains("\\WindowsApps\\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase);
    }

    private void SuppressWatchTriggeredAutoSync(TimeSpan? duration = null)
    {
        _ignoreWatchEventsUntilUtc = DateTime.UtcNow.Add(duration ?? TimeSpan.FromSeconds(8));
        _pendingConfigHome = null;
        _pendingConfigFile = string.Empty;
        _autoSyncPending = false;
    }

    private async Task<T> ExecuteSerializedRuntimeWorkAsync<T>(Func<T> action, bool suppressWatchEvents)
    {
        await _runtimeSyncLock.WaitAsync();
        try
        {
            if (suppressWatchEvents)
            {
                SuppressWatchTriggeredAutoSync();
            }

            return await Task.Run(action);
        }
        finally
        {
            _runtimeSyncLock.Release();
        }
    }

    private async Task<T?> RunSerializedRuntimeUiActionAsync<T>(string actionName, Func<T> action, bool suppressWatchEvents = true)
    {
        ToggleBusyState(true, actionName);
        try
        {
            return await ExecuteSerializedRuntimeWorkAsync(action, suppressWatchEvents);
        }
        catch (Exception ex)
        {
            Log($"{actionName} \u5931\u8d25\uff1a{ex.Message}");
            MessageBox.Show(this, ex.Message, actionName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return default;
        }
        finally
        {
            ToggleBusyState(false, null);
            RefreshStatuses();
        }
    }

    private async Task<T?> RunUiActionAsync<T>(string actionName, Func<T> action)
    {
        ToggleBusyState(true, actionName);
        try
        {
            return await Task.Run(action);
        }
        catch (Exception ex)
        {
            Log($"{actionName} \u5931\u8d25\uff1a{ex.Message}");
            MessageBox.Show(this, ex.Message, actionName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return default;
        }
        finally
        {
            ToggleBusyState(false, null);
            RefreshStatuses();
        }
    }

    private void ReplaceSessions(IReadOnlyList<SessionRecord> sessions)
    {
        sessionsGrid.SuspendLayout();
        _sessions.RaiseListChangedEvents = false;
        try
        {
            _sessions.Clear();
            foreach (var session in sessions)
            {
                _sessions.Add(session);
            }
        }
        finally
        {
            _sessions.RaiseListChangedEvents = true;
            _sessions.ResetBindings();
            sessionsGrid.ResumeLayout();
        }

        UpdateSessionCountLabel();
    }

    private void UpdateSessionCountLabel()
    {
        lblSessionCount.Text = $"{_currentSessionScope}\uff1a{_sessions.Count}";
    }

    private void ToggleBusyState(bool isBusy, string? statusText)
    {
        btnUseDefaults.Enabled = !isBusy;
        btnLoadSessions.Enabled = !isBusy;
        btnLoadSharedSessions.Enabled = !isBusy;
        btnPrepareHome.Enabled = !isBusy;
        btnImportSelected.Enabled = !isBusy;
        btnRepairTargetHome.Enabled = !isBusy;
        btnRefreshProfiles.Enabled = !isBusy;
        btnSaveProfile.Enabled = !isBusy;
        btnCreateEmptyProfile.Enabled = !isBusy;
        btnRenameProfile.Enabled = !isBusy;
        btnDeleteProfile.Enabled = !isBusy;
        btnEditProfileContents.Enabled = !isBusy;
        btnSetDefaultLaunchProfile.Enabled = !isBusy;
        btnApplyProfile.Enabled = !isBusy;
        btnImportProfile.Enabled = !isBusy;
        btnExportProfile.Enabled = !isBusy;
        btnLaunchDefaultProfile.Enabled = !isBusy;
        btnManageSharedStoreDefaults.Enabled = !isBusy;
        btnCloseApp.Enabled = !isBusy;
        btnSwitchProfileAndLaunch.Enabled = !isBusy;
        btnLaunchApp.Enabled = !isBusy;
        lblStatus.Text = isBusy ? statusText ?? "\u5fd9\u788c" : "\u7a7a\u95f2";
    }

    private void RefreshStatuses()
    {
        lblAppStatus.Text = _manager.IsCodexAppRunning() ? "\u7a0b\u5e8f\uff1a\u8fd0\u884c\u4e2d" : "\u7a0b\u5e8f\uff1a\u5df2\u505c\u6b62";

        var provider = string.Empty;
        var runtimeHome = NullIfWhiteSpace(txtTargetHome.Text);
        if (!string.IsNullOrWhiteSpace(runtimeHome) && Directory.Exists(runtimeHome))
        {
            provider = _manager.GetEffectiveModelProvider(runtimeHome);
        }

        if (string.IsNullOrWhiteSpace(provider))
        {
            var authHome = NullIfWhiteSpace(txtAuthHome.Text);
            if (!string.IsNullOrWhiteSpace(authHome) && Directory.Exists(authHome))
            {
                provider = _manager.GetEffectiveModelProvider(authHome);
            }
        }

        lblProviderStatus.Text = $"\u63d0\u4f9b\u65b9\uff1a{DisplayProvider(provider)}";
        lblDefaultProfileStatus.Text = BuildDefaultLaunchProfileStatus();

        if (!chkAutoSyncConfigChanges.Checked)
        {
            lblWatchStatus.Text = "监控：关闭";
        }
        else if (_autoSyncInProgress)
        {
            lblWatchStatus.Text = "\u76d1\u63a7\uff1a\u81ea\u52a8\u540c\u6b65\u4e2d";
        }
        else if (_autoSyncPending)
        {
            lblWatchStatus.Text = "\u76d1\u63a7\uff1a\u5f85\u5904\u7406";
        }
        else if (_configWatchers.Count == 0)
        {
            lblWatchStatus.Text = "\u76d1\u63a7\uff1a\u672a\u5c31\u7eea";
        }
    }

    private void Log(string message)
    {
        txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    private static string FirstNonBlank(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
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

    private static Dictionary<string, string> NormalizeSharedStoreDefaultLaunchProfiles(Dictionary<string, string>? profiles)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (profiles is null)
        {
            return normalized;
        }

        foreach (var pair in profiles)
        {
            var storeKey = NormalizeStoreKey(pair.Key);
            var profileName = pair.Value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(storeKey) || string.IsNullOrWhiteSpace(profileName))
            {
                continue;
            }

            normalized[storeKey] = profileName;
        }

        return normalized;
    }

    private bool TryGetCurrentSharedStoreDefaultLaunchProfileName(out string profileName)
    {
        var storeKey = NormalizeStoreKey(txtSharedStoreHome.Text);
        if (!string.IsNullOrWhiteSpace(storeKey) &&
            _sharedStoreDefaultLaunchProfiles.TryGetValue(storeKey, out var sharedStoreProfileName) &&
            !string.IsNullOrWhiteSpace(sharedStoreProfileName))
        {
            profileName = sharedStoreProfileName.Trim();
            return true;
        }

        profileName = string.Empty;
        return false;
    }

    private string GetCurrentDefaultLaunchProfileName()
    {
        return TryGetCurrentSharedStoreDefaultLaunchProfileName(out var profileName)
            ? profileName
            : _defaultLaunchProfileName;
    }

    private void SetCurrentDefaultLaunchProfileName(string profileName)
    {
        var trimmedProfileName = profileName.Trim();
        _defaultLaunchProfileName = trimmedProfileName;

        var storeKey = NormalizeStoreKey(txtSharedStoreHome.Text);
        if (string.IsNullOrWhiteSpace(storeKey))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(trimmedProfileName))
        {
            _sharedStoreDefaultLaunchProfiles.Remove(storeKey);
            return;
        }

        _sharedStoreDefaultLaunchProfiles[storeKey] = trimmedProfileName;
    }

    private string BuildDefaultLaunchProfileStatus()
    {
        var hasSharedStoreDefault = TryGetCurrentSharedStoreDefaultLaunchProfileName(out var defaultProfileName);
        if (!hasSharedStoreDefault)
        {
            defaultProfileName = _defaultLaunchProfileName;
        }

        if (string.IsNullOrWhiteSpace(defaultProfileName))
        {
            return "\u9ed8\u8ba4\u8d26\u53f7\uff1a\u672a\u8bbe\u7f6e";
        }

        var sourceLabel = hasSharedStoreDefault
            ? "\u5171\u4eab\u4ed3"
            : "\u5168\u5c40";
        var profile = _profiles.FirstOrDefault(item =>
            string.Equals(item.Name, defaultProfileName, StringComparison.OrdinalIgnoreCase));
        if (profile is not null)
        {
            return $"\u9ed8\u8ba4\u8d26\u53f7\uff1a{profile.Name} | {DisplayProvider(profile.ModelProvider)} | {sourceLabel}";
        }

        return $"\u9ed8\u8ba4\u8d26\u53f7\uff1a{defaultProfileName} | \u672a\u627e\u5230 | {sourceLabel}";
    }

    private static string DisplayProvider(string? provider)
    {
        return string.IsNullOrWhiteSpace(provider) ? "\u672a\u8bbe\u7f6e" : provider.Trim();
    }

    private static string GetDefaultTargetHome()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex-runtime");
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var normalizedLeft = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRight = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }
}
















