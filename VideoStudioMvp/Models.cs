namespace VideoStudioMvp;

public sealed record VideoGenerationRequest(
    string Prompt, string? ImagePath, string Ratio, int Duration, string Resolution,
    bool GenerateAudio, string ApiKey, string Endpoint, string Model);

public static class VideoProviders
{
    public const string Volcengine = "volcengine";
    public const string Xai = "xai";
    public const string Kling = "kling";
    public const string Runway = "runway";
    public const string Luma = "luma";
    public const string GoogleVeo = "google-veo";
    public const string AlibabaWan = "alibaba-wan";
    public const string Minimax = "minimax";
}

public sealed record ModelOption(string Id, string Label);

public sealed class VideoTask
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Prompt { get; init; } = "";
    public string Status { get; set; } = "排队中";
    public string? ErrorMessage { get; set; }
    public double Progress { get; set; }
    public double ProgressWidth => Math.Max(0, Math.Min(180, Progress * 1.8));
    public string ProgressText => Progress <= 0 ? "等待中" : Progress >= 100 ? "已完成" : $"{Progress:0}%";
    public string? VideoPath { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.Now;
}

public sealed record ProviderResult(string TaskId, string? VideoUrl = null, string? LocalPath = null);

public sealed record AgentGenerationCommand(
    string Prompt,
    string? ImagePath = null,
    string Ratio = "9:16",
    int Duration = 5,
    string Resolution = "720p",
    bool GenerateAudio = false,
    bool Confirm = false);
