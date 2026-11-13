using System;

namespace Planet.Simulation.Climate;

/// <summary>Phase de la vie de l'étoile.</summary>
public enum StarPhase
{
    /// <summary>Séquence principale : l'étoile brûle l'hydrogène de son cœur et s'éclaire lentement.</summary>
    MainSequence,
    /// <summary>Sous-géante puis géante rouge : l'étoile enfle et devient des centaines de fois plus lumineuse.</summary>
    RedGiant,
    /// <summary>Morte : ses couches externes sont parties, il reste une naine blanche. Fin de la partie.</summary>
    Dead,
}

/// <summary>
/// L'étoile du système (une étoile, une planète, une lune). Sa masse fixe tout le reste :
/// <list type="bullet">
/// <item><b>Durée de vie</b> sur la séquence principale : τ ≈ 10 milliards d'années × (M/M☉)^−2,5
/// (une étoile plus massive brûle son combustible bien plus vite) ; le Soleil, à 4,6 milliards
/// d'années, est à mi-vie.</item>
/// <item><b>Luminosité</b> : L ≈ (M/M☉)⁴ L☉ à mi-vie. Elle augmente pendant toute la séquence
/// principale : le jeune Soleil brillait ~30 % de moins qu'aujourd'hui (Gough 1981 :
/// L(t) = L / (1 + 0,4 (1 − t/t☉))), d'où le « paradoxe du jeune Soleil faible ».</item>
/// <item><b>Fin</b> : quand le cœur n'a plus d'hydrogène, l'étoile devient une géante rouge en
/// ~10 % de sa vie (luminosité ×1 000 et plus, rayon ×100), puis perd ses couches externes et
/// laisse une naine blanche. La planète est grillée bien avant, peut-être engloutie.</item>
/// <item><b>Thermostat des roches</b> (Walker, Hays et Kasting 1981) : le gaz carbonique de
/// l'air se règle sur des millions d'années (volcans d'un côté, altération des roches de
/// l'autre), ce qui compense une étoile trop faible ; il compense mal une étoile qui devient
/// trop forte (on ne peut pas descendre sous zéro gaz carbonique) : au-delà de ~+10 % de
/// lumière, la planète se réchauffe sans retour (Caldeira et Kasting 1992).</item>
/// </list>
/// La planète est placée à la distance où elle reçoit, à mi-vie de l'étoile, autant de lumière
/// que la Terre ; la durée de son année suit (troisième loi de Kepler, T² = a³ / M).
/// </summary>
public sealed class Star
{
    public const double SunLifetimeGyr = 10.0;
    private const double SunAgeFraction = 4.57 / 10.0;   // âge actuel du Soleil / durée de vie
    private const double GiantShare = 0.12;              // part de la vie passée en géante rouge
    private const double AbsorbedWm2 = 240.0;            // lumière absorbée par la Terre (moyenne)
    private const double MaxCo2WarmingWm2 = 90.0;        // compensation maximale d'une étoile faible
    private const double MaxCo2CoolingWm2 = 25.0;        // ... d'une étoile trop forte

    /// <summary>Masse en masses solaires (0,7 à 1,5 conseillé).</summary>
    public double Mass { get; }

    public Star(double mass)
    {
        Mass = Math.Clamp(mass, 0.5, 2.0);
    }

    /// <summary>Durée de la séquence principale (milliards d'années).</summary>
    public double LifetimeGyr => SunLifetimeGyr * Math.Pow(Mass, -2.5);
    /// <summary>Âge de la mort de l'étoile (fin de la géante rouge), en milliards d'années.</summary>
    public double DeathGyr => LifetimeGyr * (1 + GiantShare);
    /// <summary>Luminosité à mi-vie (luminosités solaires).</summary>
    public double ReferenceLuminosity => Math.Pow(Mass, 4);
    /// <summary>Distance de la planète (UA) : même lumière que la Terre à mi-vie de l'étoile.</summary>
    public double PlanetDistanceAu => Math.Sqrt(ReferenceLuminosity);
    /// <summary>Durée de l'année de la planète (jours).</summary>
    public double YearLengthDays => 365.25 * Math.Sqrt(Math.Pow(PlanetDistanceAu, 3) / Mass);

    public StarPhase PhaseAt(double ageGyr) =>
        ageGyr < LifetimeGyr ? StarPhase.MainSequence : ageGyr < DeathGyr ? StarPhase.RedGiant : StarPhase.Dead;

