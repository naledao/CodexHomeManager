using System.Drawing.Drawing2D;

namespace CodexHomeManager;

internal sealed class SplashForm : Form
{
    private readonly Label _titleLabel = new();
    private readonly Label _subtitleLabel = new();
    private readonly Label _statusLabel = new();
    private readonly Label _hintLabel = new();
    private readonly Label _badgeLabel = new();
    private readonly Label _summaryLabel = new();
    private readonly PictureBox _iconBox = new();
    private readonly Panel _cardPanel = new();
    private readonly Panel _progressTrack = new();
    private readonly Panel _progressFill = new();
    private readonly TableLayoutPanel _rootLayout = new();
    private readonly TableLayoutPanel _headerLayout = new();
    private readonly TableLayoutPanel _headerTextLayout = new();
    private readonly TableLayoutPanel _footerLayout = new();
    private readonly System.Windows.Forms.Timer _progressTimer = new();
    private readonly System.Windows.Forms.Timer _fadeTimer = new();

    private float _targetProgress;
    private float _displayProgress;
    private bool _isFadingOut;
    private TaskCompletionSource<bool>? _closeTcs;

    public SplashForm()
    {
        AutoScaleMode = AutoScaleMode.Font;
        BackColor = Color.FromArgb(236, 242, 248);
        ClientSize = new Size(920, 500);
        DoubleBuffered = true;
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        MinimumSize = new Size(820, 460);
        Name = "SplashForm";
        Opacity = 0D;
        Padding = new Padding(22);
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;

        ConfigureLayout();
        ConfigureTimers();
        UpdateStatus("正在准备启动环境...", 0.12f);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyRoundedRegion();
        AdjustAdaptiveLayout();
        _fadeTimer.Start();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyRoundedRegion();
        AdjustAdaptiveLayout();
        UpdateProgressVisual();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var brush = new LinearGradientBrush(
            ClientRectangle,
            Color.FromArgb(240, 246, 252),
            Color.FromArgb(220, 233, 244),
            LinearGradientMode.ForwardDiagonal);
        e.Graphics.FillRectangle(brush, ClientRectangle);

        using var glowBrush = new SolidBrush(Color.FromArgb(52, 15, 118, 110));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillEllipse(
            glowBrush,
            new Rectangle(42, 32, Math.Max(180, Width / 4), Math.Max(180, Width / 4)));
        e.Graphics.FillEllipse(
            glowBrush,
            new Rectangle(
                Math.Max(Width - 240, Width - Width / 3),
                Math.Max(Height - 220, Height - Height / 3),
                Math.Max(160, Width / 5),
                Math.Max(160, Width / 5)));
    }

    internal void UpdateStatus(string status, float progress)
    {
        _statusLabel.Text = status;
        _targetProgress = Math.Clamp(progress, 0F, 1F);
        if (!_progressTimer.Enabled)
        {
            _progressTimer.Start();
        }
    }

    internal Task PlayCloseAnimationAsync()
    {
        if (_isFadingOut)
        {
            return _closeTcs?.Task ?? Task.CompletedTask;
        }

        _isFadingOut = true;
        _closeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_fadeTimer.Enabled)
        {
            _fadeTimer.Start();
        }

