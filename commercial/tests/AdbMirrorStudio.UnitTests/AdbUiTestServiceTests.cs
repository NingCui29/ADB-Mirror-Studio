using AdbMirrorStudio.Application.Adb;
using AdbMirrorStudio.Application.Commands;
using AdbMirrorStudio.Infrastructure.Adb;

namespace AdbMirrorStudio.UnitTests;

public sealed class AdbUiTestServiceTests
{
    private const string Serial = "emulator-5564";
    private const string SignInNode = """
        <node text="登录 &amp; 继续" resource-id="org.example:id/sign_in" content-desc="提交表单" class="android.widget.Button" enabled="true" clickable="true" scrollable="false" bounds="[12,22][91,65]" />
        """;
    private const string ScrollNode = """
        <node class="android.widget.ScrollView" enabled="true" scrollable="true" bounds="[0,200][800,1000]" />
        """;

    [Fact]
    public async Task InspectUi_ReadsLabelsAndBoundsFromNestedAndroidNodes()
    {
        var runner = new UiRunner(Wrap($"""<node class="android.widget.FrameLayout" bounds="[0,0][1080,1920]">{SignInNode}</node>"""));
        var nodes = await Service(runner).InspectUiAsync(Serial);

        var button = Assert.Single(nodes, node => node.ResourceId == "org.example:id/sign_in");
        Assert.Equal("登录 & 继续", button.Text);
        Assert.Equal("提交表单", button.Description);
        Assert.Equal("android.widget.Button", button.ClassName);
        Assert.Equal("[12,22][91,65]", button.Bounds);
        Assert.Equal(1, button.Depth);
        Assert.True(button.Clickable);
        Assert.True(button.Enabled);
        Assert.False(button.Scrollable);
        Assert.Single(runner.Requests);
    }

    [Theory]
    [InlineData("登录 & 继续")]
    [InlineData("org.example:id/sign_in")]
    [InlineData("提交表单")]
    public async Task Tap_FindsExactNodeAndUsesCurrentCenter(string selector)
    {
        var runner = new UiRunner(Wrap(SignInNode));
        await Service(runner).ExecuteUiStepAsync(Serial, new(UiTestAction.Tap, selector));

        Assert.Equal(2, runner.Requests.Count);
        Assert.Equal(["-s", Serial, "shell", "input", "tap", "51", "43"], runner.Requests[1].Arguments);
    }

    [Fact]
    public async Task AssertVisible_DoesNotTapOrSwipeWhenAlreadyVisible()
    {
        var runner = new UiRunner(Wrap(ScrollNode + SignInNode));
        await Service(runner).ExecuteUiStepAsync(Serial, new(UiTestAction.AssertVisible, "提交表单", true));

        Assert.Single(runner.Requests);
        Assert.Empty(runner.InputCommands);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("登录")]
    public async Task MissingTarget_WithoutRetryDoesNotMoveScrollablePage(string selector)
    {
        var runner = new UiRunner(Wrap(ScrollNode + SignInNode));
        await Assert.ThrowsAsync<AdbCommandException>(() => Service(runner).ExecuteUiStepAsync(Serial, new(UiTestAction.Tap, selector)));

        Assert.Single(runner.Requests);
        Assert.Empty(runner.InputCommands);
    }

    [Theory]
    [InlineData("[12,22][12,22]")]
    [InlineData("invalid bounds")]
    public async Task Tap_RejectsTargetWithoutUsableBounds(string bounds)
    {
        var runner = new UiRunner(Wrap(SignInNode.Replace("[12,22][91,65]", bounds)));
        await Assert.ThrowsAsync<AdbCommandException>(() => Service(runner).ExecuteUiStepAsync(Serial, new(UiTestAction.Tap, "提交表单")));

        Assert.Empty(runner.InputCommands);
    }

    [Fact]
    public async Task Tap_AmbiguousSelectorDoesNotPickFirstOrScroll()
    {
        var secondButton = SignInNode.Replace("[12,22][91,65]", "[0,200][300,300]");
        var runner = new UiRunner(Wrap(ScrollNode + SignInNode + secondButton));
        await Assert.ThrowsAsync<AdbCommandException>(() => Service(runner).ExecuteUiStepAsync(Serial, new(UiTestAction.Tap, "提交表单", true)));

        Assert.Empty(runner.InputCommands);
    }

