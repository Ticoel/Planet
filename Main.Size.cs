namespace Planet;

/// <summary>
/// Taille de la planète : une petite planète « éprouvette » de 2 000 km de rayon, à peu près la
/// taille de la Lune. Sa surface fait le dixième de celle de la Terre : 5 plaques, un point chaud,
/// une poignée de volcans et de sources chaudes, une ou deux chaînes de montagnes. Tout est simulé
/// comme sur la Terre (tectonique, climat, eau, croûte modulaire, chimie), sur une grille de 2 562
/// cellules d'environ 140 km : tout va vite, et l'on fait le tour de ce qui s'y passe en quelques
/// minutes.
/// <para>Simplification : la planète garde la gravité, l'atmosphère, la rotation et l'orbite de la
/// Terre (ce sont ces réglages qui font son climat) ; seule la taille change. La simulation, elle,
/// reste valable pour n'importe quel rayon (les tests la vérifient aussi à l'échelle de la Terre).</para>
/// </summary>
public partial class Main
{
    public const float PlanetRadiusKm = 2000f;
    /// <summary>Finesse de la grille (icosphère subdivisée 4 fois : 2 562 cellules).</summary>
    private const int Subdivisions = 4;
    /// <summary>Nombre de plaques tectoniques visé.</summary>
    private const int PlateCount = 5;

    /// <summary>Altitude de la vue d'ensemble, à l'échelle de la planète.</summary>
    private double OrbitView => OrbitAltitude * PlanetRadiusKm / Simulation.PlanetConstants.EarthRadiusKm;
}
