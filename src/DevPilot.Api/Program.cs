using DevPilot.Infrastructure;
using Hangfire;
using Hangfire.PostgreSql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddHangfire(configuration =>
{
    var connectionString = builder.Configuration.GetConnectionString("DevPilotDb")
        ?? throw new InvalidOperationException("Connection string 'DevPilotDb' is not configured.");

    configuration.UsePostgreSqlStorage(options =>
    {
        options.UseNpgsqlConnection(connectionString);
    });
});
builder.Services.AddHangfireServer();

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
