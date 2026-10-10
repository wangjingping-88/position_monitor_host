using System.Diagnostics;
using System.IO;
using System.Windows;
using PositionMonitor.Update;

namespace PositionMonitor.App;

public partial class UpdateWindow : Window
{
    private readonly MainWindow _main;
    private readonly ApplicationBuildInfo _build = ApplicationBuildInfo.FromAssembly(typeof(App).Assembly);
    private readonly UpdateService _service;
    private readonly CancellationTokenSource _cancel = new();
    private UpdateReleaseInfo? _release;
    private bool _working, _disposed;

    public UpdateWindow(MainWindow main)
    {
        _main = main;
        _service = new(UpdatePaths.DefaultUpdateRoot, _build.InstallDirectory, _build.Version);
        InitializeComponent();
        VersionText.Text = $"当前版本 {_build.DisplayVersion}";
        ReleaseText.Text = "更新完成后自动重启，保留连接设置。";
        Closed += (_, _) => { _cancel.Cancel(); if (!_working) DisposeService(); };
    }

    public async Task CheckAsync()
    {
        if (_working) return;
        SetWorking(true, false);
        _release = null;
        StatusText.Text = "正在检查 GitHub 正式版…";
        try
        {
            var result = await _service.CheckForUpdatesAsync(true, _cancel.Token);
            _release = result.Release;
            if (_release is { } release)
            {
                VersionText.Text = $"当前 {_build.DisplayVersion}  →  新版 v{release.Version}";
                ReleaseText.Text = $"发布时间：{release.PublishedAt?.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm") ?? "未知"} · 包大小 {Bytes(release.PackageAsset.Size)}";
                NotesText.Text = release.ReleaseNotes;
                StatusText.Text = _service.CanInstallInPlace(_build.InstallDirectory, out var reason) ? "发现新版本，可以下载并安装。" : reason;
            }
            else
            {
                VersionText.Text = $"当前版本 {_build.DisplayVersion}";
                ReleaseText.Text = "更新完成后自动重启，保留连接设置。";
                StatusText.Text = result.Status == UpdateCheckStatus.NoUpdate ? "当前已是最新正式版。" : $"检查未完成：{result.ErrorMessage}";
                NotesText.Text = "安装新版本前会校验文件，并在新版启动失败时自动恢复旧版。";
            }
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消检查。"; }
        catch (Exception error) { StatusText.Text = $"检查失败：{error.Message}"; }
        finally { SetWorking(false, false); if (!IsVisible) DisposeService(); }
    }

    private void SetWorking(bool working, bool installing)
    {
        _working = working;
        CheckButton.IsEnabled = !working;
        InstallButton.IsEnabled = !working && _release is not null && _service.CanInstallInPlace(_build.InstallDirectory, out _);
        CancelButton.Content = working ? "取消" : "关闭";
        DownloadProgress.Visibility = installing ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void CheckClick(object sender, RoutedEventArgs e) => await CheckAsync();
    private void CancelClick(object sender, RoutedEventArgs e) => Close();
    private void ReleaseClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo((_release?.ReleasePageUri ?? _service.ReleasesPageUri).AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) { StatusText.Text = $"发布页未打开：{error.Message}"; }
    }

    private async void InstallClick(object sender, RoutedEventArgs e)
    {
        if (_working || _release is null) return;
        if (!_main.Model.BeginApplicationUpdate(out var reason)) { StatusText.Text = reason; return; }
        SetWorking(true, true);
        PreparedUpdate? prepared = null;
        var launched = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            var progress = new Progress<UpdateDownloadProgress>(value =>
            {
                DownloadProgress.Value = value.Percent ?? 0;
                StatusText.Text = $"{value.Stage} · {Bytes(value.BytesReceived)} / {Bytes(value.TotalBytes ?? 0)}";
            });
            prepared = await _service.DownloadAndPrepareAsync(_release, progress, timeout.Token);
            StatusText.Text = "校验完成，正在断开 MQTT 并重启安装…";
            await _main.Model.StopSessionAsync();
            timeout.Token.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(prepared.UpdaterExecutablePath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(prepared.UpdaterExecutablePath)! };
            start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(UpdatePaths.DefaultUpdateRoot, "runtime", "native");
            start.ArgumentList.Add("--job"); start.ArgumentList.Add(prepared.JobFilePath);
            using var updater = Process.Start(start) ?? throw new InvalidOperationException("更新器未启动。");
            launched = true;
            _main.Close();
        }
        catch (OperationCanceledException) { StatusText.Text = "下载已取消或超时，当前版本保持不变。"; }
        catch (Exception error) { StatusText.Text = $"更新未完成：{error.Message}"; }
        finally
        {
            if (!launched)
            {
                if (prepared is not null)
                {
                    try { _service.CancelPreparedUpdate(prepared); }
                    catch (Exception error) { StatusText.Text += $" 暂存清理失败：{error.Message}"; }
                }
                _main.Model.CancelApplicationUpdate();
                SetWorking(false, false);
            }
            else _working = false;
            if (!IsVisible) DisposeService();
        }
    }
    private void DisposeService() { if (_disposed) return; _disposed = true; _service.Dispose(); _cancel.Dispose(); }
    private static string Bytes(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1048576d:0.0} MB" : $"{bytes / 1024d:0.0} KB";
}
