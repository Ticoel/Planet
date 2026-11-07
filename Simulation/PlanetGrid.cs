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

    private PlanetGrid(int subdivisions, Vector3[] positions, int[] triangles,
                       int[][] neighbors, int[][] cellTriangles)
    {
        Subdivisions = subdivisions;
        Positions = positions;
        Triangles = triangles;
        Neighbors = neighbors;
        CellTriangles = cellTriangles;
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
