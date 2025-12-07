namespace DiaryApp.Client.Services;

public enum ToastType
{
    Success,
    Error,
    Warning,
    Info
}

public record ToastMessage(string Message, ToastType Type, int DurationMs = 4000);

public class ToastService
{
    public event Action<ToastMessage>? OnShow;
    public event Action? OnHide;

    public void ShowSuccess(string message, int durationMs = 4000)
        => this.Show(new ToastMessage(message, ToastType.Success, durationMs));

    public void ShowError(string message, int durationMs = 5000)
        => this.Show(new ToastMessage(message, ToastType.Error, durationMs));

    public void ShowWarning(string message, int durationMs = 4000)
        => this.Show(new ToastMessage(message, ToastType.Warning, durationMs));

    public void ShowInfo(string message, int durationMs = 4000)
        => this.Show(new ToastMessage(message, ToastType.Info, durationMs));

    public void Show(ToastMessage toast)
        => OnShow?.Invoke(toast);

    public void Hide()
        => OnHide?.Invoke();
}
