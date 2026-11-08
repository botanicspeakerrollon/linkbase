using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Linkbase.Core;

// Traces, metrics and logs in OpenTelemetry's standard form, for whatever collector a platform runs: Dynatrace, Elastic,
// Grafana, Azure Monitor. Nothing is sent unless OTEL_EXPORTER_OTLP_ENDPOINT is set, and the other standard OTEL_
// variables (protocol, headers, resource attributes, intervals) work as usual. Logs always go to stdout as well.
public static class Telemetry
{
    public const string ServiceName = "linkbase";

    public static void Add(IServiceCollection services, IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            return;
        }

        var version = typeof(Telemetry).Assembly.GetName().Version?.ToString();
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(configuration["OTEL_SERVICE_NAME"] ?? ServiceName, serviceVersion: version))
            .WithTracing(tracing => tracing
                // Probes would otherwise be most of the traces.
                .AddAspNetCoreInstrumentation(options => options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddSource("Npgsql"))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("Npgsql"))
            .UseOtlpExporter();
    }
}
