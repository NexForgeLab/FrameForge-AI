using System.Net.Http.Json;
using System.Text.Json;

namespace FrameForgeMcp;

internal static class Program
{
    private static readonly HttpClient Http = new() { BaseAddress = new Uri("http://127.0.0.1:47821") };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static async Task Main()
    {
        while (Console.ReadLine() is { } line && !string.IsNullOrWhiteSpace(line))
        {
            try
            {
                using var request = JsonDocument.Parse(line);
                var root = request.RootElement;
                if (!root.TryGetProperty("id", out var id)) continue;
                var method = root.TryGetProperty("method", out var methodValue) ? methodValue.GetString() : null;
                var result = method switch
                {
                    "initialize" => InitializeResult(),
                    "tools/list" => ToolsListResult(),
                    "tools/call" => await CallToolAsync(root),
                    _ => new { }
                };
                await WriteAsync(new { jsonrpc = "2.0", id, result });
            }
            catch (Exception ex)
            {
                await WriteAsync(new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32603, message = ex.Message } });
            }
        }
    }

    private static object InitializeResult() => new
    {
        protocolVersion = "2024-11-05",
        capabilities = new { tools = new { } },
        serverInfo = new { name = "frameforge-video", version = "1.0.0" }
    };

    private static object ToolsListResult() => new
    {
        tools = new object[]
        {
            Tool("video_status", "查询当前视频服务商、模型和 API Key 是否已配置。", new { type = "object", properties = new { } }),
            Tool("video_models", "查询当前服务商可用的视频模型。", new { type = "object", properties = new { } }),
            Tool("video_assets", "列出 imgSoure 素材库中的图片。", new { type = "object", properties = new { } }),
            Tool("video_tasks", "查询最近的视频生成任务及进度。", new { type = "object", properties = new { } }),
            Tool("generate_video", "提交图生视频任务。必须先向用户确认可能产生的费用，再传 confirm=true。", new
            {
                type = "object",
                required = new[] { "prompt", "confirm" },
                properties = new
                {
                    prompt = new { type = "string", description = "视频动作、镜头和风格描述" },
                    imagePath = new { type = "string", description = "参考图片本地路径，可选" },
                    ratio = new { type = "string", @enum = new[] { "16:9", "9:16", "1:1" }, @default = "9:16" },
                    duration = new { type = "integer", minimum = 1, maximum = 15, @default = 5 },
                    resolution = new { type = "string", @enum = new[] { "480p", "720p", "1080p" }, @default = "720p" },
                    generateAudio = new { type = "boolean", @default = false },
                    confirm = new { type = "boolean", description = "用户已确认生成和可能产生的费用" }
                }
            })
        }
    };

    private static object Tool(string name, string description, object schema) => new { name, description, inputSchema = schema };

    private static async Task<object> CallToolAsync(JsonElement root)
    {
        var parameters = root.TryGetProperty("params", out var p) ? p : default;
        var name = parameters.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(name)) return TextResult("缺少工具名称。", true);
        var path = name switch
        {
            "video_status" => "/api/v1/status",
            "video_models" => "/api/v1/models",
            "video_assets" => "/api/v1/assets",
            "video_tasks" => "/api/v1/tasks",
            "generate_video" => "/api/v1/generations",
            _ => null
        };
        if (path is null) return TextResult($"未知工具：{name}", true);

        HttpResponseMessage response;
        if (name == "generate_video")
        {
            var arguments = parameters.TryGetProperty("arguments", out var args) ? args : default;
            response = await Http.PostAsJsonAsync(path, arguments, Json);
        }
        else
        {
            response = await Http.GetAsync(path);
        }
        var body = await response.Content.ReadAsStringAsync();
        return TextResult(body, response.StatusCode != System.Net.HttpStatusCode.OK && response.StatusCode != System.Net.HttpStatusCode.Accepted);
    }

    private static object TextResult(string text, bool isError) => new
    {
        content = new[] { new { type = "text", text } },
        isError
    };

    private static async Task WriteAsync(object value)
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value, Json));
        await Console.Out.FlushAsync();
    }
}
