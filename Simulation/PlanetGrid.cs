using System;
using System.Collections.Generic;
using Godot;

namespace Planet.Simulation;

/// <summary>
/// Grille géodésique de la planète : un icosaèdre subdivisé dont les sommets sont projetés
/// sur la sphère unité. Chaque sommet est une cellule de simulation.
/// On obtient toujours 12 cellules pentagonales (les sommets de l'icosaèdre d'origine),
/// toutes les autres sont hexagonales. Nombre de cellules = 10 × 4^n + 2.
/// </summary>
public sealed class PlanetGrid
{
    public const int MaxSubdivisions = 9;

    public int Subdivisions { get; }

    /// <summary>Position de chaque cellule sur la sphère unité.</summary>
    public Vector3[] Positions { get; }

    /// <summary>Triangles du maillage géodésique (triplets d'indices de cellules), orientés
    /// dans le sens anti-horaire vu de l'extérieur.</summary>
    public int[] Triangles { get; }

    /// <summary>Voisines de chaque cellule, ordonnées dans le sens anti-horaire.</summary>
    public int[][] Neighbors { get; }

    /// <summary>Triangles autour de chaque cellule, dans le même ordre que <see cref="Neighbors"/>.
    /// Le triangle k est celui compris entre les voisines k et k+1.</summary>
    public int[][] CellTriangles { get; }

    public int CellCount => Positions.Length;
    public int TriangleCount => Triangles.Length / 3;

    /// <summary>Distance angulaire moyenne entre deux cellules voisines (radians).
    /// Sert d'unité de longueur pour borner les pas de temps.</summary>
    public float MeanEdgeAngle { get; }

    private PlanetGrid(int subdivisions, Vector3[] positions, int[] triangles,
                       int[][] neighbors, int[][] cellTriangles)
    {
        Subdivisions = subdivisions;
        Positions = positions;
        Triangles = triangles;
        Neighbors = neighbors;
        CellTriangles = cellTriangles;
        MeanEdgeAngle = ComputeMeanEdgeAngle();
    }

