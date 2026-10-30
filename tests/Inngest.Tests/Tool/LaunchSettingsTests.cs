using Inngest.Tool;

namespace Inngest.Tests.Tool;

public sealed class LaunchSettingsTests : IDisposable
{
    private readonly TempDirectory _project = new();

    public void Dispose() => _project.Dispose();

    private AppUrl? Find(string json, string? path = null)
    {
        Directory.CreateDirectory(_project.Combine("Properties"));
        File.WriteAllText(_project.Combine("Properties", "launchSettings.json"), json);
        return LaunchSettings.Find(path ?? _project.Path);
    }

    [Fact]
    public void The_app_is_called_where_its_launch_profile_listens()
    {
        var app = Find("""
            { "profiles": { "http": { "commandName": "Project", "applicationUrl": "http://localhost:5280" } } }
            """);

        app.ShouldNotBeNull();
        app.Url.ShouldBe("http://localhost:5280/api/inngest");
        app.Profile.ShouldBe("http");
        app.File.ShouldBe(_project.Combine("Properties", "launchSettings.json"));
    }

    [Fact]
    public void The_http_address_is_chosen_from_the_list_of_a_profile_that_also_has_https()
    {
        Find("""
            { "profiles": { "https": { "commandName": "Project", "applicationUrl": "https://localhost:7280;http://localhost:5280" } } }
            """)!.Url.ShouldBe("http://localhost:5280/api/inngest");
    }

    [Fact]
    public void A_profile_that_has_only_https_gives_nothing_since_the_dev_server_may_not_trust_the_certificate()
    {
        Find("""
            { "profiles": { "https": { "commandName": "Project", "applicationUrl": "https://localhost:7280" } } }
            """).ShouldBeNull();
    }

    [Fact]
    public void The_first_profile_that_runs_the_project_with_an_address_decides()
    {
        Find("""
            {
              "profiles": {
                "IIS Express": { "commandName": "IISExpress", "launchBrowser": true },
                "Docker": { "commandName": "Docker", "applicationUrl": "http://localhost:9999" },
                "worker": { "commandName": "Project" },
                "first": { "commandName": "project", "applicationUrl": "http://localhost:5001" },
                "second": { "commandName": "Project", "applicationUrl": "http://localhost:5002" }
              }
            }
            """)!.Url.ShouldBe("http://localhost:5001/api/inngest");
    }

    [Theory]
    [InlineData("http://*:5280", "http://localhost:5280/api/inngest")]
    [InlineData("http://+:5280", "http://localhost:5280/api/inngest")]
    [InlineData("http://0.0.0.0:5280", "http://localhost:5280/api/inngest")]
    [InlineData("http://[::]:5280", "http://localhost:5280/api/inngest")]
    [InlineData("http://127.0.0.1:5280", "http://127.0.0.1:5280/api/inngest")]
    [InlineData("http://shop.dev.localhost:5280", "http://shop.dev.localhost:5280/api/inngest")]
    [InlineData("http://localhost:5280/", "http://localhost:5280/api/inngest")]
    [InlineData("HTTP://LOCALHOST:5280", "http://localhost:5280/api/inngest")]
    public void A_wildcard_address_is_called_on_localhost(string applicationUrl, string expected)
    {
        Find($$"""
            { "profiles": { "http": { "commandName": "Project", "applicationUrl": "{{applicationUrl}}" } } }
            """)!.Url.ShouldBe(expected);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_allowed_as_in_the_files_visual_studio_writes()
    {
        Find("""
            {
              // Only the profile for the project is used.
              "profiles": {
                "http": {
                  "commandName": "Project",
                  "applicationUrl": "http://localhost:5280", /* the port */
                },
              },
            }
            """)!.Url.ShouldBe("http://localhost:5280/api/inngest");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{ "profiles": [] }""")]
    [InlineData("""{ "profiles": { "http": "Project" } }""")]
    [InlineData("""{ "profiles": { "http": { "commandName": 1, "applicationUrl": 2 } } }""")]
    [InlineData("""{ "profiles": { "http": { "commandName": "Project", "applicationUrl": "not a url" } } }""")]
    public void A_file_without_an_address_gives_nothing_and_is_no_error(string json)
    {
        Find(json).ShouldBeNull();
    }

    [Fact]
    public void A_folder_without_launch_settings_gives_nothing()
    {
        LaunchSettings.Find(_project.Path).ShouldBeNull();
    }

    [Fact]
    public void A_project_file_stands_for_its_folder()
    {
        File.WriteAllText(_project.Combine("Shop.csproj"), "<Project />");
        Find("""
            { "profiles": { "http": { "commandName": "Project", "applicationUrl": "http://localhost:5280" } } }
            """, _project.Combine("Shop.csproj"))!.Url.ShouldBe("http://localhost:5280/api/inngest");
    }
}