    /// <summary>Luminosité relative à la luminosité de mi-vie (1 au même stade que le Soleil actuel).</summary>
    public double RelativeLuminosity(double ageGyr)
    {
        double x = Math.Max(0.0, ageGyr) / LifetimeGyr;
        if (x <= 1.0)
            return 1.0 / (1.0 + 0.4 * (1.0 - x / SunAgeFraction));
        // Géante rouge : la luminosité grimpe d'un facteur ~2 000 jusqu'à la mort
        double endOfMainSequence = 1.0 / (1.0 + 0.4 * (1.0 - 1.0 / SunAgeFraction));
        double g = Math.Min(1.0, (x - 1.0) / GiantShare);
        return endOfMainSequence * Math.Pow(2000.0, g * g);
    }

    /// <summary>
    /// Lumière effective reçue par le climat (relative à aujourd'hui), thermostat des roches compris :
    /// le forçage de l'étoile ΔF = 240 (L − 1) W/m² est compensé par le gaz carbonique, dans la limite
    /// de +90 W/m² (étoile faible) et −25 W/m² (étoile forte).
    /// </summary>
    public double EffectiveIrradiance(double ageGyr)
    {
        double forcing = AbsorbedWm2 * (RelativeLuminosity(ageGyr) - 1.0);
        double compensation = Math.Clamp(-forcing, -MaxCo2CoolingWm2, MaxCo2WarmingWm2);
        return 1.0 + (forcing + compensation) / AbsorbedWm2;
    }

    /// <summary>
    /// Effet de serre supplémentaire (W/m²) que le thermostat des roches fournit à cet âge par le
    /// gaz carbonique : positif pour une étoile faible, négatif pour une étoile trop forte.
    /// </summary>
    public double Co2CompensationWm2(double ageGyr) =>
        AbsorbedWm2 * (EffectiveIrradiance(ageGyr) - RelativeLuminosity(ageGyr));

    /// <summary>Applique l'étoile à l'orbite de la planète à l'âge donné (flux, distance, année).</summary>
    public void ApplyTo(OrbitalParameters orbit, double ageGyr)
    {
        // (plafonné à 3 fois la lumière actuelle : au-delà, le modèle de climat n'a plus de sens ;
        // la planète est déjà stérilisée, océans évaporés)
        orbit.SolarConstant = (float)(1361.0 * Math.Min(3.0, EffectiveIrradiance(ageGyr)));
        orbit.SemiMajorAxisAu = 1f;   // le flux ci-dessus tient déjà compte de la vraie distance
        orbit.YearLengthDays = (float)YearLengthDays;
    }

    /// <summary>Rayon de l'étoile (rayons solaires) : ~M^0,8 sur la séquence principale, jusqu'à ~150 en géante rouge.</summary>
    public double RadiusSolar(double ageGyr)
    {
        double mainSequence = Math.Pow(Mass, 0.8);
        double x = Math.Max(0.0, ageGyr) / LifetimeGyr;
        if (x <= 1.0)
            return mainSequence * (0.9 + 0.2 * x);
        double g = Math.Min(1.0, (x - 1.0) / GiantShare);
        return mainSequence * 1.1 * Math.Pow(150.0, g * g);
    }

    /// <summary>Rayon apparent vu de la planète (radians).</summary>
    public double AngularRadius(double ageGyr) => 0.00465 * RadiusSolar(ageGyr) / PlanetDistanceAu;

    /// <summary>Teinte du disque : plus bleutée pour une étoile massive, orangée pour une petite,
    /// rouge en géante rouge (surface plus froide).</summary>
    public (float r, float g, float b) Tint(double ageGyr)
    {
        double hot = Math.Clamp((Mass - 1.0) * 1.5, -0.6, 0.6);
        double r = 1.0 - Math.Max(0, hot) * 0.25, g = 1.0 + Math.Min(0, hot) * 0.25, b = 1.0 + hot * 0.45;
        if (PhaseAt(ageGyr) != StarPhase.MainSequence)
        {
            double giant = Math.Min(1.0, (ageGyr / LifetimeGyr - 1.0) / GiantShare * 3);
            r = 1.0; g *= 1 - 0.55 * giant; b *= 1 - 0.8 * giant;
        }
        return ((float)r, (float)g, (float)b);
    }

    /// <summary>Nom de la phase, pour l'interface.</summary>
    public static string PhaseName(StarPhase phase) => phase switch
    {
        StarPhase.MainSequence => "séquence principale",
        StarPhase.RedGiant => "géante rouge",
        _ => "naine blanche (morte)",
    };
}
