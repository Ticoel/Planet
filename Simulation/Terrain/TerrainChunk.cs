using System;

namespace Planet.Simulation.Terrain;

/// <summary>
/// Géométrie d'un morceau de terrain, prête à envoyer à la carte graphique. Positions relatives
/// au centre du morceau (float suffisant à cette échelle), plus de quoi faire le « morphing »
/// dans le vertex shader : chaque sommet sait où il serait sur la grille deux fois plus
/// grossière du morceau parent, et glisse vers cette position avant que le parent ne le
/// remplace : le relief ne saute pas quand le niveau de détail change.
/// </summary>
public sealed class TerrainChunk
{
    public ChunkKey Key { get; init; }
    /// <summary>Centre du morceau (m), au niveau de la mer, dans le repère de la planète.</summary>
    public Double3 Center { get; init; }
    /// <summary>Sommets : x, y, z relatifs au centre (m).</summary>
    public float[] Positions { get; init; }
    public float[] Normals { get; init; }
    /// <summary>Morphing : déplacement vers la position « parent » (xyz, m) et altitude réelle (w, m).</summary>
    public float[] Custom0 { get; init; }
    /// <summary>Morphing : normale « parent » (xyz) et altitude réelle « parent » (w, m).</summary>
    public float[] Custom1 { get; init; }
    /// <summary>Eau : niveau de l'eau (x, m ; 0 = mer, plus haut pour un lac) et lit de fleuve (y, 0 à 1).</summary>
    public float[] Water { get; init; }
    /// <summary>Vrai si une partie du morceau est sous une étendue d'eau (mer ou lac).</summary>
    public bool HasWater { get; init; }
    public double MinHeight { get; init; }
    public double MaxHeight { get; init; }
    /// <summary>Sphère englobante (centre absolu, rayon), surfaces de raccord comprises.</summary>
    public Double3 BoundsCenter { get; init; }
    public double BoundsRadius { get; init; }
}

/// <summary>Construit les morceaux de terrain (thread-safe, appelé par les threads de calcul).</summary>
public static class TerrainChunkBuilder
{
    /// <summary>Nombre de quadrilatères par côté d'un morceau (pair).</summary>
    public const int Resolution = 32;
    public const int Side = Resolution + 1;
    private const int Padded = Resolution + 3;   // une rangée de plus de chaque côté, pour les normales

    /// <summary>Nombre de sommets : grille + jupe (un sommet abaissé par sommet du bord).</summary>
    public static int VertexCount => Side * Side + 4 * Resolution;

    private static readonly Lazy<int[]> SharedIndices = new(BuildIndices);

    /// <summary>Triangles, communs à tous les morceaux (sens horaire vu de l'extérieur, convention Godot).</summary>
    public static int[] Indices => SharedIndices.Value;

    /// <summary>Indices des sommets du bord, dans l'ordre du pourtour.</summary>
    private static int[] BorderVertices()
    {
        var border = new int[4 * Resolution];
        int k = 0;
        for (int i = 0; i < Resolution; i++) border[k++] = i;                                   // bas, vers la droite
        for (int j = 0; j < Resolution; j++) border[k++] = j * Side + Resolution;               // droite, vers le haut
        for (int i = Resolution; i > 0; i--) border[k++] = Resolution * Side + i;               // haut, vers la gauche
        for (int j = Resolution; j > 0; j--) border[k++] = j * Side;                            // gauche, vers le bas
        return border;
    }

