using SQLite;

namespace CATRA.Data.Entities;

/// <summary>
/// SQLite row for the <c>WatchState</c> table.
/// </summary>
[Table("WatchState")]
public sealed class WatchStateEntity
{
    /// <summary>Primary key.</summary>
    [PrimaryKey, AutoIncrement, Column("Id")]
    public int Id { get; set; }

    /// <summary>Associated episode id (unique).</summary>
    /// <remarks>
    /// A single <see cref="IndexedAttribute"/> with <c>Unique = true</c> is used instead of
    /// separate <c>[Unique]</c> + <c>[Indexed]</c> attributes: sqlite-net gives both the same
    /// default index name (IX_WatchState_EpisodeId) but conflicting Unique flags, which makes
    /// CreateTable throw "All the columns in an index must have the same value for their Unique property".
    /// </remarks>
    [NotNull, Indexed(Unique = true), Column("EpisodeId")]
    public int EpisodeId { get; set; }

    /// <summary>Watched flag (0/1).</summary>
    [NotNull, Column("Watched")]
    public bool Watched { get; set; }

    /// <summary>Progress percentage.</summary>
    [NotNull, Column("ProgressPct")]
    public double ProgressPct { get; set; }

    /// <summary>Last position seconds (original file).</summary>
    [NotNull, Column("LastPositionSec")]
    public double LastPositionSec { get; set; }

    /// <summary>Last update timestamp (UTC).</summary>
    [NotNull, Column("UpdatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
