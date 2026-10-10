var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/", (HttpRequest request, IConfiguration configuration) => new
{
    message = "Hello through Groundhog",
    publicOrigin = configuration["PublicOrigin"],
    host = request.Host.Value,
});
app.Run();
