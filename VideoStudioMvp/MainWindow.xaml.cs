using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;

namespace VideoStudioMvp;

public partial class MainWindow : System.Windows.Window
{
    private readonly ObservableCollection<VideoTask> _tasks = new();
    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TuShengVideo", "settings.json");
    private string? _lastVideoPath;
    private AppSettings _settings;
    private string _settingsEditingProvider = VideoProviders.Volcengine;
    private bool _isLoadingSettings;
    private Grid? _assetsRoot;
    private WrapPanel? _assetPanel;
    private TextBlock? _assetSummary;
    private CancellationTokenSource? _assetsLoadCts;
    private bool _assetsLoaded;
    private bool _assetsLoading;
    private System.Windows.Threading.DispatcherTimer? _modeMarqueeTimer;
    private double _modeMarqueeOffset;
    private double _modeMarqueeMaxOffset;
    private int _modeMarqueeDirection;
    private DateTime _modeMarqueeHoldUntil;
    private readonly LocalAgentBridge _agentBridge;

    public MainWindow()
    {
        InitializeComponent();
        BuildAssetsPage();
        SetNavigationState(CreateNavButton);
        if (EmptyPreview.Children.Count > 0 && EmptyPreview.Children[0] is System.Windows.Controls.Border playSurface && playSurface.Child is System.Windows.Shapes.Path playIcon)
        {
            playIcon.Width = 24;
            playIcon.Height = 28;
            playIcon.Stretch = System.Windows.Media.Stretch.Fill;
            playIcon.Data = System.Windows.Media.Geometry.Parse("M 3,2 L 22,14 L 3,26 Z");
            playIcon.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            playIcon.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        }
        TaskList.ItemsSource = _tasks;
        EmptyTaskHint.Visibility = System.Windows.Visibility.Visible;
        _settings = LoadSettings();
        UpdateModeDisplay();
        _agentBridge = new LocalAgentBridge(47821, HandleAgentRequestAsync);
        _agentBridge.Start();
        Closed += (_, _) => _agentBridge.Dispose();
    }

