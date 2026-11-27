using AdbMirrorStudio.Application.Adb;

namespace AdbMirrorStudio.UnitTests;

public sealed class UiTestSelectorTests
{
    [Theory]
    [InlineData("[0,10][0,40]")]
    [InlineData("[0,0][10,0]")]
    [InlineData("[1,2][2147483648,10]")]
    [InlineData("malformed")]
    public void InvalidDuplicateCannotBeSelectedAndDoesNotHideValidTarget(string badBounds)
    {
        var valid = Node("继续");
        var invalid = valid with { Bounds = badBounds };
        UiTestNode[] nodes = [invalid, valid];

        Assert.Null(UiTestSelector.ForNode(nodes, invalid));
        Assert.Equal(valid.Text, UiTestSelector.ForNode(nodes, valid));
        var target = UiTestSelector.FindUnique(nodes, valid.Text, out var ambiguous);
        Assert.False(ambiguous);
        Assert.Same(valid, target?.Node);
        Assert.Equal(20, target?.Bounds.CenterX);
        Assert.Equal(30, target?.Bounds.CenterY);
    }

    [Fact]
    public void UsesExactDescriptionWhenResourceIdAndTextAreAmbiguousAcrossAttributes()
    {
        var selected = Node("名称") with { ResourceId = "app:id/title", Description = "  有空格  " };
        var duplicate = Node("app:id/title") with { Description = "名称" };
        UiTestNode[] nodes = [duplicate, selected];
        Assert.Null(UiTestSelector.FindUnique(nodes, selected.ResourceId, out var ambiguous));
        Assert.True(ambiguous);
        var selector = UiTestSelector.ForNode(nodes, selected);
        Assert.Equal("  有空格  ", selector);
        Assert.Same(selected, UiTestSelector.FindUnique(nodes, selector!, out ambiguous)?.Node);
        Assert.False(ambiguous);
        Assert.Null(UiTestSelector.FindUnique(nodes, "有空格", out ambiguous));
        Assert.False(ambiguous);
        Assert.Null(UiTestSelector.ForNode(nodes, duplicate));
    }

    [Fact]
    public void RejectsBlankSelectorsAndCountsMatchingPropertiesOnOneNodeOnlyOnce()
    {
        var blank = Node(" ");
        var repeated = Node("登录") with { ResourceId = "登录", Description = "登录" };
        UiTestNode[] nodes = [blank, repeated];
        Assert.Null(UiTestSelector.ForNode(nodes, blank));
        Assert.Equal("登录", UiTestSelector.ForNode(nodes, repeated));
        Assert.Same(repeated, UiTestSelector.FindUnique(nodes, "登录", out var ambiguous)?.Node);
        Assert.False(ambiguous);
    }

    private static UiTestNode Node(string label) =>
        new(label, "", "", "android.widget.Button", "[10,20][30,40]", true, true, false);
}
