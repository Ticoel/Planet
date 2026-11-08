using System;
using System.Collections.Generic;

namespace Planet.Simulation.Climate;

/// <summary>Réseau hydrographique calculé pour un relief et un ruissellement donnés.</summary>
public sealed class HydrologyResult
{
    /// <summary>Cellule vers laquelle l'eau de chaque cellule s'écoule (−1 : océan).</summary>
    public int[] Downstream { get; init; }

    /// <summary>Débit traversant chaque cellule (m³/s).</summary>
    public float[] Discharge { get; init; }

    /// <summary>Nombre de cellules de terre drainées par chaque cellule (elle comprise).</summary>
    public int[] UpstreamCells { get; init; }

    /// <summary>Vrai pour les cellules parcourues par une rivière assez grande pour être dessinée.</summary>
    public bool[] River { get; init; }

    /// <summary>Altitude de la surface de l'eau après remplissage des cuvettes (km).</summary>
    public float[] FilledElevation { get; init; }

    /// <summary>Vrai pour les cellules couvertes par un lac.</summary>
    public bool[] Lake { get; init; }

    /// <summary>Vrai pour les cellules couvertes par un glacier ou une calotte.</summary>
    public bool[] Glacier { get; init; }

    public int RiverCellCount { get; init; }
    public int LakeCellCount { get; init; }
    public int GlacierCellCount { get; init; }

    /// <summary>Débit du plus grand fleuve, à son embouchure (m³/s).</summary>
    public float LargestDischarge { get; init; }
}

/// <summary>
/// Rivières, lacs et glaciers (étape 7).
/// <list type="number">
/// <item>Les cuvettes du relief sont remplies (algorithme « priority-flood » : on part des côtes
/// et on remonte toujours par la cellule la plus basse) : chaque cellule de terre reçoit ainsi
/// un chemin vers la mer, et l'eau qui stagne dans une cuvette forme un lac.</item>
/// <item>Le ruissellement annuel de chaque cellule descend ce chemin ; les débits s'additionnent
/// de l'amont vers l'aval : on obtient les bassins versants et les fleuves.</item>
/// <item>Là où la neige ne fond plus d'une année sur l'autre (plus de 2 m d'équivalent eau),
/// elle forme un glacier.</item>
/// </list>
/// Le calcul est indépendant de la carte graphique et peut tourner en tâche de fond.
/// </summary>
public static class HydrologyModel
{
    // Une rivière est dessinée au-delà d'un débit (m³/s) et d'un bassin versant minimal (en
    // cellules : sans ce second critère, à basse résolution, chaque cellule pluvieuse de
    // plusieurs dizaines de milliers de km² dépasserait déjà le seuil de débit) ; débit minimal
    // d'un lac
    public const float RiverThreshold = 150f;
    public const int RiverMinUpstreamCells = 8;
    public const float LakeInflowThreshold = 50f;
    // Profondeur minimale d'une cuvette remplie pour former un lac (km)
    public const float LakeMinDepthKm = 0.02f;
    // Pente minimale imposée en remontant les cuvettes (km par cellule), pour que l'eau trouve
    // toujours une direction
    private const float FillEpsilonKm = 1e-4f;

    public static HydrologyResult Compute(PlanetGrid grid, float[] elevationKm, float[] annualRunoffMm, float[] maxSnowMm)
    {
        int count = grid.CellCount;
        var downstream = new int[count];
        var filled = new float[count];
        var order = new int[count];
        var visited = new bool[count];
        int orderLength = 0;

        // Priority-flood (Barnes et al., 2014) : file de priorité par altitude, depuis l'océan
        var queue = new PriorityQueue<int, float>();
        // Initialisation à part : dans la boucle d'amorçage ci-dessous, une côte d'indice plus
        // grand que sa voisine océanique perdrait sinon son exutoire (bug trouvé par les tests)
        Array.Fill(downstream, -1);
        for (int cell = 0; cell < count; cell++)
        {
            if (elevationKm[cell] <= 0f)
            {
                visited[cell] = true;
                filled[cell] = 0f;
                // Les côtes amorcent la remontée vers l'intérieur des terres
                foreach (int n in grid.Neighbors[cell])
                    if (elevationKm[n] > 0f && !visited[n])
                    {
                        visited[n] = true;
                        filled[n] = elevationKm[n];
                        downstream[n] = cell;
                        queue.Enqueue(n, filled[n]);
                    }
            }
        }
        while (queue.Count > 0)
        {
            int cell = queue.Dequeue();
            order[orderLength++] = cell;
            foreach (int n in grid.Neighbors[cell])
            {
                if (visited[n])
                    continue;
                visited[n] = true;
                filled[n] = MathF.Max(elevationKm[n], filled[cell] + FillEpsilonKm);
                downstream[n] = cell;
                queue.Enqueue(n, filled[n]);
            }
        }

        // Débits : de l'amont (fin de l'ordre) vers l'aval (début)
        double cellAreaM2 = 4.0 * Math.PI * Math.Pow(PlanetConstants.RadiusKm * 1000.0, 2) / count;
        const double secondsPerYear = 365.25 * 86400.0;
        var discharge = new float[count];
        var upstream = new int[count];
        for (int cell = 0; cell < count; cell++)
            if (elevationKm[cell] > 0f)
            {
                discharge[cell] = (float)(annualRunoffMm[cell] * 1e-3 * cellAreaM2 / secondsPerYear);
                upstream[cell] = 1;
            }
        for (int k = orderLength - 1; k >= 0; k--)
        {
            int cell = order[k];
            int down = downstream[cell];
            if (down >= 0 && elevationKm[down] > 0f)
            {
                discharge[down] += discharge[cell];
                upstream[down] += upstream[cell];
            }
        }

        var lake = new bool[count];
        var glacier = new bool[count];
        var river = new bool[count];
        int rivers = 0, lakes = 0, glaciers = 0;
        float largest = 0f;
        for (int cell = 0; cell < count; cell++)
        {
            if (elevationKm[cell] <= 0f)
            {
                filled[cell] = elevationKm[cell];
                continue;
            }
            glacier[cell] = maxSnowMm[cell] >= AtmosphereModel.GlacierThresholdMm;
            lake[cell] = !glacier[cell] && filled[cell] - elevationKm[cell] > LakeMinDepthKm
                         && discharge[cell] > LakeInflowThreshold;
            river[cell] = !glacier[cell] && discharge[cell] > RiverThreshold && upstream[cell] >= RiverMinUpstreamCells;
            if (river[cell]) rivers++;
            if (lake[cell]) lakes++;
            if (glacier[cell]) glaciers++;
            int down = downstream[cell];
            if (down >= 0 && elevationKm[down] <= 0f)
                largest = MathF.Max(largest, discharge[cell]);
        }

        return new HydrologyResult
        {
            Downstream = downstream,
            Discharge = discharge,
            UpstreamCells = upstream,
            River = river,
            FilledElevation = filled,
            Lake = lake,
            Glacier = glacier,
            RiverCellCount = rivers,
            LakeCellCount = lakes,
            GlacierCellCount = glaciers,
            LargestDischarge = largest,
        };
    }
}
