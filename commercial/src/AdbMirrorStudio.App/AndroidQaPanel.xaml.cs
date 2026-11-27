using System.ComponentModel;
using AdbMirrorStudio.App.ViewModels;
using AdbMirrorStudio.Application.Adb;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AdbMirrorStudio.App;

public sealed partial class AndroidQaPanel : UserControl
{
    private readonly List<UiTestStep> _steps = [];
    private IReadOnlyList<UiTestNode> _filteredNodes = [];
    private MainViewModel? _model;
    private string? _pickedSelector;

    public AndroidQaPanel()
    {
        InitializeComponent();
        ActionBox.SelectedIndex = 0;
        Loaded += (_, _) => Attach(DataContext as MainViewModel);
        Unloaded += (_, _) => Attach(null);
        DataContextChanged += (_, args) =>
        {
            if (IsLoaded) Attach(args.NewValue as MainViewModel);
        };
        UpdateAvailability();
    }

    private UiTestAction Action => (ActionBox.SelectedItem as ComboBoxItem)?.Tag is string tag
        ? Enum.Parse<UiTestAction>(tag)
        : UiTestAction.Tap;

    private static bool UsesSelector(UiTestAction action) =>
        action is UiTestAction.Tap or UiTestAction.AssertVisible;

    private void Attach(MainViewModel? model)
    {
        if (ReferenceEquals(_model, model)) return;
        if (_model is not null) _model.PropertyChanged -= Model_Changed;
        _model = model;
        if (model is not null) model.PropertyChanged += Model_Changed;
        FilterNodes();
        UpdateAvailability();
    }

