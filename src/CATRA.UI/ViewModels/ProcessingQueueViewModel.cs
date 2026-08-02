using System.Collections.ObjectModel;
using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Library;
using CATRA.Core.Models;
using CATRA.Core.Processing;
using CATRA.UI.Converters;
using CATRA.UI.Models;
using CATRA.UI.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CATRA.UI.ViewModels;

/// <summary>
/// Pre-processing queue screen (Tela 3, ST-19). Mirrors the active sliding
/// window: the job currently processing (live progress via
/// <see cref="IProcessingQueueService.ProgressChanged"/>), the episodes in the
/// window with their badge status, and disk used/estimated. Queue events arrive
/// on a background thread, so every mutation is marshalled to the captured
/// <see cref="SynchronizationContext"/> (null in tests → inline).
/// </summary>
public sealed partial class ProcessingQueueViewModel : ObservableObject, IDisposable
{
    private readonly IProcessingQueueService _queue;
    private readonly ISlidingWindowService _window;
    private readonly IProcessedFileRepository _processedFiles;
    private readonly IAppSettingsRepository _settings;
    private readonly ILibraryService _library;
    private readonly IAppNavigator _navigator;
    private readonly SynchronizationContext? _syncContext;

    private int? _mediaItemId;
    private ProcessProfile _profile = ProcessProfile.Local;

    /// <summary>Creates the view model and subscribes to the queue events.</summary>
    public ProcessingQueueViewModel(
        IProcessingQueueService queue,
        ISlidingWindowService window,
        IProcessedFileRepository processedFiles,
        IAppSettingsRepository settings,
        ILibraryService library,
        IAppNavigator navigator)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _processedFiles = processedFiles ?? throw new ArgumentNullException(nameof(processedFiles));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _syncContext = SynchronizationContext.Current;

