using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace StreamOrchestrator.IntegrationTests.Support;

/// <summary>
/// Collects the measurements of the meters created by one IMeterFactory only, so tests running in
/// parallel never see each other's instruments.
/// </summary>
internal sealed class MetricsCollector : IDisposable
{
    private readonly MeterListener listener = new();
    private readonly ConcurrentDictionary<string, long> counters = new();
    private readonly ConcurrentDictionary<string, int> gauges = new();
    private int histogramRecords;

    public MetricsCollector(IMeterFactory scope)
    {
        listener.InstrumentPublished = (instrument, subscription) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, scope))
            {
                subscription.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, value, _, _) => counters.AddOrUpdate(
                instrument.Name,
                value,
                (_, total) => total + value
            )
        );
        listener.SetMeasurementEventCallback<int>(
            (instrument, value, _, _) => gauges[instrument.Name] = value
        );
        listener.SetMeasurementEventCallback<double>(
            (_, _, _, _) => Interlocked.Increment(ref histogramRecords)
        );
        listener.Start();
    }

    public int DurationRecords => Volatile.Read(ref histogramRecords);

    public long Counter(string name) => counters.GetValueOrDefault(name);

    public int Gauge(string name)
    {
        listener.RecordObservableInstruments();
        return gauges.GetValueOrDefault(name);
    }

    public void Dispose() => listener.Dispose();
}
