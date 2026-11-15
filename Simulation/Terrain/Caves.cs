using System;
using System.Collections.Generic;
using Godot;

namespace Planet.Simulation.Terrain;

/// <summary>Sorte de grotte.</summary>
public enum CaveKind
{
    /// <summary>Grotte karstique : galeries creusées par l'eau dans les calcaires, en amont d'une résurgence.</summary>
    Karst,
    /// <summary>Tunnel de lave : une coulée s'est figée en surface pendant que la lave continuait de couler dessous.</summary>
    LavaTube,
}

/// <summary>Emplacement d'une grotte (sa construction attend que la caméra s'en approche).</summary>
public readonly record struct CaveSite(CaveKind Kind, Double3 Direction, int Seed, Double3 Downhill = default);

/// <summary>Espace libre d'une grotte à l'aplomb d'un point (rayons depuis le centre de la planète, m).</summary>
public readonly record struct CaveSpace(bool Solid, double FloorRadius, double CeilingRadius);

/// <summary>Maillage d'une grotte, dans le repère de la planète, relatif à <see cref="Cave.Origin"/>.</summary>
public sealed class CaveMesh
{
    public Vector3[] Vertices = Array.Empty<Vector3>();
    public Vector3[] Normals = Array.Empty<Vector3>();
    /// <summary>x : ouverture vers le ciel (1 à l'entrée, 0 au fond) ; y : profondeur sous la surface (m) ;
    /// z : coordonnée locale (m) pour la texture ; w : 0 si le point est caché (sol de surface, déjà
    /// dessiné par le relief).</summary>
    public Color[] Attributes = Array.Empty<Color>();
    public int[] Indices = Array.Empty<int>();
}

/// <summary>
/// Relief en volume (étape 18) : une grotte. Le relief détaillé est une surface (une hauteur par
/// point), qui ne peut pas contenir de cavité ; les grottes sont donc décrites par un <b>champ de
/// densité 3D</b> (positif : roche, négatif : vide), calculé à la demande autour de l'entrée,
/// comme le reste du relief : le volume existe partout sans jamais être stocké en entier.
/// <list type="bullet">
/// <item>Galeries : chapelet de segments (rayons de 2 à 10 m, salles), parois irrégulières (bruit 3D) ;</item>
/// <item>Sol de surface autour de l'entrée : la surface du relief détaillé, si bien que le volume
/// se raccorde au relief ; ailleurs, la roche est pleine en dehors des galeries ;</item>
/// <item>Maillage : <i>surface nets</i> (Gibson 1998), un sommet par cube traversé par la surface,
/// placé à la moyenne des points où la densité s'annule sur ses arêtes ; lisse et sans table.</item>
/// </list>
/// Repère local plat (x, y dans le plan tangent à l'entrée, z vers le haut) : sur 200 m, la
/// courbure de la planète ne fait que quelques millimètres (corrigée tout de même).
/// </summary>
public sealed class Cave
{
    public const double VoxelM = 0.75;
    private const double WallNoiseM = 0.7;

    private readonly List<(Double3 a, double ra, Double3 b, double rb)> _segments = new();
    private readonly Func<double, double, double> _groundZ;     // hauteur du sol dans le repère local

    public CaveKind Kind { get; }
    /// <summary>Entrée, au niveau du sol (m, repère de la planète).</summary>
    public Double3 Origin { get; }
    public Double3 Up { get; }
    public Double3 AxisX { get; }
    public Double3 AxisY { get; }
    /// <summary>Rayon de l'ouverture (m) : le relief détaillé n'est pas dessiné en deçà.</summary>
    public double HoleRadius { get; private set; }
    /// <summary>Longueur des galeries (m).</summary>
    public double LengthM { get; private set; }
    /// <summary>Profondeur maximale sous la surface (m).</summary>
    public double MaxDepthM { get; private set; }
    public IReadOnlyList<(Double3 a, double ra, Double3 b, double rb)> Segments => _segments;
    private Double3 _min, _max;       // boîte englobante locale

