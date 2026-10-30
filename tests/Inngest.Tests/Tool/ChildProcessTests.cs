using Inngest.Tool;

namespace Inngest.Tests.Tool;

public sealed class ChildProcessTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    // A program that writes the arguments it got, one per line, and exits with the code named by the first.
    private string Script()
    {
        var path = _directory.Combine("program");
        File.WriteAllText(path, "#!/bin/sh\ncode=\"$1\"\nshift\nprintf '%s\\n' \"$@\" > \"$(dirname \"$0\")/arguments.txt\"\nexit \"$code\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(42)]
    public async Task The_exit_code_of_the_program_is_returned(int code)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The program is a shell script.");

        var exitCode = await ChildProcess.RunAsync(Script(), [code.ToString()]);

        exitCode.ShouldBe(code);
    }

    [Fact]
    public async Task The_arguments_reach_the_program_one_by_one_with_their_spaces_and_quotes()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The program is a shell script.");

        await ChildProcess.RunAsync(Script(), ["0", "dev", "-u", "http://localhost:5280/api/inngest", "--config", "my \"quoted\" config.json", ""]);

        File.ReadAllLines(_directory.Combine("arguments.txt")).ShouldBe(["dev", "-u", "http://localhost:5280/api/inngest", "--config", "my \"quoted\" config.json", ""]);
    }

    [Fact]
    public async Task A_program_that_cannot_be_started_is_reported_with_its_path()
    {
        var path = _directory.Combine("missing");

        var exception = await Should.ThrowAsync<ToolException>(() => ChildProcess.RunAsync(path, []));

        exception.Message.ShouldStartWith($"Could not start {path}");
    }
}
