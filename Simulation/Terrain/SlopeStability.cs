using System;

namespace Planet.Simulation.Terrain;

/// <summary>Sorte de mouvement de terrain.</summary>
public enum SlideKind
{
    /// <summary>Éboulement : blocs qui se détachent d'une paroi rocheuse (pente de plus de ~45°).</summary>
    Rockfall,
    /// <summary>Glissement de terrain : le sol et les altérites glissent sur la roche saine.</summary>
    Landslide,
    /// <summary>Avalanche rocheuse : effondrement d'un versant entier, très long parcours.</summary>
    RockAvalanche,
}

/// <summary>Conditions du moment qui fragilisent les pentes.</summary>
/// <param name="Saturation">Part du sol gorgée d'eau (0 : sec, 1 : nappe jusqu'en surface).</param>
/// <param name="SeismicK">Accélération horizontale d'un séisme, en fraction de g (0 : aucun).</param>
/// <param name="FreezeThaw">Gel et dégel (0 à 1) : l'eau gèle dans les fissures et écarte la roche.</param>
public readonly record struct SlopeConditions(double Saturation, double SeismicK, double FreezeThaw);

/// <summary>
/// Stabilité des pentes (étape 16a).
/// <list type="bullet">
/// <item><b>Pente infinie</b> (Skempton et DeLory 1957 ; forme pseudo-statique pour les séismes,
/// Newmark 1965, Jibson 2011) : un sol d'épaisseur z sur une pente β tient tant que le
/// frottement et la cohésion l'emportent sur son poids :
/// FS = [c + (γ z cos²β (1 − m γw/γ) − k γ z sinβ cosβ) tanφ] / [γ z sinβ cosβ + k γ z cos²β],
/// m : part saturée, k : accélération sismique (fraction de g). FS &lt; 1 : le sol part.
/// Sol type : γ = 19 kN/m³, c = 5 kPa, φ = 35°, z = 1,5 m. Une pente de 35° tient à sec
/// (FS ≈ 1,4) et cède gorgée d'eau (≈ 0,86) ; une pente de 25° tient même saturée.</item>
/// <item><b>Seuil de pluie</b> (Caine 1980) : il faut, pour déclencher des glissements, une pluie
/// d'intensité I &gt; 14,82 D^−0,39 mm/h pendant D heures ; ici, la saturation du sol en tient lieu.</item>
/// <item><b>Séismes</b> (Keefer 1984 ; Wald et al. 1999) : accélération du sol déduite de
/// l'intensité ressentie, log₁₀ PGA (cm/s²) = (I + 1,66) / 3,66 ; k ≈ PGA / 2.</item>
/// <item><b>Parois rocheuses</b> : au-delà de ~45°, la roche se débite en blocs (gel et dégel,
/// pluie, séismes) ; c'est l'éboulement, plus fréquent mais plus petit.</item>
/// </list>
/// </summary>
public static class SlopeStability
{
    public const double SoilUnitWeight = 19.0;     // kN/m³
    public const double WaterUnitWeight = 9.81;    // kN/m³
    public const double SoilCohesionKpa = 5.0;
    public const double SoilFrictionDeg = 35.0;
    public const double SoilDepthM = 1.5;
    /// <summary>Pente (rad) au-delà de laquelle la roche est à nu et se débite en blocs.</summary>
    public static readonly double CliffSlope = 45.0 * Math.PI / 180.0;

    /// <summary>Facteur de sécurité d'une pente infinie (sans dimension ; &lt; 1 : rupture).</summary>
    public static double FactorOfSafety(double slopeRad, SlopeConditions conditions,
                                        double depthM = SoilDepthM, double cohesionKpa = SoilCohesionKpa, double frictionDeg = SoilFrictionDeg)
    {
        if (slopeRad <= 1e-4)
            return double.PositiveInfinity;
        double cos = Math.Cos(slopeRad), sin = Math.Sin(slopeRad);
        double weight = SoilUnitWeight * depthM;
        double m = Math.Clamp(conditions.Saturation, 0, 1);
        double k = Math.Max(0, conditions.SeismicK);
        double normal = weight * cos * cos * (1 - m * WaterUnitWeight / SoilUnitWeight) - k * weight * sin * cos;
        double resisting = cohesionKpa + Math.Max(0, normal) * Math.Tan(frictionDeg * Math.PI / 180.0);
        double driving = weight * sin * cos + k * weight * cos * cos;
        return resisting / driving;
    }

    /// <summary>Accélération sismique (fraction de g) d'après l'intensité ressentie (échelle de Mercalli).</summary>
    public static double SeismicCoefficient(double intensity)
    {
        if (intensity < 4)
            return 0;
        double pgaCm = Math.Pow(10, (intensity + 1.66) / 3.66);
        return Math.Min(0.6, 0.5 * pgaCm / 981.0);
    }