    private Cave(CaveKind kind, Double3 direction, double groundHeight, Func<Double3, double> height)
    {
        Kind = kind;
        Up = direction.Normalized();
        Origin = Up * (TerrainHeightField.RadiusMeters + groundHeight);
        (AxisX, AxisY) = LavaFlow.TangentFrame(Up);
        double r0 = Origin.Length();
        _groundZ = (x, y) =>
        {
            Double3 p = Origin + AxisX * x + AxisY * y;
            // (z local = distance au centre − rayon de l'entrée ; voir ToLocal)
            return TerrainHeightField.RadiusMeters + height(p.Normalized()) - r0;
        };
    }

    /// <summary>Hauteur du sol (m, repère local) à l'aplomb de (x, y).</summary>
    public double GroundZ(double x, double y) => _groundZ(x, y);

    public Double3 ToLocal(Double3 world)
    {
        Double3 d = world - Origin;
        double x = d.Dot(AxisX), y = d.Dot(AxisY);
        // (correction de courbure : un point à la verticale de (x, y) s'éloigne du plan tangent)
        return new Double3(x, y, d.Dot(Up) + (x * x + y * y) / (2 * Origin.Length()));
    }

    public Double3 ToWorld(Double3 local) =>
        Origin + AxisX * local.X + AxisY * local.Y + Up * (local.Z - (local.X * local.X + local.Y * local.Y) / (2 * Origin.Length()));

    // --- Construction ------------------------------------------------------------------------------

    /// <summary>
    /// Grotte karstique : entrée dans le versant, à une trentaine de mètres en amont d'une
    /// résurgence ; galeries qui s'enfoncent dans la colline (10 à 30 m sous la surface) en
    /// serpentant, avec quelques salles.
    /// </summary>
    public static Cave Karst(Double3 spring, int seed, Func<Double3, double> height)
    {
        var random = new Random(seed);
        spring = spring.Normalized();
        var (ex, ey) = LavaFlow.TangentFrame(spring);
        // Vers l'amont : le côté où le sol monte le plus
        Double3 uphill = Uphill(spring, ex, ey, height, 25);
        Double3 entrance = (spring + uphill * (35 / TerrainHeightField.RadiusMeters)).Normalized();
        var cave = new Cave(CaveKind.Karst, entrance, height(entrance), height);
        Double3 heading = cave.Flatten(uphill);
        double r = 2.6 + random.NextDouble() * 1.2;
        cave.HoleRadius = r + 2.5;
        double targetDepth = 12 + random.NextDouble() * 16;
        var p = new Double3(0, 0, -0.3 * r);
        double pitch = -0.6;
        int count = 22 + random.Next(12);
        for (int k = 0; k < count; k++)
        {
            double yaw = (random.NextDouble() - 0.5) * 0.8;
            heading = new Double3(heading.X * Math.Cos(yaw) - heading.Y * Math.Sin(yaw), heading.X * Math.Sin(yaw) + heading.Y * Math.Cos(yaw), 0).Normalized();
            // On revient vers l'entrée si l'on s'en éloigne trop (la grotte tient dans une boîte de 150 m)
            double rho = Math.Sqrt(p.X * p.X + p.Y * p.Y);
            if (rho > 60)
                heading = (heading - new Double3(p.X, p.Y, 0) / rho * 0.6).Normalized();
            double step = 5.5 + random.NextDouble() * 2;
            var next = p + new Double3(heading.X * Math.Cos(pitch), heading.Y * Math.Cos(pitch), Math.Sin(pitch)) * step;
            double nr = Math.Clamp(r + (random.NextDouble() - 0.5) * 1.4, 2.0, 5.0);
            if (k > 3 && random.NextDouble() < 0.12)
                nr = 6 + random.NextDouble() * 4;                     // salle
            // Profondeur : jamais à moins de (rayon + 3 m) sous la surface, une fois sorti de l'entrée
            double ground = cave.GroundZ(next.X, next.Y);
            double depth = ground - next.Z;
            if (k >= 2 && depth < nr + 3)
                next = new Double3(next.X, next.Y, ground - nr - 3);
            pitch = k < 2 ? -0.6 : depth < targetDepth ? -0.25 : depth > targetDepth + 8 ? 0.15 : (random.NextDouble() - 0.5) * 0.3;
            cave.AddSegment(p, k == 0 ? r : (r + nr) * 0.5, next, nr);
            p = next;
            r = nr > 5.5 ? 3.5 : nr;
        }
        cave.Finish();
        return cave;
    }

