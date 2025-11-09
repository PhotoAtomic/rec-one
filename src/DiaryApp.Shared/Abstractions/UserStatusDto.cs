namespace DiaryApp.Shared.Abstractions;

public sealed record UserStatusDto(bool IsAuthenticated, string? Name, bool AuthenticationEnabled);
