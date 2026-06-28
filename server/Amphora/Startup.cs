using Microsoft.ServiceFabric.Services.Communication.AspNetCore;
using PithosDB.Core;

public static class Startup
{
    public static WebApplication CreateApp(string url, AspNetCoreCommunicationListener listener)
    {
        var builder = WebApplication.CreateBuilder();

        builder.WebHost
            .UseKestrel()
            .UseServiceFabricIntegration(listener, ServiceFabricIntegrationOptions.None)
            .UseUrls(url);

        builder.Services.AddOpenApi();
        builder.Services.AddControllers();

        var dataDir = builder.Configuration["Amphora:DataDirectory"]
            ?? Path.Combine(AppContext.BaseDirectory, "data");

        builder.Services.AddSingleton<PithosDb>(_ => new PithosDb(dataDir));

        var app = builder.Build();

        if (app.Environment.IsDevelopment())
            app.MapOpenApi();

        _ = app.Services.GetRequiredService<PithosDb>();

        app.MapControllers();

        return app;
    }
}
