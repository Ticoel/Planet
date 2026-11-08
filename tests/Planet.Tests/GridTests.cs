using System;
using System.Linq;
using Godot;
using Planet.Simulation;

namespace Planet.Tests;

/// <summary>Grille icosaédrique : topologie, géométrie exacte des cellules, recherche de voisine.</summary>
public static class GridTests
{
    [Test]
    public static void CellCountMatchesFormula()
    {
        for (int n = 0; n <= 5; n++)
            Assert.Equal(10 * (1 << (2 * n)) + 2, Fixtures.Grid(n).CellCount, $"cellules au niveau {n}");
    }

    [Test]
    public static void TwelvePentagonsAndHexagonsElsewhere()
    {
        var grid = Fixtures.Grid(4);
        int pentagons = grid.Neighbors.Count(n => n.Length == 5);
        int hexagons = grid.Neighbors.Count(n => n.Length == 6);
        Assert.Equal(12, pentagons, "pentagones");
        Assert.Equal(grid.CellCount - 12, hexagons, "hexagones");
    }

    [Test]
    public static void NeighborsAreSymmetricAndDistinct()
    {
        var grid = Fixtures.Grid(4);
        for (int cell = 0; cell < grid.CellCount; cell++)
        {
            int[] neighbors = grid.Neighbors[cell];
            Assert.True(neighbors.Distinct().Count() == neighbors.Length, $"voisine en double pour la cellule {cell}");
            foreach (int n in neighbors)
            {
                Assert.True(n != cell, $"la cellule {cell} est sa propre voisine");
                Assert.True(grid.Neighbors[n].Contains(cell), $"voisinage non réciproque {cell} ↔ {n}");
            }
        }
    }

    [Test]
    public static void PositionsAreOnUnitSphere()
    {
        var grid = Fixtures.Grid(5);
        float worst = grid.Positions.Max(p => MathF.Abs(p.Length() - 1f));
        Assert.True(worst < 1e-5f, $"écart maximal au rayon 1 : {worst}");
    }

    [Test]
    public static void CoarseVerticesKeepTheirIndices()
    {
        // L'estimateur climatique et les flèches reposent sur ce point : les sommets du niveau n
        // sont les premières cellules de tout niveau plus fin
        var coarse = Fixtures.Grid(3);
        var fine = Fixtures.Grid(5);
        for (int cell = 0; cell < coarse.CellCount; cell++)
            Assert.True(coarse.Positions[cell].DistanceTo(fine.Positions[cell]) < 1e-5f, $"sommet {cell} déplacé");
    }

    [Test]
    public static void NearestCellMatchesBruteForce()
    {
        var grid = Fixtures.Grid(4);
        var random = new Random(3);
        int start = 0;
        for (int i = 0; i < 2000; i++)
        {
            var point = new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f,
                                    (float)random.NextDouble() - 0.5f).Normalized();
            int walked = grid.NearestCell(point, start);
            float best = grid.Positions.Max(p => p.Dot(point));
            Assert.True(grid.Positions[walked].Dot(point) >= best - 1e-6f, $"point {i} : cellule {walked} n'est pas la plus proche");
            start = walked;
        }
    }

    [Test]
    public static void CellAreasCoverTheSphere()
    {
        foreach (int n in new[] { 3, 5 })
        {
            float[] areas = Fixtures.Grid(n).ComputeCellAreas();
            double total = areas.Sum(a => (double)a);
            Assert.Near(total, 4.0 * Math.PI, 4.0 * Math.PI * 2e-3, $"aire totale au niveau {n} (4π)");
            Assert.True(areas.All(a => a > 0f), "aire nulle ou négative");
        }
    }

    [Test]
    public static void RotationWindHasNoDivergence()
    {
        // Rotation solide autour d'un axe incliné : divergence exactement nulle
        var grid = Fixtures.Grid(5);
        float[][] weights = grid.ComputeFaceWeights();
        var axis = new Vector3(0.3f, 0.9f, 0.2f).Normalized();
        double rms = 0, scale = 1.0 / grid.MeanEdgeAngle;
        for (int cell = 0; cell < grid.CellCount; cell++)
        {
            double divergence = Divergence(grid, weights, cell, p => axis.Cross(p));
            rms += divergence * divergence;
        }
        rms = Math.Sqrt(rms / grid.CellCount);
        Assert.True(rms < 1e-3 * scale, $"divergence parasite rms {rms:G3} (échelle U/h {scale:G3})");
    }

    [Test]
    public static void GradientWindDivergenceMatchesAnalytic()
    {
        // u = composante tangente d'un vecteur fixe e : div u = −2 (p·e) sur la sphère unité
        var grid = Fixtures.Grid(5);
        float[][] weights = grid.ComputeFaceWeights();
        var e = new Vector3(0.2f, 0.7f, -0.4f).Normalized();
        double errorSquares = 0, valueSquares = 0;
        for (int cell = 0; cell < grid.CellCount; cell++)
        {
            Vector3 p = grid.Positions[cell];
            double numeric = Divergence(grid, weights, cell, q => e - q * q.Dot(e));
            double exact = -2.0 * p.Dot(e);
            errorSquares += (numeric - exact) * (numeric - exact);
            valueSquares += exact * exact;
        }
        double relative = Math.Sqrt(errorSquares / valueSquares);
        Assert.True(relative < 0.01, $"erreur relative rms {relative:P2}");
    }

    /// <summary>Divergence discrète utilisée par les modèles (flux sur les faces, géométrie exacte).</summary>
    private static double Divergence(PlanetGrid grid, float[][] weights, int cell, Func<Vector3, Vector3> field)
    {
        Vector3 p = grid.Positions[cell];
        double outflow = 0;
        int[] neighbors = grid.Neighbors[cell];
        for (int k = 0; k < neighbors.Length; k++)
        {
            Vector3 q = grid.Positions[neighbors[k]];
            Vector3 d = q - p;
            Vector3 direction = (d - p * d.Dot(p)).Normalized();
            outflow += 0.5f * (field(p) + field(q)).Dot(direction) * weights[cell][k];
        }
        return outflow;
    }
}
