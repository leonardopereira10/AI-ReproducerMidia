namespace CATRA.Core.Models;

/// <summary>
/// Describes a single audio stream inside a container (ST-05). MKV files may
/// expose several; the UI lists them so the user can pick one.
/// </summary>
/// <param name="Index">Zero-based stream index within the container.</param>
/// <param name="Language">ISO 639-2 language tag from stream metadata, if present.</param>
/// <param name="Codec">Audio codec name (e.g. <c>aac</c>, <c>opus</c>, <c>ac3</c>).</param>
/// <param name="Channels">Channel count, if known.</param>
/// <param name="SampleRate">Sample rate in Hz, if known.</param>
public sealed record AudioTrack(
    int Index,
    string? Language,
    string Codec,
    int Channels,
    int SampleRate);
