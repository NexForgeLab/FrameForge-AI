# 土生视频（Windows MVP）

这是一个 WinUI 风格的 WPF .NET 8 桌面 MVP：支持选择图片、输入提示词、异步任务状态、多服务商模型配置、视频预览和本地历史。

## 运行

```powershell
dotnet run --project .\VideoStudioMvp\VideoStudioMvp.csproj
```

首次运行需要在“接口设置”中选择服务商并填写对应 API Key，生成的视频会保存到“视频\土生视频”。

## 接入火山 API

在火山方舟控制台开通视频模型并创建 API Key，然后在设置中填写 API Key、生成接口地址和模型/接入点 ID。不同火山产品的任务查询地址和返回字段可能不同，适配器已经集中在 `VideoProvider.cs`，后续只需调整该文件。

正式发布时建议把 API Key 放到自有服务端，不要把永久密钥分发到客户端。
