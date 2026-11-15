using System;
using System.Collections.Generic;
using Godot;
using Planet.Simulation.Terrain;

namespace Planet.Simulation.Climate;

/// <summary>Sorte de source d'eau douce.</summary>
public enum SpringKind
{
    /// <summary>Source de pente : la nappe affleure au pied d'un versant.</summary>
    Slope,
    /// <summary>Résurgence karstique : une rivière souterraine ressort au pied d'une falaise calcaire.</summary>
    Karst,
}

/// <summary>Une source d'eau douce.</summary>
public sealed class Spring
{
    public SpringKind Kind { get; init; }
    public Vector3 Position { get; init; }
    public int Cell { get; init; }
    /// <summary>Débit (L/s).</summary>
    public float FlowLs { get; init; }
    /// <summary>Rayon de la vasque (m).</summary>
    public float RadiusM { get; init; }
}

/// <summary>
/// Eau souterraine et sources (étape 17).
/// <list type="bullet">
/// <item><b>Recharge</b> : une part de la pluie s'infiltre jusqu'à la nappe (~25 % en climat
/// tempéré ; plus de la moitié dans les calcaires karstiques, où l'eau s'engouffre dans les
/// fissures ; presque rien en désert ou sur un sol gelé).</item>
/// <item><b>Profondeur de la nappe</b> : d'autant plus près de la surface que la recharge est
/// forte, et que le lieu est bas par rapport à ses voisins ; près des fleuves, des lacs et des
/// côtes, la nappe affleure presque (Fan et al. 2013, profondeur de la nappe à l'échelle du
/// globe : quelques mètres dans les plaines humides, des dizaines à des centaines de mètres sous
/// les reliefs et les déserts).</item>
/// <item><b>Sources</b> : là où la nappe recoupe la surface, au pied des versants ; dans les
/// calcaires, l'eau circule en rivières souterraines et ressort en grosses résurgences au pied des
/// falaises (comme la Fontaine de Vaucluse). Débit : la recharge du bassin qui l'alimente.</item>
/// </list>
/// </summary>
public sealed class Groundwater
{
    private readonly List<Spring> _springs = new();
    private int[][] _cellSprings;

    public PlanetGrid Grid { get; }
    /// <summary>Recharge de la nappe (mm/an) de chaque cellule de terre.</summary>
    public float[] Recharge { get; }
    /// <summary>Profondeur de la nappe sous la surface (m) ; 0 en mer.</summary>
    public float[] TableDepthM { get; }
    /// <summary>Cellules karstiques (calcaires).</summary>
    public bool[] Karst { get; }
    public IReadOnlyList<Spring> Springs => _springs;

