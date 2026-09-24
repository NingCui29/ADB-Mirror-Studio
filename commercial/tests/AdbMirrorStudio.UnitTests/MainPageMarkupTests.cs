using System.Xml.Linq;

namespace AdbMirrorStudio.UnitTests;

public sealed class MainPageMarkupTests
{
    private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void PrimaryNavigationSeparatesDeviceWorkspaceAndCrossDeviceTasks()
    {
        var navigation = Markup.Descendants(Ui + "NavigationView").Single();
        var menuItems = navigation.Element(Ui + "NavigationView.MenuItems")!
            .Elements(Ui + "NavigationViewItem")
            .Select(item => item.Attribute("Content")?.Value ?? throw new InvalidDataException("导航项缺少 Content。"))
            .ToArray();

        Assert.Equal(["设备工作区", "任务中心"], menuItems);
    }

    [Fact]
    public void DeviceWorkspaceContainsFiveDeviceScopedAreas()
    {
        var headers = ElementNamed("WorkspaceTabs").Elements(Ui + "TabViewItem")
            .Select(item => item.Attribute("Header")?.Value ?? throw new InvalidDataException("工作区缺少 Header。"))
            .ToArray();

        Assert.Equal(["概览", "屏幕", "文件", "应用", "终端"], headers);
    }

