namespace DevPilot.Api.Dtos;

public sealed record SystemInfoResponse(string ApplicationName, string Status, DateTime UtcTimestamp);
