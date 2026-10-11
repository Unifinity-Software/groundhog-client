using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);
var web = builder.AddProject<Projects.Web>("web").WithHttpEndpoint(name: "http");

if (builder.ExecutionContext.IsRunMode)
{
    // Replace aspire-quickstart with the tunnel name you created in Groundhog: the name only, without https:// or a
    // domain. Aspire then asks for that tunnel's connection key as the groundhog-<name>-key parameter.
    web.WithGroundhogTunnel("aspire-quickstart", configure: tunnel =>
    {
        tunnel.WithRequestLogging();
        web.WithEnvironment("PublicOrigin", tunnel.Resource.PublicAddress);
    });
}

builder.Build().Run();
