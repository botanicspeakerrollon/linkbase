using Linkbase.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Linkbase.Tests;

public sealed class TelemetryTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        Telemetry.Add(services, new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Nothing_is_set_up_without_a_collector()
    {
        using var services = Build([]);

        Assert.Null(services.GetService<TracerProvider>());
        Assert.Null(services.GetService<MeterProvider>());
    }

    [Fact]
    public void Traces_and_metrics_go_to_the_collector_named()
    {
        using var services = Build(new() { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://collector:4317" });

        Assert.NotNull(services.GetService<TracerProvider>());
        Assert.NotNull(services.GetService<MeterProvider>());
    }
}
