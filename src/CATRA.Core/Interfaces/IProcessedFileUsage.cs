namespace CATRA.Core.Interfaces;

/// <summary>
/// Proactive "is this processed file currently in use" probe (ST-18, RF-04:
/// "nunca deletar arquivo em uso"). Lets the sliding window skip deletion of a
/// processed file that is being played locally or streamed over DLNA, before even
/// attempting <see cref="System.IO.File.Delete(string)"/>.
/// </summary>
/// <remarks>
/// This is an optimisation / first line of defence. The authoritative guarantee that
/// an in-use file is never deleted is the sliding window's reactive retry-and-give-up
/// handling of <see cref="System.IO.IOException"/> from <c>File.Delete</c>, which works
/// even when no component reports the file as in use.
/// </remarks>
public interface IProcessedFileUsage
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="filePath"/> is currently being played
    /// or streamed and therefore must not be deleted.
    /// </summary>
    bool IsInUse(string filePath);
}