    [Fact]
    public void FilesAndAppsOwnTheirRespectiveCommands()
    {
        var fileHandlers = WorkspaceArea("文件").Descendants()
            .Select(element => (string?)element.Attribute("Click"))
            .Where(value => value is not null)
            .ToHashSet(StringComparer.Ordinal);
        var appHandlers = WorkspaceArea("应用").Descendants()
            .Select(element => (string?)element.Attribute("Click"))
            .Where(value => value is not null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("ChooseFile_Click", fileHandlers);
        Assert.Contains("PushFile_Click", fileHandlers);
        Assert.Contains("ChooseDownloadDirectory_Click", fileHandlers);
        Assert.Contains("PullRemoteFile_Click", fileHandlers);
        Assert.DoesNotContain("ChooseApk_Click", fileHandlers);
        Assert.Contains("ChooseApk_Click", appHandlers);
        Assert.Contains("InstallApk_Click", appHandlers);
        Assert.Contains("AppAction_Click", appHandlers);
    }

    [Fact]
    public void DeviceRailProvidesSingleSharedTargetSelection()
    {
        var selector = ElementNamed("DeviceRail").Descendants(Ui + "ListView").Single();

        Assert.Equal("Serial", (string?)selector.Attribute("SelectedValuePath"));
        Assert.Contains("SelectedDeviceSerial", (string?)selector.Attribute("SelectedValue"));
        Assert.DoesNotContain(Markup.Descendants(), element =>
            (string?)element.Attribute(Xaml + "Name") is "TransferDeviceSelector" or "ToolsDeviceSelector");
    }

    [Fact]
    public void SettingsOwnsOnlyAppScopedSections()
    {
        var labels = ElementNamed("SettingsView").Descendants(Ui + "TextBlock")
            .Select(item => (string?)item.Attribute("Text"))
            .Where(value => value is not null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("外观", labels);
        Assert.Contains("设备刷新", labels);
        Assert.Contains("系统健康", labels);
        Assert.Contains("更新", labels);
        Assert.Contains("本地数据与关于", labels);
        Assert.Empty(ElementNamed("SettingsView").Descendants(Ui + "TabViewItem"));
    }

    [Fact]
    public void GlobalStatusProvidesCopyAndDiagnosticsActions()
    {
        var actions = Markup.Descendants(Ui + "MenuFlyoutItem")
            .Select(item => new
            {
                Text = (string?)item.Attribute("Text"),
                Click = (string?)item.Attribute("Click")
            })
            .ToArray();

        Assert.Contains(actions, action => action is { Text: "复制状态", Click: "CopyStatus_Click" });
        Assert.Contains(actions, action => action is { Text: "打开诊断", Click: "OpenDiagnostics_Click" });
    }

    [Fact]
    public void NavigationCodeUsesOnlyNewTopLevelScopes()
    {
        var code = File.ReadAllText(FixturePath("MainPage.xaml.cs"));

        Assert.Contains("case \"tasks\"", code, StringComparison.Ordinal);
        Assert.DoesNotContain("case \"sessions\"", code, StringComparison.Ordinal);
        Assert.DoesNotContain("case \"files\"", code, StringComparison.Ordinal);
        Assert.DoesNotContain("case \"tools\"", code, StringComparison.Ordinal);
        Assert.DoesNotContain("case \"diagnostics\"", code, StringComparison.Ordinal);
        Assert.DoesNotContain("case \"about\"", code, StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceCardShowsStatusImmediatelyAfterDeviceName()
    {
        var deviceName = Markup.Descendants(Ui + "TextBlock")
            .Single(element => (string?)element.Attribute("Text") == "{Binding DisplayName}");
        var row = deviceName.Parent ?? throw new InvalidDataException("设备名称缺少容器。");
        var bindings = row.Descendants(Ui + "TextBlock")
            .Select(element => (string?)element.Attribute("Text")
                ?? throw new InvalidDataException("设备状态缺少 Text。"))
            .ToArray();

        Assert.Equal(
            ["{Binding DisplayName}", "{Binding StateLabel}", "{Binding ConnectionLabel}"],
            bindings);
    }

    [Fact]
    public void DeviceCardExposesAllActionsWithoutMoreMenu()
    {
        var clickHandlers = Markup.Descendants()
            .Select(element => (string?)element.Attribute("Click"))
            .Where(value => value is not null)
            .ToHashSet(StringComparer.Ordinal);
        var code = File.ReadAllText(FixturePath("MainPage.xaml.cs"));

        Assert.Contains("Mirror_Click", clickHandlers);
        Assert.Contains("EnableTcpIp_Click", clickHandlers);
        Assert.Contains("Reboot_Click", clickHandlers);
        Assert.Contains("Disconnect_Click", clickHandlers);
        Assert.DoesNotContain("DeviceMore_Click", clickHandlers);
        Assert.DoesNotContain("DeviceMore_Click", code, StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceWorkspaceReflowsRailAboveContentAtNarrowWidths()
    {
        var adaptiveWidths = Markup.Descendants(Ui + "AdaptiveTrigger")
            .Select(trigger => (string?)trigger.Attribute("MinWindowWidth"))
            .Where(width => width is not null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("980", adaptiveWidths);
        Assert.Contains("0", adaptiveWidths);
        Assert.NotNull(ElementNamed("DeviceRail"));
        Assert.NotNull(ElementNamed("WorkspaceContent"));
        Assert.Contains(Markup.Descendants(Ui + "Setter"), setter =>
            (string?)setter.Attribute("Target") == "WorkspaceContent.(Grid.Row)"
            && (string?)setter.Attribute("Value") == "1");
    }

    [Fact]
    public void DeviceWorkspaceUsesTwoColumnCardsAtFullScreenWidths()
    {
        var adaptiveWidths = Markup.Descendants(Ui + "AdaptiveTrigger")
            .Select(trigger => (string?)trigger.Attribute("MinWindowWidth"))
            .Where(width => width is not null)
            .ToHashSet(StringComparer.Ordinal);
        var responsiveCardNames = new[] { "OverviewCards", "ScreenCards", "FilesCards", "AppsCards" };

        Assert.Contains("1500", adaptiveWidths);
        foreach (var cardName in responsiveCardNames)
        {
            var cards = ElementNamed(cardName);
            Assert.Equal("1400", (string?)cards.Attribute("MaxWidth"));
            Assert.Equal("Stretch", (string?)cards.Attribute("HorizontalAlignment"));
        }

        Assert.Contains(Markup.Descendants(Ui + "Setter"), setter =>
            (string?)setter.Attribute("Target") == "OverviewConnectionCard.(Grid.Column)"
            && (string?)setter.Attribute("Value") == "1");
        Assert.Contains(Markup.Descendants(Ui + "Setter"), setter =>
            (string?)setter.Attribute("Target") == "ScreenControlCard.(Grid.Column)"
            && (string?)setter.Attribute("Value") == "1");

        var tabs = ElementNamed("WorkspaceTabs");
        Assert.Equal("WorkspaceTabs_SizeChanged", (string?)tabs.Attribute("SizeChanged"));
        var pageCode = File.ReadAllText(FixturePath("MainPage.xaml.cs"));
        Assert.Contains("Math.Min(1400, availableWidth)", pageCode, StringComparison.Ordinal);
        Assert.Contains("scrollViewer.Width = contentWidth;", pageCode, StringComparison.Ordinal);
    }

    [Fact]
    public void OverviewShowsCurrentDeviceCpuAndGpuWithThirtySecondAverages()
    {
        var overview = WorkspaceArea("概览");
        var bindings = overview.Descendants()
            .Select(element => (string?)element.Attribute("Text"))
            .Where(text => text is not null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("{Binding CpuUsageText}", bindings);
        Assert.Contains("{Binding CpuAverageText}", bindings);
        Assert.Contains("{Binding CpuTemperatureText}", bindings);
        Assert.Contains("{Binding GpuUsageText}", bindings);
        Assert.Contains("{Binding GpuAverageText}", bindings);
        Assert.Contains("{Binding GpuTemperatureText}", bindings);
        Assert.Contains("{Binding MemoryUsageText}", bindings);
        Assert.Contains("{Binding MemoryAverageText}", bindings);
        Assert.Contains("{Binding MemoryDetailText}", bindings);
        Assert.Contains("{Binding DdrUsageText}", bindings);
        Assert.Contains("{Binding DdrAverageText}", bindings);
        Assert.Contains("{Binding DdrFrequencyText}", bindings);
    }

    [Fact]
    public void ConnectUsesCurrentEditorTextAndRejectsBlankEndpoint()
    {
        Assert.Equal("{Binding Endpoint, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}",
            (string?)ElementNamed("EndpointBox").Attribute("Text"));

        var pageCode = File.ReadAllText(FixturePath("MainPage.xaml.cs"));
        var viewModelCode = File.ReadAllText(FixturePath("MainViewModel.cs"));

        Assert.Contains("ViewModel.Endpoint = EndpointBox.Text?.Trim() ?? string.Empty;", pageCode, StringComparison.Ordinal);
        Assert.Contains("if (string.IsNullOrWhiteSpace(endpoint))", viewModelCode, StringComparison.Ordinal);
        Assert.Contains("StatusText = \"请输入设备 IP 地址和端口\";", viewModelCode, StringComparison.Ordinal);
        Assert.Contains("Endpoint = string.Empty;", viewModelCode, StringComparison.Ordinal);
    }

    [Fact]
    public void AddDeviceFlyoutContainsAddressConnectionAndWirelessPairing()
    {
        var addDeviceButton = ElementNamed("DeviceRail").Descendants(Ui + "Button")
            .Single(button => (string?)button.Attribute("Content") == "添加设备");
        var flyout = addDeviceButton.Descendants(Ui + "Flyout").Single();
        var labels = flyout.Descendants(Ui + "TextBlock")
            .Select(element => (string?)element.Attribute("Text"))
            .Where(value => value is not null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("地址连接", labels);
        Assert.Contains("无线配对", labels);
        Assert.NotNull(ElementNamed("EndpointBox"));
        Assert.NotNull(ElementNamed("PairingCodeBox"));
        Assert.Contains(
            flyout.Descendants(Ui + "Button"),
            button => (string?)button.Attribute("Content") == "配对"
                      && (string?)button.Attribute("Click") == "Pair_Click");
    }

    [Fact]
    public void AppRegistersAndRedirectsToSingleMainInstance()
    {
        var code = File.ReadAllText(FixturePath("App.xaml.cs"));

        Assert.Contains("AppInstance.FindOrRegisterForKey(MainInstanceKey)", code, StringComparison.Ordinal);
        Assert.Contains("RedirectActivationToAsync", code, StringComparison.Ordinal);
        Assert.Contains("ActivateExistingWindow", code, StringComparison.Ordinal);
    }

    private static XDocument Markup => XDocument.Load(FixturePath("MainPage.xaml"));

    private static XElement ElementNamed(string name) => Markup.Descendants()
        .Single(element => (string?)element.Attribute(Xaml + "Name") == name);

    private static XElement WorkspaceArea(string header) => ElementNamed("WorkspaceTabs")
        .Elements(Ui + "TabViewItem")
        .Single(element => (string?)element.Attribute("Header") == header);

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
}