    /// <summary>
    /// Tunnel de lave : à mi-flanc d'un volcan, un tube de 4 à 7 m de rayon, aplati, qui suit la
    /// pente à quelques mètres sous la surface ; on y entre par un puits d'effondrement (lucarne).
    /// </summary>
    public static Cave LavaTube(Double3 site, Double3 downhill, int seed, Func<Double3, double> height)
    {
        var random = new Random(seed);
        site = site.Normalized();
        var cave = new Cave(CaveKind.LavaTube, site, height(site), height);
        Double3 heading = cave.Flatten(downhill);
        double r = 4 + random.NextDouble() * 3;
        double roof = 3 + random.NextDouble() * 3;                    // épaisseur du toit
        cave.HoleRadius = r * 0.8 + 1.5;
        // Le tube passe sous la lucarne : 40 m en amont, 120 m en aval
        var points = new List<Double3>();
        var p = -heading * 40;
        for (int k = 0; k <= 26; k++)
        {
            double wiggle = (random.NextDouble() - 0.5) * 0.25;
            heading = new Double3(heading.X * Math.Cos(wiggle) - heading.Y * Math.Sin(wiggle), heading.X * Math.Sin(wiggle) + heading.Y * Math.Cos(wiggle), 0).Normalized();
            double ground = cave.GroundZ(p.X, p.Y);
            points.Add(new Double3(p.X, p.Y, ground - roof - r * 0.7));
            p += heading * 6.2;
        }
        for (int k = 0; k + 1 < points.Count; k++)
            cave.AddSegment(points[k], r, points[k + 1], r);
        // Puits : de la surface jusqu'au tube le plus proche de la lucarne
        Double3 below = points[0];
        foreach (var q in points)
            if (q.X * q.X + q.Y * q.Y < below.X * below.X + below.Y * below.Y)
                below = q;
        cave.AddSegment(new Double3(0, 0, 0.5), cave.HoleRadius - 1.5, below, r * 0.8);
        cave.Finish();
        return cave;
    }

    private static Double3 Uphill(Double3 at, Double3 ex, Double3 ey, Func<Double3, double> height, double stepM)
    {
        double s = stepM / TerrainHeightField.RadiusMeters;
        double gx = height((at + ex * s).Normalized()) - height((at - ex * s).Normalized());
        double gy = height((at + ey * s).Normalized()) - height((at - ey * s).Normalized());
        Double3 g = ex * gx + ey * gy;
        return g.LengthSquared() < 1e-12 ? ex : g.Normalized();
    }

    /// <summary>Direction du plan tangent (repère de la planète) → (x, y, 0) du repère local.</summary>
    private Double3 Flatten(Double3 direction)
    {
        var d = new Double3(direction.Dot(AxisX), direction.Dot(AxisY), 0);
        return d.LengthSquared() < 1e-12 ? new Double3(1, 0, 0) : d.Normalized();
    }

    private void AddSegment(Double3 a, double ra, Double3 b, double rb)
    {
        _segments.Add((a, ra, b, rb));
        LengthM += a.DistanceTo(b);
    }

    private void Finish()
    {
        var min = new Double3(-HoleRadius - 4, -HoleRadius - 4, double.MaxValue);
        var max = new Double3(HoleRadius + 4, HoleRadius + 4, double.MinValue);
        foreach (var (a, ra, b, rb) in _segments)
            foreach (var (q, rq) in new[] { (a, ra), (b, rb) })
            {
                double m = rq * 1.3 + WallNoiseM + 2;
                min = new Double3(Math.Min(min.X, q.X - m), Math.Min(min.Y, q.Y - m), Math.Min(min.Z, q.Z - m));
                max = new Double3(Math.Max(max.X, q.X + m), Math.Max(max.Y, q.Y + m), Math.Max(max.Z, q.Z + m));
                MaxDepthM = Math.Max(MaxDepthM, GroundZ(q.X, q.Y) - q.Z);
            }
        _min = min;
        _max = new Double3(max.X, max.Y, Math.Max(max.Z, 4));
    }

    /// <summary>La boîte de la grotte contient-elle ce point local ?</summary>
    public bool Contains(Double3 local) =>
        local.X >= _min.X && local.X <= _max.X && local.Y >= _min.Y && local.Y <= _max.Y && local.Z >= _min.Z && local.Z <= _max.Z;

