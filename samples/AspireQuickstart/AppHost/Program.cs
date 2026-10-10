using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);
var web = builder.AddProject<Projects.Web>("web").WithHttpEndpoint(name: "http");

if (builder.ExecutionContext.IsRunMode)
{
    var relay = builder.AddParameter("groundhog-relay");
    var name = builder.AddParameter("groundhog-name");
    var key = builder.AddParameter("groundhog-key", secret: true);
    var tunnel = builder.AddGroundhogTunnel("tunnel", relay, name, key)
        .WithReference(web.GetEndpoint("http"))
        .WithRequestLogging();
    web.WithEnvironment("PublicOrigin", tunnel.Resource.PublicAddress);
}

builder.Build().Run();