    private static int[] BuildIndices()
    {
        var indices = new System.Collections.Generic.List<int>(Resolution * Resolution * 6 + Resolution * 48);
        for (int j = 0; j < Resolution; j++)
            for (int i = 0; i < Resolution; i++)
            {
                int a = j * Side + i, b = a + 1, c = a + Side, d = c + 1;
                // Diagonale a–d partout : c'est sur elle que glissent les sommets « impairs-impairs »
                indices.Add(a); indices.Add(d); indices.Add(b);
                indices.Add(a); indices.Add(c); indices.Add(d);
            }
        // Jupe : bandes verticales sous le pourtour, visibles des deux côtés, qui masquent les
        // éventuelles fentes entre morceaux de niveaux différents
        int[] border = BorderVertices();
        for (int k = 0; k < border.Length; k++)
        {
            int top0 = border[k], top1 = border[(k + 1) % border.Length];
            int bottom0 = Side * Side + k, bottom1 = Side * Side + (k + 1) % border.Length;
            indices.Add(top0); indices.Add(top1); indices.Add(bottom1);
            indices.Add(top0); indices.Add(bottom1); indices.Add(bottom0);
            indices.Add(top0); indices.Add(bottom1); indices.Add(top1);
            indices.Add(top0); indices.Add(bottom0); indices.Add(bottom1);
        }
        return indices.ToArray();
    }

