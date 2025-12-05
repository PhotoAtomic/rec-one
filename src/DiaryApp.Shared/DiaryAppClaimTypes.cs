namespace DiaryApp.Shared;

/// <summary>
/// Defines custom claim types for the DiaryApp application.
/// </summary>
public static class DiaryAppClaimTypes
{
    /// <summary>
    /// Represents a stable, unique identifier for a user, suitable for creating storage segments.
    /// This value is derived from the best available claim from the external identity provider,
    /// such as UPN or email address.
    /// </summary>
    public const string UserId = "urn:diaryapp:userid";
}
