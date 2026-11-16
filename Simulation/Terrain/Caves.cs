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

/// <summary>
/// Une grotte (étape 18) : chapelet de galeries (rayons de 2 à 10 m, salles), parois irrégulières
/// (bruit 3D), creusé dans le relief en volume autour de la caméra (<see cref="VolumeChunkBuilder"/>),
/// qui en fait des parois continues avec le sol de surface : l'ouverture est simplement là où une
/// galerie recoupe la surface. Sert aussi à la caméra (sol et parois des galeries).
/// Repère local plat (x, y dans le plan tangent à l'entrée, z vers le haut) : sur 200 m, la
/// courbure de la planète ne fait que quelques millimètres (corrigée tout de même).
/// </summary>
public sealed class Cave
{
    // Irrégularité des parois (m) : galeries et ouvertures jamais parfaitement rondes
    private const double WallNoiseM = 1.3;

    private readonly List<(Double3 a, double ra, Double3 b, double rb)> _segments = new();
    private readonly Func<double, double, double> _groundZ;     // hauteur du sol dans le repère local

    public CaveKind Kind { get; }
    /// <summary>Entrée, au niveau du sol (m, repère de la planète).</summary>
    public Double3 Origin { get; }
    public Double3 Up { get; }
    public Double3 AxisX { get; }
    public Double3 AxisY { get; }
    /// <summary>Rayon de l'ouverture (m), à peu près.</summary>
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
        double roof = 5 + random.NextDouble() * 3;                    // épaisseur du toit
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

    /// <summary>Rayon de la sphère qui contient la grotte, centrée sur l'entrée (m).</summary>
    public double BoundingRadius =>
        new Double3(Math.Max(-_min.X, _max.X), Math.Max(-_min.Y, _max.Y), Math.Max(-_min.Z, _max.Z)).Length() + 10;

    /// <summary>La boîte de la grotte contient-elle ce point local ?</summary>
    public bool Contains(Double3 local) =>
        local.X >= _min.X && local.X <= _max.X && local.Y >= _min.Y && local.Y <= _max.Y && local.Z >= _min.Z && local.Z <= _max.Z;

    // --- Champ de densité --------------------------------------------------------------------------

    /// <summary>Distance signée aux galeries (m, parois lisses) : négative dans le vide, positive dans la roche.</summary>
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
        return best;
    }

    /// <summary>Densité : positive dans la roche, négative dans le vide (galeries et air au-dessus du sol).</summary>
    public double Density(Double3 q, double groundZ) => Density(q, groundZ, null);

    // Roche « loin de toute galerie » (m)
    private const double FarRock = 12;

    private double Density(Double3 q, double groundZ, int[] candidates) => Density(q, groundZ, candidates, out _);

    /// <param name="ground">vrai si la surface la plus proche est le sol de surface (et non une paroi)</param>
    private double Density(Double3 q, double groundZ, int[] candidates, out bool ground)
    {
        double depth = groundZ - q.Z;
        double tunnel = TunnelDistance(q, candidates);
        if (tunnel < FarRock)
            tunnel += WallNoiseM * WallNoise(q);
        // Roche sous le sol, hors des galeries ; air au-dessus du sol
        ground = depth < tunnel;
        return ground ? depth : tunnel;
    }

    /// <summary>
    /// Distance signée aux parois (m, repère local de la grotte) : négative dans le vide, positive
    /// dans la roche, parois irrégulières. <paramref name="candidates"/> : galeries proches
    /// (<see cref="SegmentsNear"/>), ou null pour toutes.
    /// </summary>
    public double WallDistance(Double3 q, int[] candidates)
    {
        double tunnel = TunnelDistance(q, candidates);
        return tunnel < FarRock ? tunnel + WallNoiseM * WallNoise(q) : tunnel;
    }

    /// <summary>Galeries assez proches d'un point (repère local) pour compter à moins de <paramref name="radiusM"/> mètres.</summary>
    public int[] SegmentsNear(Double3 q, double radiusM)
    {
        var list = new List<int>();
        for (int g = 0; g < _segments.Count; g++)
        {
            var (a, ra, b, rb) = _segments[g];
            Double3 ab = b - a;
            double t = Math.Clamp((q - a).Dot(ab) / Math.Max(ab.LengthSquared(), 1e-9), 0, 1);
            if (q.DistanceTo(a + ab * t) < Math.Max(ra, rb) * 2.2 + WallNoiseM + radiusM + FarRock)
                list.Add(g);
        }
        return list.ToArray();
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

    // Bruit de valeur 3D (parois irrégulières), de −1 à 1, trois octaves (7 m, 3 m et 1,2 m) :
    // galeries qui s'évasent et se resserrent, ouvertures aux contours découpés
    private static double WallNoise(Double3 q) => 0.5 * ValueNoise(q / 7.0 + new Double3(5.3, 1.7, 8.9))
                                                  + 0.35 * ValueNoise(q / 3.0) + 0.15 * ValueNoise(q / 1.2 + new Double3(17.1, 3.3, 9.7));

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
}
