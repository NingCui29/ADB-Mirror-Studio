using AdbMirrorStudio.App.Mirroring;

namespace AdbMirrorStudio.UnitTests;

public sealed class MirrorToolbarPlacementTests
{
    [Fact]
    public void Fit_CentersFullToolbarAndTracksMove()
    {
        var initial = MirrorToolbarPlacement.Fit(0, 0, 862, 30, 96);
        var moved = MirrorToolbarPlacement.Fit(-740, 165, 862, 30, 96);

        Assert.Equal(new MirrorToolbarPlacement(319, 0, 224, 30, false), initial);
        Assert.Equal(new MirrorToolbarPlacement(-421, 165, 224, 30, false), moved);
    }

    [Theory]
    [InlineData(460, 213)]
    [InlineData(270, 84)]
    public void Fit_UsesOverflowWhenOnlyTheCompactButtonFits(int width, int expectedX)
    {
        Assert.Equal(new MirrorToolbarPlacement(expectedX, 0, 34, 30, true),
            MirrorToolbarPlacement.Fit(0, 0, width, 30, 96));
    }

    [Fact]
    public void Fit_ScalesToolbarWithTheTargetWindowDpi()
    {
        Assert.Equal(new MirrorToolbarPlacement(478, 0, 336, 45, false),
            MirrorToolbarPlacement.Fit(0, 0, 1293, 45, 144));
    }

    [Theory]
    [InlineData(862, 0, 96)]
    [InlineData(250, 30, 96)]
    [InlineData(862, 30, 0)]
    public void Fit_HidesWhenThereIsNoUsableCaption(int width, int captionHeight, uint dpi)
    {
        Assert.Null(MirrorToolbarPlacement.Fit(0, 0, width, captionHeight, dpi));
    }
}
