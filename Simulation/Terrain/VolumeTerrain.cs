using System;
using System.Collections.Generic;
using Godot;

namespace Planet.Simulation.Terrain;

/// <summary>
/// Repère local plat du relief en volume : x, y dans le plan tangent en un point d'ancrage, z
/// vers le haut, en mètres. Un point (x, y, z) est à la distance (rayon de l'ancrage + z) du
/// centre de la planète : la courbure est corrigée (sur 1,5 km, elle fait 0,5 m sur une planète
/// de 2 000 km).
/// </summary>
public sealed class VolumeFrame
{
    public Double3 Origin { get; }
    public Double3 Up { get; }
    public Double3 AxisX { get; }
    public Double3 AxisY { get; }
    private readonly double _r0;

    public VolumeFrame(Double3 direction, double groundHeight)
    {
        Up = direction.Normalized();
        Origin = Up * (TerrainHeightField.RadiusMeters + groundHeight);
        (AxisX, AxisY) = LavaFlow.TangentFrame(Up);
        _r0 = Origin.Length();
    }

    public Double3 ToLocal(Double3 world)
    {
        Double3 d = world - Origin;
        double x = d.Dot(AxisX), y = d.Dot(AxisY);
        return new Double3(x, y, d.Dot(Up) + (x * x + y * y) / (2 * _r0));
    }

    public Double3 ToWorld(Double3 local) =>
        Origin + AxisX * local.X + AxisY * local.Y + Up * (local.Z - (local.X * local.X + local.Y * local.Y) / (2 * _r0));

    /// <summary>Hauteur du sol (m, z local) à l'aplomb de (x, y).</summary>
    public double GroundZ(TerrainHeightField field, double x, double y, ref int hint, out TerrainSample sample)
    {
        Double3 direction = ToWorld(new Double3(x, y, 0)).Normalized();
        sample = field.Sample(direction, 0.5, ref hint);
        return TerrainHeightField.RadiusMeters + sample.Height - _r0;
    }
}

/// <summary>
/// Un bloc du relief en volume, prêt à dessiner avec le shader du terrain : mêmes attributs que
/// les morceaux de terrain (hauteur, niveau de l'eau, fleuves, croûte remaniée, vasières), plus
/// la nature de la paroi (surface, ou paroi de grotte : calcaire, basalte) et son ouverture vers
/// le ciel.
/// </summary>
public sealed class VolumeChunkMesh
{
    public int I, J;
    /// <summary>Centre du bloc (repère de la planète, m) ; les sommets lui sont relatifs.</summary>
    public Double3 Center;
    public Vector3[] Vertices = Array.Empty<Vector3>();
    public Vector3[] Normals = Array.Empty<Vector3>();
    /// <summary>Comme les morceaux de terrain : (déplacement du morphing, hauteur), ici sans morphing.</summary>
    public float[] Custom0 = Array.Empty<float>();
    public float[] Custom1 = Array.Empty<float>();
    /// <summary>Niveau de l'eau, part de fleuve.</summary>
    public Vector2[] Water = Array.Empty<Vector2>();
    /// <summary>Croûte remaniée : code, jour.</summary>
    public Vector2[] Debris = Array.Empty<Vector2>();
    /// <summary>r : vasière ; g : paroi de grotte (0 : surface, 0,5 : calcaire, 1 : basalte) ;
    /// b : 1 (bloc de volume) ; a : ouverture vers le ciel (1 en surface, vers 0 au fond).</summary>
    public Color[] Colors = Array.Empty<Color>();
    public int[] Indices = Array.Empty<int>();
}

