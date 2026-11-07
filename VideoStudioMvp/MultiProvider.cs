using System.Net.Http.Headers;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;

namespace VideoStudioMvp;

/// <summary>Adapters for the additional image-to-video providers. Endpoints and model IDs remain editable in settings.</summary>
public sealed class MultiProviderVideoProvider : IVideoGenerationProvider
{
    private readonly string _provider;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public MultiProviderVideoProvider(string provider) => _provider = provider;

    public async Task<ProviderResult> CreateAsync(VideoGenerationRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey)) throw new InvalidOperationException("请先填写当前服务商的 API Key。");
        if (string.IsNullOrWhiteSpace(request.Endpoint)) throw new InvalidOperationException("请先填写生成接口地址。");
        if (string.IsNullOrWhiteSpace(request.ImagePath)) throw new InvalidOperationException("图生视频需要参考图 URL。");

        var isMinimaxH3 = _provider == VideoProviders.Minimax && IsMinimaxH3Model(request.Model);
        var endpoint = isMinimaxH3
            ? BuildMinimaxV2Endpoint(request.Endpoint)
            : request.Endpoint.Replace("{model}", Uri.EscapeDataString(request.Model), StringComparison.OrdinalIgnoreCase);
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
        if (_provider == VideoProviders.AlibabaWan) message.Headers.Add("X-DashScope-Async", "enable");
        if (_provider == VideoProviders.Runway) message.Headers.Add("X-Runway-Version", "2024-11-06");
        message.Content = new StringContent(JsonSerializer.Serialize(BuildPayload(request), _json), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(message, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{DisplayName()} 提交失败 ({(int)response.StatusCode})：{ExtractApiError(body)}");
        using var doc = JsonDocument.Parse(body);
        var id = FindString(doc.RootElement, "task_id") ?? FindString(doc.RootElement, "id") ?? FindString(doc.RootElement, "operationName") ?? FindString(doc.RootElement, "name");
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException($"{DisplayName()} 提交失败：{ExtractApiError(body)}");
        return new ProviderResult(id);
    }

    public async Task<ProviderResult> WaitAsync(string taskId, VideoGenerationRequest request, CancellationToken ct)
    {
        var url = GetPollUrl(taskId, request);
        for (var i = 0; i < 180; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            using var message = new HttpRequestMessage(HttpMethod.Get, url);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
            using var response = await _http.SendAsync(message, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{DisplayName()} 查询失败 ({(int)response.StatusCode})：{ExtractApiError(body)}");
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = (FindString(root, "status") ?? FindString(root, "state") ?? "").ToLowerInvariant();
            var video = FindString(root, "video_url") ?? FindString(root, "videoUrl") ?? FindString(root, "url") ?? FindString(root, "download_url") ?? FindString(root, "video");
            if (status is "failed" or "error" or "canceled" or "cancelled" or "fail") throw new InvalidOperationException($"{DisplayName()} 视频生成失败：{ExtractApiError(body)}");
            if (status is "succeeded" or "success" or "completed" or "done" || !string.IsNullOrWhiteSpace(video)) return new ProviderResult(taskId, VideoUrl: video);
        }
        throw new TimeoutException($"{DisplayName()} 任务超过 15 分钟仍未完成。");
    }

    private object BuildPayload(VideoGenerationRequest r) => _provider switch
    {
        VideoProviders.Kling => new { model = r.Model, prompt = r.Prompt, image = r.ImagePath, duration = r.Duration, aspect_ratio = r.Ratio },
        VideoProviders.Runway => new { model = r.Model, promptText = r.Prompt, promptImage = r.ImagePath, ratio = r.Ratio, duration = r.Duration },
        VideoProviders.Luma => new { prompt = r.Prompt, model = r.Model, aspect_ratio = r.Ratio, duration = r.Duration, keyframes = new { frame0 = new { type = "image", url = r.ImagePath } } },
        VideoProviders.GoogleVeo => new { instances = new[] { new { prompt = r.Prompt, image = new { gcsUri = r.ImagePath, mimeType = "image/png" } } }, parameters = new { aspectRatio = r.Ratio, durationSeconds = r.Duration } },
        VideoProviders.AlibabaWan => new { model = r.Model, input = new { prompt = r.Prompt, media = new[] { new { type = "image", url = r.ImagePath } } }, parameters = new { duration = r.Duration, size = r.Resolution } },
        VideoProviders.Minimax when IsMinimaxH3Model(r.Model) => BuildMinimaxH3Payload(r),
        VideoProviders.Minimax => new { model = r.Model, prompt = r.Prompt, first_frame_image = ToImageData(r.ImagePath), duration = r.Duration, resolution = ToMinimaxResolution(r.Resolution) },
        _ => new { model = r.Model, prompt = r.Prompt, image = r.ImagePath, duration = r.Duration, ratio = r.Ratio }
    };

    private string GetPollUrl(string taskId, VideoGenerationRequest r) => _provider switch
    {
        VideoProviders.Runway => "https://api.dev.runwayml.com/v1/tasks/" + Uri.EscapeDataString(taskId),
        VideoProviders.Luma => r.Endpoint.TrimEnd('/') + "/" + Uri.EscapeDataString(taskId),
        VideoProviders.AlibabaWan => "https://dashscope.aliyuncs.com/api/v1/tasks/" + Uri.EscapeDataString(taskId),
        VideoProviders.Minimax when IsMinimaxH3Model(r.Model) => BuildMinimaxV2Endpoint(r.Endpoint).Replace("/video_generation", "/query/video_generation", StringComparison.OrdinalIgnoreCase) + "/" + Uri.EscapeDataString(taskId),
        VideoProviders.Minimax => BuildMinimaxV1Endpoint(r.Endpoint).Replace("/video_generation", "/query/video_generation", StringComparison.OrdinalIgnoreCase) + "?task_id=" + Uri.EscapeDataString(taskId),
        _ => r.Endpoint.TrimEnd('/') + "/" + Uri.EscapeDataString(taskId)
    };

    private string DisplayName() => AppSettings.GetPreset(_provider).Name;

    private static bool IsMinimaxH3Model(string model) => model.StartsWith("MiniMax-H3", StringComparison.OrdinalIgnoreCase);

    private static string BuildMinimaxV2Endpoint(string endpoint)
    {
        var uri = new Uri(endpoint.Replace("{model}", "", StringComparison.OrdinalIgnoreCase));
        return $"{uri.Scheme}://{uri.Authority}/v2/video_generation";
    }

    private static string BuildMinimaxV1Endpoint(string endpoint)
    {
        var uri = new Uri(endpoint.Replace("{model}", "", StringComparison.OrdinalIgnoreCase));
        return $"{uri.Scheme}://{uri.Authority}/v1/video_generation";
    }

    private static object BuildMinimaxH3Payload(VideoGenerationRequest r)
    {
        var content = new List<object> { new { type = "text", text = r.Prompt } };
        if (!string.IsNullOrWhiteSpace(r.ImagePath))
            content.Add(new { type = "image_url", image_url = new { url = ToImageData(r.ImagePath) }, role = "first_frame" });

        return new
        {
            model = r.Model,
            content,
            resolution = ToMinimaxResolution(r.Resolution),
            duration = Math.Clamp(r.Duration, 4, 15),
            ratio = r.Ratio
        };
    }

    private static string ToMinimaxResolution(string resolution) => resolution.ToLowerInvariant() switch
    {
        "480p" => "480P",
        "1080p" or "2k" => "2K",
        _ => "768P"
    };

    private static string ToImageData(string? pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl)) throw new InvalidOperationException("MiniMax 图生视频缺少参考图片。");
        if (pathOrUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return pathOrUrl;
        if (!File.Exists(pathOrUrl)) throw new InvalidOperationException($"参考图片不存在：{pathOrUrl}");
        var bytes = File.ReadAllBytes(pathOrUrl);
        var mime = Path.GetExtension(pathOrUrl).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "image/png"
        };
        return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    }

    private static string ExtractApiError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var code = FindString(root, "status_code");
            var message = FindString(root, "status_msg") ?? FindString(root, "message") ?? FindString(root, "error");
            if (!string.IsNullOrWhiteSpace(code) || !string.IsNullOrWhiteSpace(message))
                return $"MiniMax 返回码 {code ?? "未知"}：{message ?? "未提供具体原因"}";
        }
        catch (JsonException) { }
        return body;
    }

    private static string? FindString(JsonElement root, string name)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString();
            foreach (var p in root.EnumerateObject()) { var nested = FindString(p.Value, name); if (!string.IsNullOrWhiteSpace(nested)) return nested; }
        }
        else if (root.ValueKind == JsonValueKind.Array)
            foreach (var item in root.EnumerateArray()) { var nested = FindString(item, name); if (!string.IsNullOrWhiteSpace(nested)) return nested; }
        return null;
    }
}
