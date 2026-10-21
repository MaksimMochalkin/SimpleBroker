using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

using SimpleBroker.Accounts;
using SimpleBroker.Orders;
using SimpleBroker.Trades;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddAccountsModule(builder.Configuration)
    .AddOrdersModule(builder.Configuration)
    .AddTradesModule(builder.Configuration);

// Endpoint comes from OTEL_EXPORTER_OTLP_ENDPOINT (default http://localhost:4317).
// Set OTEL_SDK_DISABLED=true to turn telemetry off (used by integration tests).
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("simple-broker"))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddMeter("Npgsql")
        .AddMeter("SimpleBroker.*")
        .AddOtlpExporter())
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddSource("Npgsql")
        .AddSource("SimpleBroker.*")
        .AddOtlpExporter());

var app = builder.Build();

app.MapAccountsEndpoints();
app.MapOrdersEndpoints();
app.MapTradesEndpoints();

app.Run();

// Needed by WebApplicationFactory<Program> in integration tests.
public partial class Program;
