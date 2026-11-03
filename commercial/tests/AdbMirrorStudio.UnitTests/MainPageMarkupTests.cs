using System.Xml.Linq;

namespace AdbMirrorStudio.UnitTests;

public sealed class MainPageMarkupTests
{
    [Fact]
    public void CancelledFilePickerPreservesExistingQueue()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MainPage.xaml.cs"));
        var start = source.IndexOf("var files = await picker.PickMultipleFilesAsync();", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("ViewModel.SetTransferFiles(files.Select(file => file.Path));", start, StringComparison.Ordinal);
        Assert.True(end > start);
        Assert.Contains("if (_shutdown || files.Count == 0) return;", source[start..end]);
    }

    private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void UnifiedRailSeparatesDeviceFunctionsFromGlobalTasksAndSettings()
    {
        var navigation = ElementNamed("Navigation");
        var footer = ElementNamed("FooterNavigation");
        Assert.Equal(Ui + "ListView", navigation.Name);
        Assert.Equal(Ui + "ListView", footer.Name);
        Assert.Equal(["screen", "files", "apps", "terminal"], navigation
            .Elements(Ui + "ListViewItem").Select(item => (string?)item.Attribute("Tag")));
        Assert.Equal(["屏幕镜像", "文件管理", "应用管理", "终端"], navigation
            .Elements(Ui + "ListViewItem").Select(item => (string?)item.Attribute("AutomationProperties.Name")));
        Assert.Equal(["tasks", "settings"], footer
            .Elements(Ui + "ListViewItem").Select(item => (string?)item.Attribute("Tag")));
        foreach (var selector in new[] { navigation, footer })
        {
            Assert.Equal("Single", (string?)selector.Attribute("SelectionMode"));
            Assert.Equal("Navigation_SelectionChanged", (string?)selector.Attribute("SelectionChanged"));
            Assert.Contains(selector.Ancestors(), ancestor => (string?)ancestor.Attribute(Xaml + "Name") == "DeviceRail");
        }
        Assert.Equal("0", (string?)navigation.Attribute("SelectedIndex"));
        Assert.Equal("WorkbenchShell", (string?)ElementNamed("DeviceRail").Parent?.Attribute(Xaml + "Name"));
        Assert.Empty(Markup.Descendants(Ui + "NavigationView"));
        Assert.DoesNotContain(Markup.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == "WorkspaceTabs");
    }

