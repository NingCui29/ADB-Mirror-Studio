using AdbMirrorStudio.Application.Adb;

namespace AdbMirrorStudio.UnitTests;

public sealed class UiTestStepDraftTests
{
    [Theory]
    [InlineData("", "文本")]
    [InlineData("中文", "ASCII")]
    [InlineData("a\tb", "ASCII")]
    [InlineData("a\u007Fb", "ASCII")]
    [InlineData("100%s", "%s")]
    public void InvalidInputHasAnEditorError(string value, string reason)
    {
        var step = UiTestStep.CreateDraft(UiTestAction.InputText, value, null, false);

        Assert.Contains(reason, step.ValueError);
    }

    [Fact]
    public void InputTextPreservesPrintableSpacesAndAcceptsTheLengthLimit()
    {
        var value = " " + new string('a', 1022) + " ";
        var step = UiTestStep.CreateDraft(UiTestAction.InputText, value, null, false);

        Assert.Equal(value, step.Value);
        Assert.Null(step.ValueError);
        Assert.Contains("1024", UiTestStep.CreateDraft(UiTestAction.InputText, value + "!", null, false).ValueError);
    }

    [Theory]
    [InlineData(UiTestAction.Tap)]
    [InlineData(UiTestAction.AssertVisible)]
    public void ManualSelectorsTrimWhilePickedLabelsKeepTheirExactText(UiTestAction action)
    {
        const string padded = "  打开 设置　";
        Assert.Equal("打开 设置", UiTestStep.CreateDraft(action, padded, null, true).Value);
        Assert.Equal("打开 设置", UiTestStep.CreateDraft(action, padded, "上一个节点", true).Value);
        var picked = UiTestStep.CreateDraft(action, padded, padded, true);
        Assert.Equal(padded, picked.Value);
        Assert.True(picked.ScrollIfMissing);
        Assert.NotNull(UiTestStep.CreateDraft(action, "   ", null, false).ValueError);
    }
}
