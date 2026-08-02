using System.Collections.ObjectModel;
using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Library;
using CATRA.UI.Models;
using CATRA.UI.Navigation;
using CATRA.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CATRA.UI.ViewModels;

/// <summary>
/// Media detail screen (Tela 2): header/cover placeholders, the disabled
/// pre-process section (ST-19) and the episodes split into unwatched/watched
/// grids with context-menu actions (RF-07).
/// </summary>
public sealed partial class MediaDetailViewModel : ObservableObject
{
    private readonly ILibraryService _library;
    private readonly IWatchStateService _watchStateService;
    private readonly IThumbnailService _thumbnails;
    private readonly IAppNavigator _navigator;
    private readonly IDialogService _dialogs;
    private readonly ISlidingWindowService _window;
    private readonly IProcessedFileRepository _processedFiles;
    private readonly IProcessJobRepository _jobs;
    private readonly IAppSettingsRepository _settings;

    private int _mediaItemId;

    /// <summary>Creates the view model with its dependencies.</summary>
    public MediaDetailViewModel(
        ILibraryService library,
        IWatchStateService watchStateService,
        IThumbnailService thumbnails,
        IAppNavigator navigator,
        IDialogService dialogs,
        ISlidingWindowService window,
        IProcessedFileRepository processedFiles,
        IProcessJobRepository jobs,
        IAppSettingsRepository settings)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _watchStateService = watchStateService ?? throw new ArgumentNullException(nameof(watchStateService));
        _thumbnails = thumbnails ?? throw new ArgumentNullException(nameof(thumbnails));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _processedFiles = processedFiles ?? throw new ArgumentNullException(nameof(processedFiles));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>Unwatched episodes (display order).</summary>
    public ObservableCollection<EpisodeDetail> UnwatchedEpisodes { get; } = [];

    /// <summary>Watched episodes (display order, rendered with reduced opacity).</summary>
    public ObservableCollection<EpisodeDetail> WatchedEpisodes { get; } = [];

    /// <summary>Normalized media title.</summary>
    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>Metadata line ("Ano: — | Gênero: —" until Phase 3).</summary>
    [ObservableProperty]
    private string _infoLine = "Ano: — | Gênero: —";

    /// <summary>Formatted intro-skip length (RN-04 default 1:25).</summary>
    [ObservableProperty]
    private string _skipIntroDisplay = "1:25";

    /// <summary>Type/progress summary line under the cover.</summary>
    [ObservableProperty]
    private string _progressLine = string.Empty;

    /// <summary>Whether the unwatched section has episodes.</summary>
    [ObservableProperty]
    private bool _hasUnwatched;

    /// <summary>Whether the watched section has episodes.</summary>
    [ObservableProperty]
    private bool _hasWatched;

    /// <summary>Selected pre-process profile (ST-19, bound to ProfileRadioSwitch).</summary>
    [ObservableProperty]
    private ProcessProfile _selectedProfile = ProcessProfile.Local;

    /// <summary>Whether the active sliding window belongs to this series.</summary>
    [ObservableProperty]
    private bool _isWindowActiveForThisSeries;

    /// <summary>Whether another series has an active window (warn on switch).</summary>
    [ObservableProperty]
    private bool _isOtherSeriesWindowActive;

    /// <summary>Window estimate line ("Janela: 5 episódios | ~16.5 GB estimados").</summary>
    [ObservableProperty]
    private string _windowEstimateLabel = "Janela: 5 episódios | ~0.0 GB estimados";

    /// <summary>Start/stop button label ("📥 Iniciar" / "⏹ Parar").</summary>
    [ObservableProperty]
    private string _startStopLabel = "📥 Iniciar";

    /// <summary>Whether the profile radio-switch can be edited (disabled while this series' window is active).</summary>
    [ObservableProperty]
    private bool _isProfileSwitchEnabled = true;

