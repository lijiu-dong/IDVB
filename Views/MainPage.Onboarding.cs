using IDVBuff.Features.Maps;
using IDVBuff.Helps;
using IDVBuff.Modules;

namespace IDVBuff.Views;

public sealed partial class MainPage
{
    private async Task ShowMapSubscriptionGuideAsync()
    {
        var subscriptions = new MapSubscriptionService(new MapRepository());
        await NavigateToCoCreationCommunityAsync();
        if (ModuleContentHost.Content is not MapListPage communityPage)
            return;
        await communityPage.SelectCoCreationCommunityAsync();

        var guide = new EmphasisGuide(EmphasisGuideHost);
        await guide.ShowAsync(
        [
            new EmphasisGuideStep(
                "获取社区地图",
                "这里展示地图社区的地图。点击想要的地图卡片，登录后选择“获取并添加到本地”，完成后点击检查。",
                CheckAsync: _ => Task.FromResult(subscriptions.GetSubscriptions().Count > 0
                    ? EmphasisGuideCheckResult.Passed
                    : EmphasisGuideCheckResult.TryAgain("尚未检测到新获取的社区地图，请点击地图卡片完成获取后再检查。")),
                TargetProvider: communityPage.GetCoCreationCommunityControl),
            new EmphasisGuideStep(
                "保持地图最新",
                "无需单独管理更新。每次进入共创地图的“社区”页面，IDVB 都会对比本地与社区的版本；有新版本时，地图卡片右上角会显示蓝色更新图标，点击卡片即可更新。",
                TargetProvider: communityPage.GetCoCreationCommunityControl,
                NextButtonDelay: TimeSpan.Zero,
                AdvanceButtonText: "完成")
        ]);
    }

    /// <summary>Runs after the user accepts the first-run recommended configuration.</summary>
    public Task ShowRecommendedConfigurationGuideAsync() =>
        ShowRecommendedConfigurationGuideAsync(CancellationToken.None);

    public async Task ShowRecommendedConfigurationGuideAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _onboardingNavigationLocked = true;
        SetNavigationCompact(false);
        foreach (var entry in NavigationItems)
            ExpandOnboardingNavigation(entry);

