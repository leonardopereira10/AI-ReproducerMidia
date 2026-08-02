namespace CATRA.Services.Processing;

/// <summary>
/// Raised when the <c>catra-gpu</c> native bridge is unavailable or a native
/// call returns a negative <c>CATRA_ERR_*</c> code (ST-12). Shields callers from
/// raw <see cref="DllNotFoundException"/> / <see cref="EntryPointNotFoundException"/>
/// so the processing pipeline can degrade or surface a meaningful error.
/// </summary>
public sealed class NativeBridgeException : Exception
{
    /// <summary>
    /// The native <c>CATRA_ERR_*</c> code (e.g. -2 = not implemented), or
    /// <c>0</c> when the failure is not tied to a specific native return code
    /// (e.g. the library is missing).
    /// </summary>
    public int ErrorCode { get; }

    /// <summary>Creates the exception.</summary>
    public NativeBridgeException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception carrying a native error code.</summary>
    public NativeBridgeException(string message, int errorCode)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>Creates the exception wrapping an inner failure.</summary>
    public NativeBridgeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
