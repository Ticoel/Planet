using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Planet.Simulation;

namespace Planet.Simulation.Terrain;

/// <summary>Une falaise trouvée sur la côte.</summary>
/// <param name="Shore">Point du rivage, au pied de la falaise (direction unitaire).</param>
/// <param name="Inland">Direction vers l'intérieur des terres (tangente unitaire au rivage).</param>
/// <param name="HeightM">Hauteur du rebord au-dessus de la mer (m).</param>
/// <param name="DistanceM">Distance du rivage au rebord (m) : plus elle est courte, plus la paroi est raide.</param>
public readonly record struct Cliff(Double3 Shore, Double3 Inland, double HeightM, double DistanceM)
{
    /// <summary>Pente moyenne de la paroi (degrés).</summary>
    public double SlopeDegrees => Math.Atan2(HeightM, Math.Max(1, DistanceM)) * 180 / Math.PI;

    /// <summary>
    /// Note de spectacle : hauteur × (pente / 45°)², plafonnée à la hauteur. Le carré préfère une
    /// paroi de 80 m à pic à une colline de 200 m en pente douce.
    /// </summary>
    public double Score => HeightM * Math.Pow(Math.Min(1.0, SlopeDegrees / 45.0), 2);
}

/// <summary>
/// Cherche la falaise côtière la plus spectaculaire : pour chaque paire de cellules terre-mer de la
/// grille, on suit le relief détaillé de la mer vers la terre, on situe le rivage par dichotomie
/// (à ~5 m près), puis on mesure ce qui se dresse dans les <see cref="ReachM"/> mètres qui suivent.
/// Note : hauteur × raideur² (<see cref="Cliff.Score"/>), pour préférer une vraie paroi à une colline.
/// </summary>
public static class CliffFinder
{
    /// <summary>Profondeur de terre examinée derrière le rivage (m).</summary>
    public const double ReachM = 500;
    /// <summary>Distance du point de vue, au large (m) : elle doit être en eau libre.</summary>
    public const double ViewDistanceM = 700;
    private const int CoarseSamples = 48;
    private const int ProfileSamples = 25;
    private const int MaxCandidates = 400;
    private const double DetailWavelength = 8;

    public static Cliff? Find(TerrainHeightField field, PlanetGrid grid, float[] elevationKm)
    {
        // Candidats : terre basse ou moyenne au bord de la mer, les plus hautes d'abord
        var pairs = new List<(int land, int sea, float score)>();
        for (int c = 0; c < grid.CellCount; c++)
        {
            if (elevationKm[c] <= 0)
                continue;
            foreach (int n in grid.Neighbors[c])
                if (elevationKm[n] < 0)
                    pairs.Add((c, n, elevationKm[c] - elevationKm[n] * 0.1f));
        }
        if (pairs.Count == 0)
            return null;
        pairs.Sort((a, b) => b.score.CompareTo(a.score));
        if (pairs.Count > MaxCandidates)
            pairs.RemoveRange(MaxCandidates, pairs.Count - MaxCandidates);

        var results = new Cliff?[pairs.Count];
        Parallel.For(0, pairs.Count, i =>
        {
            Double3 land = Double3.From(grid.Positions[pairs[i].land]).Normalized();
            Double3 sea = Double3.From(grid.Positions[pairs[i].sea]).Normalized();
            results[i] = Measure(field, sea, land);
        });

        Cliff? best = null;
        double bestScore = double.MinValue;
        foreach (var cliff in results)
        {
            if (cliff is not { } c)
                continue;
            double score = c.Score;
            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }
        return best;
    }

    /// <summary>Profil mer → terre entre deux directions : rivage, puis hauteur du rebord.</summary>
    public static Cliff? Measure(TerrainHeightField field, Double3 sea, Double3 land)
    {
        int hint = 0;
        Double3 At(double t) => (sea * (1 - t) + land * t).Normalized();
        double H(double t) => field.Height(At(t), DetailWavelength, ref hint);

        // 1. Premier passage au-dessus de la mer, en partant du large (qui doit être en mer)
        if (H(0) > -2)
            return null;
        double previous = 0;
        double crossing = -1;
        for (int k = 1; k <= CoarseSamples; k++)
        {
            double t = k / (double)CoarseSamples;
            if (H(t) > 0)
            {
                crossing = t;
                break;
            }
            previous = t;
        }
        if (crossing < 0)
            return null;

        // 2. Dichotomie jusqu'à ~5 m
        double length = sea.DistanceTo(land) * TerrainHeightField.RadiusMeters;
        double low = previous, high = crossing;
        while ((high - low) * length > 5)
        {
            double mid = 0.5 * (low + high);
            if (H(mid) > 0) high = mid; else low = mid;
        }
        Double3 shore = At(low);
        Double3 inland = land - shore * land.Dot(shore);
        if (inland.LengthSquared() < 1e-20)
            return null;
        inland = inland.Normalized();

        // 2 bis. Le large doit être de l'eau libre jusqu'au point de vue (pas un îlot, pas une
        // cuvette sous le niveau de la mer)
        for (double d = 100; d <= ViewDistanceM; d += 100)
            if (field.Height((shore - inland * (d / TerrainHeightField.RadiusMeters)).Normalized(), DetailWavelength, ref hint) > -1)
                return null;

        // 3. Ce qui se dresse derrière le rivage : le rebord est le point du profil le mieux noté
        Cliff? best = null;
        for (int k = 1; k <= ProfileSamples; k++)
        {
            double d = ReachM * k / ProfileSamples;
            double h = field.Height((shore + inland * (d / TerrainHeightField.RadiusMeters)).Normalized(), DetailWavelength, ref hint);
            var candidate = new Cliff(shore, inland, h, d);
            if (h >= 5 && (best == null || candidate.Score > best.Value.Score))
                best = candidate;
        }
        return best;
    }
}
