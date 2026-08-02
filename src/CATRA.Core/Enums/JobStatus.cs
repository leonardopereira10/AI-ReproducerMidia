namespace CATRA.Core.Enums;

/// <summary>
/// Lifecycle state of a <see cref="Models.ProcessJob"/>. Persisted as lowercase
/// text in the <c>ProcessJob.Status</c> column.
/// </summary>
public enum JobStatus
{
    /// <summary>Waiting in the queue.</summary>
    Queued,

    /// <summary>Currently being processed.</summary>
    Processing,

    /// <summary>Finished successfully.</summary>
    Completed,

    /// <summary>Finished with an error.</summary>
    Failed,

    /// <summary>Cancelled by the user.</summary>
    Cancelled,
}