/// <summary>
/// Relief en volume autour de la caméra (étape 18). Le relief détaillé est une surface (une
/// hauteur par point), qui ne peut contenir ni grotte ni surplomb ; près de la caméra, il est
/// remplacé par un <b>champ de densité 3D</b> (positif : roche, négatif : air ou vide) :
/// <list type="bullet">
/// <item>le sol : densité = hauteur du relief détaillé − altitude (le même relief : le
/// raccord avec la surface au-delà ne se voit pas) ;</item>
/// <item>moins les galeries des grottes (<see cref="Cave"/>) : l'ouverture d'une grotte est
/// simplement là où une galerie recoupe le sol, irrégulière comme ses parois ;</item>
/// <item>plus tard, ce que la vie creusera ou bâtira.</item>
/// </list>
/// Maillage par blocs de 16 m : <i>surface nets</i> (Gibson 1998), un sommet par cube traversé
/// par la surface, à la moyenne des points où la densité s'annule sur ses arêtes ; chaque arête
/// appartient à un seul bloc, et les blocs voisins partagent leurs sommets de bord : pas de fente.
/// Le volume n'est jamais stocké : calculé à la demande, n'importe où sur la planète.
/// </summary>
public static class VolumeChunkBuilder
{
    public const int Cells = 16;
    public const double VoxelM = 1.0;
    public const double ChunkM = Cells * VoxelM;

