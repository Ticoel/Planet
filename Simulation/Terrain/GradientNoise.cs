using System;
using System.Runtime.CompilerServices;

namespace Planet.Simulation.Terrain;

/// <summary>
/// Bruit de gradient 3D (Perlin « amélioré », interpolation quintique) en double précision,
/// avec ses dérivées analytiques. Les nœuds du réseau sont des entiers 64 bits passés dans un
/// hachage : le bruit reste exact même à des millions de longueurs d'onde de l'origine (il en faut
/// 3 millions pour décrire la Terre au mètre près). Valeur comprise environ entre −1 et 1, de
/// moyenne nulle.
/// </summary>
public static class GradientNoise
{
    // Les 12 milieux des arêtes d'un cube (gradients de Perlin), puis 4 répétés pour arriver à 16
    private static readonly double[] Gradients =
    {
        1, 1, 0, -1, 1, 0, 1, -1, 0, -1, -1, 0,
        1, 0, 1, -1, 0, 1, 1, 0, -1, -1, 0, -1,
        0, 1, 1, 0, -1, 1, 0, 1, -1, 0, -1, -1,
        1, 1, 0, -1, 1, 0, 0, -1, 1, 0, -1, -1,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mix(ulong h)
    {
        // Finaliseur de MurmurHash3 : chaque bit d'entrée influence tous les bits de sortie
        h ^= h >> 33;
        h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 33;
        h *= 0xC4CEB9FE1A85EC53UL;
        h ^= h >> 33;
        return h;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Hash(long x, long y, long z, int seed)
    {
        // Mélange coordonnée par coordonnée. Un simple « ou exclusif » des coordonnées
        // multipliées laissait des motifs rectilignes loin de l'origine (trouvé à l'image :
        // lignes droites dans le relief, alignées sur les axes)
        ulong h = Mix((ulong)seed * 0x9E3779B97F4A7C15UL ^ (ulong)x);
        h = Mix(h ^ (ulong)y);
        h = Mix(h ^ (ulong)z);
        return (int)(h >> 60);   // 0 à 15
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Corner(long ix, long iy, long iz, int seed, double fx, double fy, double fz,
                                 out double gx, out double gy, out double gz)
    {
        int g = Hash(ix, iy, iz, seed) * 3;
        gx = Gradients[g];
        gy = Gradients[g + 1];
        gz = Gradients[g + 2];
        return gx * fx + gy * fy + gz * fz;
    }

    /// <summary>Valeur du bruit au point (x, y, z), en unités de réseau.</summary>
    public static double Sample(double x, double y, double z, int seed) => Sample(x, y, z, seed, out _, out _, out _);

    /// <summary>Valeur et gradient (dérivées par unité de réseau).</summary>
    public static double Sample(double x, double y, double z, int seed, out double dx, out double dy, out double dz)
    {
        double flx = Math.Floor(x), fly = Math.Floor(y), flz = Math.Floor(z);
        long ix = (long)flx, iy = (long)fly, iz = (long)flz;
        double fx = x - flx, fy = y - fly, fz = z - flz;

        // Interpolation quintique 6t⁵ − 15t⁴ + 10t³ et sa dérivée
        double ux = fx * fx * fx * (fx * (fx * 6 - 15) + 10), dux = 30 * fx * fx * (fx * (fx - 2) + 1);
        double uy = fy * fy * fy * (fy * (fy * 6 - 15) + 10), duy = 30 * fy * fy * (fy * (fy - 2) + 1);
        double uz = fz * fz * fz * (fz * (fz * 6 - 15) + 10), duz = 30 * fz * fz * (fz * (fz - 2) + 1);

        double a = Corner(ix, iy, iz, seed, fx, fy, fz, out double gax, out double gay, out double gaz);
        double b = Corner(ix + 1, iy, iz, seed, fx - 1, fy, fz, out double gbx, out double gby, out double gbz);
        double c = Corner(ix, iy + 1, iz, seed, fx, fy - 1, fz, out double gcx, out double gcy, out double gcz);
        double d = Corner(ix + 1, iy + 1, iz, seed, fx - 1, fy - 1, fz, out double gdx, out double gdy, out double gdz);
        double e = Corner(ix, iy, iz + 1, seed, fx, fy, fz - 1, out double gex, out double gey, out double gez);
        double f = Corner(ix + 1, iy, iz + 1, seed, fx - 1, fy, fz - 1, out double gfx, out double gfy, out double gfz);
        double g = Corner(ix, iy + 1, iz + 1, seed, fx, fy - 1, fz - 1, out double ggx, out double ggy, out double ggz);
        double h = Corner(ix + 1, iy + 1, iz + 1, seed, fx - 1, fy - 1, fz - 1, out double ghx, out double ghy, out double ghz);

        // Interpolation trilinéaire développée (forme d'Íñigo Quílez), qui donne aussi les dérivées
        double k1 = b - a, k2 = c - a, k3 = e - a, k4 = a - b - c + d;
        double k5 = a - c - e + g, k6 = a - b - e + f, k7 = -a + b + c - d + e - f - g + h;
        double value = a + k1 * ux + k2 * uy + k3 * uz + k4 * ux * uy + k5 * uy * uz + k6 * uz * ux + k7 * ux * uy * uz;

        // Dérivées : terme des gradients interpolés + terme des poids
        double gx1 = gbx - gax, gx2 = gcx - gax, gx3 = gex - gax, gx4 = gax - gbx - gcx + gdx;
        double gx5 = gax - gcx - gex + ggx, gx6 = gax - gbx - gex + gfx, gx7 = -gax + gbx + gcx - gdx + gex - gfx - ggx + ghx;
        double gy1 = gby - gay, gy2 = gcy - gay, gy3 = gey - gay, gy4 = gay - gby - gcy + gdy;
        double gy5 = gay - gcy - gey + ggy, gy6 = gay - gby - gey + gfy, gy7 = -gay + gby + gcy - gdy + gey - gfy - ggy + ghy;
        double gz1 = gbz - gaz, gz2 = gcz - gaz, gz3 = gez - gaz, gz4 = gaz - gbz - gcz + gdz;
        double gz5 = gaz - gcz - gez + ggz, gz6 = gaz - gbz - gez + gfz, gz7 = -gaz + gbz + gcz - gdz + gez - gfz - ggz + ghz;
        dx = gax + gx1 * ux + gx2 * uy + gx3 * uz + gx4 * ux * uy + gx5 * uy * uz + gx6 * uz * ux + gx7 * ux * uy * uz
             + dux * (k1 + k4 * uy + k6 * uz + k7 * uy * uz);
        dy = gay + gy1 * ux + gy2 * uy + gy3 * uz + gy4 * ux * uy + gy5 * uy * uz + gy6 * uz * ux + gy7 * ux * uy * uz
             + duy * (k2 + k5 * uz + k4 * ux + k7 * uz * ux);
        dz = gaz + gz1 * ux + gz2 * uy + gz3 * uz + gz4 * ux * uy + gz5 * uy * uz + gz6 * uz * ux + gz7 * ux * uy * uz
             + duz * (k3 + k6 * ux + k5 * uy + k7 * ux * uy);
        return value;
    }
}
