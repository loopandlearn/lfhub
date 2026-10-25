using LFHub.Api;
using LFHub.Apns;
using LFHub.Db;
using LFHub.Engine;
using Npgsql;

var builder = WebApplication.CreateSlimBuilder(args);
var logBuffer = new HubLogBuffer();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});
builder.Logging.AddProvider(logBuffer);

string Required(string name) => builder.Configuration[name] is { Length: > 0 } v
    ? v
    : throw new InvalidOperationException($"{name} is required");

var dataSource = new NpgsqlDataSourceBuilder(Required("DatabaseConnectionstring")).Build();
var box = new SecretBox(Required("LFHUB_MASTER_KEY"));
var allowPrivateSources = string.Equals(builder.Configuration["LFHUB_ALLOW_PRIVATE_NIGHTSCOUT"], "true",
    StringComparison.OrdinalIgnoreCase);

builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.MimeTypes = ["application/json"];
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new HubStore(dataSource, box, sp.GetRequiredService<ILogger<HubStore>>()));
builder.Services.AddSingleton<IHubStore>(sp => sp.GetRequiredService<HubStore>());
builder.Services.AddSingleton(logBuffer);
builder.Services.AddSingleton<ApnsClient>();
builder.Services.AddSingleton<IApnsClient>(sp => sp.GetRequiredService<ApnsClient>());
builder.Services.AddSingleton(sp => new HubEngine(sp.GetRequiredService<IHubStore>(),
    sp.GetRequiredService<IApnsClient>(), sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<TimeProvider>())
{
    AllowPrivateSources = allowPrivateSources,
});
builder.Services.AddHostedService(sp => sp.GetRequiredService<HubEngine>());

var app = builder.Build();
app.Services.GetRequiredService<HubStore>().Migrate();
app.UseResponseCompression();
HubApi.Map(app, Required("LFHUB_SETUP_TOKEN"),
    builder.Configuration["LFHUB_STATUS_KEY"] is { Length: > 0 } statusKey ? statusKey : null, allowPrivateSources);
app.Run();
