using System.Diagnostics.Metrics;

namespace StreamOrchestrator.IntegrationTests.Support;

/// <summary>
/// Creates meters scoped to this factory, so a MeterListener can subscribe to the meters of one test only
/// and tests running in parallel do not see each other's measurements.
/// </summary>
internal sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> meters = [];

    public Meter Create(MeterOptions options)
    {
        var meter = new Meter(options.Name, options.Version, options.Tags, scope: this);
        lock (meters)
        {
            meters.Add(meter);
        }

        return meter;
    }

    public void Dispose()
    {
        lock (meters)
        {
            meters.ForEach(meter => meter.Dispose());
            meters.Clear();
        }
    }
}
