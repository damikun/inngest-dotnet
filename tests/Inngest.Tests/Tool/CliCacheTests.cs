using Inngest.Tool;
using Inngest.Tool.Download;

namespace Inngest.Tests.Tool;

public sealed class CliCacheTests
{
    [Fact]
    public void The_cache_is_a_folder_of_its_own_in_the_local_application_data()
    {
        CliCache.Root(null, "/home/ada/.local/share").ShouldBe(Path.Combine("/home/ada/.local/share", "inngest-dotnet", "cli"));
    }

    [Fact]
    public void A_folder_in_the_variable_replaces_it_and_is_made_absolute()
    {
        var folder = Path.Combine(Path.GetTempPath(), "inngest-cache");

        CliCache.Root(folder, "/home/ada/.local/share").ShouldBe(folder);
        CliCache.Root(Path.Combine("relative", "cache"), "/home/ada/.local/share").ShouldBe(Path.GetFullPath(Path.Combine("relative", "cache")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_variable_that_is_empty_is_not_set(string? configured)
    {
        CliCache.Root(configured, "/home/ada/.local/share").ShouldBe(Path.Combine("/home/ada/.local/share", "inngest-dotnet", "cli"));
    }

    [Fact]
    public void A_user_without_a_home_folder_is_told_which_variable_to_set()
    {
        Should.Throw<ToolException>(() => CliCache.Root(null, "")).Message.ShouldContain("INNGEST_DOTNET_CACHE");
        CliCache.Root("/var/cache/inngest", "").ShouldBe(Path.GetFullPath("/var/cache/inngest"));
    }
}
