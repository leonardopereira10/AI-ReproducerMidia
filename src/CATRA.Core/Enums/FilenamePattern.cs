namespace CATRA.Core.Enums;

/// <summary>
/// Filename pattern matched by the parser (RF-02).
/// Priority: <see cref="Pattern1"/> &gt; <see cref="Pattern2"/> &gt;
/// <see cref="Pattern3"/> &gt; <see cref="Fallback"/>.
/// </summary>
public enum FilenamePattern
{
    /// <summary>P1: <c>[Site][Name] - Episódio NN.ext</c> → name + episode.</summary>
    Pattern1,

    /// <summary>P2: <c>[Site] Name - Episódio NN (Quality).ext</c> → name + episode.</summary>
    Pattern2,

    /// <summary>P3: <c>ABREV##EP##.ext</c> → season + episode.</summary>
    Pattern3,

    /// <summary>
    /// Fansub/publisher release: <c>[Ep. NNN] Name - NT [Publisher] [Quality] [Lang].ext</c>
    /// → name + season + episode + publisher (e.g.
    /// <c>[Ep. 001] Martial Master - 1T [DonghuaNoSekai] [1080p] [PT-BR]</c>).
    /// </summary>
    PublisherRelease,

    /// <summary>P4: no pattern matched → use folder name + container metadata.</summary>
    Fallback,
}