        _queue.JobStarted += OnJobEvent;
        _queue.JobCompleted += OnJobEvent;
        _queue.JobFailed += OnJobEvent;
        _queue.ProgressChanged += OnProgressChanged;
    }

    /// <summary>Episodes currently in the sliding window, with badge status.</summary>
    public ObservableCollection<WindowEpisodeItem> WindowEpisodes { get; } = [];

    /// <summary>Header series title.</summary>
    [ObservableProperty]
    private string _seriesTitle = "Nenhuma janela ativa";

    /// <summary>Active profile label, e.g. "Local (1080p 135fps)".</summary>
    [ObservableProperty]
    private string _activeProfileLabel = string.Empty;

    /// <summary>Whether a job is currently processing.</summary>
    [ObservableProperty]
    private bool _hasCurrentJob;

    /// <summary>Current job episode label ("EP03").</summary>
    [ObservableProperty]
    private string _currentJobEpisodeLabel = string.Empty;

    /// <summary>Current job episode title.</summary>
    [ObservableProperty]
    private string _currentJobTitle = string.Empty;

    /// <summary>Current job overall progress (0–100).</summary>
    [ObservableProperty]
    private double _currentProgressPct;

    /// <summary>Current pipeline step label, e.g. "Upscale (FSR4)".</summary>
    [ObservableProperty]
    private string _currentStepLabel = string.Empty;

    /// <summary>Combined progress text, e.g. "58% — Upscale (FSR4)".</summary>
    [ObservableProperty]
    private string _currentProgressText = string.Empty;

    /// <summary>Estimated time remaining, e.g. "~3 min".</summary>
    [ObservableProperty]
    private string _etaDisplay = "—";

    /// <summary>Disk used by the series' processed files (bytes).</summary>
    [ObservableProperty]
    private long _diskUsedBytes;

    /// <summary>Estimated disk for a full window (bytes).</summary>
    [ObservableProperty]
    private long _diskEstimatedBytes;

    /// <summary>Formatted disk used ("3.1 GB").</summary>
    [ObservableProperty]
    private string _diskUsedLabel = "0.0 GB";

    /// <summary>Formatted disk estimated ("16.5 GB").</summary>
    [ObservableProperty]
    private string _diskEstimatedLabel = "0.0 GB";

    /// <summary>Disk bar percentage (used / estimated, 0–100).</summary>
    [ObservableProperty]
    private double _diskProgressPct;

    /// <summary>Footer explaining the window rotation (RN-10).</summary>
    public string FooterText { get; } =
        "Ao assistir um episódio → o arquivo processado é apagado e o próximo não-assistido entra na janela.";

    /// <summary>
    /// Loads (or reloads) the queue screen. Uses <paramref name="mediaItemId"/>
    /// when given, otherwise the active window's series.
    /// </summary>
    public async Task LoadAsync(int? mediaItemId = null)
    {
        _mediaItemId = mediaItemId ?? _window.ActiveMediaItemId;
        _profile = _window.ActiveProfile ?? ProcessProfile.Local;

        if (_mediaItemId is null)
        {
            SeriesTitle = "Nenhuma janela ativa";
            ActiveProfileLabel = BuildProfileLabel(_profile);
            WindowEpisodes.Clear();
            HasCurrentJob = false;
            RecomputeDisk();
            return;
        }

        var item = await _library.GetMediaItemAsync(_mediaItemId.Value);
        SeriesTitle = item?.Title ?? "Série";
        ActiveProfileLabel = BuildProfileLabel(_profile);

        RebuildWindowEpisodes();
        RecomputeDisk();
        RefreshCurrentJob();
    }

    /// <summary>← Voltar.</summary>
    [RelayCommand]
    private void GoBack() => _navigator.GoBack();

    /// <summary>Cancels the job currently processing.</summary>
    [RelayCommand]
    private async Task CancelAsync() => await _queue.CancelCurrentAsync();

    // --- event handlers (background thread → UI thread) --------------------

    private void OnJobEvent(object? sender, ProcessJob job) => Post(() =>
    {
        RebuildWindowEpisodes();
        RecomputeDisk();
        RefreshCurrentJob();
    });

    private void OnProgressChanged(object? sender, PipelineProgress sample) => Post(() =>
    {
        CurrentProgressPct = Clamp(sample.OverallPct);
        CurrentStepLabel = StepToLabelConverter.ToLabel(sample.CurrentStep, MethodForStep(sample.CurrentStep));
        CurrentProgressText = $"{CurrentProgressPct:F0}% — {CurrentStepLabel}";
        EtaDisplay = sample.Eta is { } eta ? FormatEta(eta) : "—";
    });

    private void RefreshCurrentJob()
    {
        var job = _queue.CurrentJob;
        if (job is null)
        {
            HasCurrentJob = false;
            CurrentProgressPct = 0d;
            CurrentProgressText = string.Empty;
            EtaDisplay = "—";
            return;
        }

        var episode = _window.WindowEpisodes.FirstOrDefault(e => e.Id == job.EpisodeId);
        HasCurrentJob = true;
        CurrentJobEpisodeLabel = EpisodeLabel(episode);
        CurrentJobTitle = episode?.DisplayTitle ?? episode?.FileName ?? string.Empty;
        CurrentProgressPct = Clamp(job.ProgressPct);
        CurrentStepLabel = job.CurrentStep is { } step
            ? StepToLabelConverter.ToLabel(MapStep(step), MethodForStep(MapStep(step)))
            : string.Empty;
        CurrentProgressText = $"{CurrentProgressPct:F0}% — {CurrentStepLabel}";
    }

    private void RebuildWindowEpisodes()
    {
        // GroupBy/last-wins: a duplicated EpisodeId must not throw
        // ArgumentException (defensive — queued jobs should be unique per episode).
        var queuedByEpisode = _queue.QueuedJobs
            .GroupBy(j => j.EpisodeId)
            .ToDictionary(g => g.Key, g => g.Last());
        var current = _queue.CurrentJob;

        WindowEpisodes.Clear();
        foreach (var episode in _window.WindowEpisodes)
        {
            var processed = _processedFiles.GetByEpisodeAndProfile(episode.Id, _profile);
            if (!queuedByEpisode.TryGetValue(episode.Id, out var job) && current?.EpisodeId == episode.Id)
            {
                job = current;
            }

            var status = EpisodeProcessStatusMapper.Compute(episode, processed, job);
            WindowEpisodes.Add(new WindowEpisodeItem
            {
                Label = EpisodeLabel(episode),
                Title = string.IsNullOrWhiteSpace(episode.DisplayTitle) ? episode.FileName : episode.DisplayTitle,
                Status = status,
                SizeLabel = FormatBytes(EpisodeSizeBytes(episode, processed)),
            });
        }
    }

    private void RecomputeDisk()
    {
        long used = 0;
        foreach (var episode in _window.WindowEpisodes)
        {
            used += _processedFiles.GetByEpisodeAndProfile(episode.Id, _profile)?.FileSizeBytes ?? 0L;
        }

        var window = _window.WindowEpisodes;
        int windowSize = window.Count > 0 ? window.Count : GetWindowSize();
        double avgDuration = window.Count > 0
            ? window.Average(e => e.DurationSec ?? 0d)
            : 0d;
        int bitrateKbps = GetBitrateKbps(_profile);

        // bytes = windowSize × avgDurationSec × (bitrateKbps × 1000 bits / 8).
        long estimated = (long)(windowSize * avgDuration * bitrateKbps * 1000d / 8d);

        DiskUsedBytes = used;
        DiskEstimatedBytes = estimated;
        DiskUsedLabel = FormatBytes(used);
        DiskEstimatedLabel = FormatBytes(estimated);
        DiskProgressPct = estimated > 0 ? Math.Min(100d, used * 100d / estimated) : 0d;
    }

    // --- helpers -----------------------------------------------------------

    private void Post(Action action)
    {
        if (_syncContext is not null)
        {
            _syncContext.Post(_ => action(), null);
        }
        else
        {
            action();
        }
    }

    private long EpisodeSizeBytes(Episode episode, ProcessedFile? processed) =>
        processed?.FileSizeBytes ?? episode.FileSizeBytes ?? 0L;

    private string? MethodForStep(PipelineStep step) => step switch
    {
        PipelineStep.Upscale => _settings.Get("upscale_method") ?? "fsr4",
        PipelineStep.Interp => _settings.Get("interp_method") ?? "rife",
        _ => null,
    };

    private string BuildProfileLabel(ProcessProfile profile)
    {
        bool dlna = profile == ProcessProfile.Dlna;
        string prefix = dlna ? "dlna" : "local";
        int height = GetInt($"{prefix}_target_height", dlna ? 2160 : 1080);
        double fps = GetDouble($"{prefix}_target_fps", dlna ? 55d : 135d);
        string name = dlna ? "DLNA" : "Local";
        return $"{name} ({height}p {fps:F0}fps)";
    }

    private int GetWindowSize()
    {
        if (int.TryParse(_settings.Get("window_size"), out int size) && size > 0)
        {
            return size;
        }

        return 5;
    }

    private int GetBitrateKbps(ProcessProfile profile)
    {
        string key = profile == ProcessProfile.Dlna ? "dlna_encode_bitrate_kbps" : "local_encode_bitrate_kbps";
        int fallback = profile == ProcessProfile.Dlna ? 45_000 : 20_000;
        return GetInt(key, fallback);
    }

    private int GetInt(string key, int fallback) =>
        int.TryParse(_settings.Get(key), out int value) ? value : fallback;

    private double GetDouble(string key, double fallback) =>
        double.TryParse(_settings.Get(key), out double value) ? value : fallback;

    private static string EpisodeLabel(Episode? episode) =>
        episode?.EpisodeNumber is { } number ? $"EP{number:D2}" : "EP??";

    private static PipelineStep MapStep(ProcessStep step) => step switch
    {
        ProcessStep.Decode => PipelineStep.Decode,
        ProcessStep.Interp => PipelineStep.Interp,
        ProcessStep.Upscale => PipelineStep.Upscale,
        ProcessStep.Encode => PipelineStep.Encode,
        _ => PipelineStep.Encode,
    };

    private static double Clamp(double value) => Math.Clamp(value, 0d, 100d);

    private static string FormatEta(TimeSpan eta)
    {
        if (eta.TotalHours >= 1d)
        {
            return $"~{(int)eta.TotalHours}h {eta.Minutes:D2}min";
        }

        int minutes = (int)Math.Ceiling(eta.TotalMinutes);
        return minutes <= 0 ? "~<1 min" : $"~{minutes} min";
    }

    private static string FormatBytes(long bytes) =>
        $"{(bytes / 1_000_000_000d).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} GB";

    /// <inheritdoc />
    public void Dispose()
    {
        _queue.JobStarted -= OnJobEvent;
        _queue.JobCompleted -= OnJobEvent;
        _queue.JobFailed -= OnJobEvent;
        _queue.ProgressChanged -= OnProgressChanged;
    }

    /// <summary>A single episode row in the window list (Tela 3).</summary>
    public sealed partial class WindowEpisodeItem : ObservableObject
    {
        /// <summary>Zero-padded episode label ("EP01").</summary>
        [ObservableProperty]
        private string _label = string.Empty;

        /// <summary>Episode display title.</summary>
        [ObservableProperty]
        private string _title = string.Empty;

        /// <summary>Badge status (drives glyph + color via converters).</summary>
        [ObservableProperty]
        private EpisodeProcessStatus _status = EpisodeProcessStatus.Original;

        /// <summary>Formatted size ("3.1 GB").</summary>
        [ObservableProperty]
        private string _sizeLabel = string.Empty;
    }
}
