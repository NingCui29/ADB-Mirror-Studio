using AdbMirrorStudio.Application.Adb;
using AdbMirrorStudio.Application.Commands;
using AdbMirrorStudio.Infrastructure.Adb;
using AdbMirrorStudio.Infrastructure.Processes;

namespace AdbMirrorStudio.UnitTests;

public sealed class UiTreeOutputCaptureTests
{
    [Theory]
    [InlineData(0, OutputTruncation.None)]
    [InlineData(1, OutputTruncation.CharacterLimit)]
    public async Task Runner_ReportsActualOverflowIndependentlyForEachStream(int extraCharacters, OutputTruncation expected)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, new string('x', CommandResult.CaptureCharacterLimit + extraCharacters));
            var shell = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var request = new CommandRequest(shell, ["/d", "/c", $"type {Path.GetFileName(path)}"],
                WorkingDirectory: Path.GetDirectoryName(path));
            var result = await new ProcessCommandRunner().RunAsync(request);

            Assert.True(result.IsSuccess);
            Assert.Equal(expected, result.StandardOutputTruncation);
            Assert.Equal(OutputTruncation.None, result.StandardErrorTruncation);
            Assert.Equal(extraCharacters != 0, result.StandardOutput.EndsWith("[输出已截断]", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(OutputTruncation.CharacterLimit, "读取上限")]
    [InlineData(OutputTruncation.Interrupted, "输出中断")]
    [InlineData(OutputTruncation.None, "XML 格式无效")]
    public async Task InspectUi_DistinguishesOutputCaptureFailureFromMalformedXml(OutputTruncation reason, string message)
    {
        var result = Completed("<hierarchy><node") with { StandardOutputTruncation = reason };
        var service = CreateService(result);
        var exception = await Assert.ThrowsAsync<AdbCommandException>(() => service.InspectUiAsync("emulator-5554"));
        Assert.Contains(message, exception.Message);
    }

    [Fact]
    public async Task InspectUi_LiteralMarkerAndTruncatedStderrDoNotInvalidateCompleteXml()
    {
        var result = Completed("<hierarchy><node text=\"[输出已截断]\"/></hierarchy>") with
        {
            StandardError = "[输出已截断]",
            StandardErrorTruncation = OutputTruncation.CharacterLimit
        };
        var nodes = await CreateService(result).InspectUiAsync("emulator-5554");
        Assert.Equal("[输出已截断]", Assert.Single(nodes).Text);
    }

    [Fact]
    public async Task InspectUi_CompleteXmlAtCharacterLimitCanBeParsed()
    {
        var xml = "<hierarchy><node/></hierarchy>".PadRight(CommandResult.CaptureCharacterLimit);
        Assert.Single(await CreateService(Completed(xml)).InspectUiAsync("emulator-5554"));
    }

    private static CommandResult Completed(string xml) => new(0, xml, "", TimeSpan.Zero, false, false);

    private static AdbService CreateService(CommandResult result) =>
        new(new ResultRunner(result), typeof(UiTreeOutputCaptureTests).Assembly.Location);

    private sealed class ResultRunner(CommandResult result) : ICommandRunner
    {
        public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }
}
