using DevPilot.Infrastructure;
using Hangfire;
using Hangfire.PostgreSql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddInfrastructure(builder.Configuration);

if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddSimpleConsole(options =>
    {
        options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss.fff] ";
        options.SingleLine = true;
    });
}

builder.Services.AddHangfire(configuration =>
{
    var connectionString = builder.Configuration.GetConnectionString("DevPilotDb")
        ?? throw new InvalidOperationException("Connection string 'DevPilotDb' is not configured.");

    configuration.UsePostgreSqlStorage(options =>
    {
        options.UseNpgsqlConnection(connectionString);
    });
});
// Each execution runs dotnet builds/tests plus AI calls; cap parallel workers (default 2) so queued
// executions wait instead of competing for CPU, disk and provider rate limits.
var hangfireWorkerCount = Math.Max(1, builder.Configuration.GetValue<int?>("Hangfire:WorkerCount") ?? 2);
builder.Services.AddHangfireServer(options => options.WorkerCount = hangfireWorkerCount);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.UseHangfireDashboard("/hangfire");
}

app.UseHttpsRedirection();

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));

app.Run();