        try
        {
            NavigateTo("map-status", NavigationItems.FirstOrDefault(entry => entry.ModuleId == "map-status"));
            await Task.Delay(50, cancellationToken);
            if (ModuleContentHost.Content is not MapStatusPage statusPage)
                return;

            var guide = new EmphasisGuide(EmphasisGuideHost);
            await guide.ShowAsync(
            [
                CreateBindingStep("绑定游戏地图开关", "请先检查你游戏内打开地图的按键是哪个，然后在这里绑定一样的按键。千万不要按 ESC 或者用鼠标关闭地图，必须通过这个按键来关闭地图。", statusPage, MapRuntimeBindingTarget.GameMapToggle, "游戏地图开关", "game-map-toggle.png"),
                CreateBindingStep("绑定外置控件层", "在进入战局后，按下此按键打开控件菜单。请在选择好本局使用的地图后，点击进入对局。\n\n在结束对局后，同样需要按下此按键打开控件菜单，点击结束对局。", statusPage, MapRuntimeBindingTarget.ControlPanelToggle, "外置控件层", "control-panel-start.png", "control-panel-end.png"),
                new EmphasisGuideStep("绑定快捷扫描", string.Empty, statusPage.GetBindingControl(MapRuntimeBindingTarget.QuickScan), CheckAsync: _ => RequireBindingAsync(MapRuntimeBindingTarget.QuickScan, "快捷扫描"), DescriptionSegmentsFactory: CreateQuickScanGuideDescription, ImageUris: GuideImages("quick-scan-start.png", "quick-scan-complete.png", "quick-scan-select-map.png", "quick-scan-map-open.png")),
            ], cancellationToken);
            await ShowMapImportAndActivationGuideAsync(cancellationToken);
        }
        finally
        {
            _onboardingNavigationLocked = false;
        }
    }

    private void ExpandOnboardingNavigation(NavigationEntry entry)
    {
        if (entry.Children.Count > 0 && !entry.Node.IsExpanded)
        {
            entry.Node.ToggleExpanded();
            AnimateNavigationExpansion(entry);
        }

        foreach (var child in entry.Children)
            ExpandOnboardingNavigation(child);
    }

    private async Task ShowMapImportAndActivationGuideAsync(CancellationToken cancellationToken)
    {
        var repository = new MapRepository();
        var mapCountBeforeImport = (await repository.GetMapsAsync()).Count;
        cancellationToken.ThrowIfCancellationRequested();
        NavigateTo("map-list");
        await Task.Delay(50, cancellationToken);
        if (ModuleContentHost.Content is not MapListPage mapListPage)
            return;

        var importGuide = new EmphasisGuide(EmphasisGuideHost);
        await importGuide.ShowAsync(
        [
            new EmphasisGuideStep(
                "导入地图包",
                "点击“导入”，选择“导入数据包”，然后选择你自己的 IDVM 地图包。导入完成后点击检查。",
                CheckAsync: _ => RequireImportedMapAsync(repository, mapCountBeforeImport),
                TargetProvider: mapListPage.GetImportControl)
        ], cancellationToken);

        NavigateTo("map-status");
        await Task.Delay(50, cancellationToken);
        if (ModuleContentHost.Content is not MapStatusPage statusPage)
            return;

        var activationGuide = new EmphasisGuide(EmphasisGuideHost);
        await activationGuide.ShowAsync(
        [
            new EmphasisGuideStep(
                "打开总开关",
                "地图包已导入。回到“加页手记 → 配置”后，在这里打开总开关。",
                statusPage.GetRuntimeEnableControl(),
                CheckAsync: _ => RequireRuntimeEnabledAsync())
        ], cancellationToken);

        await ShowPreMatchVideoGuidesAsync(cancellationToken);
    }

    private async Task ShowPreMatchVideoGuidesAsync(CancellationToken cancellationToken)
    {
        var startMatchVideoOpened = false;
        var inGameVideoOpened = false;
        var endMatchVideoOpened = false;
        var guide = new EmphasisGuide(EmphasisGuideHost);
        await guide.ShowAsync(
        [
            new EmphasisGuideStep(
                "如何开始对局",
                "观看教程，了解如何打开外置控件层、选择本局地图并开始对局。",
                CheckAsync: _ => RequireVideoOpenedAsync(startMatchVideoOpened),
                TutorialVideoUri: TutorialVideoUri("vid2.mp4"),
                VideoOpened: () => startMatchVideoOpened = true),
            new EmphasisGuideStep(
                "游戏实机操作",
                "观看教程，了解进入游戏后打开地图、快捷扫描和选择对应地图的操作。",
                CheckAsync: _ => RequireVideoOpenedAsync(inGameVideoOpened),
                TutorialVideoUri: TutorialVideoUri("vid3.mp4"),
                VideoOpened: () => inGameVideoOpened = true),
            new EmphasisGuideStep(
                "结束游戏",
                "观看教程，了解结束对局后如何通过外置控件层结束本局。",
                CheckAsync: _ => RequireVideoOpenedAsync(endMatchVideoOpened),
                TutorialVideoUri: TutorialVideoUri("vid4.mp4"),
                VideoOpened: () => endMatchVideoOpened = true),
            new EmphasisGuideStep(
                "进入游戏体验",
                "准备完成。请进入游戏，使用刚才绑定的按键开始体验 Identity Vision Bridge。",
                NextButtonDelay: TimeSpan.Zero),
            new EmphasisGuideStep(
                "了解更多能力",
                "如果需要了解其他能力的介绍和使用方法，可以前往左侧导航栏的“教程”查找。",
                TargetProvider: () => TryGetNavigationRow(TutorialNavigationItem),
                NextButtonDelay: TimeSpan.Zero,
                AdvanceButtonText: "完成")
        ], cancellationToken);
    }

    private static EmphasisGuideStep CreateBindingStep(string title, string description, MapStatusPage page, MapRuntimeBindingTarget target, string displayName, params string[] imageFiles) =>
        new(title, description, page.GetBindingControl(target), CheckAsync: _ => RequireBindingAsync(target, displayName), ImageUris: GuideImages(imageFiles));

    private static IReadOnlyList<string> GuideImages(params string[] imageFiles) =>
        imageFiles.Select(file => $"ms-appx:///Assets/Guide/{file}").ToArray();

    private static Task<EmphasisGuideCheckResult> RequireBindingAsync(MapRuntimeBindingTarget target, string displayName)
    {
        var binding = target switch
        {
            MapRuntimeBindingTarget.GameMapToggle => App.Session.Settings.GameMapToggleBinding,
            MapRuntimeBindingTarget.ControlPanelToggle => App.Session.Settings.ControlPanelToggleBinding,
            MapRuntimeBindingTarget.QuickScan => App.Session.Settings.QuickScanBinding,
            MapRuntimeBindingTarget.SwitchFloor => App.Session.Settings.SwitchFloorBinding,
            MapRuntimeBindingTarget.SaveMapCache => App.Session.Settings.SaveMapCacheBinding,
            MapRuntimeBindingTarget.RestMapDisplay => App.Session.Settings.RestMapDisplayBinding,
            MapRuntimeBindingTarget.MatchStateToggle => App.Session.Settings.MatchStateToggleBinding,
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, null)
        };
        return Task.FromResult(binding.IsConfigured ? EmphasisGuideCheckResult.Passed : EmphasisGuideCheckResult.TryAgain($"请先为“{displayName}”设置按键。"));
    }

    private static async Task<EmphasisGuideCheckResult> RequireImportedMapAsync(
        MapRepository repository,
        int mapCountBeforeImport)
    {
        var mapCount = (await repository.GetMapsAsync()).Count;
        return mapCount > mapCountBeforeImport
            ? EmphasisGuideCheckResult.Passed
            : EmphasisGuideCheckResult.TryAgain("尚未检测到新导入的地图包，请完成 IDVM 数据包导入后再检查。");
    }

    private static Task<EmphasisGuideCheckResult> RequireRuntimeEnabledAsync() =>
        Task.FromResult(App.Session.Settings.IsEnabled
            ? EmphasisGuideCheckResult.Passed
            : EmphasisGuideCheckResult.TryAgain("请先打开总开关。"));

    private static string TutorialVideoUri(string fileName) =>
        $"https://download.xgflee.com/guides/onboarding/{fileName}";

    private static Task<EmphasisGuideCheckResult> RequireVideoOpenedAsync(bool wasOpened) =>
        Task.FromResult(wasOpened
            ? EmphasisGuideCheckResult.Passed
            : EmphasisGuideCheckResult.TryAgain("请先点击“观看视频教程”并打开教程，再点击检查。"));

    private static IReadOnlyList<EmphasisGuideTextSegment> CreateQuickScanGuideDescription()
    {
        var gameMapKey = App.Session.Settings.GameMapToggleBinding.DisplayName;
        return [new("在打开游戏内地图后，按下此按键即可开始扫描地图。扫描工作开始后，请用“游戏地图开关”对应的按键（也就是“"), new(gameMapKey, true), new("”）关闭地图。扫描期间不要打开地图，等待扫描完毕后，按下“游戏地图开关”对应的按键（也就是“"), new(gameMapKey, true), new("”），选择这局对应的地图。")];
    }
}
