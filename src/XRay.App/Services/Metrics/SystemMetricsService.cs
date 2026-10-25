using Microsoft.Extensions.Logging;
using XRay.Core.Collections;

namespace XRay.App.Services.Metrics;

/// <summary>
/// Polls system metrics on a background task at the configured interval and publishes snapshots on the UI thread.
/// Session history is kept in fixed-size ring buffers, so memory use is bounded.
/// Per-process sampling (the most expensive part) only runs while a screen requests it.
/// </summary>
public sealed class SystemMetricsService : IDisposable
{
    public const int HistoryCapacity = 180;

    private readonly SettingsService _settings;
    private readonly AppLifetime _lifetime;
    private readonly ILogger<SystemMetricsService> _logger;
    private readonly CpuSampler _cpu = new();
    private readonly GpuMonitor _gpu = new();
    private readonly DiskActivitySampler _disk = new();
    private readonly ProcessSampler _processes = new();
    private SynchronizationContext? _ui;
    private CancellationTokenSource? _loopCts;
    private int _processConsumers;
    private DateTime _lastDriveSample = DateTime.MinValue;
    private IReadOnlyList<DriveSample> _drives = [];

    public SystemMetricsService(SettingsService settings, AppLifetime lifetime, ILogger<SystemMetricsService> logger)
    {
        _settings = settings;
        _lifetime = lifetime;
        _logger = logger;
        _settings.Changed += (_, _) =>
        {
            if (_loopCts is not null && CurrentInterval != _settings.Current.PollingIntervalMs)
            {
                Restart();
            }
        };
    }

    public MetricsSnapshot? Latest { get; private set; }

    public RingBuffer<double> CpuHistory { get; } = new(HistoryCapacity);

    public RingBuffer<double> MemoryHistory { get; } = new(HistoryCapacity);

    public RingBuffer<double> GpuHistory { get; } = new(HistoryCapacity);

    public RingBuffer<double> DiskReadHistory { get; } = new(HistoryCapacity);

    public RingBuffer<double> DiskWriteHistory { get; } = new(HistoryCapacity);

    /// <summary>Incremented each time new data is published; bind charts to it to trigger redraws.</summary>
    public long Revision { get; private set; }

    public int CurrentInterval { get; private set; }

    /// <summary>Raised on the UI thread after <see cref="Latest"/> and the history buffers were updated.</summary>
    public event EventHandler? Updated;

    /// <summary>Starts polling. Must be called on the UI thread.</summary>
    public void Start()
    {
        _ui = SynchronizationContext.Current;
        Restart();
    }

    /// <summary>Enables per-process sampling until the returned handle is disposed.</summary>
    public IDisposable RequestProcessSampling()
    {
        Interlocked.Increment(ref _processConsumers);
        return new Release(() =>
        {
            if (Interlocked.Decrement(ref _processConsumers) == 0)
            {
                _processes.Reset();
            }
        });
    }

    public void Dispose()
    {
        _loopCts?.Cancel();
        _disk.Dispose();
    }

    private void Restart()
    {
        _loopCts?.Cancel();
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Stopping);
        CurrentInterval = _settings.Current.PollingIntervalMs;
        CancellationToken token = _loopCts.Token;
        _ = Task.Run(() => LoopAsync(TimeSpan.FromMilliseconds(CurrentInterval), token), token);
    }

    private readonly object _sampleLock = new();
    private bool _initialized;

    private async Task LoopAsync(TimeSpan interval, CancellationToken token)
    {
        try
        {
            lock (_sampleLock)
            {
                if (!_initialized)
                {
                    // Counter and DXGI initialisation can take several seconds, so it happens off the UI thread,
                    // after publishing the cheap metrics (memory, drives) so the UI is not empty meanwhile.
                    _initialized = true;
                    _cpu.Sample();
                    MetricsSnapshot early = SampleOnce();
                    _ui?.Post(_ => Publish(early), null);
                    _gpu.Initialize();
                    _disk.Initialize();
                    _logger.LogInformation("Metrics initialised. GPU: {Gpu}. Disk counters: {Disk}",
                        _gpu.UnavailableReason ?? "available", _disk.UnavailableReason ?? "available");
                }
            }

            using var timer = new PeriodicTimer(interval);
            do
            {
                MetricsSnapshot snapshot;
                lock (_sampleLock)
                {
                    // A restarted loop may briefly overlap the previous one; samplers are not thread-safe.
                    snapshot = SampleOnce();
                }

                _ui?.Post(_ => Publish(snapshot), null);
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Metrics polling stopped unexpectedly");
        }
    }

    private MetricsSnapshot SampleOnce()
    {
        double? cpu = _cpu.Sample();
        MemoryInfo? memory = MemorySampler.Sample();
        IReadOnlyList<GpuInfo> gpus = _gpu.Sample();
        DiskActivity? disk = _disk.Sample();

        if (DateTime.UtcNow - _lastDriveSample > TimeSpan.FromSeconds(5))
        {
            _drives = DriveSampler.Sample();
            _lastDriveSample = DateTime.UtcNow;
        }

        List<ProcessSample>? processes = null;
        int? withoutAccess = null;
        if (Volatile.Read(ref _processConsumers) > 0)
        {
            (processes, int denied) = _processes.Sample();
            withoutAccess = denied;
        }

        return new MetricsSnapshot(
            DateTime.UtcNow,
            cpu,
            cpu is null && Latest is not null ? "CPU times could not be read." : null,
            memory,
            gpus,
            _gpu.UnavailableReason,
            disk,
            _disk.UnavailableReason,
            _drives,
            processes,
            withoutAccess);
    }

    private void Publish(MetricsSnapshot snapshot)
    {
        Latest = snapshot;
        if (snapshot.CpuPercent is { } cpu)
        {
            CpuHistory.Add(cpu);
        }

        if (snapshot.Memory is { } mem)
        {
            MemoryHistory.Add(mem.UsedPercent);
        }

        if (snapshot.Gpus.Count > 0 && snapshot.Gpus[0].UtilizationPercent is { } gpu)
        {
            GpuHistory.Add(gpu);
        }

        if (snapshot.Disk is { } disk)
        {
            DiskReadHistory.Add(disk.ReadBytesPerSecond ?? 0);
            DiskWriteHistory.Add(disk.WriteBytesPerSecond ?? 0);
        }

        Revision++;
        Updated?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Release(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}
