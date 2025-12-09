namespace DiaryApp.Shared.Abstractions;

public sealed record HttpsCertificateInfo(bool IsConfigured, string? SuggestedFileName);

public sealed record DevicePreferences(
    string? CameraDeviceId,
    string? MicrophoneDeviceId,
    string? CameraLabel,
    string? MicrophoneLabel)
{
    public static readonly DevicePreferences Default = new(null, null, null, null);
}
