using System;
using Godot;

namespace Planet.Simulation;

/// <summary>
/// Bruit de valeur 3D déterministe, évalué sur la sphère : donne des motifs naturels et
/// continus (bassins, plateaux, anciennes chaînes) sans couture ni déformation aux pôles,
/// puisqu'on l'échantillonne directement en 3D sur la surface.
/// </summary>
public sealed class SphereNoise
{
    private readonly int _seed;

    public SphereNoise(int seed)
    {
        _seed = seed;
    }

    /// <summary>Bruit fractal (somme d'octaves de plus en plus fines), dans [-1, 1] environ.
    /// <paramref name="frequency"/> : nombre de motifs par rayon pour la première octave.</summary>
    public float Fractal(Vector3 position, float frequency, int octaves, float persistence = 0.5f)
    {
        float sum = 0f, amplitude = 1f, total = 0f;
        for (int octave = 0; octave < octaves; octave++)
        {
            sum += amplitude * Value(position * frequency, octave);
            total += amplitude;
            amplitude *= persistence;
            frequency *= 2f;
        }
        return sum / total;
    }

    /// <summary>Bruit « en crêtes », dans [0, 1] : des lignes fines et sinueuses, proches de 1
    /// sur les crêtes, idéal pour dessiner d'anciennes chaînes de montagnes.</summary>
    public float Ridged(Vector3 position, float frequency, int octaves)
    {
        float sum = 0f, amplitude = 1f, total = 0f;
        for (int octave = 0; octave < octaves; octave++)
        {
            float ridge = 1f - Mathf.Abs(Value(position * frequency, 100 + octave));
            sum += amplitude * ridge * ridge;
            total += amplitude;
            amplitude *= 0.5f;
            frequency *= 2f;
        }
        return sum / total;
    }

    /// <summary>Bruit de valeur 3D dans [-1, 1] : valeurs aléatoires aux sommets d'une grille
    /// cubique, interpolées en douceur.</summary>
    private float Value(Vector3 p, int salt)
    {
        int x0 = (int)MathF.Floor(p.X), y0 = (int)MathF.Floor(p.Y), z0 = (int)MathF.Floor(p.Z);
        float fx = Fade(p.X - x0), fy = Fade(p.Y - y0), fz = Fade(p.Z - z0);

        float c000 = Hash(x0, y0, z0, salt), c100 = Hash(x0 + 1, y0, z0, salt);
        float c010 = Hash(x0, y0 + 1, z0, salt), c110 = Hash(x0 + 1, y0 + 1, z0, salt);
        float c001 = Hash(x0, y0, z0 + 1, salt), c101 = Hash(x0 + 1, y0, z0 + 1, salt);
        float c011 = Hash(x0, y0 + 1, z0 + 1, salt), c111 = Hash(x0 + 1, y0 + 1, z0 + 1, salt);

        float x00 = Mathf.Lerp(c000, c100, fx), x10 = Mathf.Lerp(c010, c110, fx);
        float x01 = Mathf.Lerp(c001, c101, fx), x11 = Mathf.Lerp(c011, c111, fx);
        return Mathf.Lerp(Mathf.Lerp(x00, x10, fy), Mathf.Lerp(x01, x11, fy), fz);
    }

    private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

    /// <summary>Valeur pseudo-aléatoire dans [-1, 1] pour un sommet de la grille.</summary>
    private float Hash(int x, int y, int z, int salt)
    {
        unchecked
        {
            uint h = (uint)_seed * 0x9E3779B1u;
            h ^= (uint)x * 0x85EBCA6Bu;
            h ^= (uint)y * 0xC2B2AE35u;
            h ^= (uint)z * 0x27D4EB2Fu;
            h ^= (uint)salt * 0x165667B1u;
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            h *= 0x297A2D39u;
            h ^= h >> 15;
            return (h & 0xFFFFFF) / (float)0x7FFFFF - 1f;
        }
    }
}
