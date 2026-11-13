namespace Planet.Simulation;

/// <summary>
/// Constantes physiques de la planète. La simulation travaille sur une sphère de rayon 1 ;
/// ces valeurs servent à convertir en unités réelles pour l'affichage et la physique.
/// </summary>
public static class PlanetConstants
{
    /// <summary>Rayon de la Terre (km).</summary>
    public const float EarthRadiusKm = 6371f;

    /// <summary>
    /// Rayon de la planète en kilomètres : la Terre par défaut, moins pour une petite planète
    /// « éprouvette » (voir <see cref="SetRadius"/>). À changer seulement entre deux planètes :
    /// la grille, le climat et le relief se recréent avec le nouveau rayon.
    /// </summary>
    public static float RadiusKm { get; private set; } = EarthRadiusKm;

    /// <summary>Conversion d'une vitesse exprimée en rayons par million d'années vers des cm/an
    /// (Terre : 1 rayon/Ma = 6371 km / 1 000 000 ans = 637,1 cm/an).</summary>
    public static float CmPerYearPerRadiusPerMyr => RadiusKm * 1e5f / 1e6f;

    /// <summary>Surface de la planète par rapport à celle de la Terre.</summary>
    public static float AreaRatio => RadiusKm * RadiusKm / (EarthRadiusKm * EarthRadiusKm);

    /// <summary>Change le rayon de la planète (km, borné entre 500 et 10 000 km).</summary>
    public static void SetRadius(float radiusKm) => RadiusKm = System.Math.Clamp(radiusKm, 500f, 10000f);
}
