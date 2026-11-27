namespace AdbMirrorStudio.Application.Adb;

public static class UiTestSelector
{
    public static (UiTestNode Node, UiTestBounds Bounds)? FindUnique(
        IReadOnlyList<UiTestNode> nodes, string value, out bool ambiguous)
    {
        ambiguous = false;
        if (string.IsNullOrWhiteSpace(value)) return null;

        (UiTestNode, UiTestBounds)? match = null;
        foreach (var node in nodes)
        {
            if (node.ResourceId != value && node.Text != value && node.Description != value) continue;
            if (UiTestBounds.Parse(node.Bounds) is not { } bounds) continue;
            if (match.HasValue)
            {
                ambiguous = true;
                return null;
            }
            match = (node, bounds);
        }
        return match;
    }

    public static string? ForNode(IReadOnlyList<UiTestNode> nodes, UiTestNode selected)
    {
        foreach (var value in new[] { selected.ResourceId, selected.Text, selected.Description })
        {
            var match = FindUnique(nodes, value, out _);
            if (ReferenceEquals(match?.Node, selected)) return value;
        }
        return null;
    }
}