    /// <summary>Calcule un morceau : altitudes, positions, normales et données de morphing.</summary>
    public static TerrainChunk Build(TerrainHeightField field, ChunkKey key)
    {
        double radius = TerrainHeightField.RadiusMeters;
        double step = key.FaceSize / Resolution;
        // Plus courte longueur d'onde utile : deux fois l'écart entre sommets
        double spacing = CubeSphere.ChunkSizeMeters(key.Level) / Resolution;
        double minWavelength = 2.0 * spacing;

        // Le sol est affiché à sa vraie altitude, fonds marins compris : l'eau est une surface à part
        var surface = new Double3[Padded * Padded];
        var height = new double[Padded * Padded];
        var water = new double[Padded * Padded];
        var river = new double[Padded * Padded];
        int hint = -1;
        bool hasWater = false;
        for (int j = 0; j < Padded; j++)
            for (int i = 0; i < Padded; i++)
            {
                Double3 direction = CubeSphere.Direction(key.Face, key.MinS + (i - 1) * step, key.MinT + (j - 1) * step);
                var sample = field.Sample(direction, minWavelength, ref hint);
                int index = j * Padded + i;
                height[index] = sample.Height;
                water[index] = sample.WaterLevel;
                river[index] = sample.River;
                // (côtes basses comprises : la marée haute et la remontée de la mer peuvent les couvrir)
                hasWater |= sample.WaterLevel > sample.Height
                            || sample.Height < Climate.Tides.MaxHeightMeters + Climate.SeaLevel.MaxRiseMeters;
                surface[index] = direction * (radius + sample.Height);
            }

        // Relief tel que le calcule le morceau parent (sans les longueurs d'onde que lui-même
        // ignore), aux sommets qu'il partage avec ce morceau (une rangée sur deux). C'est la cible
        // du morphing : arrivé au bout, ce morceau coïncide exactement avec son voisin plus
        // grossier. Sans cela, les petites ondulations propres au morceau fin restaient et, près
        // du niveau de la mer, la côte changeait d'un coup le long de la limite entre niveaux
        var parentSurface = new Double3[Side * Side];
        var parentHeight = new double[Side * Side];
        for (int j = 0; j <= Resolution; j += 2)
            for (int i = 0; i <= Resolution; i += 2)
            {
                Double3 direction = CubeSphere.Direction(key.Face, key.MinS + i * step, key.MinT + j * step);
                double h = field.Sample(direction, 2.0 * minWavelength, ref hint).Height;
                parentHeight[j * Side + i] = h;
                parentSurface[j * Side + i] = direction * (radius + h);
            }

        Double3 center = CubeSphere.CenterDirection(key) * radius;
        int count = VertexCount;
        var positions = new float[count * 3];
        var normals = new float[count * 3];
        var custom0 = new float[count * 4];
        var custom1 = new float[count * 4];
        var waterData = new float[count * 2];
        double minHeight = double.MaxValue, maxHeight = double.MinValue;

        Double3 P(int i, int j) => surface[(j + 1) * Padded + i + 1];
        double H(int i, int j) => height[(j + 1) * Padded + i + 1];
        Double3 N(int i, int j)
        {
            Double3 n = (P(i + 1, j) - P(i - 1, j)).Cross(P(i, j + 1) - P(i, j - 1)).Normalized();
            return n.Dot(P(i, j)) < 0 ? -n : n;
        }

        for (int j = 0; j <= Resolution; j++)
            for (int i = 0; i <= Resolution; i++)
            {
                int v = j * Side + i;
                Double3 p = P(i, j);
                Double3 n = N(i, j);
                double h = H(i, j);
                minHeight = Math.Min(minHeight, h);
                maxHeight = Math.Max(maxHeight, Math.Max(h, water[(j + 1) * Padded + i + 1]));

                // Position sur la grille du parent (une rangée sur deux) : milieu d'une arête ou
                // de la diagonale du quadrilatère parent
                int i0 = i, i1 = i, j0 = j, j1 = j;
                if ((i & 1) == 1) { i0 = i - 1; i1 = i + 1; }
                if ((j & 1) == 1) { j0 = j - 1; j1 = j + 1; }
                Double3 coarse = (parentSurface[j0 * Side + i0] + parentSurface[j1 * Side + i1]) * 0.5;
                Double3 coarseNormal = (N(i0, j0) + N(i1, j1)).Normalized();
                double coarseHeight = (parentHeight[j0 * Side + i0] + parentHeight[j1 * Side + i1]) * 0.5;

                Write3(positions, v, p - center);
                Write3(normals, v, n);
                Write4(custom0, v, coarse - p, h);
                Write4(custom1, v, coarseNormal, coarseHeight);
                waterData[v * 2] = (float)water[(j + 1) * Padded + i + 1];
                waterData[v * 2 + 1] = (float)river[(j + 1) * Padded + i + 1];
            }

        // Jupe : copie des sommets du bord, abaissée vers le centre de la planète
        double skirtDepth = 20.0 + spacing * 4.0;
        int[] border = BorderVertices();
        for (int k = 0; k < border.Length; k++)
        {
            int source = border[k], v = Side * Side + k;
            var p = new Double3(positions[source * 3], positions[source * 3 + 1], positions[source * 3 + 2]) + center;
            Write3(positions, v, p - p.Normalized() * skirtDepth - center);
            Array.Copy(normals, source * 3, normals, v * 3, 3);
            Array.Copy(custom0, source * 4, custom0, v * 4, 4);
            Array.Copy(custom1, source * 4, custom1, v * 4, 4);
            Array.Copy(waterData, source * 2, waterData, v * 2, 2);
        }

        // Sphère englobante : tous les sommets (morphing compris)
        Double3 boundsCenter = CubeSphere.CenterDirection(key) * (radius + (minHeight + maxHeight) / 2);
        double boundsRadius = 0;
        for (int v = 0; v < count; v++)
        {
            var p = new Double3(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]) + center;
            var m = p + new Double3(custom0[v * 4], custom0[v * 4 + 1], custom0[v * 4 + 2]);
            boundsRadius = Math.Max(boundsRadius, Math.Max(p.DistanceTo(boundsCenter), m.DistanceTo(boundsCenter)));
        }

        return new TerrainChunk
        {
            Key = key,
            Center = center,
            Positions = positions,
            Normals = normals,
            Custom0 = custom0,
            Custom1 = custom1,
            Water = waterData,
            HasWater = hasWater,
            MinHeight = minHeight,
            MaxHeight = maxHeight,
            BoundsCenter = boundsCenter,
            BoundsRadius = boundsRadius,
        };
    }

    private static void Write3(float[] array, int vertex, Double3 value)
    {
        array[vertex * 3] = (float)value.X;
        array[vertex * 3 + 1] = (float)value.Y;
        array[vertex * 3 + 2] = (float)value.Z;
    }

    private static void Write4(float[] array, int vertex, Double3 value, double w)
    {
        array[vertex * 4] = (float)value.X;
        array[vertex * 4 + 1] = (float)value.Y;
        array[vertex * 4 + 2] = (float)value.Z;
        array[vertex * 4 + 3] = (float)w;
    }
}
