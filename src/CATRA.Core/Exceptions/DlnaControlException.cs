namespace CATRA.Core.Exceptions;

/// <summary>
/// Raised when a DLNA renderer rejects (or is unreachable for) a SOAP control
/// call — AVTransport / RenderingControl (ST-08). Carries the SOAP fault or
/// transport message so the UI can surface a meaningful error.
/// </summary>
public sealed class DlnaControlException : Exception
{
    /// <summary>Creates the exception.</summary>
    public DlnaControlException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception wrapping a transport failure.</summary>
    public DlnaControlException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
