namespace CodexHomeManager;

internal sealed class StartupApplicationContext : ApplicationContext
{
    private readonly SplashForm _splashForm = new();
    private Form1? _mainForm;

    public StartupApplicationContext()
    {
        _splashForm.Shown += SplashForm_Shown;
        _splashForm.FormClosed += SplashForm_FormClosed;
        _splashForm.Show();
    }

    private async void SplashForm_Shown(object? sender, EventArgs e)
    {
        _splashForm.Shown -= SplashForm_Shown;
        await RunStartupSequenceAsync();
    }

    private void SplashForm_FormClosed(object? sender, FormClosedEventArgs e)
    {
        if (_mainForm is null || _mainForm.IsDisposed)
        {
            ExitThread();
        }
    }

    private async Task RunStartupSequenceAsync()
    {
        try
        {
            _splashForm.UpdateStatus("正在检查界面资源...", 0.18f);
            await Task.Delay(180);

            _splashForm.UpdateStatus("正在加载本地设置...", 0.42f);
            await Task.Delay(180);

            _splashForm.UpdateStatus("正在准备主界面...", 0.68f);
            await Task.Yield();

            _mainForm = new Form1();
            _mainForm.PrepareForFirstShow();
            _mainForm.FormClosed += (_, _) => ExitThread();

            _splashForm.UpdateStatus("正在进入主界面...", 0.92f);
            await Task.Delay(140);

            MainForm = _mainForm;
            _mainForm.Show();

            _splashForm.UpdateStatus("启动完成", 1.0f);
            await Task.Delay(120);
            await _splashForm.PlayCloseAnimationAsync();
            _splashForm.Close();
        }
        catch (Exception ex)
        {
            _splashForm.Hide();
            MessageBox.Show(
                $"启动 Codex Home Manager 时发生错误：{Environment.NewLine}{ex.Message}",
                "启动失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            ExitThread();
        }
    }
}