    private void ChooseImage_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.webp|所有文件|*.*" };
        if (dialog.ShowDialog() != true) return;
        ImagePathBox.Text = dialog.FileName;
        ImagePreview.Source = new BitmapImage(new Uri(dialog.FileName));
        ImageEmptyHint.Visibility = System.Windows.Visibility.Collapsed;
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control && GenerateButton.IsEnabled)
        {
            Generate_Click(GenerateButton, new System.Windows.RoutedEventArgs());
            e.Handled = true;
        }
    }
    private void Resolution_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        UpdatePreviewMeta();
    }
    private string GetResolution() => Resolution1080Button.IsChecked == true ? "1080p" : Resolution480Button.IsChecked == true ? "480p" : "720p";
    private string GetRatio() => Ratio916Button.IsChecked == true ? "9:16" : Ratio11Button.IsChecked == true ? "1:1" : "16:9";
    private void Format_Changed(object sender, System.Windows.RoutedEventArgs e) => UpdatePreviewMeta();
    private void Duration_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdatePreviewMeta();
    private void UpdatePreviewMeta()
    {
        if (PreviewResolutionText is null) return;
        var duration = string.IsNullOrWhiteSpace(DurationBox?.Text) ? "5" : DurationBox.Text.Trim();
        PreviewResolutionText.Text = $"{GetResolution()} · {GetRatio()} · {duration}s";
    }

    private void ProjectLibrary_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SetNavigationState(ProjectNavButton);
        NavigationHintText.Text = "项目库 · 当前项目已在创作工作台中打开";
    }
    private void CreateWorkspace_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SetNavigationState(CreateNavButton);
        _assetsRoot!.Visibility = Visibility.Collapsed;
        SettingsRoot.Visibility = Visibility.Collapsed;
        WorkspaceRoot.Visibility = Visibility.Visible;
    }
    private async void AssetManager_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SetNavigationState(AssetNavButton);
        WorkspaceRoot.Visibility = Visibility.Collapsed;
        SettingsRoot.Visibility = Visibility.Collapsed;
        _assetsRoot!.Visibility = Visibility.Visible;
        await LoadAssetsPageAsync();
    }

    private void UseAssetFromLibrary(string path)
    {
        SetNavigationState(CreateNavButton);
        ImagePathBox.Text = path;
        ImageUrlBox.Text = "";
        ImagePreview.Source = new BitmapImage(new Uri(path));
        ImageEmptyHint.Visibility = System.Windows.Visibility.Collapsed;
        WorkspaceRoot.Visibility = System.Windows.Visibility.Visible;
        SettingsRoot.Visibility = System.Windows.Visibility.Collapsed;
        _assetsRoot!.Visibility = Visibility.Collapsed;
        NavigationHintText.Text = "已从素材管理选择图片：" + Path.GetFileName(path);
    }

    private void BackToWorkspaceFromAssets_Click(object sender, RoutedEventArgs e)
    {
        SetNavigationState(CreateNavButton);
        _assetsRoot!.Visibility = Visibility.Collapsed;
        WorkspaceRoot.Visibility = Visibility.Visible;
    }

    private async Task LoadAssetsPageAsync(bool forceRefresh = false)
    {
        if (_assetsLoading) return;
        if (_assetsLoaded && !forceRefresh) return;

        _assetsLoading = true;
        _assetsLoadCts?.Cancel();
        _assetsLoadCts?.Dispose();
        _assetsLoadCts = new CancellationTokenSource();
        var cancellationToken = _assetsLoadCts.Token;
        var directory = Path.Combine(AppContext.BaseDirectory, "imgSoure");
        Directory.CreateDirectory(directory);
        _assetPanel!.Children.Clear();
        _assetSummary!.Text = "正在读取素材…";

        try
        {
            var files = await Task.Run(() => Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                .Where(IsImageFile)
                .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(part => part.Equals(".cache", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList(), cancellationToken);

            _assetSummary.Text = files.Count == 0 ? "暂无素材 · 请将图片放入 imgSoure 文件夹" : $"{files.Count} 张素材 · 点击图片即可使用";
            const int batchSize = 12;
            for (var offset = 0; offset < files.Count; offset += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = files.Skip(offset).Take(batchSize).ToList();
                var thumbnails = await Task.Run(() => batch.Select(file => (Path: file, Image: LoadThumbnail(file))).ToList(), cancellationToken);
                foreach (var thumbnail in thumbnails)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _assetPanel.Children.Add(CreateAssetCard(thumbnail.Path, thumbnail.Image));
                }
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background, cancellationToken);
            }

            _assetsLoaded = true;
        }
        catch (OperationCanceledException)
        {
            // A newer refresh request replaced this load.
        }
        catch (Exception ex)
        {
            _assetSummary.Text = "素材读取失败 · " + ex.Message;
        }
        finally
        {
            _assetsLoading = false;
        }
    }

    private UIElement CreateAssetCard(string path, BitmapSource? thumbnail)
    {
        var card = new Button
        {
            Width = 190,
            Height = 205,
            Margin = new Thickness(0, 0, 14, 14),
            Padding = new Thickness(8),
            Background = (Brush)FindResource("Surface"),
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            ToolTip = path,
            Tag = path
        };
        var content = new StackPanel();
        var image = new Image { Height = 158, Stretch = Stretch.UniformToFill, SnapsToDevicePixels = true, Source = thumbnail };
        content.Children.Add(image);
        content.Children.Add(new TextBlock { Text = Path.GetFileName(path), FontSize = 10, Foreground = (Brush)FindResource("TextSecondary"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 8, 2, 0) });
        card.Content = content;
        card.Click += (_, _) => UseAssetFromLibrary(path);
        return card;
    }

    private static bool IsImageFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp";

    private static BitmapSource? LoadThumbnail(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var cacheDirectory = Path.Combine(info.DirectoryName!, ".cache");
            Directory.CreateDirectory(cacheDirectory);
            var cacheKey = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"))).ToLowerInvariant();
            var cachePath = Path.Combine(cacheDirectory, cacheKey + ".jpg");
            if (File.Exists(cachePath)) return ReadBitmap(cachePath);

            var source = ReadBitmap(path);
            if (source is null) return null;
            using var stream = File.Create(cachePath);
            var encoder = new JpegBitmapEncoder { QualityLevel = 86 };
            encoder.Frames.Add(BitmapFrame.Create(source));
            encoder.Save(stream);
            return source;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? ReadBitmap(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(path);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 380;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void BuildAssetsPage()
    {
        var shell = (Grid)Content;
        _assetsRoot = new Grid { Margin = new Thickness(24, 0, 24, 18), Visibility = Visibility.Collapsed };
        Grid.SetColumn(_assetsRoot, 1);
        _assetsRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(76) });
        _assetsRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _assetsRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var heading = new Grid();
        var headingStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        headingStack.Children.Add(new TextBlock { Text = "素材管理", FontSize = 26, FontWeight = FontWeights.SemiBold });
        headingStack.Children.Add(new TextBlock { Text = "浏览 imgSoure 中的图片，选择后直接用于创作", FontSize = 13, Foreground = (Brush)FindResource("TextSecondary"), Margin = new Thickness(0, 5, 0, 0) });
        heading.Children.Add(headingStack);
        var back = new Button { Content = "返回创作", Width = 100, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        back.Click += BackToWorkspaceFromAssets_Click;
        heading.Children.Add(back);
        _assetsRoot.Children.Add(heading);

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14), VerticalAlignment = VerticalAlignment.Center };
        _assetSummary = new TextBlock { Text = "正在读取素材…", Foreground = (Brush)FindResource("TextSecondary"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        toolbar.Children.Add(_assetSummary);
        var refreshIcon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 20,7 A 8,8 0 1 0 22,14 M 20,3 L 20,8 L 15,8"),
            Stroke = new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#65DDE0"), (Color)ColorConverter.ConvertFromString("#B28CFF"), 0),
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 20,
            Height = 20,
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var refresh = new Button { Content = refreshIcon, Style = (Style)FindResource("IconButton"), Margin = new Thickness(8, 0, 0, 0), ToolTip = "刷新素材" };
        System.Windows.Automation.AutomationProperties.SetName(refresh, "刷新素材");
        refresh.Click += async (_, _) =>
        {
            _assetsLoaded = false;
            await LoadAssetsPageAsync(true);
        };
        toolbar.Children.Add(refresh);
        Grid.SetRow(toolbar, 1);
        _assetsRoot.Children.Add(toolbar);

        var surface = new Border { Background = (Brush)FindResource("Surface"), BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(16), MaxWidth = 1180, HorizontalAlignment = HorizontalAlignment.Center };
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, PanningMode = PanningMode.VerticalOnly, CanContentScroll = false };
        _assetPanel = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        scroll.HorizontalContentAlignment = HorizontalAlignment.Center;
        scroll.Content = _assetPanel;
        surface.Child = scroll;
        Grid.SetRow(surface, 2);
        _assetsRoot.Children.Add(surface);
        shell.Children.Add(_assetsRoot);
    }
    private void Guide_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SetNavigationState(GuideNavButton);
        NavigationHintText.Text = "使用指南 · 先上传参考图，再填写提示词并选择比例、时长和分辨率";
    }

    private void SetNavigationState(Button active)
    {
        foreach (var button in new[] { CreateNavButton, ProjectNavButton, AssetNavButton, GuideNavButton })
        {
            button.Background = button == active ? (Brush)FindResource("AccentSoft") : Brushes.Transparent;
            button.Foreground = button == active ? (Brush)FindResource("TextPrimary") : (Brush)FindResource("TextSecondary");
            button.FontWeight = button == active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private async void Generate_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PromptBox.Text)) { StatusText.Text = "请输入提示词。"; return; }
        VideoPreview.Visibility = Visibility.Collapsed;
        VideoPreview.Stop();
        VideoPreview.Source = null;
        _lastVideoPath = null;
        GenerateButton.IsEnabled = false; GenerateButton.Content = "生成中…"; OpenFolderButton.IsEnabled = false; EmptyPreview.Visibility = System.Windows.Visibility.Visible;
        var task = new VideoTask { Prompt = PromptBox.Text.Trim(), Progress = 8 }; _tasks.Insert(0, task); RefreshTasks();
        try
        {
            if (!int.TryParse(DurationBox.Text.Trim(), out var duration) || duration < 1 || duration > 15)
                throw new InvalidOperationException("视频时长请输入 1 到 15 之间的整数秒数。");
            var image = string.IsNullOrWhiteSpace(ImageUrlBox.Text) ? ImagePathBox.Text : ImageUrlBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(image) && !Uri.TryCreate(image, UriKind.Absolute, out var parsedImage))
                throw new InvalidOperationException("真实 API 模式下，请在“参考图 URL”中填写公网可访问的图片地址；本地文件暂不能直接上传到模型接口。");
            var ratio = Ratio916Button.IsChecked == true ? "9:16" : Ratio11Button.IsChecked == true ? "1:1" : "16:9";
            var resolution = GetResolution();
            UpdatePreviewMeta();
            var request = new VideoGenerationRequest(PromptBox.Text.Trim(), image, ratio, duration, resolution, AudioBox.IsChecked == true, _settings.GetApiKey(_settings.Provider), _settings.Endpoint, _settings.Model);
            IVideoGenerationProvider provider = CreateProvider(_settings.Provider);
            task.Status = "提交中…"; task.Progress = 18; RefreshTasks(); StatusText.Text = "正在提交任务…";
            var created = await provider.CreateAsync(request, CancellationToken.None);
            task.Status = "生成中…"; task.Progress = 52; RefreshTasks(); StatusText.Text = $"任务 {created.TaskId[..8]} 正在生成，模型处理中…";
            var result = await provider.WaitAsync(created.TaskId, request, CancellationToken.None);
            task.Status = "已完成"; task.Progress = 100;
            if (!string.IsNullOrWhiteSpace(result.LocalPath)) { _lastVideoPath = result.LocalPath; OpenFolderButton.IsEnabled = true; StatusText.Text = "生成完成：" + result.LocalPath; }
            else if (!string.IsNullOrWhiteSpace(result.VideoUrl)) { _lastVideoPath = await DownloadVideoAsync(result.VideoUrl); OpenFolderButton.IsEnabled = true; StatusText.Text = "生成完成，视频已保存。"; }
            task.VideoPath = _lastVideoPath;
            if (_lastVideoPath is not null && Path.GetExtension(_lastVideoPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                VideoPreview.Source = new Uri(_lastVideoPath);
                VideoPreview.Visibility = Visibility.Visible;
                VideoPreview.Play();
                EmptyPreview.Visibility = System.Windows.Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            task.Status = "失败";
            task.Progress = 0;
            task.ErrorMessage = FormatGenerationError(ex);
            StatusText.Text = "生成失败：" + task.ErrorMessage;
            RefreshTasks();
        }
        finally { RefreshTasks(); GenerateButton.Content = "生成视频"; GenerateButton.IsEnabled = true; }
    }

    private async Task<AgentBridgeResponse> HandleAgentRequestAsync(AgentHttpRequest request)
    {
        if (!string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase) && !string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            return new(405, new { error = "仅支持 GET 和 POST。" });

        var path = request.Path.Split('?', 2)[0].TrimEnd('/');
        if (path.Equals("/api/v1/status", StringComparison.OrdinalIgnoreCase))
        {
            var preset = AppSettings.GetPreset(_settings.Provider);
            return new(200, new
            {
                ready = !string.IsNullOrWhiteSpace(_settings.GetApiKey(_settings.Provider)),
                provider = _settings.Provider,
                providerName = preset.Name,
                model = _settings.Model,
                capabilities = new[] { "list_models", "list_assets", "generate_video", "task_status" }
            });
        }

        if (path.Equals("/api/v1/models", StringComparison.OrdinalIgnoreCase))
            return new(200, AppSettings.GetModels(_settings.Provider));

        if (path.Equals("/api/v1/assets", StringComparison.OrdinalIgnoreCase))
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "imgSoure");
            Directory.CreateDirectory(directory);
            var assets = Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                .Where(IsImageFile)
                .Where(file => !file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part.Equals(".cache", StringComparison.OrdinalIgnoreCase)))
                .Select(file => new { name = Path.GetFileName(file), path = file })
                .ToList();
            return new(200, assets);
        }

        if (path.Equals("/api/v1/tasks", StringComparison.OrdinalIgnoreCase))
        {
            return new(200, _tasks.Select(task => new
            {
                id = task.Id, task.Status, task.Progress, task.ErrorMessage, task.VideoPath, task.CreatedAt
            }).ToList());
        }

        if (path.Equals("/api/v1/generations", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            var command = JsonSerializer.Deserialize<AgentGenerationCommand>(request.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (command is null || string.IsNullOrWhiteSpace(command.Prompt))
                return new(400, new { error = "prompt 不能为空。" });
            if (!command.Confirm)
                return new(409, new { requiresConfirmation = true, message = "生成视频可能产生费用，请确认 provider、model、时长和分辨率后再提交。" });
            if (!GenerateButton.IsEnabled)
                return new(409, new { error = "当前已有视频生成任务正在运行，请稍后再试。" });

            var taskId = await Dispatcher.InvokeAsync(() =>
            {
                PromptBox.Text = command.Prompt.Trim();
                ImagePathBox.Text = command.ImagePath ?? "";
                DurationBox.Text = Math.Clamp(command.Duration, 1, 15).ToString();
                Ratio169Button.IsChecked = command.Ratio == "16:9";
                Ratio916Button.IsChecked = command.Ratio == "9:16";
                Ratio11Button.IsChecked = command.Ratio == "1:1";
                Resolution480Button.IsChecked = command.Resolution == "480p";
                Resolution720Button.IsChecked = command.Resolution == "720p";
                Resolution1080Button.IsChecked = command.Resolution == "1080p";
                AudioBox.IsChecked = command.GenerateAudio;
                Generate_Click(GenerateButton, new RoutedEventArgs());
                return _tasks.Count > 0 ? _tasks[0].Id : "";
            });
            return new(202, new { accepted = true, taskId, message = "任务已提交，使用 GET /api/v1/tasks 查询状态。" });
        }

        return new(404, new { error = "未找到接口。" });
    }

    private static string FormatGenerationError(Exception ex)
    {
        if (ex is TaskCanceledException or TimeoutException) return "请求超时，请检查网络或稍后重试。";
        if (ex is HttpRequestException) return "网络请求失败：" + ex.Message;
        return string.IsNullOrWhiteSpace(ex.Message) ? "接口未返回具体失败原因，请检查服务商配置。" : ex.Message;
    }

    private static IVideoGenerationProvider CreateProvider(string provider) => provider switch
    {
        VideoProviders.Xai => new XaiVideoProvider(),
        VideoProviders.Volcengine => new VolcengineProvider(),
        _ => new MultiProviderVideoProvider(provider)
    };

    private async Task<string> DownloadVideoAsync(string url)
    {
        using var client = new HttpClient(); var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "土生视频"); Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"video-{DateTime.Now:yyyyMMdd-HHmmss}.mp4"); await File.WriteAllBytesAsync(path, await client.GetByteArrayAsync(url)); return path;
    }

    private void RefreshTasks()
    {
        TaskList.Items.Refresh();
        if (TaskCountText is not null) TaskCountText.Text = _tasks.Count.ToString();
        if (EmptyTaskHint is not null) EmptyTaskHint.Visibility = _tasks.Count == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }
    private void OpenFolder_Click(object sender, System.Windows.RoutedEventArgs e) { if (_lastVideoPath is null) return; Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastVideoPath}\"") { UseShellExecute = true }); }
    private void Settings_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _isLoadingSettings = true;
        WorkspaceRoot.Visibility = System.Windows.Visibility.Collapsed;
        SettingsRoot.Visibility = System.Windows.Visibility.Visible;
        _settingsEditingProvider = _settings.Provider;
        SetProviderRadio(_settings.Provider);
        SettingsApiKeyBox.Password = _settings.GetApiKey(_settings.Provider);
        SettingsEndpointBox.Text = string.IsNullOrWhiteSpace(_settings.Endpoint) ? AppSettings.GetPreset(_settings.Provider).Endpoint : _settings.Endpoint;
        SetModelOptions(_settings.Provider, _settings.Model);
        UpdateSettingsModeState();
        _isLoadingSettings = false;
    }
    private void Provider_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!IsInitialized || _isLoadingSettings) return;
        if (SettingsApiKeyBox is not null)
            _settings.SetApiKey(_settingsEditingProvider, SettingsApiKeyBox.Password);
        var provider = GetSelectedProvider();
        _settingsEditingProvider = provider;
        var preset = AppSettings.GetPreset(provider);
        SettingsEndpointBox.Text = preset.Endpoint;
        SetModelOptions(provider, preset.Model);
        if (SettingsApiKeyBox is not null) SettingsApiKeyBox.Password = _settings.GetApiKey(provider);
    }
    private void UpdateSettingsModeState()
    {
        SettingsApiKeyBox.IsEnabled = true;
        SettingsEndpointBox.IsEnabled = true;
        SettingsModelBox.IsEnabled = true;
    }

    private void SetModelOptions(string provider, string preferredModel)
    {
        var options = AppSettings.GetModels(provider);
        SettingsModelBox.ItemsSource = options;
        SettingsModelBox.DisplayMemberPath = nameof(ModelOption.Label);
        SettingsModelBox.SelectedValuePath = nameof(ModelOption.Id);
        var selected = options.FirstOrDefault(x => x.Id.Equals(preferredModel, StringComparison.OrdinalIgnoreCase))?.Id;
        SettingsModelBox.SelectedValue = selected;
        if (SettingsModelBox.SelectedIndex < 0) SettingsModelBox.Text = preferredModel;
    }
    private void SettingsSave_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _settings.Provider = GetSelectedProvider();
        _settings.ApiKey = SettingsApiKeyBox.Password;
        _settings.SetApiKey(_settings.Provider, _settings.ApiKey);
        _settings.Endpoint = SettingsEndpointBox.Text.Trim();
        _settings.Model = SettingsModelBox.Text.Trim();
        SaveSettings();
        UpdateModeDisplay();
        SettingsRoot.Visibility = System.Windows.Visibility.Collapsed;
        WorkspaceRoot.Visibility = System.Windows.Visibility.Visible;
    }
    private void SettingsCancel_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SettingsRoot.Visibility = System.Windows.Visibility.Collapsed;
        WorkspaceRoot.Visibility = System.Windows.Visibility.Visible;
    }
    private void UpdateModeDisplay()
    {
        var providerName = AppSettings.GetPreset(_settings.Provider).Name;
        ModeText.Text = $"{providerName} 真实生成";
        ModeMarqueeViewport.ToolTip = ModeText.Text;
        ModeHint.Text = "使用已配置模型";
        StatusText.Text = $"准备就绪 · {providerName}";
        UpdateModeMarquee();
    }

    private void UpdateModeMarquee()
    {
        if (ModeMarqueeViewport is null || ModeText is null) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _modeMarqueeTimer?.Stop();
            _modeMarqueeTimer = null;
            ModeMarqueeViewport.ScrollToHorizontalOffset(0);
            ModeText.Measure(new Size(double.PositiveInfinity, ModeMarqueeViewport.ActualHeight));
            var textWidth = Math.Max(ModeText.ActualWidth, ModeText.DesiredSize.Width);
            var viewportWidth = ModeMarqueeViewport.ViewportWidth > 0 ? ModeMarqueeViewport.ViewportWidth : ModeMarqueeViewport.ActualWidth;
            if (textWidth <= viewportWidth + 1) return;

            _modeMarqueeOffset = 0;
            _modeMarqueeMaxOffset = Math.Max(0, textWidth - viewportWidth + 12);
            _modeMarqueeDirection = 1;
            _modeMarqueeHoldUntil = DateTime.UtcNow.AddSeconds(1.8);
            _modeMarqueeTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(40)
            };
            _modeMarqueeTimer.Tick += (_, _) =>
            {
                if (DateTime.UtcNow < _modeMarqueeHoldUntil) return;
                _modeMarqueeOffset += _modeMarqueeDirection * 1.1;
                if (_modeMarqueeOffset >= _modeMarqueeMaxOffset)
                {
                    _modeMarqueeOffset = _modeMarqueeMaxOffset;
                    _modeMarqueeDirection = -1;
                    _modeMarqueeHoldUntil = DateTime.UtcNow.AddSeconds(1.8);
                }
                else if (_modeMarqueeOffset <= 0)
                {
                    _modeMarqueeOffset = 0;
                    _modeMarqueeDirection = 1;
                    _modeMarqueeHoldUntil = DateTime.UtcNow.AddSeconds(1.8);
                }
                ModeMarqueeViewport.ScrollToHorizontalOffset(_modeMarqueeOffset);
            };
            _modeMarqueeTimer.Start();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ModeMarqueeViewport_Loaded(object sender, RoutedEventArgs e) => UpdateModeMarquee();

    private void ModeMarqueeViewport_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateModeMarquee();

    private string GetSelectedProvider()
    {
        if (XaiProviderButton.IsChecked == true) return VideoProviders.Xai;
        if (KlingProviderButton.IsChecked == true) return VideoProviders.Kling;
        if (RunwayProviderButton.IsChecked == true) return VideoProviders.Runway;
        if (LumaProviderButton.IsChecked == true) return VideoProviders.Luma;
        if (GoogleVeoProviderButton.IsChecked == true) return VideoProviders.GoogleVeo;
        if (WanProviderButton.IsChecked == true) return VideoProviders.AlibabaWan;
        if (MinimaxProviderButton.IsChecked == true) return VideoProviders.Minimax;
        return VideoProviders.Volcengine;
    }

    private void SetProviderRadio(string provider)
    {
        VolcProviderButton.IsChecked = provider == VideoProviders.Volcengine;
        XaiProviderButton.IsChecked = provider == VideoProviders.Xai;
        KlingProviderButton.IsChecked = provider == VideoProviders.Kling;
        RunwayProviderButton.IsChecked = provider == VideoProviders.Runway;
        LumaProviderButton.IsChecked = provider == VideoProviders.Luma;
        GoogleVeoProviderButton.IsChecked = provider == VideoProviders.GoogleVeo;
        WanProviderButton.IsChecked = provider == VideoProviders.AlibabaWan;
        MinimaxProviderButton.IsChecked = provider == VideoProviders.Minimax;
    }
    private AppSettings LoadSettings()
    {
        try
        {
            var settings = File.Exists(_settingsPath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath)) ?? new() : new();
            settings.ApiKeys ??= new();
            if (!string.IsNullOrWhiteSpace(settings.ApiKey) && !settings.ApiKeys.ContainsKey(settings.Provider))
                settings.ApiKeys[settings.Provider] = settings.ApiKey;
            if (settings.ApiKeys.TryGetValue(settings.Provider, out var providerKey))
                settings.ApiKey = providerKey;
            if (settings.Provider == VideoProviders.Minimax && settings.Endpoint.Contains("api.minimax.io", StringComparison.OrdinalIgnoreCase))
                settings.Endpoint = settings.Endpoint.Replace("api.minimax.io", "api.minimaxi.com", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(settings.Endpoint)) settings.Endpoint = AppSettings.DefaultEndpoint;
            if (string.IsNullOrWhiteSpace(settings.Model) || settings.Model == "doubao-seedance-2-0") settings.Model = AppSettings.DefaultModel;
            if (string.IsNullOrWhiteSpace(settings.Provider)) settings.Provider = "volcengine";
            return settings;
        }
        catch { return new AppSettings { Endpoint = AppSettings.DefaultEndpoint, Model = AppSettings.DefaultModel, Provider = "volcengine" }; }
    }
    private void SaveSettings() { Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!); File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings)); }
}

