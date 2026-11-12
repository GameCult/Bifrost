using Bifrost.Web.Configuration;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Tests;

public sealed class HeimdallAppSecretFileTests : IDisposable
{
    private const string FileSecret = "file-secret-7f3a91c2";
    private const string EnvSecret = "env-secret-0b5d44e8";

    private readonly string _directory = Directory.CreateTempSubdirectory("bifrost-appsecret-").FullName;
    private readonly CapturingLoggerProvider _logs = new();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Write(string content)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, content);
        return path;
    }

    private HeimdallOptions Resolve(string appSecret = "", string appSecretFile = "")
    {
        var options = new HeimdallOptions { AppSecret = appSecret, AppSecretFile = appSecretFile };
        HeimdallOptions.ResolveAppSecret(options, _logs.CreateLogger("test"));
        return options;
    }

    private void AssertNoSecretLogged()
    {
        foreach (var line in _logs.Lines)
        {
            Assert.DoesNotContain(FileSecret, line);
            Assert.DoesNotContain(EnvSecret, line);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void The_file_is_the_secret_with_only_the_trailing_newline_trimmed(string ending)
    {
        var options = Resolve(appSecretFile: Write(FileSecret + ending));

        Assert.Equal(FileSecret, options.AppSecret);
        Assert.False(options.IsDiscordConfigured); // no guild configured
        Assert.True(options.IsPatreonConfigured);
        Assert.Empty(_logs.Lines);
    }

    [Fact]
    public void Inner_whitespace_survives_the_trim()
    {
        Assert.Equal(" a b ", Resolve(appSecretFile: Write(" a b \n")).AppSecret);
    }

    [Fact]
    public void The_file_wins_over_the_environment_value_and_the_deprecation_is_logged()
    {
        var options = Resolve(EnvSecret, Write(FileSecret + "\n"));

        Assert.Equal(FileSecret, options.AppSecret);
        Assert.Contains(_logs.Lines, line => line.Contains("deprecated"));
        AssertNoSecretLogged();
    }

    [Fact]
    public void A_missing_file_fails_closed_naming_option_and_path_but_no_secret()
    {
        var path = Path.Combine(_directory, "absent");

        var failure = Assert.Throws<InvalidOperationException>(() => Resolve(EnvSecret, path));

        Assert.Contains("Heimdall:AppSecretFile", failure.Message);
        Assert.Contains(path, failure.Message);
        Assert.DoesNotContain(EnvSecret, failure.Message);
    }

    [Fact]
    public void A_directory_in_place_of_the_file_fails_closed()
    {
        Assert.Throws<InvalidOperationException>(() => Resolve(appSecretFile: _directory));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("  \r\n")]
    public void An_empty_file_fails_closed_even_when_the_environment_value_is_set(string content)
    {
        var path = Write(content);

        var failure = Assert.Throws<InvalidOperationException>(() => Resolve(EnvSecret, path));

        Assert.Contains("Heimdall:AppSecretFile", failure.Message);
        Assert.Contains(path, failure.Message);
        Assert.DoesNotContain(EnvSecret, failure.Message);
    }

    [Fact]
    public void The_environment_value_alone_still_works_and_warns_without_logging_it()
    {
        var options = Resolve(EnvSecret);

        Assert.Equal(EnvSecret, options.AppSecret);
        Assert.Single(_logs.Lines, line => line.Contains("deprecated") && line.Contains("Heimdall:AppSecretFile"));
        AssertNoSecretLogged();
    }

    [Fact]
    public void Neither_set_is_unconfigured_as_before_and_silent()
    {
        var options = Resolve();

        Assert.Equal(string.Empty, options.AppSecret);
        Assert.False(options.IsPatreonConfigured);
        Assert.Empty(_logs.Lines);
    }

    private TestWebApplicationFactory Host(string file) => new TestHost(file, _logs);

    private sealed class TestHost(string file, CapturingLoggerProvider logs) : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Heimdall:AppSecretFile"] = file }));
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
        }
    }

    [Fact]
    public void The_running_host_resolves_the_file_into_its_options()
    {
        using var host = Host(Write(FileSecret + "\n"));

        Assert.Equal(FileSecret, host.Services.GetRequiredService<IOptions<HeimdallOptions>>().Value.AppSecret);
    }

    [Fact]
    public void The_running_host_refuses_to_start_on_a_missing_file_in_every_environment()
    {
        using var host = Host(Path.Combine(_directory, "absent"));

        var failure = Assert.ThrowsAny<Exception>(() => host.CreateClient());

        Assert.Contains("Heimdall:AppSecretFile", failure.ToString());
    }
}
