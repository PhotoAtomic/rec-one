namespace DiaryApp.Shared;

/// <summary>
/// Defines application-specific role names.
/// </summary>
public static class DiaryAppRoles
{
    /// <summary>
    /// Users with this role can physically delete video entries and their associated files.
    /// Without this role, deletion is a "soft" delete, preserving files on disk.
    /// </summary>
    public const string CanDeepDelete = "CanDeepDelete";
}