public sealed class AppSettings
{
    public const string DefaultEndpoint = "https://ark.cn-beijing.volces.com/api/v3/contents/generations/tasks";
    public const string DefaultModel = "doubao-seedance-2-5-260628";
    public const string XaiEndpoint = "https://api.x.ai/v1/videos/generations";
    public const string XaiModel = "grok-imagine-video-1.5";
    public string ApiKey { get; set; } = "";
    public string Endpoint { get; set; } = DefaultEndpoint;
    public string Model { get; set; } = DefaultModel;
    public string Provider { get; set; } = VideoProviders.Volcengine;
    public Dictionary<string, string> ApiKeys { get; set; } = new();

    public string GetApiKey(string provider)
    {
        if (ApiKeys.TryGetValue(provider, out var key)) return key;
        return provider == VideoProviders.Xai ? ApiKey : "";
    }

    public void SetApiKey(string provider, string key)
    {
        ApiKeys[provider] = key;
        if (provider == Provider) ApiKey = key;
    }

    public sealed record ProviderPreset(string Name, string Endpoint, string Model);
    public static ProviderPreset GetPreset(string provider) => provider switch
    {
        VideoProviders.Xai => new("xAI · Grok Imagine", XaiEndpoint, XaiModel),
        VideoProviders.Kling => new("Kling AI", "https://api-singapore.klingai.com/image-to-video/{model}", "kling-3.0"),
        VideoProviders.Runway => new("Runway", "https://api.dev.runwayml.com/v1/image_to_video", "gen4.5"),
        VideoProviders.Luma => new("Luma Dream Machine", "https://api.lumalabs.ai/dream-machine/v1/generations", "ray-2"),
        VideoProviders.GoogleVeo => new("Google Veo", "https://generativelanguage.googleapis.com/v1beta/models/{model}:predictLongRunning", "veo-3.1-generate-preview"),
        VideoProviders.AlibabaWan => new("阿里云 Wan", "https://dashscope.aliyuncs.com/api/v1/services/aigc/video-generation/video-synthesis", "wan2.7-i2v"),
        VideoProviders.Minimax => new("MiniMax · Hailuo", "https://api.minimaxi.com/v1/video_generation", "MiniMax-Hailuo-02"),
        _ => new("火山方舟 · Seedance", DefaultEndpoint, DefaultModel)
    };

