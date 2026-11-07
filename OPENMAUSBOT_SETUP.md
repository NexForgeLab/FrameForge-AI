# OpenMausBot 接入说明

当前项目已经提供一个 MCP 适配器：`FrameForgeMcp.exe`。

使用前先启动：

```text
E:\code_tushp\dist\VideoStudioMvp.exe
```

然后在 OpenMausBot 的 MCP 配置中加入：

```json
{
  "mcpServers": {
    "frameforge-video": {
      "command": "E:\\code_tushp\\dist\\FrameForgeMcp.exe"
    }
  }
}
```

适配器提供以下工具：

- `video_status`：查询服务商、模型和配置状态
- `video_models`：查询可用视频模型
- `video_assets`：列出 `imgSoure` 素材
- `video_tasks`：查询任务进度和失败原因
- `generate_video`：提交图生视频任务

`generate_video` 必须先使用 `confirm=false` 进行确认流程，用户确认可能产生的费用后，才能使用 `confirm=true` 提交。
