using Inngest.Tool;

namespace Inngest.Tests.Tool;

public sealed class ChildProcessTests : IDisposable
{
    private const string Shell = "/bin/sh";

    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    // A script that writes the arguments it got, one per line, and exits with the code named by the first. The shell
    // runs it, so that it needs no execute bit and the temporary folder may be one that forbids running programs.
    private string Script()
    {
        var path = _directory.Combine("program.sh");
        File.WriteAllText(path, "code=\"$1\"\nshift\nprintf '%s\\n' \"$@\" > \"$(dirname \"$0\")/arguments.txt\"\nexit \"$code\"\n");
        return path;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(42)]
    public async Task The_exit_code_of_the_program_is_returned(int code)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The program is a shell script.");

        var exitCode = await ChildProcess.RunAsync(Shell, [Script(), code.ToString()]);

        exitCode.ShouldBe(code);
    }

    [Fact]
    public async Task The_arguments_reach_the_program_one_by_one_with_their_spaces_and_quotes()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The program is a shell script.");

        await ChildProcess.RunAsync(
            Shell, [Script(), "0", "dev", "-u", "http://localhost:5280/api/inngest", "--config", "my \"quoted\" config.json", ""]);

        File.ReadAllLines(_directory.Combine("arguments.txt"))
            .ShouldBe(["dev", "-u", "http://localhost:5280/api/inngest", "--config", "my \"quoted\" config.json", ""]);
    }

    [Fact]
    public async Task A_program_that_cannot_be_started_is_reported_with_its_path()
    {
        var path = _directory.Combine("missing");

        var exception = await Should.ThrowAsync<ToolException>(() => ChildProcess.RunAsync(path, []));

        exception.Message.ShouldStartWith($"Could not start {path}");
    }
}
