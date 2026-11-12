namespace ZiYin.Core;

public sealed record TaskRequest(string Command, int TimeoutSeconds = 60, string? WorkingDirectory = null);
public sealed record TaskSnapshot(string Id, string Status, DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, int? ExitCode, string? Error,
    string StandardOutput, string StandardError, bool OutputTruncated);
public sealed record DesktopRequest(string Action, int X = 0, int Y = 0, string? Text = null, string? Key = null);
public sealed record DesktopResponse(bool Available, string Message, string? ImageBase64 = null,
    int Width = 0, int Height = 0, int Left = 0, int Top = 0);