    [Fact]
    public void DeviceWorkspaceStartsOnScreenAndContainsFourDeviceScopedAreas()
    {
        Assert.Equal(["ScreenScrollViewer", "FilesScrollViewer", "AppsScrollViewer", "TerminalScrollViewer"],
            ElementNamed("WorkspacePanels").Elements(Ui + "ScrollViewer")
                .Select(panel => (string?)panel.Attribute(Xaml + "Name")));
        Assert.Equal(["Visible", "Collapsed", "Collapsed", "Collapsed"],
            ElementNamed("WorkspacePanels").Elements(Ui + "ScrollViewer")
                .Select(panel => (string?)panel.Attribute("Visibility")));
        Assert.DoesNotContain(Markup.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == "OverviewScrollViewer");
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
        var selector = ElementNamed("DeviceList");

        Assert.Contains(selector.Ancestors(), ancestor => (string?)ancestor.Attribute(Xaml + "Name") == "DeviceRail");
        Assert.Equal("Serial", (string?)selector.Attribute("SelectedValuePath"));
        Assert.Equal("{Binding SelectedDeviceSerial, Mode=TwoWay}", (string?)selector.Attribute("SelectedValue"));
        Assert.Single(Markup.Descendants(Ui + "ListView"), element => (string?)element.Attribute("ItemsSource") == "{Binding Devices}");
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
    public void GlobalStatusIsBoundedAndIndependentOfSwitchedViews()
    {
        var notification = ElementNamed("StatusNotification");
        Assert.Equal("WorkbenchShell", (string?)notification.Parent?.Attribute(Xaml + "Name"));
        Assert.Equal("{Binding IsStatusOpen, Mode=TwoWay}", (string?)notification.Attribute("IsOpen"));
        Assert.Equal("{Binding StatusText}", (string?)notification.Attribute("Message"));
        Assert.Equal("Right", (string?)notification.Attribute("HorizontalAlignment"));
        Assert.Equal("Bottom", (string?)notification.Attribute("VerticalAlignment"));
        Assert.Equal("560", (string?)notification.Attribute("MaxWidth"));
        Assert.Null(notification.Element(Ui + "InfoBar.ActionButton"));
        var actions = notification.Descendants(Ui + "MenuFlyoutItem")
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
    public void NavigationKeepsOneActiveSectionWithoutChangingTargetDevice()
    {
        var selection = MethodCode("SelectSection");
        Assert.Contains("Navigation.SelectedItem =", selection, StringComparison.Ordinal);
        Assert.Contains("FooterNavigation.SelectedItem =", selection, StringComparison.Ordinal);
        Assert.Contains("_synchronizingNavigation = true;", selection, StringComparison.Ordinal);
        Assert.Contains("_synchronizingNavigation = false;", selection, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedDeviceSerial", selection, StringComparison.Ordinal);
        Assert.DoesNotContain("DeviceList.Selected", selection, StringComparison.Ordinal);
        foreach (var panel in new[] { "ScreenScrollViewer", "FilesScrollViewer", "AppsScrollViewer", "TerminalScrollViewer", "TasksView", "SettingsView" })
            Assert.Contains($"{panel}.Visibility =", selection, StringComparison.Ordinal);

        var handler = MethodCode("Navigation_SelectionChanged");
        Assert.Contains("_synchronizingNavigation", handler, StringComparison.Ordinal);
        Assert.Contains("SelectSection(_selectedSection);", handler, StringComparison.Ordinal);
        Assert.DoesNotContain(".Focus(", handler, StringComparison.Ordinal);
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
    public void DeviceHeaderOwnsUtilityActionsAndTargetedConnectionMenu()
    {
        var toolbar = ElementNamed("DeviceToolbar");
        Assert.Equal("{Binding HasSelectedDevice, Converter={StaticResource BooleanVisibility}}",
            (string?)toolbar.Attribute("Visibility"));
        var screenshot = toolbar.Elements(Ui + "Button")
            .Single(button => (string?)button.Attribute("Click") == "SaveScreenshot_Click");
        Assert.Equal("{Binding CanUseSelectedDevice}", (string?)screenshot.Attribute("IsEnabled"));

        var menuItems = ElementNamed("DeviceMoreButton").Descendants(Ui + "MenuFlyoutItem").ToArray();
        Assert.Equal(["DeviceDetails_Click", "EnableTcpIp_Click", "Reboot_Click", "Disconnect_Click"],
            menuItems.Select(item => (string?)item.Attribute("Click")));
        foreach (var item in menuItems)
        {
            Assert.Equal("{Binding SelectedDeviceSerial}", (string?)item.Attribute("CommandParameter"));
            Assert.Equal("{Binding CanUseSelectedDevice}", (string?)item.Attribute("IsEnabled"));
        }
        Assert.DoesNotContain(WorkspaceArea("屏幕").Descendants(), element =>
            (string?)element.Attribute("Click") is "EnableTcpIp_Click" or "Reboot_Click" or "Disconnect_Click");
    }

    [Fact]
    public void DeviceMenuCommandsRetainTheirCapturedTargetAcrossConfirmation()
    {
        var code = File.ReadAllText(FixturePath("MainPage.xaml.cs"));
        Assert.Contains("MenuFlyoutItem { CommandParameter: string serial } => serial", code, StringComparison.Ordinal);

        foreach (var (handler, execution) in new[]
                 {
                     ("Disconnect_Click", "ViewModel.DisconnectAsync(serial)"),
                     ("Reboot_Click", "ViewModel.RebootAsync(serial)"),
                     ("EnableTcpIp_Click", "ViewModel.EnableTcpIpAsync(serial, (int)portBox.Value)")
                 })
        {
            var method = MethodCode(handler);
            Assert.Contains("GetCommandSerial(sender) is not { } serial", method, StringComparison.Ordinal);
            Assert.Contains("await dialog.ShowAsync()", method, StringComparison.Ordinal);
            Assert.Contains(execution, method, StringComparison.Ordinal);
            Assert.True(method.IndexOf("GetCommandSerial(sender)", StringComparison.Ordinal)
                < method.IndexOf("await dialog.ShowAsync()", StringComparison.Ordinal));
            Assert.True(method.IndexOf(execution, StringComparison.Ordinal)
                > method.IndexOf("await dialog.ShowAsync()", StringComparison.Ordinal));
            Assert.DoesNotContain("ViewModel.SelectedDeviceSerial", method, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DeviceWorkspaceReflowsRailAboveContentAtNarrowWidths()
    {
        var adaptiveWidths = Markup.Descendants(Ui + "AdaptiveTrigger")
            .Select(trigger => (string?)trigger.Attribute("MinWindowWidth"))
            .Where(width => width is not null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("900", adaptiveWidths);
        Assert.Contains("0", adaptiveWidths);
        Assert.NotNull(ElementNamed("DeviceRail"));
        Assert.NotNull(ElementNamed("PageViews"));
        Assert.Contains(Markup.Descendants(Ui + "Setter"), setter =>
            (string?)setter.Attribute("Target") == "PageViews.(Grid.Row)"
            && (string?)setter.Attribute("Value") == "1");
        foreach (var navigation in new[] { "Navigation", "FooterNavigation" })
            Assert.Contains(Markup.Descendants(Ui + "Setter"), setter =>
                (string?)setter.Attribute("Target") == $"{navigation}.ItemsPanel"
                && (string?)setter.Attribute("Value") == "{StaticResource RailHorizontalPanel}");
    }

    [Fact]
    public void CompactRailKeepsRefreshAccessibleWhenTheDeviceHeaderIsHidden()
    {
        var refresh = ElementNamed("CompactRefreshDevicesButton");
        Assert.Equal("Collapsed", (string?)refresh.Attribute("Visibility"));
        Assert.Equal("Refresh_Click", (string?)refresh.Attribute("Click"));
        Assert.Equal("{Binding IsIdle}", (string?)refresh.Attribute("IsEnabled"));
        Assert.Equal("DeviceConnectionActions", (string?)refresh.Parent?.Attribute(Xaml + "Name"));
        Assert.Equal("DeviceConnectionActions", (string?)ElementNamed("AddDeviceButton").Parent?.Attribute(Xaml + "Name"));
        Assert.DoesNotContain(refresh.Ancestors(), element =>
            (string?)element.Attribute(Xaml + "Name") == "DeviceRailHeader");

        AssertStateSetter("CompactWorkspace", "DeviceRailHeader.Visibility", "Collapsed");
        AssertStateSetter("CompactWorkspace", "CompactRefreshDevicesButton.Visibility", "Visible");
        AssertStateSetter("CompactWorkspace", "DeviceConnectionActions.(Grid.Row)", "0");
        AssertStateSetter("CompactWorkspace", "DeviceConnectionActions.(Grid.Column)", "1");
    }

    [Fact]
    public void HorizontalNavigationAndPresetRowsCanScrollOnNarrowWindows()
    {
        foreach (var navigation in new[] { "Navigation", "FooterNavigation" })
        {
            AssertStateSetter("CompactWorkspace", $"{navigation}.(ScrollViewer.HorizontalScrollMode)", "Enabled");
            AssertStateSetter("CompactWorkspace", $"{navigation}.(ScrollViewer.VerticalScrollMode)", "Disabled");
        }

        var profiles = ElementNamed("MirrorProfileSelector");
        Assert.Equal("Enabled", (string?)profiles.Attribute("ScrollViewer.HorizontalScrollMode"));
        Assert.Equal("Disabled", (string?)profiles.Attribute("ScrollViewer.VerticalScrollMode"));
    }

    [Fact]
    public void WideWorkspacesExpandFileAndAppCardsAndPerformanceMetrics()
    {
        var adaptiveWidths = Markup.Descendants(Ui + "AdaptiveTrigger")
            .Select(trigger => (string?)trigger.Attribute("MinWindowWidth"))
            .Where(width => width is not null)
            .ToHashSet(StringComparer.Ordinal);
        var responsiveCardNames = new[] { "FilesCards", "AppsCards" };

        Assert.Contains("1440", adaptiveWidths);
        Assert.Contains("1180", adaptiveWidths);
        Assert.Contains("620", adaptiveWidths);
        foreach (var cardName in responsiveCardNames)
        {
            var cards = ElementNamed(cardName);
            Assert.Equal("Stretch", (string?)cards.Attribute("HorizontalAlignment"));
            Assert.Equal("0", (string?)cards.Attribute("ColumnSpacing"));
            AssertStateSetter("ExpandedWorkspaceContent", $"{cardName}.ColumnSpacing", "14");
        }

        foreach (var cardName in new[] { "FilesReceiveCard", "AppsManageCard" })
        {
            Assert.Contains(Markup.Descendants(Ui + "Setter"), setter =>
                (string?)setter.Attribute("Target") == $"{cardName}.(Grid.Column)"
                && (string?)setter.Attribute("Value") == "1");
        }
        Assert.Equal(Ui + "StackPanel", ElementNamed("ScreenCards").Name);
        Assert.Contains(Markup.Descendants(Ui + "Setter"), setter =>
            (string?)setter.Attribute("Target") == "MemoryMetricCard.(Grid.Column)"
            && (string?)setter.Attribute("Value") == "2");
        Assert.Contains(Markup.Descendants(Ui + "Setter"), setter =>
            (string?)setter.Attribute("Target") == "DdrMetricCard.(Grid.Column)"
            && (string?)setter.Attribute("Value") == "3");
    }

    [Fact]
    public void CollapsedMetricTracksDoNotLeaveGridSpacingBehind()
    {
        var grid = ElementNamed("PerformanceGrid");
        Assert.Equal("0", (string?)grid.Attribute("ColumnSpacing"));
        Assert.Equal("0", (string?)grid.Attribute("RowSpacing"));
        foreach (var name in new[] { "CpuMetricCard", "GpuMetricCard", "MemoryMetricCard", "DdrMetricCard" })
        {
            Assert.NotNull(ElementNamed(name).Attribute("Margin"));
            foreach (var state in new[] { "WideMetrics", "NarrowMetrics" })
                Assert.Contains(ElementNamed(state).Descendants(Ui + "Setter"), setter =>
                    (string?)setter.Attribute("Target") == $"{name}.Margin");
        }
    }

    [Fact]
    public void DeviceWorkspacePanelsStretchWithinAvailableWidth()
    {
        foreach (var name in new[] { "DeviceHeader", "WorkspacePanels" })
        {
            Assert.Equal("WorkspaceView", (string?)ElementNamed(name).Parent?.Attribute(Xaml + "Name"));
        }
        foreach (var panel in ElementNamed("WorkspacePanels").Elements(Ui + "ScrollViewer"))
        {
            Assert.Equal("Stretch", (string?)panel.Attribute("HorizontalContentAlignment"));
            Assert.Equal("Stretch", (string?)panel.Attribute("HorizontalAlignment"));
            Assert.Equal("Disabled", (string?)panel.Attribute("HorizontalScrollBarVisibility"));
            Assert.Null(panel.Attribute("Width"));
            Assert.Null(panel.Attribute("MaxWidth"));
        }
        var pageCode = File.ReadAllText(FixturePath("MainPage.xaml.cs"));
        Assert.DoesNotContain("scrollViewer.Width =", pageCode, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateWorkspaceContentWidth", pageCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NoSelectionShowsConnectionGuideInsteadOfDeviceCommands()
    {
        Assert.Equal("{Binding HasSelectedDevice, Converter={StaticResource BooleanVisibility}}",
            (string?)ElementNamed("WorkspacePanels").Attribute("Visibility"));
        Assert.Null(ElementNamed("Navigation").Attribute("Visibility"));
        Assert.Null(ElementNamed("FooterNavigation").Attribute("Visibility"));
        var emptyWorkspace = ElementNamed("EmptyWorkspace");
        Assert.Equal("{Binding HasSelectedDevice, Converter={StaticResource BooleanVisibility}, ConverterParameter=Invert}",
            (string?)emptyWorkspace.Attribute("Visibility"));

        var handlers = emptyWorkspace.Descendants(Ui + "Button")
            .Select(button => (string?)button.Attribute("Click"))
            .ToArray();
        Assert.Contains("OpenAddDevice_Click", handlers);
        Assert.Contains("Refresh_Click", handlers);
        Assert.Contains("OpenDiagnostics_Click", handlers);
        Assert.DoesNotContain("MirrorAction_Click", handlers);
    }

    [Fact]
    public void ScreenOwnsTheOnlyPerformancePanelWithNumericMetersAndContext()
    {
        var screen = WorkspaceArea("屏幕");
        var bindings = screen.Descendants()
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
        var performance = ElementNamed("PerformanceCard");
        Assert.Contains(performance.Ancestors(), element => (string?)element.Attribute(Xaml + "Name") == "ScreenScrollViewer");
        Assert.Equal("Collapsed", (string?)ElementNamed("PerformanceMetricsPanel").Attribute("Visibility"));
        Assert.Single(Markup.Descendants(Ui + "ToggleSwitch"), element =>
            (string?)element.Attribute("Toggled") == "PerformanceMonitoringToggle_Toggled");
        Assert.Equal(["{Binding CpuUsagePercent}", "{Binding GpuUsagePercent}", "{Binding MemoryUsagePercent}", "{Binding DdrUsagePercent}"],
            performance.Descendants(Ui + "ProgressBar").Select(bar => (string?)bar.Attribute("Value")));
    }

    [Fact]
    public void SingleMirrorActionUsesItsCapturedDeviceForStartOrStop()
    {
        var action = ElementNamed("MirrorActionButton");
        Assert.Equal("{Binding SelectedMirrorActionText}", (string?)action.Attribute("Content"));
        Assert.Equal("MirrorAction_Click", (string?)action.Attribute("Click"));
        Assert.Equal("{Binding SelectedDeviceSerial}", (string?)action.Attribute("CommandParameter"));
        Assert.Equal("{Binding CanUseSelectedDevice}", (string?)action.Attribute("IsEnabled"));
        Assert.Single(WorkspaceArea("屏幕").Descendants(Ui + "Button"), button =>
            (string?)button.Attribute("Click") == "MirrorAction_Click");
        Assert.DoesNotContain(WorkspaceArea("屏幕").Descendants(Ui + "Button"), button =>
            (string?)button.Attribute("Click") == "StopMirror_Click");

        var method = MethodCode("MirrorAction_Click");
        Assert.Contains("GetCommandSerial(sender) is not { } serial", method, StringComparison.Ordinal);
        Assert.Contains("session.DeviceSerial == serial", method, StringComparison.Ordinal);
        Assert.Contains("ViewModel.StopMirrorAsync(serial)", method, StringComparison.Ordinal);
        Assert.Contains("ViewModel.StartMirrorAsync(serial)", method, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewModel.SelectedDeviceSerial", method, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedDeviceIsMirroring", method, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileRailPersistsTheSelectedProfileAndRestoresDeselection()
    {
        var selector = ElementNamed("MirrorProfileSelector");
        Assert.Equal(Ui + "ListView", selector.Name);
        Assert.Equal("{Binding MirrorProfiles}", (string?)selector.Attribute("ItemsSource"));
        Assert.Equal("Id", (string?)selector.Attribute("SelectedValuePath"));
        Assert.Equal("{Binding SelectedMirrorProfileId, Mode=TwoWay}", (string?)selector.Attribute("SelectedValue"));
        Assert.Equal("Single", (string?)selector.Attribute("SelectionMode"));
        Assert.Equal("MirrorProfileSelector_SelectionChanged", (string?)selector.Attribute("SelectionChanged"));

        var handler = MethodCode("MirrorProfileSelector_SelectionChanged");
        Assert.Contains("sender is not Selector selector", handler, StringComparison.Ordinal);
        Assert.Contains("selector.SelectedValue = ViewModel.SelectedMirrorProfileId;", handler, StringComparison.Ordinal);
        Assert.Contains("await ViewModel.SetMirrorProfileAsync(profileId);", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void MirrorSettingsKeepsRecordingAndSessionActionsAvailable()
    {
        var flyout = ElementNamed("MirrorSettingsButton").Descendants(Ui + "Flyout").Single();
        Assert.Contains(flyout.Descendants(Ui + "TextBox"), textBox =>
            (string?)textBox.Attribute("Text") == "{Binding RecordingPath, Mode=TwoWay}");
        var handlers = flyout.Descendants(Ui + "Button").Select(button => (string?)button.Attribute("Click")).ToArray();
        Assert.Contains("ChooseRecordingPath_Click", handlers);
        Assert.Contains("ClearRecordingPath_Click", handlers);
        Assert.Contains("OpenTasks_Click", handlers);
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
        var addDeviceButton = ElementNamed("AddDeviceButton");
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

    private static XElement WorkspaceArea(string header) => ElementNamed(header switch
    {
        "屏幕" => "ScreenScrollViewer",
        "文件" => "FilesScrollViewer",
        "应用" => "AppsScrollViewer",
        "终端" => "TerminalScrollViewer",
        _ => throw new ArgumentOutOfRangeException(nameof(header), header, "未知设备工作区。")
    });

    private static string MethodCode(string methodName)
    {
        var code = File.ReadAllText(FixturePath("MainPage.xaml.cs"));
        var declaration = code.IndexOf($" void {methodName}(", StringComparison.Ordinal);
        Assert.True(declaration >= 0, $"未找到方法 {methodName}。");
        var start = code.LastIndexOf('\n', declaration) + 1;
        var end = new[] { "\n    private ", "\n    protected ", "\n    public ", "\n    internal " }
            .Select(prefix => code.IndexOf(prefix, declaration, StringComparison.Ordinal))
            .Where(index => index >= 0)
            .DefaultIfEmpty(code.Length)
            .Min();
        return code[start..end];
    }

    private static void AssertStateSetter(string stateName, string target, string value) =>
        Assert.Contains(ElementNamed(stateName).Descendants(Ui + "Setter"), setter =>
            (string?)setter.Attribute("Target") == target && (string?)setter.Attribute("Value") == value);

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
}