    /// <summary>
    /// Rapport hauteur de chute / distance parcourue (« Fahrböschung ») selon le volume
    /// (Scheidegger 1973) : log₁₀(H/L) = 0,624 − 0,157 log₁₀ V. Les petits éboulements s'arrêtent
    /// au pied de la pente (H/L ≈ 0,6 à 0,8) ; les grandes avalanches rocheuses vont 4 à 5 fois
    /// plus loin qu'elles ne tombent.
    /// </summary>
    public static double TravelAngleRatio(double volumeM3) =>
        Math.Clamp(Math.Pow(10, 0.624 - 0.15666 * Math.Log10(Math.Max(1, volumeM3))), 0.12, 0.75);

    /// <summary>
    /// Épaisseur moyenne d'un glissement de surface A (m²) : V = 0,146 A^1,332 (Larsen,
    /// Montgomery et Korup 2010, tous glissements confondus), donc z = V/A = 0,146 A^0,332.
    /// </summary>
    public static double MeanDepth(double areaM2) => 0.146 * Math.Pow(Math.Max(1, areaM2), 0.332);

    /// <summary>
    /// Pente (rad) et direction de la descente sur le plan tangent en un point, par différences
    /// finies sur une base de <paramref name="baseM"/> mètres.
    /// </summary>
    public static (double slope, Double3 downhill, double height) LocalSlope(Func<Double3, double> height, Double3 direction, double baseM)
    {
        direction = direction.Normalized();
        var (ax, ay) = LavaFlow.TangentFrame(direction);
        double r = TerrainHeightField.RadiusMeters;
        Double3 At(double x, double y) => (direction + ax * (x / r) + ay * (y / r)).Normalized();
        double h0 = height(direction);
        double gx = (height(At(baseM, 0)) - height(At(-baseM, 0))) / (2 * baseM);
        double gy = (height(At(0, baseM)) - height(At(0, -baseM))) / (2 * baseM);
        double g = Math.Sqrt(gx * gx + gy * gy);
        Double3 downhill = g > 1e-9 ? (ax * (-gx / g) + ay * (-gy / g)) : ax;
        return (Math.Atan(g), downhill, h0);
    }

    /// <summary>Un endroit examiné : sa pente, sa stabilité et ce qui y partirait.</summary>
    public readonly record struct Candidate(Double3 Direction, double Slope, double FactorOfSafety, SlideKind Kind, double Height);

    /// <summary>
    /// Examine <paramref name="samples"/> points tirés au hasard dans un disque autour de
    /// <paramref name="center"/> et rend le plus instable (null si tout tient). Les parois
    /// rocheuses (&gt; 45°) ne sont pas jugées par la pente infinie : leur « facteur de sécurité »
    /// baisse avec le gel, la pluie et les séismes, et une part de hasard (la roche est fracturée
    /// ici et pas là) ; elles ne cèdent que sur un tirage (<paramref name="rockfallChance"/>).
    /// </summary>
    public static Candidate? FindWeakest(Func<Double3, double> height, Double3 center, double radiusM, int samples,
                                        SlopeConditions conditions, Random random, double rockfallChance, double minHeight = 0.5)
    {
        center = center.Normalized();
        var (ax, ay) = LavaFlow.TangentFrame(center);
        double r = TerrainHeightField.RadiusMeters;
        Candidate? best = null;
        for (int s = 0; s < samples; s++)
        {
            double rho = radiusM * Math.Sqrt(random.NextDouble()), phi = random.NextDouble() * 2 * Math.PI;
            Double3 d = (center + ax * (rho * Math.Cos(phi) / r) + ay * (rho * Math.Sin(phi) / r)).Normalized();
            var (slope, _, h) = LocalSlope(height, d, 6.0);
            if (h < minHeight)
                continue;   // sous l'eau ou au ras de la mer : rien ici
            double fs;
            SlideKind kind;
            if (slope >= CliffSlope)
            {
                kind = SlideKind.Rockfall;
                // Paroi : la roche cède d'autant plus facilement qu'elle est raide, gelée puis
                // dégelée, détrempée ou secouée ; le tirage décide si un bloc part vraiment
                double weakening = 1 + 2.5 * conditions.FreezeThaw + 1.0 * conditions.Saturation + 6 * conditions.SeismicK;
                bool falls = random.NextDouble() < rockfallChance * weakening * (slope / CliffSlope);
                fs = falls ? 0.9 - 0.1 * random.NextDouble() : 1.5;
            }
            else
            {
                fs = FactorOfSafety(slope, conditions);
                kind = conditions.SeismicK > 0.2 && slope > 32 * Math.PI / 180 ? SlideKind.RockAvalanche : SlideKind.Landslide;
            }
            if (best == null || fs < best.Value.FactorOfSafety)
                best = new Candidate(d, slope, fs, kind, h);
        }
        return best is { FactorOfSafety: < 1 } ? best : null;
    }
}
