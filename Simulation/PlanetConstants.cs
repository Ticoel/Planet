namespace Planet.Simulation;

/// <summary>
/// Constantes physiques de la planète. La simulation travaille sur une sphère de rayon 1 ;
/// ces valeurs servent à convertir en unités réelles pour l'affichage et la physique.
/// </summary>
public static class PlanetConstants
{
    /// <summary>Rayon de la planète en kilomètres (valeur terrestre).</summary>
    public const float RadiusKm = 6371f;

    /// <summary>Conversion d'une vitesse exprimée en rayons par million d'années vers des cm/an.
    /// 1 rayon/Ma = 6371 km / 1 000 000 ans = 637,1 cm/an.</summary>
    public const float CmPerYearPerRadiusPerMyr = RadiusKm * 1e5f / 1e6f;
}