    /// <summary>
    /// Cellule la plus proche d'un point de la sphère, en marchant de voisine en voisine depuis
    /// une cellule de départ : on avance tant qu'une voisine est plus proche. Sur une grille
    /// géodésique cette marche aboutit à la plus proche ; elle est très rapide quand le départ
    /// est déjà voisin du résultat.
    /// </summary>
    public int NearestCell(Vector3 point, int startCell)
    {
        int current = startCell;
        float currentDot = Positions[current].Dot(point);
        while (true)
        {
            int best = current;
            float bestDot = currentDot;
            foreach (int neighbor in Neighbors[current])
            {
                float dot = Positions[neighbor].Dot(point);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    best = neighbor;
                }
            }
            if (best == current)
                return current;
            current = best;
            currentDot = bestDot;
        }
    }

    /// <summary>
    /// Géométrie exacte des cellules (diagramme de Voronoï sur la sphère unité) : pour chaque
    /// cellule et chaque voisine (même ordre que <see cref="Neighbors"/>), longueur de la face
    /// commune divisée par l'aire de la cellule (1/radian). La face relie les centres des cercles
    /// circonscrits aux deux triangles qui bordent l'arête ; elle est perpendiculaire à l'arête.
    /// Avec ces poids, un vent sans divergence n'en montre pas sur la grille : une valeur commune
    /// 2/(3h) pour toutes les faces laisse au contraire des traînées le long des arêtes de
    /// l'icosaèdre d'origine, où les cellules sont un peu déformées.
    /// </summary>
    public float[][] ComputeFaceWeights()
    {
        ComputeVoronoi(out float[][] weights, out _);
        return weights;
    }

    /// <summary>Aire de chaque cellule sur la sphère unité (stéradians ; somme ≈ 4π).</summary>
    public float[] ComputeCellAreas()
    {
        ComputeVoronoi(out _, out float[] areas);
        return areas;
    }

    private void ComputeVoronoi(out float[][] faceWeights, out float[] cellAreas)
    {
        var weights = new float[Positions.Length][];
        var areas = new float[Positions.Length];
        System.Threading.Tasks.Parallel.For(0, Positions.Length, cell =>
        {
            Vector3 p = Positions[cell];
            int[] neighbors = Neighbors[cell];
            var lengths = new double[neighbors.Length];
            double area = 0;
            for (int k = 0; k < neighbors.Length; k++)
            {
                int n = neighbors[k];
                // Les deux voisines communes à la cellule et à sa voisine n
                Vector3 c1 = Vector3.Zero, c2 = Vector3.Zero;
                int found = 0;
                foreach (int m in neighbors)
                {
                    if (m == n || Array.IndexOf(Neighbors[n], m) < 0)
                        continue;
                    Vector3 center = Circumcenter(p, Positions[n], Positions[m]);
                    if (found == 0) c1 = center; else c2 = center;
                    found++;
                }
                if (found < 2)
                    continue;
                lengths[k] = c1.AngleTo(c2);
                area += 0.5 * (c1 - p).Cross(c2 - p).Length();
            }
            weights[cell] = new float[neighbors.Length];
            for (int k = 0; k < neighbors.Length; k++)
                weights[cell][k] = (float)(lengths[k] / area);
            areas[cell] = (float)area;
        });
        faceWeights = weights;
        cellAreas = areas;
    }

    private static Vector3 Circumcenter(Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 center = (b - a).Cross(c - a).Normalized();
        return center.Dot(a) < 0f ? -center : center;
    }

    private float ComputeMeanEdgeAngle()
    {
        double sum = 0;
        long count = 0;
        for (int cell = 0; cell < Positions.Length; cell++)
            foreach (int neighbor in Neighbors[cell])
            {
                sum += Positions[cell].AngleTo(Positions[neighbor]);
                count++;
            }
        return (float)(sum / count);
    }

    /// <summary>Nombre de cellules attendu pour un niveau de subdivision donné.</summary>
    public static int ExpectedCellCount(int subdivisions) => 10 * (1 << (2 * subdivisions)) + 2;

    public static PlanetGrid Create(int subdivisions)
    {
        if (subdivisions < 0 || subdivisions > MaxSubdivisions)
            throw new ArgumentOutOfRangeException(nameof(subdivisions),
                $"Subdivisions must be between 0 and {MaxSubdivisions}.");

        var positions = new List<Vector3>(ExpectedCellCount(subdivisions));
        var triangles = CreateIcosahedron(positions);

        for (int level = 0; level < subdivisions; level++)
            triangles = Subdivide(positions, triangles);

        var positionArray = positions.ToArray();
        var triangleArray = triangles.ToArray();
        var (neighbors, cellTriangles) = BuildTopology(positionArray.Length, triangleArray);

        return new PlanetGrid(subdivisions, positionArray, triangleArray, neighbors, cellTriangles);
    }

    /// <summary>Centre (projeté sur la sphère) d'un triangle : c'est un coin des cellules
    /// qui l'entourent, utilisé pour dessiner les hexagones.</summary>
    public Vector3 TriangleCenter(int triangle)
    {
        int i = triangle * 3;
        return (Positions[Triangles[i]] + Positions[Triangles[i + 1]] + Positions[Triangles[i + 2]])
            .Normalized();
    }

    // --- Construction ------------------------------------------------------------------------

    private static List<int> CreateIcosahedron(List<Vector3> positions)
    {
        // Les 12 sommets de l'icosaèdre sont les permutations de (0, ±1, ±φ)
        float t = (1f + Mathf.Sqrt(5f)) / 2f;
        Vector3[] vertices =
        {
            new(-1,  t,  0), new( 1,  t,  0), new(-1, -t,  0), new( 1, -t,  0),
            new( 0, -1,  t), new( 0,  1,  t), new( 0, -1, -t), new( 0,  1, -t),
            new( t,  0, -1), new( t,  0,  1), new(-t,  0, -1), new(-t,  0,  1),
        };
        foreach (var v in vertices)
            positions.Add(v.Normalized());

        int[] faces =
        {
            0, 11, 5,   0, 5, 1,    0, 1, 7,    0, 7, 10,   0, 10, 11,
            1, 5, 9,    5, 11, 4,   11, 10, 2,  10, 7, 6,   7, 1, 8,
            3, 9, 4,    3, 4, 2,    3, 2, 6,    3, 6, 8,    3, 8, 9,
            4, 9, 5,    2, 4, 11,   6, 2, 10,   8, 6, 7,    9, 8, 1,
        };

        // On impose l'orientation anti-horaire vue de l'extérieur plutôt que de la supposer :
        // la normale du triangle doit pointer dans le même sens que son centre
        var triangles = new List<int>(faces.Length);
        for (int i = 0; i < faces.Length; i += 3)
        {
            int a = faces[i], b = faces[i + 1], c = faces[i + 2];
            Vector3 pa = positions[a], pb = positions[b], pc = positions[c];
            bool outward = (pb - pa).Cross(pc - pa).Dot(pa + pb + pc) > 0f;
            triangles.Add(a);
            triangles.Add(outward ? b : c);
            triangles.Add(outward ? c : b);
        }
        return triangles;
    }

    /// <summary>Découpe chaque triangle en 4 en insérant le milieu de chaque arête,
    /// projeté sur la sphère. L'orientation des triangles est conservée.</summary>
    private static List<int> Subdivide(List<Vector3> positions, List<int> triangles)
    {
        var result = new List<int>(triangles.Count * 4);
        // Cache des milieux : une arête est partagée par deux triangles, son milieu ne doit
        // être créé qu'une fois
        var midpointCache = new Dictionary<long, int>(triangles.Count * 3 / 2);

        int Midpoint(int a, int b)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            if (midpointCache.TryGetValue(key, out int index))
                return index;
            index = positions.Count;
            positions.Add(((positions[a] + positions[b]) * 0.5f).Normalized());
            midpointCache[key] = index;
            return index;
        }

        for (int i = 0; i < triangles.Count; i += 3)
        {
            int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
            int ab = Midpoint(a, b), bc = Midpoint(b, c), ca = Midpoint(c, a);

            result.AddRange(new[] { a, ab, ca });
            result.AddRange(new[] { b, bc, ab });
            result.AddRange(new[] { c, ca, bc });
            result.AddRange(new[] { ab, bc, ca });
        }
        return result;
    }

    /// <summary>Calcule pour chaque cellule ses voisines et ses triangles, ordonnés en tournant
    /// autour d'elle.</summary>
    private static (int[][] neighbors, int[][] cellTriangles) BuildTopology(int cellCount, int[] triangles)
    {
        // Pour un triangle (a, b, c) anti-horaire, en tournant autour de a on passe de b à c.
        // On mémorise donc pour chaque cellule : voisine → (voisine suivante, triangle entre les deux)
        var next = new Dictionary<int, (int nextNeighbor, int triangle)>[cellCount];
        for (int i = 0; i < cellCount; i++)
            next[i] = new Dictionary<int, (int, int)>(6);

        for (int t = 0; t < triangles.Length / 3; t++)
        {
            int a = triangles[3 * t], b = triangles[3 * t + 1], c = triangles[3 * t + 2];
            next[a][b] = (c, t);
            next[b][c] = (a, t);
            next[c][a] = (b, t);
        }

        var neighbors = new int[cellCount][];
        var cellTriangles = new int[cellCount][];

        for (int cell = 0; cell < cellCount; cell++)
        {
            var map = next[cell];
            var ring = new int[map.Count];
            var ringTriangles = new int[map.Count];

            // On part de n'importe quelle voisine et on fait le tour
            using var enumerator = map.Keys.GetEnumerator();
            enumerator.MoveNext();
            int start = enumerator.Current;
            int current = start;
            for (int k = 0; k < ring.Length; k++)
            {
                var (nextNeighbor, triangle) = map[current];
                ring[k] = current;
                ringTriangles[k] = triangle;
                current = nextNeighbor;
            }

            if (current != start)
                throw new InvalidOperationException($"Neighbor ring of cell {cell} is not closed.");

            neighbors[cell] = ring;
            cellTriangles[cell] = ringTriangles;
        }

        return (neighbors, cellTriangles);
    }
}
