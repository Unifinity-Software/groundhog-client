using Groundhog.Protocol;

namespace Groundhog.Cli.Tests;

public class GroundhogCommandTests
{
    private static readonly string _key = ConnectionKey.Generate();

    [Fact]
    public void Uses_hosted_service_unless_relay_is_overridden()
    {
        var (hosted, error, _) = HostArguments.Parse(["myapp", "--port", "5173"], Environment(_key));
        Assert.Null(error);
        Assert.Equal(new Uri(Groundhog.Agent.GroundhogService.DefaultRelayAddress), hosted!.Relay);
        var (custom, customError, _) = HostArguments.Parse(
            ["myapp", "--port", "5173", "--relay", "https://tunnel.example.com"], Environment(_key));
        Assert.Null(customError);
        Assert.Equal(new Uri("https://tunnel.example.com"), custom!.Relay);
    }

    [Theory]
    [InlineData("https://localhost:7132")]
    [InlineData("http://127.0.0.1:5173")]
    public void Parses_explicit_targets_and_local_certificate_options(string target)
    {
        var (arguments, error, _) = HostArguments.Parse(
            ["myapp", $"--target={target}", "--relay", "https://tunnel.example.com", "--local-ca", "local-ca.pem"], Environment(_key));
        Assert.Null(error);
        Assert.Equal(new Uri(target), arguments!.LocalTarget);
        Assert.Equal("local-ca.pem", arguments.LocalCaFile);
    }

    [Fact]
    public async Task Insecure_local_tls_prints_a_warning()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var error = new StringWriter();
        var exitCode = await GroundhogCommand.RunAsync(
            ["host", "myapp", "--target", "https://localhost:7132", "--relay", "https://tunnel.example.com", "--insecure-local"],
            Environment(_key), new StringWriter(), error, stop.Token);
        Assert.Equal(ExitCodes.Stopped, exitCode);
        Assert.Contains("WARNING:", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(_key, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_the_key_from_GROUNDHOG_KEY_unless_given_as_an_option()
    {
        string[] args = ["myapp", "--port", "5173", "--relay", "https://tunnel.example.com", "--pool=3", "-v"];

        var (fromEnvironment, error, _) = HostArguments.Parse(args, Environment(_key));
        Assert.Null(error);
        Assert.Equal(
            new HostArguments("myapp", 5173, new Uri("https://tunnel.example.com"), _key, 3, Verbose: true),
            fromEnvironment);

        var optionKey = ConnectionKey.Generate();
        var (fromOption, _, _) = HostArguments.Parse([.. args, "--key", optionKey], Environment(_key));
        Assert.Equal(optionKey, fromOption!.Key);
    }

    [Theory]
    [InlineData("host", "myapp", "--relay", "https://tunnel.example.com")] // no port
    [InlineData("host", "myapp", "--port", "70000", "--relay", "https://tunnel.example.com")]
    [InlineData("host", "MyApp", "--port", "5173", "--relay", "https://tunnel.example.com")] // invalid name
    [InlineData("host", "myapp", "--port", "5173", "--relay", "https://tunnel.example.com", "--pool", "9")]
    [InlineData("host", "myapp", "--port", "5173", "--relay", "https://tunnel.example.com", "--kye", "KEY")]
    [InlineData("host", "myapp", "--port", "5173", "--relay", "https://tunnel.example.com", "--key", "not-a-key-KEY")]
    [InlineData("host", "myapp", "--port", "5173", "--target", "https://localhost:7132", "--relay", "https://tunnel.example.com")]
    [InlineData("host", "myapp", "--target", "ftp://localhost", "--relay", "https://tunnel.example.com")]
    [InlineData("host", "myapp", "--target", "https://KEY@localhost", "--relay", "https://tunnel.example.com")]
    [InlineData("host", "myapp", "--target", "https://localhost/path?KEY", "--relay", "https://tunnel.example.com")]
    [InlineData("host", "myapp", "--target", "https://example.com", "--insecure-local", "--relay", "https://tunnel.example.com")]
    [InlineData("host", "myapp", "--port", "5173", "--insecure-local", "--relay", "https://tunnel.example.com")]
    [InlineData("host", "myapp", "--target", "https://localhost", "--local-ca", "KEY-missing.pem", "--relay", "https://tunnel.example.com")]
    [InlineData("host", "myapp", "--target", "https://localhost", "--local-ca", "KEY-missing.pem", "--insecure-local", "--relay", "https://tunnel.example.com")]
    [InlineData("host", "myapp", "--target", "https://localhost", "--insecure-local=true", "--relay", "https://tunnel.example.com")]
    public async Task Bad_arguments_exit_2_without_repeating_the_key(params string[] args)
    {
        // "KEY" stands for the secret: a mistyped option or a malformed key must never be echoed back.
        args = args.Select(arg => arg.Replace("KEY", _key, StringComparison.Ordinal)).ToArray();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await GroundhogCommand.RunAsync(
            args, Environment(_key), output, error, TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.BadArguments, exitCode);
        Assert.NotEmpty(error.ToString());
        Assert.DoesNotContain(_key, output + error.ToString(), StringComparison.Ordinal);
    }

    private static Func<string, string?> Environment(string key) =>
        name => name == HostArguments.KeyVariable ? key : null;
}