    [Fact]
    public async Task Tap_ScrollRetryUsesContainerAndRefreshedTree()
    {
        var runner = new UiRunner(Wrap(ScrollNode), Wrap(ScrollNode + SignInNode));
        await Service(runner).ExecuteUiStepAsync(Serial, new(UiTestAction.Tap, "org.example:id/sign_in", true));

        Assert.Equal(4, runner.Requests.Count);
        var swipe = runner.Requests[1].Arguments;
        Assert.Equal(["-s", Serial, "shell", "input", "swipe"], swipe.Take(5));
        Assert.InRange(int.Parse(swipe[5]), 1, 799);
        Assert.InRange(int.Parse(swipe[7]), 1, 799);
        var fromY = int.Parse(swipe[6]);
        Assert.InRange(fromY, 201, 999);
        Assert.InRange(int.Parse(swipe[8]), 201, fromY - 1);
        Assert.Equal(["-s", Serial, "shell", "input", "tap", "51", "43"], runner.Requests[3].Arguments);
    }

    [Fact]
    public async Task AssertVisible_StopsAfterOneUnsuccessfulScroll()
    {
        var runner = new UiRunner(Wrap(ScrollNode), Wrap(ScrollNode));
        await Assert.ThrowsAsync<AdbCommandException>(() => Service(runner).ExecuteUiStepAsync(Serial, new(UiTestAction.AssertVisible, "more", true)));

        Assert.Equal(3, runner.Requests.Count);
        Assert.Equal("swipe", Assert.Single(runner.InputCommands).Arguments[4]);
    }

    [Fact]
    public async Task Tap_IncompleteHierarchyDoesNotActOnPartialNodes()
    {
        var runner = new UiRunner("<hierarchy>" + SignInNode);
        await Assert.ThrowsAsync<AdbCommandException>(() => Service(runner).ExecuteUiStepAsync(Serial, new(UiTestAction.Tap, "提交表单")));

        Assert.Empty(runner.InputCommands);
    }

    [Fact]
    public async Task CancelledTest_DoesNotStartAdb()
    {
        var runner = new UiRunner();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(runner)
            .ExecuteUiStepAsync(Serial, new(UiTestAction.Back), new CancellationToken(canceled: true)));
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task InputText_EncodesSpacesAndQuotesForAndroidAndMasksArguments()
    {
        var runner = new UiRunner();
        await Service(runner).ExecuteUiStepAsync(Serial, new(UiTestAction.InputText, "Sam's note"));
        var request = Assert.Single(runner.Requests);

        Assert.Equal(["-s", Serial, "shell", "input", "text", "'Sam'\\''s%snote'"], request.Arguments);
        Assert.True(request.SensitiveArguments);
    }