        return _closeTcs.Task;
    }

    private void ConfigureLayout()
    {
        _cardPanel.BackColor = Color.FromArgb(248, 251, 255);
        _cardPanel.Dock = DockStyle.Fill;
        _cardPanel.Padding = new Padding(38, 34, 38, 34);
        Controls.Add(_cardPanel);

        _rootLayout.ColumnCount = 1;
        _rootLayout.RowCount = 5;
        _rootLayout.Dock = DockStyle.Fill;
        _rootLayout.BackColor = Color.Transparent;
        _rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _cardPanel.Controls.Add(_rootLayout);

        _headerLayout.ColumnCount = 2;
        _headerLayout.RowCount = 1;
        _headerLayout.Dock = DockStyle.Top;
        _headerLayout.Margin = Padding.Empty;
        _headerLayout.Padding = Padding.Empty;
        _headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110F));
        _headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        _iconBox.BackColor = Color.Transparent;
        _iconBox.Dock = DockStyle.Fill;
        _iconBox.Margin = Padding.Empty;
        _iconBox.Padding = new Padding(0, 4, 0, 0);
        _iconBox.Size = new Size(88, 88);
        _iconBox.SizeMode = PictureBoxSizeMode.Zoom;
        _iconBox.Image = BuildIconImage();

        _headerTextLayout.ColumnCount = 1;
        _headerTextLayout.RowCount = 2;
        _headerTextLayout.Dock = DockStyle.Fill;
        _headerTextLayout.Margin = Padding.Empty;
        _headerTextLayout.Padding = new Padding(0, 2, 0, 0);
        _headerTextLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _headerTextLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _titleLabel.AutoSize = true;
        _titleLabel.Font = new Font("Microsoft YaHei UI", 30F, FontStyle.Bold, GraphicsUnit.Point);
        _titleLabel.ForeColor = Color.FromArgb(20, 35, 52);
        _titleLabel.Margin = Padding.Empty;
        _titleLabel.Text = "Codex Home Manager";

        _subtitleLabel.AutoSize = true;
        _subtitleLabel.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Regular, GraphicsUnit.Point);
        _subtitleLabel.ForeColor = Color.FromArgb(74, 96, 119);
        _subtitleLabel.Margin = new Padding(0, 10, 0, 0);
        _subtitleLabel.Text = "共享会话、账号切换与运行目录管理";

        _headerTextLayout.Controls.Add(_titleLabel, 0, 0);
        _headerTextLayout.Controls.Add(_subtitleLabel, 0, 1);
        _headerLayout.Controls.Add(_iconBox, 0, 0);
        _headerLayout.Controls.Add(_headerTextLayout, 1, 0);

        _badgeLabel.AutoSize = true;
        _badgeLabel.BackColor = Color.FromArgb(223, 242, 239);
        _badgeLabel.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold, GraphicsUnit.Point);
        _badgeLabel.ForeColor = Color.FromArgb(15, 118, 110);
        _badgeLabel.Margin = new Padding(0, 26, 0, 0);
        _badgeLabel.Padding = new Padding(14, 7, 14, 7);
        _badgeLabel.Text = "启动过渡页";

        _summaryLabel.AutoSize = true;
        _summaryLabel.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Regular, GraphicsUnit.Point);
        _summaryLabel.ForeColor = Color.FromArgb(65, 82, 102);
        _summaryLabel.Margin = new Padding(0, 18, 0, 0);
        _summaryLabel.Text = "程序会先准备本地设置、账号物化目录和主界面布局，然后再进入正式界面。";

        _footerLayout.ColumnCount = 1;
        _footerLayout.RowCount = 3;
        _footerLayout.Dock = DockStyle.Fill;
        _footerLayout.Margin = Padding.Empty;
        _footerLayout.Padding = Padding.Empty;
        _footerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _footerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _footerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _statusLabel.AutoSize = true;
        _statusLabel.Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold, GraphicsUnit.Point);
        _statusLabel.ForeColor = Color.FromArgb(20, 35, 52);
        _statusLabel.Margin = new Padding(0, 0, 0, 6);

        _hintLabel.AutoSize = true;
        _hintLabel.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
        _hintLabel.ForeColor = Color.FromArgb(90, 111, 130);
        _hintLabel.Margin = new Padding(0, 0, 0, 16);
        _hintLabel.Text = "首次启动或账号较多时，准备时间会稍长一些。";

        _progressTrack.BackColor = Color.FromArgb(222, 231, 240);
        _progressTrack.Dock = DockStyle.Top;
        _progressTrack.Height = 12;
        _progressTrack.Margin = Padding.Empty;

        _progressFill.BackColor = Color.FromArgb(15, 118, 110);
        _progressFill.Location = new Point(0, 0);
        _progressFill.Size = new Size(0, _progressTrack.Height);
        _progressTrack.Controls.Add(_progressFill);

        _footerLayout.Controls.Add(_statusLabel, 0, 0);
        _footerLayout.Controls.Add(_hintLabel, 0, 1);
        _footerLayout.Controls.Add(_progressTrack, 0, 2);

        _rootLayout.Controls.Add(_headerLayout, 0, 0);
        _rootLayout.Controls.Add(_badgeLabel, 0, 1);
        _rootLayout.Controls.Add(_summaryLabel, 0, 2);
        _rootLayout.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent }, 0, 3);
        _rootLayout.Controls.Add(_footerLayout, 0, 4);
    }

    private void ConfigureTimers()
    {
        _progressTimer.Interval = 16;
        _progressTimer.Tick += (_, _) =>
        {
            var delta = _targetProgress - _displayProgress;
            if (Math.Abs(delta) < 0.01F)
            {
                _displayProgress = _targetProgress;
                UpdateProgressVisual();
                _progressTimer.Stop();
                return;
            }

            _displayProgress += delta * 0.18F;
            UpdateProgressVisual();
        };

        _fadeTimer.Interval = 16;
        _fadeTimer.Tick += (_, _) =>
        {
            if (_isFadingOut)
            {
                Opacity = Math.Max(0D, Opacity - 0.12D);
                if (Opacity <= 0.02D)
                {
                    Opacity = 0D;
                    _fadeTimer.Stop();
                    _closeTcs?.TrySetResult(true);
                }

                return;
            }

            Opacity = Math.Min(1D, Opacity + 0.12D);
            if (Opacity >= 0.98D)
            {
                Opacity = 1D;
                _fadeTimer.Stop();
            }
        };
    }

    private void AdjustAdaptiveLayout()
    {
        var textWidth = Math.Max(420, _headerTextLayout.ClientSize.Width - 6);
        var contentWidth = Math.Max(520, _cardPanel.ClientSize.Width - _cardPanel.Padding.Horizontal);

        _subtitleLabel.MaximumSize = new Size(textWidth, 0);
        _summaryLabel.MaximumSize = new Size(contentWidth, 0);
        _hintLabel.MaximumSize = new Size(contentWidth, 0);

        using var titleFont = CreateBestFitTitleFont(textWidth);
        _titleLabel.Font = (Font)titleFont.Clone();
        _titleLabel.MaximumSize = new Size(textWidth, 0);
    }

    private Font CreateBestFitTitleFont(int availableWidth)
    {
        for (var size = 30F; size >= 22F; size -= 1F)
        {
            var font = new Font("Microsoft YaHei UI", size, FontStyle.Bold, GraphicsUnit.Point);
            var measured = TextRenderer.MeasureText(
                _titleLabel.Text,
                font,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            if (measured.Width <= availableWidth)
            {
                return font;
            }

            font.Dispose();
        }

        return new Font("Microsoft YaHei UI", 22F, FontStyle.Bold, GraphicsUnit.Point);
    }

    private void UpdateProgressVisual()
    {
        var width = Math.Max(12, (int)Math.Round(_progressTrack.Width * _displayProgress));
        _progressFill.Width = Math.Min(_progressTrack.Width, width);
    }

    private void ApplyRoundedRegion()
    {
        using var path = new GraphicsPath();
        path.AddArc(0, 0, 26, 26, 180, 90);
        path.AddArc(Width - 27, 0, 26, 26, 270, 90);
        path.AddArc(Width - 27, Height - 27, 26, 26, 0, 90);
        path.AddArc(0, Height - 27, 26, 26, 90, 90);
        path.CloseFigure();
        Region = new Region(path);
    }

    private static Image BuildIconImage()
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (icon is not null)
            {
                return icon.ToBitmap();
            }
        }
        catch
        {
            // Fall back to a generated glyph.
        }

        var bitmap = new Bitmap(88, 88);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var baseBrush = new SolidBrush(Color.FromArgb(15, 118, 110));
        using var houseBrush = new SolidBrush(Color.FromArgb(245, 247, 250));
        graphics.FillEllipse(baseBrush, 0, 0, 88, 88);
        graphics.FillPolygon(houseBrush, [new Point(18, 42), new Point(44, 18), new Point(70, 42)]);
        graphics.FillRectangle(houseBrush, 24, 40, 40, 28);
        graphics.FillRectangle(baseBrush, 34, 49, 9, 19);
        graphics.FillRectangle(baseBrush, 47, 49, 9, 19);
        return bitmap;
    }
}