    /// <summary>Loads (or reloads) the media item and its episodes.</summary>
    public async Task LoadAsync(int mediaItemId)
    {
        _mediaItemId = mediaItemId;

        var item = await _library.GetMediaItemAsync(mediaItemId);
        if (item is null)
        {
            _navigator.GoToHome();
            return;
        }

        Title = item.Title;
        InfoLine = $"Ano: {item.Year?.ToString() ?? "—"} | Gênero: {item.Genre ?? "—"}";
        SkipIntroDisplay = FormatDuration(item.SkipIntroSec);

        var episodes = await _library.GetEpisodesAsync(mediaItemId);

        UnwatchedEpisodes.Clear();
        WatchedEpisodes.Clear();
        foreach (var episode in episodes)
        {
            (episode.IsWatched ? WatchedEpisodes : UnwatchedEpisodes).Add(episode);
        }

        HasUnwatched = UnwatchedEpisodes.Count > 0;
        HasWatched = WatchedEpisodes.Count > 0;

        var watched = WatchedEpisodes.Count;
        ProgressLine = episodes.Count == 0
            ? "Sem episódios catalogados"
            : $"{episodes.Count} eps | {watched} assistidos";

        ComputeEpisodeStatuses();
        RefreshPreProcessState();

        QueueThumbnailLoads(episodes);
    }

    /// <summary>
    /// Lazily resolves episode thumbnails for the detail grids (ST-09). Each
    /// lookup runs on the thread pool so a slow ffmpeg never blocks the UI; a
    /// failure leaves <c>ThumbnailPath</c> null and the card shows its
    /// placeholder.
    /// </summary>
    private void QueueThumbnailLoads(IEnumerable<EpisodeDetail> episodes)
    {
        foreach (var episode in episodes.ToList())
        {
            if (!string.IsNullOrEmpty(episode.ThumbnailPath))
            {
                continue;
            }

            _ = LoadEpisodeThumbnailAsync(episode);
        }
    }

    private async Task LoadEpisodeThumbnailAsync(EpisodeDetail episode)
    {
        try
        {
            var path = await Task.Run(() => _thumbnails.GetOrCreateThumbnailAsync(episode.Episode));
            if (path is not null)
            {
                episode.ThumbnailPath = path;
            }
        }
        catch (Exception)
        {
            // Best effort: the card keeps its placeholder.
        }
    }

    /// <summary>← Voltar.</summary>
    [RelayCommand]
    private void GoBack() => _navigator.GoBack();

    /// <summary>
    /// Starts the sliding window for this series under
    /// <see cref="SelectedProfile"/>, or stops it when it is already active for
    /// this series (ST-19, RF-03). Starting while another series is active
    /// implicitly cancels that series' queue (RN-10, handled by the service).
    /// </summary>
    [RelayCommand]
    private async Task StartStopWindowAsync()
    {
        if (IsWindowActiveForThisSeries)
        {
            await _window.StopWindowAsync(_mediaItemId);
        }
        else
        {
            await _window.StartWindowAsync(_mediaItemId, SelectedProfile);
        }

        await LoadAsync(_mediaItemId);
    }

    /// <summary>Opens the pre-processing queue screen (Tela 3) for this series.</summary>
    [RelayCommand]
    private void OpenQueue() => _navigator.GoToProcessingQueue(_mediaItemId);

    /// <summary>
    /// Computes the pre-processing badge (⚙/✓/○/↻) for every loaded episode
    /// under the active profile (ST-19). Uses the window's active profile when
    /// this series owns it, otherwise the locally selected profile.
    /// </summary>
    private void ComputeEpisodeStatuses()
    {
        var profile = _window.ActiveMediaItemId == _mediaItemId && _window.ActiveProfile is { } active
            ? active
            : SelectedProfile;

        // GroupBy/last-wins: a duplicated EpisodeId must not throw
        // ArgumentException (defensive — active jobs should be unique per episode).
        var activeJobs = _jobs.GetActiveByMediaItem(_mediaItemId)
            .GroupBy(j => j.EpisodeId)
            .ToDictionary(g => g.Key, g => g.Last());

        foreach (var detail in UnwatchedEpisodes.Concat(WatchedEpisodes))
        {
            var processed = _processedFiles.GetByEpisodeAndProfile(detail.Id, profile);
            activeJobs.TryGetValue(detail.Id, out var job);
            detail.ProcessStatus = EpisodeProcessStatusMapper.Compute(detail.Episode, processed, job);
        }
    }

    /// <summary>Refreshes the pre-process section state (button label, warning, estimate).</summary>
    private void RefreshPreProcessState()
    {
        var activeId = _window.ActiveMediaItemId;
        IsWindowActiveForThisSeries = activeId == _mediaItemId;
        IsOtherSeriesWindowActive = activeId.HasValue && activeId.Value != _mediaItemId;
        IsProfileSwitchEnabled = !IsWindowActiveForThisSeries;
        StartStopLabel = IsWindowActiveForThisSeries ? "⏹ Parar" : "📥 Iniciar";
        WindowEstimateLabel = BuildEstimateLabel();
    }

