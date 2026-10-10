using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);
var web = builder.AddProject<Projects.Web>("web").WithHttpEndpoint(name: "http");

if (builder.ExecutionContext.IsRunMode)
{
    var name = builder.AddParameter("groundhog-name")
        .WithDescription("Enter the name you created in Groundhog, for example aspire-quickstart. Enter only the name, without https:// or a domain.");
    var key = builder.AddParameter("groundhog-key", secret: true)
        .WithDescription("Paste the connection key shown when you created this tunnel. Enable Save to user secrets to remember it on this computer. This is not your account password.");
    var tunnel = builder.AddGroundhogTunnel("tunnel", name, key)
        .WithReference(web.GetEndpoint("http"))
        .WithRequestLogging();
    web.WithEnvironment("PublicOrigin", tunnel.Resource.PublicAddress);
}

builder.Build().Run();
