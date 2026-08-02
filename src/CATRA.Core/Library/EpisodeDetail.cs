using System.ComponentModel;
using System.Runtime.CompilerServices;
using CATRA.Core.Enums;
using CATRA.Core.Models;

namespace CATRA.Core.Library;

/// <summary>
/// Read model for an episode card on the Detail screen (Tela 2): the episode
/// joined with its (optional) watch state.
/// </summary>
public sealed class EpisodeDetail : INotifyPropertyChanged
{
    private string? _thumbnailPath;
    private EpisodeProcessStatus _processStatus = EpisodeProcessStatus.Original;

    /// <summary>Creates a detail from an episode and its optional watch state.</summary>
    public EpisodeDetail(Episode episode, WatchState? watchState)
    {
        Episode = episode ?? throw new ArgumentNullException(nameof(episode));
        WatchState = watchState;
    }

    /// <summary>Underlying episode.</summary>
    public Episode Episode { get; }

    /// <summary>Watch state, or <c>null</c> when never played.</summary>
    public WatchState? WatchState { get; }

    /// <summary>Episode database id.</summary>
    public int Id => Episode.Id;

    /// <summary>Whether the episode is marked watched.</summary>
    public bool IsWatched => WatchState?.Watched == true;

    /// <summary>Watched progress percentage (0–100); 0 when never played.</summary>
    public double ProgressPct => WatchState?.ProgressPct ?? 0d;

    /// <summary>
    /// Whether the episode has playback progress but is not watched yet
    /// (candidate for "continue watching", RN-03).
    /// </summary>
    public bool HasProgress => !IsWatched && ProgressPct > 0d;

    /// <summary>Zero-padded episode label ("EP01"); falls back to "EP??".</summary>
    public string EpisodeLabel =>
        Episode.EpisodeNumber is { } number ? $"EP{number:D2}" : "EP??";

    /// <summary>Display title; falls back to the raw file name.</summary>
    public string Title =>
        string.IsNullOrWhiteSpace(Episode.DisplayTitle) ? Episode.FileName : Episode.DisplayTitle;

    /// <summary>
    /// Resolved thumbnail image path (ST-09), populated lazily by the view
    /// model after an async thumbnail lookup. <c>null</c> means "show
    /// placeholder".
    /// </summary>
    public string? ThumbnailPath
    {
        get => _thumbnailPath;
        set => SetField(ref _thumbnailPath, value);
    }

    /// <summary>
    /// Pre-processing badge state for the active profile (ST-19): ⚙ fila /
    /// ✓ pronto / ○ original / ↻ stale. Populated by the detail view model;
    /// defaults to <see cref="EpisodeProcessStatus.Original"/>.
    /// </summary>
    public EpisodeProcessStatus ProcessStatus
    {
        get => _processStatus;
        set => SetField(ref _processStatus, value);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
