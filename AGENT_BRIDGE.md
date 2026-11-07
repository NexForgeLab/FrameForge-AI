# OpenMausBot 本地桥接接口

应用启动后会在本机回环地址监听：

```text
http://127.0.0.1:47821
```

接口只监听本机，不暴露 API Key。生成视频属于可能产生费用的操作，必须显式传入 `confirm: true`。

## 查询当前状态

```http
GET /api/v1/status
```

返回当前服务商、模型、是否已配置 Key，以及可用能力。Key 只返回 `ready` 状态，不返回实际内容。

## 查询模型

```http
GET /api/v1/models
```

## 查询素材

```http
GET /api/v1/assets
```

返回 `imgSoure` 中的图片名称和本地路径。

## 查询任务

```http
GET /api/v1/tasks
```

## 提交生成任务

第一次请求建议由 Agent 用于确认参数：

```json
{
  "prompt": "镜头缓慢推进，主体自然运动",
  "imagePath": "E:\\code_tushp\\dist\\imgSoure\\example.png",
  "ratio": "9:16",
  "duration": 5,
  "resolution": "720p",
  "generateAudio": false,
  "confirm": false
}
```

接口会返回 `409` 和 `requiresConfirmation: true`。用户确认后，将 `confirm` 改为 `true` 再提交，接口返回任务 ID。

```http
POST /api/v1/generations
Content-Type: application/json
```

OpenMausBot 后续可以通过一个轻量 MCP/HTTP 适配器调用这些接口，把当前应用作为“视频生成工具”接入。
