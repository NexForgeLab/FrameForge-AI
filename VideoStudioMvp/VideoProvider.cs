using System.Net.Http.Headers;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;

namespace VideoStudioMvp;

public interface IVideoGenerationProvider
{
    Task<ProviderResult> CreateAsync(VideoGenerationRequest request, CancellationToken ct);
    Task<ProviderResult> WaitAsync(string taskId, VideoGenerationRequest request, CancellationToken ct);
}

public sealed class XaiVideoProvider : IVideoGenerationProvider
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private const string BaseUrl = "https://api.x.ai/v1/videos";

    public async Task<ProviderResult> CreateAsync(VideoGenerationRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey)) throw new InvalidOperationException("请先填写 xAI API Key。");
        if (string.IsNullOrWhiteSpace(request.ImagePath)) throw new InvalidOperationException("请选择参考图片，Grok 图生视频不能只提交文本。");
        var imageReference = await BuildImageReferenceAsync(request.ImagePath, ct);
        using var message = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/generations");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
        var payload = new { model = request.Model, prompt = request.Prompt, image = new { url = imageReference }, duration = request.Duration, resolution = request.Resolution, aspect_ratio = request.Ratio, generate_audio = request.GenerateAudio };
        message.Content = new StringContent(JsonSerializer.Serialize(payload, _json), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(message, ct); var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Grok 提交失败 ({(int)response.StatusCode})：{body}");
        using var doc = JsonDocument.Parse(body); var id = FindString(doc.RootElement, "request_id") ?? FindString(doc.RootElement, "id");
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("Grok 返回中没有 request_id。");
        return new ProviderResult(id);
    }

    public async Task<ProviderResult> WaitAsync(string taskId, VideoGenerationRequest request, CancellationToken ct)
    {
        for (var i = 0; i < 180; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            using var message = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/" + Uri.EscapeDataString(taskId)); message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
            using var response = await _http.SendAsync(message, ct); var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Grok 查询失败 ({(int)response.StatusCode})：{body}");
            using var doc = JsonDocument.Parse(body); var status = (FindString(doc.RootElement, "status") ?? "").ToLowerInvariant();
            if (status is "failed" or "error") throw new InvalidOperationException("Grok 视频生成失败：" + (FindString(doc.RootElement, "error") ?? FindString(doc.RootElement, "message") ?? FindString(doc.RootElement, "failure_reason") ?? "接口未返回具体原因。"));
            var url = FindString(doc.RootElement, "url") ?? FindString(doc.RootElement, "video_url");
            if (status is "done" or "succeeded" or "completed" || !string.IsNullOrWhiteSpace(url)) return new ProviderResult(taskId, VideoUrl: url);
        }
        throw new TimeoutException("Grok 任务超过 15 分钟仍未完成。");
    }

    private static async Task<string> BuildImageReferenceAsync(string imagePath, CancellationToken ct)
    {
        if (Uri.TryCreate(imagePath, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == "data")) return imagePath;
        if (!File.Exists(imagePath)) throw new InvalidOperationException("参考图片路径不存在，请重新选择图片。");
        var bytes = await File.ReadAllBytesAsync(imagePath, ct);
        if (bytes.Length > 35 * 1024 * 1024) throw new InvalidOperationException("参考图片超过 35 MB，无法提交给 Grok。");
        var mime = Path.GetExtension(imagePath).ToLowerInvariant() switch { ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif", _ => "image/png" };
        return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    }

    private static string? FindString(JsonElement root, string name)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString();
            foreach (var property in root.EnumerateObject()) { var nested = FindString(property.Value, name); if (!string.IsNullOrWhiteSpace(nested)) return nested; }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray()) { var nested = FindString(item, name); if (!string.IsNullOrWhiteSpace(nested)) return nested; }
        }
        return null;
    }
}

/// <summary>火山方舟/媒体代理的薄适配器。接口字段通过 JSON 读取，方便模型版本升级。</summary>
public sealed class VolcengineProvider : IVideoGenerationProvider
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task<ProviderResult> CreateAsync(VideoGenerationRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey)) throw new InvalidOperationException("请先在设置中填写 API Key。");
        if (string.IsNullOrWhiteSpace(request.Endpoint)) throw new InvalidOperationException("请先在设置中填写生成接口地址。");

        using var message = new HttpRequestMessage(HttpMethod.Post, request.Endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
        var content = new List<object> { new { type = "text", text = request.Prompt } };
        if (!string.IsNullOrWhiteSpace(request.ImagePath))
            content.Add(new { type = "image_url", image_url = new { url = request.ImagePath } });
        var payload = new { model = request.Model, content, ratio = request.Ratio, duration = request.Duration, resolution = request.Resolution, generate_audio = request.GenerateAudio, watermark = false };
        message.Content = new StringContent(JsonSerializer.Serialize(payload, _json), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(message, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"提交失败 ({(int)response.StatusCode})：{body}");
        using var doc = JsonDocument.Parse(body);
        var id = FindString(doc.RootElement, "task_id") ?? FindString(doc.RootElement, "id");
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("接口返回中没有 task_id，请检查模型接口版本。");
        return new ProviderResult(id);
    }

    public async Task<ProviderResult> WaitAsync(string taskId, VideoGenerationRequest request, CancellationToken ct)
    {
        // 不同火山产品的查询地址不同，因此允许在 Endpoint 后配置 /{task_id} 的通用形式。
        var url = request.Endpoint.TrimEnd('/') + "/" + Uri.EscapeDataString(taskId);
        for (var i = 0; i < 120; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            using var message = new HttpRequestMessage(HttpMethod.Get, url);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
            using var response = await _http.SendAsync(message, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"查询失败 ({(int)response.StatusCode})：{body}");
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = (FindString(root, "status") ?? "").ToLowerInvariant();
            if (status is "failed" or "error") throw new InvalidOperationException(FindString(root, "error") ?? "视频生成失败。");
            var urlResult = FindString(root, "video_url") ?? FindString(root, "url");
            if (status is "succeeded" or "success" or "completed" || !string.IsNullOrWhiteSpace(urlResult))
                return new ProviderResult(taskId, VideoUrl: urlResult);
        }
        throw new TimeoutException("任务超过 10 分钟仍未完成。");
    }

    private static string? FindString(JsonElement root, string name)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString();
            foreach (var property in root.EnumerateObject())
            {
                var nested = FindString(property.Value, name);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                var nested = FindString(item, name);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        return null;
    }
}
