namespace DiaryApp.Shared.Abstractions;

public sealed record HttpsCertificateInfo(bool IsConfigured, string? SuggestedFileName);

public sealed record DevicePreferences(
    string? CameraDeviceId,
    string? MicrophoneDeviceId)
{
    public static readonly DevicePreferences Default = new(null, null);
}