    public static IReadOnlyList<ModelOption> GetModels(string provider) => provider switch
    {
        VideoProviders.Xai => new[] { new ModelOption(XaiModel, XaiModel) },
        VideoProviders.Kling => new[] { new ModelOption("kling-3.0", "Kling 3.0"), new ModelOption("kling-2.6", "Kling 2.6"), new ModelOption("kling-2.5-turbo", "Kling 2.5 Turbo") },
        VideoProviders.Runway => new[] { new ModelOption("gen4.5", "Gen-4.5"), new ModelOption("gen4_turbo", "Gen-4 Turbo"), new ModelOption("veo3.1", "Veo 3.1"), new ModelOption("klingO3_4k", "Kling O3 4K"), new ModelOption("hailuo3", "Hailuo 3"), new ModelOption("seedance2_5", "Seedance 2.5") },
        VideoProviders.Luma => new[] { new ModelOption("ray-2", "Ray 2"), new ModelOption("ray-flash-2", "Ray Flash 2") },
        VideoProviders.GoogleVeo => new[] { new ModelOption("veo-3.1-generate-preview", "Veo 3.1"), new ModelOption("veo-3.1-fast-generate-preview", "Veo 3.1 Fast"), new ModelOption("veo-3.1-lite-generate-preview", "Veo 3.1 Lite") },
        VideoProviders.AlibabaWan => new[] { new ModelOption("wan2.7-i2v", "Wan 2.7 图生视频"), new ModelOption("wan3.0-video", "Wan 3.0 Video") },
        VideoProviders.Minimax => new[] { new ModelOption("MiniMax-H3", "MiniMax-H3"), new ModelOption("MiniMax-H3-Highspeed", "MiniMax-H3-Highspeed"), new ModelOption("MiniMax-H3-Lite", "MiniMax-H3-Lite · 480P"), new ModelOption("MiniMax-H3-Max", "MiniMax-H3-Max") },
        _ => new[] { new ModelOption(DefaultModel, "Seedance 2.5"), new ModelOption("doubao-seedance-1-0-pro-250528", "Seedance 1.0 Pro") }
    };
}