    /// <param name="elevationKm">Altitude de chaque cellule (km).</param>
    /// <param name="annualMm">Pluie annuelle (mm/an).</param>
    /// <param name="meanTemperatureC">Température moyenne (°C).</param>
    /// <param name="carbonPpm">Carbone des roches (ppm) ; null : pas de karst connu. Au-delà de
    /// ~5 000 ppm (calcaires), la roche est karstique.</param>
    /// <param name="river">Cellules parcourues par un fleuve ; null : aucune.</param>
    public Groundwater(PlanetGrid grid, float[] elevationKm, float[] annualMm, float[] meanTemperatureC, float[] carbonPpm, bool[] river, int seed)
    {
        Grid = grid;
        int n = grid.CellCount;
        Recharge = new float[n];
        TableDepthM = new float[n];
        Karst = new bool[n];
        _cellSprings = new int[n][];
        var random = new Random(seed * 7919 + 3);
        double cellAreaM2 = 4 * Math.PI * Math.Pow(PlanetConstants.RadiusKm * 1000, 2) / n;
        for (int c = 0; c < n; c++)
        {
            if (elevationKm[c] <= 0)
                continue;
            Karst[c] = carbonPpm != null && carbonPpm[c] > 5000;
            double p = Math.Max(0, annualMm[c]);
            double share = Karst[c] ? 0.5 : 0.25;
            share *= Math.Clamp((p - 150) / 350, 0, 1);             // désert : presque rien ne s'infiltre
            if (meanTemperatureC[c] < -5) share *= 0.08;            // sol gelé (pergélisol)
            Recharge[c] = (float)(p * share);
            // Relief : hauteur au-dessus de la plus basse des voisines
            double lowest = elevationKm[c];
            foreach (int nb in grid.Neighbors[c])
                lowest = Math.Min(lowest, Math.Max(0, elevationKm[nb]));
            double relief = (elevationKm[c] - lowest) * 1000;
            double depth = 60 * Math.Exp(-Recharge[c] / 120) + 0.08 * relief + (Karst[c] ? 20 : 0);
            bool wet = river != null && river[c];
            foreach (int nb in grid.Neighbors[c])
                wet |= elevationKm[nb] <= 0;
            if (wet) depth *= 0.25;
            TableDepthM[c] = (float)Math.Clamp(depth, 0.5, 300);
        }
        // Sources : là où la nappe est assez près de la surface et la recharge suffisante
        var lists = new List<int>[n];
        for (int c = 0; c < n; c++)
        {
            if (elevationKm[c] <= 0.005 || Recharge[c] < 60)
                continue;
            int count = Karst[c] ? 1 : (int)Math.Min(4, Recharge[c] / 120 * (TableDepthM[c] < 20 ? 1.5 : 0.6));
            Vector3 center = grid.Positions[c].Normalized();
            Vector3 east = Vector3.Up.Cross(center);
            east = east.LengthSquared() < 1e-6f ? Vector3.Right : east.Normalized();
            Vector3 north = center.Cross(east);
            float spread = grid.MeanEdgeAngle * 0.45f;
            for (int k = 0; k < count; k++)
            {
                float a = (float)(random.NextDouble() * 2 - 1) * spread, b = (float)(random.NextDouble() * 2 - 1) * spread;
                Vector3 position = (center + east * a + north * b).Normalized();
                // Débit : la recharge d'une part du bassin (L/s)
                double share = Karst[c] ? 0.4 : 0.02 / Math.Max(1, count);
                float flow = (float)(Recharge[c] / 1000 * cellAreaM2 * share / (365.25 * 86400) * 1000);
                var spring = new Spring
                {
                    Kind = Karst[c] ? SpringKind.Karst : SpringKind.Slope,
                    Position = position, Cell = c, FlowLs = flow,
                    RadiusM = Karst[c] ? (float)Math.Clamp(6 + Math.Sqrt(flow) * 0.15, 8, 30) : (float)Math.Clamp(2 + Math.Sqrt(flow) * 0.3, 2.5, 8),
                };
                (lists[c] ??= new()).Add(_springs.Count);
                _springs.Add(spring);
            }
        }
        for (int c = 0; c < n; c++)
            _cellSprings[c] = lists[c]?.ToArray();
    }

    /// <summary>Sources de la cellule (indices dans <see cref="Springs"/>).</summary>
    public int[] InCell(int cell) => _cellSprings[cell] ?? Array.Empty<int>();

    /// <summary>Vasques des sources, à creuser dans le relief détaillé (pleines d'eau).</summary>
    public TerrainBench[] Benches()
    {
        var benches = new TerrainBench[_springs.Count];
        for (int i = 0; i < _springs.Count; i++)
            benches[i] = new TerrainBench(Double3.From(_springs[i].Position).Normalized(), _springs[i].RadiusM * 1.6, _springs[i].RadiusM, true);
        return benches;
    }

    /// <summary>Fiche de la sonde : nappe et sources de la cellule.</summary>
    public string Describe(int cell)
    {
        if (TableDepthM[cell] <= 0)
            return null;
        string text = $"nappe à {TableDepthM[cell]:0} m sous la surface, rechargée de {Recharge[cell]:0} mm/an" + (Karst[cell] ? " (calcaires karstiques)" : "");
        var springs = InCell(cell);
        if (springs.Length > 0)
        {
            float flow = 0;
            foreach (int i in springs) flow += _springs[i].FlowLs;
            text += Karst[cell] ? $" ; résurgence de {flow:N0} L/s" : $" ; {springs.Length} source{(springs.Length > 1 ? "s" : "")} ({flow:N0} L/s)";
        }
        return text;
    }
}
