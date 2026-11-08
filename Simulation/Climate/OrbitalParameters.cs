using System;
using Godot;

namespace Planet.Simulation.Climate;

/// <summary>Position du Soleil vue de la planète à un instant donné.</summary>
public readonly struct SunState
{
    /// <summary>Direction du Soleil dans le repère de la planète (vecteur unitaire).
    /// Repère : Y = axe de rotation (nord), longitude 0 sur +X, l'est vers −Z.</summary>
    public Vector3 Direction { get; init; }

    /// <summary>Flux solaire reçu au sommet de l'atmosphère, face au Soleil (W/m²).</summary>
    public float Flux { get; init; }

    /// <summary>Distance au Soleil en unités astronomiques.</summary>
    public float DistanceAu { get; init; }

    /// <summary>Déclinaison : latitude où le Soleil est au zénith à midi (radians).</summary>
    public float Declination { get; init; }

    /// <summary>Longitude écliptique du Soleil (radians, 0 = équinoxe de printemps nord).</summary>
    public float EclipticLongitude { get; init; }

    /// <summary>Longitude où il est midi solaire (radians).</summary>
    public float SubsolarLongitude { get; init; }
}

/// <summary>
/// Orbite et rotation de la planète. Valeurs par défaut : celles de la Terre.
/// <list type="bullet">
/// <item>L'orbite est une ellipse (lois de Kepler) : la distance au Soleil, donc le flux reçu,
/// varie au cours de l'année (±3,4 % pour la Terre).</item>
/// <item>L'inclinaison de l'axe fait monter et descendre le Soleil entre les tropiques :
/// c'est elle, et non la distance, qui crée les saisons.</item>
/// <item>La rotation fait tourner le point où il est midi : jour et nuit.</item>
/// </list>
/// Le temps est compté en jours solaires depuis le 1er janvier à minuit (méridien 0).
/// </summary>
public sealed class OrbitalParameters
{
    /// <summary>Constante solaire à 1 UA (W/m²).</summary>
    public float SolarConstant { get; set; } = 1361f;

    /// <summary>Demi-grand axe de l'orbite (UA).</summary>
    public float SemiMajorAxisAu { get; set; } = 1f;

    /// <summary>Excentricité de l'orbite (0 = cercle).</summary>
    public float Eccentricity { get; set; } = 0.0167f;

    /// <summary>Inclinaison de l'axe de rotation (degrés). 23,44° pour la Terre.</summary>
    public float AxialTiltDeg { get; set; } = 23.44f;

    /// <summary>Durée de l'année en jours solaires.</summary>
    public float YearLengthDays { get; set; } = 365.25f;

    /// <summary>Longitude écliptique du Soleil au périhélie (degrés) : fixe à quelle saison la
    /// planète est la plus proche du Soleil. Terre : ~283°, soit début janvier.</summary>
    public float PerihelionLongitudeDeg { get; set; } = 282.9f;

    /// <summary>Jour de l'année du passage au périhélie (Terre : ~3 janvier).</summary>
    public float PerihelionDay { get; set; } = 2.5f;

    public SunState ComputeSun(double timeDays)
    {
        float e = Mathf.Clamp(Eccentricity, 0f, 0.95f);

        // Anomalie moyenne : progression uniforme sur l'orbite
        double meanAnomaly = 2.0 * Math.PI * (timeDays - PerihelionDay) / YearLengthDays;
        meanAnomaly = PositiveModulo(meanAnomaly, 2.0 * Math.PI);

        // Équation de Kepler M = E − e sin E, résolue par la méthode de Newton
        double eccentricAnomaly = e < 0.8 ? meanAnomaly : Math.PI;
        for (int i = 0; i < 12; i++)
        {
            double f = eccentricAnomaly - e * Math.Sin(eccentricAnomaly) - meanAnomaly;
            eccentricAnomaly -= f / (1.0 - e * Math.Cos(eccentricAnomaly));
        }

        // Anomalie vraie (angle réel depuis le périhélie) et distance
        double trueAnomaly = 2.0 * Math.Atan2(
            Math.Sqrt(1.0 + e) * Math.Sin(eccentricAnomaly / 2.0),
            Math.Sqrt(1.0 - e) * Math.Cos(eccentricAnomaly / 2.0));
        double distance = SemiMajorAxisAu * (1.0 - e * Math.Cos(eccentricAnomaly));

        double eclipticLongitude = PositiveModulo(trueAnomaly + Mathf.DegToRad(PerihelionLongitudeDeg), 2.0 * Math.PI);
        double tilt = Mathf.DegToRad(AxialTiltDeg);
        double declination = Math.Asin(Math.Sin(tilt) * Math.Sin(eclipticLongitude));

        // Midi solaire : sur le méridien 0 à 12 h, puis le point de midi file vers l'ouest
        double dayFraction = PositiveModulo(timeDays, 1.0);
        double subsolarLongitude = Math.PI - 2.0 * Math.PI * dayFraction;

        double cosDecl = Math.Cos(declination);
        var direction = new Vector3(
            (float)(cosDecl * Math.Cos(subsolarLongitude)),
            (float)Math.Sin(declination),
            (float)(-cosDecl * Math.Sin(subsolarLongitude)));

        return new SunState
        {
            Direction = direction.Normalized(),
            Flux = (float)(SolarConstant / (distance * distance)),
            DistanceAu = (float)distance,
            Declination = (float)declination,
            EclipticLongitude = (float)eclipticLongitude,
            SubsolarLongitude = (float)subsolarLongitude,
        };
    }

    private static double PositiveModulo(double value, double modulus) =>
        ((value % modulus) + modulus) % modulus;
}