    // --- Champ de densité --------------------------------------------------------------------------

    /// <summary>Distance signée aux galeries (m) : négative dans le vide, positive dans la roche.</summary>
    public double TunnelDistance(Double3 q) => TunnelDistance(q, null);

    private double TunnelDistance(Double3 q, int[] candidates)
    {
        double best = double.MaxValue;
        bool flat = Kind == CaveKind.LavaTube;
        int count = candidates?.Length ?? _segments.Count;
        if (count == 0)
            return FarRock;
        for (int c = 0; c < count; c++)
        {
            var (a, ra, b, rb) = _segments[candidates?[c] ?? c];
            Double3 ab = b - a;
            double t = Math.Clamp((q - a).Dot(ab) / Math.Max(ab.LengthSquared(), 1e-9), 0, 1);
            Double3 d = q - (a + ab * t);
            // Tunnels de lave aplatis (plus larges que hauts, sol presque plat)
            if (flat && Math.Abs(ab.Z) < ab.Length() * 0.7)
                d = new Double3(d.X, d.Y, d.Z * (d.Z < 0 ? 2.2 : 1.35));
            best = Math.Min(best, d.Length() - (ra + (rb - ra) * t));
        }
        return best + WallNoiseM * WallNoise(q);
    }

    /// <summary>Densité : positive dans la roche, négative dans le vide (galeries et air au-dessus du sol).</summary>
    public double Density(Double3 q, double groundZ) => Density(q, groundZ, null);

    // Roche « loin de toute galerie » (m)
    private const double FarRock = 12;

    private double Density(Double3 q, double groundZ, int[] candidates)
    {
        double tunnel = TunnelDistance(q, candidates);
        double rho = Math.Sqrt(q.X * q.X + q.Y * q.Y);
        // Autour de l'entrée, le sol de surface ; ailleurs, la roche est pleine hors des galeries
        return rho < HoleRadius + 3 ? Math.Min(groundZ - q.Z, tunnel) : tunnel;
    }

    /// <summary>
    /// Espace libre à l'aplomb d'un point (pour la caméra) ; null si le point n'est pas dans la
    /// grotte ni au-dessus de son ouverture (le relief détaillé s'en charge).
    /// </summary>
    public CaveSpace? SpaceAt(Double3 world)
    {
        Double3 q = ToLocal(world);
        if (!Contains(q) && q.Z < 30)
            return null;
        double ground = GroundZ(q.X, q.Y);
        double rho = Math.Sqrt(q.X * q.X + q.Y * q.Y);
        bool overHole = rho < HoleRadius - 0.5;
        if (q.Z > ground + (overHole ? 6 : 0.3))
            return null;                                   // au-dessus du sol : relief détaillé
        if (Density(q, ground) > 0)
            return overHole ? null : new CaveSpace(true, 0, 0);
        double floor = q.Z, ceiling = double.PositiveInfinity;
        for (double z = q.Z; z > q.Z - 60; z -= 0.25)
        {
            if (Density(new Double3(q.X, q.Y, z), ground) > 0) break;
            floor = z;
        }
        for (double z = q.Z; z < ground + 1; z += 0.25)
        {
            if (Density(new Double3(q.X, q.Y, z), ground) > 0) { ceiling = z; break; }
        }
        return new CaveSpace(false, Origin.Length() + floor, Origin.Length() + ceiling);
    }

    // Bruit de valeur 3D (parois irrégulières), de −1 à 1, deux octaves (4 m et 1,5 m)
    private static double WallNoise(Double3 q) => 0.7 * ValueNoise(q / 4.0) + 0.3 * ValueNoise(q / 1.5 + new Double3(17.1, 3.3, 9.7));

    private static double ValueNoise(Double3 p)
    {
        double fx = Math.Floor(p.X), fy = Math.Floor(p.Y), fz = Math.Floor(p.Z);
        int ix = (int)fx, iy = (int)fy, iz = (int)fz;
        double tx = Smooth(p.X - fx), ty = Smooth(p.Y - fy), tz = Smooth(p.Z - fz);
        double Lerp(double a, double b, double t) => a + (b - a) * t;
        double x00 = Lerp(Hash(ix, iy, iz), Hash(ix + 1, iy, iz), tx), x10 = Lerp(Hash(ix, iy + 1, iz), Hash(ix + 1, iy + 1, iz), tx);
        double x01 = Lerp(Hash(ix, iy, iz + 1), Hash(ix + 1, iy, iz + 1), tx), x11 = Lerp(Hash(ix, iy + 1, iz + 1), Hash(ix + 1, iy + 1, iz + 1), tx);
        return Lerp(Lerp(x00, x10, ty), Lerp(x01, x11, ty), tz);
    }

