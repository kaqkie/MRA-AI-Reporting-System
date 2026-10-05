namespace MraReporting.Infrastructure;

/// <summary>Settings for the local model server (the "Llm" section of appsettings.json).</summary>
public sealed class LlmOptions
{
    public string Endpoint { get; set; } = "http://localhost:8080/v1";
    public string Model { get; set; } = "qwen3-4b";
    public float Temperature { get; set; } = 0.1f;
    public int MaxHistoryMessages { get; set; } = 10;
}

/// <summary>General app settings (the "App" section of appsettings.json).</summary>
public sealed class AppOptions
{
    public string TimeZone { get; set; } = "Africa/Blantyre";
    public int MaxRows { get; set; } = 500;
    public int CommandTimeoutSeconds { get; set; } = 180;
    public int MaxRangeDays { get; set; } = 400;
    public string AuditFolder { get; set; } = "logs";
}