    private string BuildEstimateLabel()
    {
        // Settings-driven so the estimate matches ProcessingQueueViewModel
        // (same window_size + *_encode_bitrate_kbps keys) instead of hardcoded
        // Take(5) / 20k / 45k values.
        int windowSize = GetWindowSize();
        var candidates = UnwatchedEpisodes.Take(windowSize).Select(e => e.Episode).ToList();
        double avgDuration = candidates.Count > 0 ? candidates.Average(e => e.DurationSec ?? 0d) : 0d;
        int bitrateKbps = GetBitrateKbps(SelectedProfile);

        // bytes = windowSize × avgDurationSec × (bitrateKbps × 1000 / 8).
        double bytes = windowSize * avgDuration * bitrateKbps * 1000d / 8d;
        return $"Janela: {windowSize} episódios | ~{bytes / 1_000_000_000d:F1} GB estimados";
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
        return int.TryParse(_settings.Get(key), out int value) ? value : fallback;
    }

    /// <summary>Episode click → opens the player (Tela 4) for the episode.</summary>
    [RelayCommand]
    private void PlayEpisode(EpisodeDetail? episode)
    {
        if (episode is null)
        {
            return;
        }

        _navigator.GoToPlayer(episode.Id);
    }

    /// <summary>Context menu: flips the watched flag and reloads (RF-07).</summary>
    [RelayCommand]
    private async Task ToggleWatchedAsync(EpisodeDetail? episode)
    {
        if (episode is null)
        {
            return;
        }

        // ST-07: route the manual toggle through the watch-state business layer.
        await _watchStateService.ToggleWatchedAsync(episode.Id);
        await LoadAsync(_mediaItemId);
    }

    /// <summary>Context menu: renames the episode display title (RF-02).</summary>
    [RelayCommand]
    private async Task RenameEpisodeAsync(EpisodeDetail? episode)
    {
        if (episode is null)
        {
            return;
        }

        var newName = _dialogs.Prompt(
            "Renomear episódio",
            $"Novo título para {episode.EpisodeLabel}:",
            episode.Title);

        if (string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        await _library.RenameEpisodeAsync(episode.Id, newName);
        await LoadAsync(_mediaItemId);
    }

    /// <summary>Context menu: picks a cover image for the media item (RF-08).</summary>
    [RelayCommand]
    private async Task SetCoverAsync()
    {
        var path = _dialogs.OpenFile(
            "Configurar capa",
            "Imagens|*.png;*.jpg;*.jpeg;*.bmp;*.webp|Todos os arquivos|*.*");

        if (path is null)
        {
            return;
        }

        // RF-08: route through the thumbnail service so the image is copied
        // into the cache (%AppData%/CATRA/thumbs/{id}_cover_custom.jpg) AND
        // MediaItem.CoverPath is persisted — not just the raw source path.
        await _thumbnails.SetCustomCoverAsync(_mediaItemId, path);
        _dialogs.ShowMessage("Capa configurada", path);
    }

    /// <summary>Context menu: episode details dialog.</summary>
    [RelayCommand]
    private void ShowDetails(EpisodeDetail? episode)
    {
        if (episode is null)
        {
            return;
        }

        var ep = episode.Episode;
        var resolution = ep.SourceWidth is { } w && ep.SourceHeight is { } h ? $"{w}x{h}" : "—";
        var message =
            $"Arquivo: {ep.FileName}\n" +
            $"Caminho: {ep.FilePath}\n" +
            $"Duração: {FormatDuration(ep.DurationSec)}\n" +
            $"Resolução: {resolution} | FPS: {ep.SourceFps?.ToString("F2") ?? "—"}\n" +
            $"Progresso: {episode.ProgressPct:F0}% | Assistido: {(episode.IsWatched ? "sim" : "não")}";

        _dialogs.ShowMessage($"Detalhes — {episode.EpisodeLabel}", message);
    }

    private static string FormatDuration(double? seconds)
    {
        if (seconds is null || seconds.Value <= 0d)
        {
            return "—";
        }

        var span = TimeSpan.FromSeconds(seconds.Value);
        return span.TotalHours >= 1d
            ? $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}"
            : $"{(int)span.TotalMinutes}:{span.Seconds:D2}";
    }
}
