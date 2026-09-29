using System.Windows;
using ScreenshotApp.Settings;

namespace ScreenshotApp.DesktopPet;

public partial class DesktopPetWindow
{
    private PetInjectionWindow? _injectionEditor, _injectionProgress;
    private CancellationTokenSource? _injectionCancellation;
    private string? _injectionState;

    private void ShowInjectionEditor()
    {
        CloseCommandExperience();
        if (_injectionCancellation is not null || HasAnyActiveTransfer || _activeDueAlarm) return;
        if (_injectionEditor is not null) { _injectionEditor.Activate(); return; }
        var editor = new PetInjectionWindow(true);
        _injectionEditor = editor;
        PositionInjection(editor);
        editor.Closed += (_, _) => { if (ReferenceEquals(_injectionEditor, editor)) _injectionEditor = null; };
        editor.InjectRequested += text => _ = RunInjectionAsync(text);
        editor.Show(); editor.Activate();
    }
    private void PositionInjection(Window window)
    {
        var area = GetCurrentWorkingArea(); var pet = GetPetBounds();
        window.Width = Math.Min(window.Width, Math.Max(180, area.Width - 16));
        window.Height = Math.Min(window.Height, Math.Max(90, area.Height - 16));
        window.Left = Math.Clamp(pet.Left + (pet.Width - window.Width) / 2, area.Left + 8, area.Right - window.Width - 8);
        var top = pet.Top - window.Height - 10;
        if (top < area.Top + 8) top = pet.Bottom + 10;
        window.Top = Math.Clamp(top, area.Top + 8, area.Bottom - window.Height - 8);
    }
    private void CancelInjection()
    {
        _injectionCancellation?.Cancel(); _injectionEditor?.Close(); _injectionProgress?.Close();
    }
    private async Task RunInjectionAsync(string text)
    {
        if (_injectionCancellation is not null || _closed) return;
        using var stop = new CancellationTokenSource(); _injectionCancellation = stop;
        _injectionState = "state-06";
        var bubble = new PetInjectionWindow(false); _injectionProgress = bubble;
        try
        {
            await SwitchStateSafelyAsync("state-06");
            stop.Token.ThrowIfCancellationRequested();
            PositionInjection(bubble); bubble.Show();
            var seconds = Math.Clamp(AppPreferences.Load().PetInjectionDelaySeconds, 1, 30);
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                if (PetInjectionService.EscapePressed) throw new OperationCanceledException();
                bubble.Status.Text = $"{Math.Ceiling((deadline - DateTime.UtcNow).TotalSeconds):0} 秒后注入 · 请点击目标输入框\nEsc 取消";
                await Task.Delay(50, stop.Token);
            }
            bubble.Status.Text = "正在写入 0% · Esc 取消";
            var progress = new Progress<int>(value => { if (!stop.IsCancellationRequested) { bubble.Progress.Value = value; bubble.Status.Text = $"正在写入 {value}% · Esc 取消"; } });
            await Task.Run(() => PetInjectionService.WriteAsync(text, progress, stop.Token), stop.Token);
            bubble.Progress.Value = 100; bubble.Status.Text = "写入完成";
            _injectionState = "state-05";
            await SwitchStateSafelyAsync("state-05");
            await Task.Delay(3000, stop.Token);
        }
        catch (OperationCanceledException) { if (!_closed && IsVisible) { bubble.Status.Text = "注入已取消"; await Task.Delay(900); } }
        catch (Exception ex) { if (!_closed && IsVisible) { bubble.Status.Text = ex is InvalidOperationException ? ex.Message : "注入失败，已停止写入"; await Task.Delay(3000); } }
        finally
        {
            bubble.Close(); _injectionProgress = null; _injectionCancellation = null; _injectionState = null;
            if (!_closed) await RestoreStateAfterWheelAsync();
        }
    }
}