    private static double Smooth(double t) => t * t * (3 - 2 * t);

    private static double Hash(int x, int y, int z)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + z * 1274126177);
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return h / (double)uint.MaxValue * 2 - 1;
        }
    }

    // --- Maillage (surface nets) -------------------------------------------------------------------

    /// <summary>Maillage des parois (et du sol autour de l'entrée), calculé en tâche de fond.</summary>
    public CaveMesh BuildMesh()
    {
        double s = VoxelM;
        int nx = (int)Math.Ceiling((_max.X - _min.X) / s) + 1, ny = (int)Math.Ceiling((_max.Y - _min.Y) / s) + 1;
        int nz = (int)Math.Ceiling((_max.Z - _min.Z) / s) + 1;
        var ground = new double[nx * ny];
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
                ground[j * nx + i] = GroundZ(_min.X + i * s, _min.Y + j * s);
        int Index(int i, int j, int k) => (k * ny + j) * nx + i;
        var density = new float[nx * ny * nz];
        // Blocs de 8³ cubes : seules les galeries proches d'un bloc y sont examinées
        const int B = 8;
        int bx = (nx + B - 1) / B, by = (ny + B - 1) / B, bz = (nz + B - 1) / B;
        var blockSegments = new int[bx * by * bz][];
        double half = B * s * 0.5, diagonal = half * Math.Sqrt(3);
        for (int c = 0; c < blockSegments.Length; c++)
        {
            int ci = c % bx, cj = c / bx % by, ck = c / (bx * by);
            var center = new Double3(_min.X + ci * B * s + half, _min.Y + cj * B * s + half, _min.Z + ck * B * s + half);
            var list = new List<int>();
            for (int g = 0; g < _segments.Count; g++)
            {
                var (a, ra, b, rb) = _segments[g];
                Double3 ab = b - a;
                double t = Math.Clamp((center - a).Dot(ab) / Math.Max(ab.LengthSquared(), 1e-9), 0, 1);
                if (center.DistanceTo(a + ab * t) < Math.Max(ra, rb) * 2.2 + WallNoiseM + diagonal + FarRock)
                    list.Add(g);
            }
            blockSegments[c] = list.ToArray();
        }
        System.Threading.Tasks.Parallel.For(0, nz, k =>
        {
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    var q = new Double3(_min.X + i * s, _min.Y + j * s, _min.Z + k * s);
                    var candidates = blockSegments[(k / B * by + j / B) * bx + i / B];
                    density[Index(i, j, k)] = (float)Density(q, ground[j * nx + i], candidates);
                }
        });

        var mesh = new CaveMesh();
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var attributes = new List<Color>();
        var indices = new List<int>();
        var cellVertex = new int[(nx - 1) * (ny - 1) * (nz - 1)];
        int Cell(int i, int j, int k) => (k * (ny - 1) + j) * (nx - 1) + i;
        Array.Fill(cellVertex, -1);
        double r0 = Origin.Length();
        Span<float> corner = stackalloc float[8];
        for (int k = 0; k < nz - 1; k++)
            for (int j = 0; j < ny - 1; j++)
                for (int i = 0; i < nx - 1; i++)
                {
                    int mask = 0;
                    for (int c = 0; c < 8; c++)
                    {
                        corner[c] = density[Index(i + (c & 1), j + ((c >> 1) & 1), k + ((c >> 2) & 1))];
                        if (corner[c] > 0) mask |= 1 << c;
                    }
                    if (mask == 0 || mask == 255)
                        continue;
                    // Sommet : moyenne des points où la densité s'annule sur les 12 arêtes
                    double sx = 0, sy = 0, sz = 0;
                    int crossings = 0;
                    for (int a = 0; a < 8; a++)
                        for (int axis = 0; axis < 3; axis++)
                        {
                            int b = a | (1 << axis);
                            if (b == a || ((mask >> a) & 1) == ((mask >> b) & 1))
                                continue;
                            double t = corner[a] / (corner[a] - corner[b]);
                            sx += (a & 1) + (axis == 0 ? t : 0);
                            sy += ((a >> 1) & 1) + (axis == 1 ? t : 0);
                            sz += ((a >> 2) & 1) + (axis == 2 ? t : 0);
                            crossings++;
                        }
                    var local = new Double3(_min.X + (i + sx / crossings) * s, _min.Y + (j + sy / crossings) * s, _min.Z + (k + sz / crossings) * s);
                    // Normale : vers le vide (la densité décroît), gradient sur le cube
                    double gx = corner[1] + corner[3] + corner[5] + corner[7] - corner[0] - corner[2] - corner[4] - corner[6];
                    double gy = corner[2] + corner[3] + corner[6] + corner[7] - corner[0] - corner[1] - corner[4] - corner[5];
                    double gz = corner[4] + corner[5] + corner[6] + corner[7] - corner[0] - corner[1] - corner[2] - corner[3];
                    Double3 n = -(AxisX * gx + AxisY * gy + Up * gz);
                    n = n.LengthSquared() < 1e-12 ? Up : n.Normalized();
                    double groundHere = GroundZ(local.X, local.Y);
                    double depth = groundHere - local.Z;
                    double rho = Math.Sqrt(local.X * local.X + local.Y * local.Y);
                    // Sol de surface hors de l'ouverture : déjà dessiné par le relief détaillé
                    bool hidden = depth < 0.4 && rho > HoleRadius;
                    double open = Math.Exp(-Math.Max(0, local.Length() - HoleRadius) / 9.0);
                    cellVertex[Cell(i, j, k)] = vertices.Count;
                    vertices.Add((ToWorld(local) - Origin).ToVector3());
                    normals.Add(n.ToVector3());
                    attributes.Add(new Color((float)open, (float)Math.Max(0, depth), (float)(local.Z + 0.37 * local.X), hidden ? 0f : 1f));
                }

        // Faces : chaque arête de la grille traversée par la surface relie les sommets des 4 cubes qui la partagent
        for (int k = 1; k < nz - 1; k++)
            for (int j = 1; j < ny - 1; j++)
                for (int i = 1; i < nx - 1; i++)
                {
                    bool solid = density[Index(i, j, k)] > 0;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        int i2 = i + (axis == 0 ? 1 : 0), j2 = j + (axis == 1 ? 1 : 0), k2 = k + (axis == 2 ? 1 : 0);
                        if (i2 >= nx || j2 >= ny || k2 >= nz || (density[Index(i2, j2, k2)] > 0) == solid)
                            continue;
                        int v0, v1, v2, v3;
                        if (axis == 0)
                        {
                            v0 = cellVertex[Cell(i, j - 1, k - 1)]; v1 = cellVertex[Cell(i, j, k - 1)];
                            v2 = cellVertex[Cell(i, j, k)]; v3 = cellVertex[Cell(i, j - 1, k)];
                        }
                        else if (axis == 1)
                        {
                            v0 = cellVertex[Cell(i - 1, j, k - 1)]; v1 = cellVertex[Cell(i - 1, j, k)];
                            v2 = cellVertex[Cell(i, j, k)]; v3 = cellVertex[Cell(i, j, k - 1)];
                        }
                        else
                        {
                            v0 = cellVertex[Cell(i - 1, j - 1, k)]; v1 = cellVertex[Cell(i, j - 1, k)];
                            v2 = cellVertex[Cell(i, j, k)]; v3 = cellVertex[Cell(i - 1, j, k)];
                        }
                        if (v0 < 0 || v1 < 0 || v2 < 0 || v3 < 0)
                            continue;
                        if (attributes[v0].A + attributes[v1].A + attributes[v2].A + attributes[v3].A == 0)
                            continue;                                   // entièrement caché
                        if (solid)
                            indices.AddRange(new[] { v0, v1, v2, v0, v2, v3 });
                        else
                            indices.AddRange(new[] { v0, v2, v1, v0, v3, v2 });
                    }
                }
        mesh.Vertices = vertices.ToArray();
        mesh.Normals = normals.ToArray();
        mesh.Attributes = attributes.ToArray();
        mesh.Indices = indices.ToArray();
        return mesh;
    }
}