    [Theory]
    [InlineData("你好")]
    [InlineData("a%sb")]
    [InlineData("one\ntwo")]
    public async Task InputText_RejectsUnsupportedTextBeforeStartingAdb(string value)
    {
        var runner = new UiRunner();
        await Assert.ThrowsAsync<ArgumentException>(() => Service(runner).ExecuteUiStepAsync(Serial, new(UiTestAction.InputText, value)));
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Launch_UsesResolvedLauncherComponentOfRequestedPackage()
    {
        var runner = new UiRunner("priority=0 match=0x108000\norg.example/.HomeActivity", "Starting: Intent\nStatus: ok\nComplete");
        await Service(runner).ExecuteUiStepAsync(Serial, new(UiTestAction.Launch, "org.example"));

        Assert.Equal(2, runner.Requests.Count);
        Assert.Contains("resolve-activity", runner.Requests[0].Arguments);
        Assert.Equal("org.example", runner.Requests[0].Arguments[^1]);
        Assert.Equal(["-s", Serial, "shell", "am", "start", "-W"], runner.Requests[1].Arguments.Take(6));
        Assert.Equal("org.example/.HomeActivity", runner.Requests[1].Arguments[^1].Trim('\''));
    }

    [Theory]
    [InlineData("No activity found")]
    [InlineData("org.example.other/.HomeActivity")]
    public async Task Launch_UnresolvedOrWrongPackageDoesNotStartAnActivity(string output)
    {
        var runner = new UiRunner(output);
        await Assert.ThrowsAsync<AdbCommandException>(() => Service(runner).ExecuteUiStepAsync(Serial, new(UiTestAction.Launch, "org.example")));
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task Launch_FailsWhenAndroidReportsAnErrorWithExitCodeZero()
    {
        var runner = new UiRunner("org.example/.HomeActivity", "Error type 3\nError: Activity class does not exist.");
        await Assert.ThrowsAsync<AdbCommandException>(() => Service(runner).ExecuteUiStepAsync(Serial, new(UiTestAction.Launch, "org.example")));
        Assert.Equal(2, runner.Requests.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(127)]
    public async Task Launch_OlderAndroidWithoutResolverVerifiesPackageScopedIntent(int resolverExitCode)
    {
        var runner = new UiRunner("/system/bin/sh: cmd: not found", "Status: ok\nActivity: org.example/.HomeActivity")
        { FirstExitCode = resolverExitCode };
        await Service(runner).ExecuteUiStepAsync(Serial, new(UiTestAction.Launch, "org.example"));

        Assert.Equal(2, runner.Requests.Count);
        Assert.Equal(["-p", "org.example"], runner.Requests[1].Arguments.TakeLast(2));
    }

    [Fact]
    public async Task Launch_OlderAndroidRejectsWrongForegroundPackage()
    {
        var runner = new UiRunner("Unknown command: resolve-activity", "Status: ok\nActivity: org.example.other/.HomeActivity");
        await Assert.ThrowsAsync<AdbCommandException>(() => Service(runner).ExecuteUiStepAsync(Serial, new(UiTestAction.Launch, "org.example")));
        Assert.Equal(2, runner.Requests.Count);
    }

    [Theory]
    [InlineData("ERROR: null root node returned by UiTestAutomationBridge.", 0)]
    [InlineData("ERROR: could not get idle state.", 1)]
    public async Task TransientWindowTransition_RetriesTreeReadAndTapsOnlyOnce(string diagnostic, int exitCode)
    {
        var runner = new UiRunner(diagnostic, Wrap(SignInNode)) { FirstExitCode = exitCode };
        await Service(runner).ExecuteUiStepAsync(Serial, new(UiTestAction.Tap, "提交表单"));

        Assert.Equal(3, runner.Requests.Count);
        Assert.Equal("tap", Assert.Single(runner.InputCommands).Arguments[4]);
    }

    [Fact]
    public async Task TransientWindowTransition_CancelBeforeRetryPreventsInput()
    {
        using var cancel = new CancellationTokenSource();
        var runner = new UiRunner("ERROR: null root node returned by UiTestAutomationBridge.", Wrap(SignInNode));
        var operation = Service(runner).ExecuteUiStepAsync(Serial, new(UiTestAction.Tap, "提交表单"), cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        Assert.Single(runner.Requests);
        Assert.Empty(runner.InputCommands);
    }
    private static string Wrap(string nodes) => $"""<?xml version="1.0" encoding="UTF-8"?><hierarchy rotation="0">{nodes}</hierarchy>""";

    // The fake handles every command; AdbService only checks that its configured executable exists.
    private static AdbService Service(UiRunner runner) => new(runner, typeof(AdbUiTestServiceTests).Assembly.Location);

    private sealed class UiRunner(params string[] outputs) : ICommandRunner
    {
        private readonly Queue<string> _outputs = new(outputs);
        public int FirstExitCode { get; init; }
        public List<CommandRequest> Requests { get; } = [];
        public IEnumerable<CommandRequest> InputCommands => Requests.Where(request => request.Arguments.Contains("input"));

        public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            Assert.Equal(["-s", Serial], request.Arguments.Take(2));
            var isDump = request.Arguments.Any(argument => argument.Contains("uiautomator", StringComparison.Ordinal));
            var expectsOutput = isDump || request.Arguments.Contains("resolve-activity") || request.Arguments.Contains("am");
            var output = expectsOutput ? _outputs.Dequeue() : string.Empty;
            var exitCode = Requests.Count == 1 ? FirstExitCode : 0;
            return Task.FromResult(new CommandResult(exitCode, output, string.Empty, TimeSpan.Zero, false, false));
        }
    }
}