    private void Model_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.UiTestNodes)) FilterNodes();
        if (e.PropertyName is nameof(MainViewModel.CanUseSelectedDevice) or nameof(MainViewModel.IsUiTestRunning))
            UpdateAvailability();
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e) => FilterNodes();

    private void FilterNodes()
    {
        if (NodeList is null) return;
        var query = NodeFilter.Text.Trim();
        var nodes = _model?.UiTestNodes ?? [];
        _filteredNodes = nodes.Where(node =>
            node.ResourceId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            node.Text.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            node.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            node.ClassName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        NodeList.ItemsSource = _filteredNodes.Select(node =>
            (query.Length == 0 ? new string('　', Math.Min(node.Depth, 8)) : string.Empty) +
            string.Join(" · ", new[] { node.ClassName.Split('.').Last(), node.Text, node.Description, node.ResourceId }
                .Where(value => value.Length > 0).Distinct())).ToArray();
        NodeDetails.Text = nodes.Count == 0 ? string.Empty : $"显示 {_filteredNodes.Count} / {nodes.Count} 个控件";
    }

    private void Node_Changed(object sender, SelectionChangedEventArgs e)
    {
        var index = NodeList.SelectedIndex;
        if (_model is null || index < 0 || index >= _filteredNodes.Count) return;
        var node = _filteredNodes[index];
        var selector = UiTestSelector.ForNode(_model.UiTestNodes, node);
        var details = $"{node.ClassName}  {node.Bounds}";
        if (node.Scrollable) details += "  ·  可滚动";
        if (node.Clickable) details += "  ·  可点击";
        if (!node.Enabled) details += "  ·  已禁用";
        if (selector is null)
        {
            if (UsesSelector(Action)) ValueBox.Text = string.Empty;
            NodeDetails.Text = details + (UiTestBounds.Parse(node.Bounds) is null
                ? "  ·  此节点没有有效的屏幕区域。"
                : "  ·  此节点缺少可唯一匹配的文本、资源 ID 或描述。");
            return;
        }
        NodeDetails.Text = details;
        if (!UsesSelector(Action)) ActionBox.SelectedIndex = 0;
        _pickedSelector = selector;
        ValueBox.Text = selector;
    }

    private void Action_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ValueBox is null || ScrollRetry is null) return;
        var selector = UsesSelector(Action);
        // Keep the selected node when switching between tap and assertion.
        if (!selector || e.RemovedItems.FirstOrDefault() is not ComboBoxItem { Tag: "Tap" or "AssertVisible" })
            ValueBox.Text = string.Empty;
        ValueBox.Visibility = selector || Action is UiTestAction.Launch or UiTestAction.InputText
            ? Visibility.Visible : Visibility.Collapsed;
        ValueBox.Header = Action switch
        {
            UiTestAction.Launch => "应用包名",
            UiTestAction.InputText => "要输入的文本",
            _ => "目标控件"
        };
        ValueBox.PlaceholderText = Action switch
        {
            UiTestAction.Launch => "例如 com.android.settings",
            UiTestAction.InputText => "可打印的 ASCII 文本",
            _ => "选择上方节点，或填写精确文本、资源 ID 或描述"
        };
        ScrollRetry.Visibility = selector ? Visibility.Visible : Visibility.Collapsed;
        UpdateAvailability();
    }

    private void Value_Changed(object sender, TextChangedEventArgs e)
    {
        if (((TextBox)sender).Text != _pickedSelector) _pickedSelector = null;
        UpdateAvailability();
    }

    private UiTestStep CreateDraft() =>
        UiTestStep.CreateDraft(Action, ValueBox.Text, _pickedSelector, ScrollRetry.IsChecked == true);

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var step = CreateDraft();
        if (step.ValueError is not null) return;
        _steps.Add(step);
        RefreshPlan(_steps.Count - 1);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var index = PlanList.SelectedIndex;
        if (index < 0) return;
        _steps.RemoveAt(index);
        RefreshPlan(Math.Min(index, _steps.Count - 1));
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveStep(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveStep(1);

    private void MoveStep(int offset)
    {
        var index = PlanList.SelectedIndex;
        var target = index + offset;
        if (index < 0 || target < 0 || target >= _steps.Count) return;
        (_steps[index], _steps[target]) = (_steps[target], _steps[index]);
        RefreshPlan(target);
    }

    private void RefreshPlan(int selected)
    {
        PlanList.ItemsSource = _steps.Select((step, index) =>
            $"{index + 1}. {step.ActionLabel}  {step.Value}{(step.ScrollIfMissing ? "（滚动重试）" : string.Empty)}".TrimEnd()).ToArray();
        PlanList.SelectedIndex = selected;
        if (selected >= 0) PlanList.ScrollIntoView(PlanList.SelectedItem);
        UpdateAvailability();
    }

    private void Plan_Changed(object sender, SelectionChangedEventArgs e) => UpdateAvailability();

    private void UpdateAvailability()
    {
        if (CancelButton is null) return;
        var running = _model?.IsUiTestRunning == true;
        var ready = _model?.CanUseSelectedDevice == true && !running;
        var index = PlanList.SelectedIndex;
        InspectButton.IsEnabled = ready;
        Editor.IsEnabled = !running;
        NodeList.IsEnabled = !running;
        var error = CreateDraft().ValueError;
        AddButton.IsEnabled = error is null;
        ValueErrorText.Text = error ?? string.Empty;
        ValueErrorText.Visibility = error is not null && ValueBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Remove.IsEnabled = index >= 0;
        MoveUp.IsEnabled = index > 0;
        MoveDown.IsEnabled = index >= 0 && index < _steps.Count - 1;
        RunButton.IsEnabled = ready && _steps.Count > 0 && OutputFolder.Text.Length > 0;
        CancelButton.IsEnabled = running;
    }

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Microsoft.UI.Xaml.Application.Current is not App { MainWindow: not null } app) return;
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(app.MainWindow));
            var folder = await picker.PickSingleFolderAsync();
            if (!IsLoaded || folder is null) return;
            OutputFolder.Text = folder.Path;
            PanelError.Visibility = Visibility.Collapsed;
            UpdateAvailability();
        }
        catch (Exception exception)
        {
            PanelError.Text = $"无法选择结果目录：{exception.Message}";
            PanelError.Visibility = Visibility.Visible;
        }
    }

    private async void Inspect_Click(object sender, RoutedEventArgs e)
    {
        if (_model is { CanUseSelectedDevice: true } model) await model.InspectUiTestAsync();
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_model is { CanUseSelectedDevice: true } model && RunButton.IsEnabled)
            await model.RunUiTestAsync(_steps.ToArray(), OutputFolder.Text);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _model?.CancelUiTest();
}