    /// <summary>Construit le bloc (i, j) du repère (tâche de fond : le relief est lu depuis plusieurs threads).</summary>
    public static VolumeChunkMesh Build(VolumeFrame frame, int i, int j, TerrainHeightField field, IReadOnlyList<Cave> caves)
    {
        double s = VoxelM;
        int p = Cells + 3;                                   // points de -1 à Cells + 1
        double x0 = i * ChunkM - s, y0 = j * ChunkM - s;     // position du point 0
        int hint = -1;
        var ground = new double[p * p];
        double low = double.MaxValue, high = double.MinValue;
        for (int b = 0; b < p; b++)
            for (int a = 0; a < p; a++)
            {
                double g = frame.GroundZ(field, x0 + a * s, y0 + b * s, ref hint, out _);
                ground[b * p + a] = g;
                low = Math.Min(low, g);
                high = Math.Max(high, g);
            }

        // Grottes proches du bloc, et leurs galeries proches
        Double3 centerLocal = new(x0 + (p - 1) * s / 2, y0 + (p - 1) * s / 2, (low + high) / 2);
        Double3 centerWorld = frame.ToWorld(centerLocal);
        double halfDiagonal = Math.Sqrt(2 * Math.Pow((p - 1) * s / 2, 2) + Math.Pow((high - low) / 2 + 3, 2));
        var nearCaves = new List<(Cave cave, int[] segments)>();
        if (caves != null)
            foreach (var cave in caves)
            {
                if (cave.Origin.DistanceTo(centerWorld) > cave.BoundingRadius + halfDiagonal + 60)
                    continue;
                int[] segments = cave.SegmentsNear(cave.ToLocal(centerWorld), halfDiagonal);
                if (segments.Length == 0)
                    continue;
                nearCaves.Add((cave, segments));
                foreach (int g in segments)
                {
                    var (sa, ra, sb, rb) = cave.Segments[g];
                    low = Math.Min(low, Math.Min(frame.ToLocal(cave.ToWorld(sa)).Z - ra, frame.ToLocal(cave.ToWorld(sb)).Z - rb) - 2.5);
                }
            }

        // Tranche verticale : du sol (et des galeries) à quelques mètres au-dessus, sur la grille commune
        int k0 = (int)Math.Floor((low - 3) / s), k1 = (int)Math.Ceiling((high + 3) / s);
        int nz = k1 - k0 + 1;
        var density = new float[p * p * nz];
        int Index(int a, int b, int k) => (k * p + b) * p + a;
        System.Threading.Tasks.Parallel.For(0, nz, k =>
        {
            double z = (k0 + k) * s;
            for (int b = 0; b < p; b++)
                for (int a = 0; a < p; a++)
                {
                    double d = ground[b * p + a] - z;
                    if (nearCaves.Count > 0)
                    {
                        Double3 world = frame.ToWorld(new Double3(x0 + a * s, y0 + b * s, z));
                        foreach (var (cave, segments) in nearCaves)
                        {
                            Double3 q = cave.ToLocal(world);
                            if (cave.Contains(q))
                                d = Math.Min(d, cave.WallDistance(q, segments));
                        }
                    }
                    density[Index(a, b, k)] = (float)d;
                }
        });

        // Sommets : un par cube traversé par la surface
        var mesh = new VolumeChunkMesh { I = i, J = j, Center = centerWorld };
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var custom0 = new List<float>();
        var custom1 = new List<float>();
        var water = new List<Vector2>();
        var debris = new List<Vector2>();
        var colors = new List<Color>();
        int cells = p - 1;
        var cellVertex = new int[cells * cells * (nz - 1)];
        Array.Fill(cellVertex, -1);
        int Cell(int a, int b, int k) => (k * cells + b) * cells + a;
        Span<float> corner = stackalloc float[8];
        for (int k = 0; k < nz - 1; k++)
            for (int b = 0; b < cells; b++)
                for (int a = 0; a < cells; a++)
                {
                    int mask = 0;
                    for (int c = 0; c < 8; c++)
                    {
                        corner[c] = density[Index(a + (c & 1), b + ((c >> 1) & 1), k + ((c >> 2) & 1))];
                        if (corner[c] > 0) mask |= 1 << c;
                    }
                    if (mask == 0 || mask == 255)
                        continue;
                    double sx = 0, sy = 0, sz = 0;
                    int crossings = 0;
                    for (int c = 0; c < 8; c++)
                        for (int axis = 0; axis < 3; axis++)
                        {
                            int e = c | (1 << axis);
                            if (e == c || ((mask >> c) & 1) == ((mask >> e) & 1))
                                continue;
                            double t = corner[c] / (corner[c] - corner[e]);
                            sx += (c & 1) + (axis == 0 ? t : 0);
                            sy += ((c >> 1) & 1) + (axis == 1 ? t : 0);
                            sz += ((c >> 2) & 1) + (axis == 2 ? t : 0);
                            crossings++;
                        }
                    var local = new Double3(x0 + (a + sx / crossings) * s, y0 + (b + sy / crossings) * s, (k0 + k + sz / crossings) * s);
                    double gx = corner[1] + corner[3] + corner[5] + corner[7] - corner[0] - corner[2] - corner[4] - corner[6];
                    double gy = corner[2] + corner[3] + corner[6] + corner[7] - corner[0] - corner[1] - corner[4] - corner[5];
                    double gz = corner[4] + corner[5] + corner[6] + corner[7] - corner[0] - corner[1] - corner[2] - corner[3];
                    // Normale vers l'air (la densité décroît), dans le repère de la planète
                    Double3 normal = -(frame.AxisX * gx + frame.AxisY * gy + frame.Up * gz);
                    normal = normal.LengthSquared() < 1e-12 ? frame.Up : normal.Normalized();

                    Double3 world = frame.ToWorld(local);
                    Double3 direction = world.Normalized();
                    double height = world.Length() - TerrainHeightField.RadiusMeters;
                    // Sol de surface ou paroi de grotte ? (la surface la plus proche à ce sommet)
                    double groundHere = Bilinear(ground, p, (local.X - x0) / s, (local.Y - y0) / s);
                    double depth = groundHere - local.Z;
                    double wall = double.MaxValue;
                    CaveKind kind = CaveKind.Karst;
                    foreach (var (cave, segments) in nearCaves)
                    {
                        double w = cave.WallDistance(cave.ToLocal(world), segments);
                        if (w < wall) { wall = w; kind = cave.Kind; }
                    }
                    bool inside = wall < depth;
                    var sample = field.Sample(direction, 0.5, ref hint);
                    double level = sample.WaterLevel > 0.01 || field.NearOcean(hint) ? sample.WaterLevel : TerrainHeightField.NoSeaLevel;

                    cellVertex[Cell(a, b, k)] = vertices.Count;
                    vertices.Add((world - centerWorld).ToVector3());
                    normals.Add(normal.ToVector3());
                    custom0.AddRange(new[] { 0f, 0f, 0f, (float)height });
                    custom1.AddRange(new[] { (float)normal.X, (float)normal.Y, (float)normal.Z, (float)height });
                    water.Add(new Vector2((float)level, inside ? 0f : (float)sample.River));
                    debris.Add(inside ? Vector2.Zero : new Vector2(sample.Debris, sample.DebrisDay));
                    float open = inside ? (float)Math.Exp(-Math.Max(0, depth) / 6.0) : 1f;
                    colors.Add(new Color(inside ? 0f : sample.Wetland, inside ? (kind == CaveKind.LavaTube ? 1f : 0.5f) : 0f, 1f, open));
                }

        // Faces : chaque arête de la grille traversée par la surface relie les sommets des 4 cubes
        // qui la partagent ; un bloc ne garde que les arêtes qui partent de ses propres points
        var indices = new List<int>();
        for (int k = 1; k < nz - 1; k++)
            for (int b = 1; b <= Cells; b++)
                for (int a = 1; a <= Cells; a++)
                {
                    bool solid = density[Index(a, b, k)] > 0;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        int a2 = a + (axis == 0 ? 1 : 0), b2 = b + (axis == 1 ? 1 : 0), k2 = k + (axis == 2 ? 1 : 0);
                        if (k2 >= nz || (density[Index(a2, b2, k2)] > 0) == solid)
                            continue;
                        int v0, v1, v2, v3;
                        if (axis == 0)
                        {
                            v0 = cellVertex[Cell(a, b - 1, k - 1)]; v1 = cellVertex[Cell(a, b, k - 1)];
                            v2 = cellVertex[Cell(a, b, k)]; v3 = cellVertex[Cell(a, b - 1, k)];
                        }
                        else if (axis == 1)
                        {
                            v0 = cellVertex[Cell(a - 1, b, k - 1)]; v1 = cellVertex[Cell(a - 1, b, k)];
                            v2 = cellVertex[Cell(a, b, k)]; v3 = cellVertex[Cell(a, b, k - 1)];
                        }
                        else
                        {
                            v0 = cellVertex[Cell(a - 1, b - 1, k)]; v1 = cellVertex[Cell(a, b - 1, k)];
                            v2 = cellVertex[Cell(a, b, k)]; v3 = cellVertex[Cell(a - 1, b, k)];
                        }
                        if (v0 < 0 || v1 < 0 || v2 < 0 || v3 < 0)
                            continue;
                        // Sens des triangles : faces avant tournées vers l'air (Godot : sens horaire vu
                        // de devant, soit un produit vectoriel opposé à la normale), pour le « cull_back »
                        // du shader du terrain
                        Vector3 n = normals[v0] + normals[v1] + normals[v2] + normals[v3];
                        Vector3 cross = (vertices[v1] - vertices[v0]).Cross(vertices[v2] - vertices[v0]);
                        if (cross.Dot(n) < 0)
                            indices.AddRange(new[] { v0, v1, v2, v0, v2, v3 });
                        else
                            indices.AddRange(new[] { v0, v2, v1, v0, v3, v2 });
                    }
                }
        mesh.Vertices = vertices.ToArray();
        mesh.Normals = normals.ToArray();
        mesh.Custom0 = custom0.ToArray();
        mesh.Custom1 = custom1.ToArray();
        mesh.Water = water.ToArray();
        mesh.Debris = debris.ToArray();
        mesh.Colors = colors.ToArray();
        mesh.Indices = indices.ToArray();
        return mesh;
    }

    private static double Bilinear(double[] grid, int p, double u, double v)
    {
        u = Math.Clamp(u, 0, p - 1.001);
        v = Math.Clamp(v, 0, p - 1.001);
        int a = (int)u, b = (int)v;
        double fu = u - a, fv = v - b;
        double g00 = grid[b * p + a], g10 = grid[b * p + a + 1], g01 = grid[(b + 1) * p + a], g11 = grid[(b + 1) * p + a + 1];
        return (g00 * (1 - fu) + g10 * fu) * (1 - fv) + (g01 * (1 - fu) + g11 * fu) * fv;
    }
}
