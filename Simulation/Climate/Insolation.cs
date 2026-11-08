using System;
using Godot;

namespace Planet.Simulation.Climate;

/// <summary>
/// Ensoleillement au sommet de l'atmosphère (W/m²), version processeur. Sert de référence pour
/// vérifier le calcul de la carte graphique, et de repli si elle n'est pas disponible.
/// Les formules sont identiques à celles du compute shader (voir InsolationKernel).
/// </summary>
public static class Insolation
{
    /// <summary>
    /// Ensoleillement instantané : flux × cosinus de l'angle entre le Soleil et la verticale
    /// (le soleil rasant étale la même lumière sur une plus grande surface), nul la nuit.
    /// </summary>
    public static float Instant(Vector3 position, in SunState sun) =>
        sun.Flux * MathF.Max(position.Dot(sun.Direction), 0f);

    /// <summary>
    /// Ensoleillement moyen sur 24 h à une latitude donnée (formule classique de Berger) :
    /// Q = (S / π) (h₀ sin φ sin δ + cos φ cos δ sin h₀), où h₀ est l'angle horaire du coucher
    /// du soleil. h₀ = 0 : nuit polaire ; h₀ = π : soleil de minuit.
    /// </summary>
    public static float DailyMean(Vector3 position, in SunState sun)
    {
        float sinLat = Mathf.Clamp(position.Y, -1f, 1f);
        float cosLat = MathF.Sqrt(MathF.Max(0f, 1f - sinLat * sinLat));
        float sinDecl = MathF.Sin(sun.Declination);
        float cosDecl = MathF.Cos(sun.Declination);

        float x = cosLat < 1e-6f || cosDecl < 1e-6f
            ? -MathF.Sign(sinLat * sinDecl) * 2f
            : -(sinLat * sinDecl) / (cosLat * cosDecl);
        float sunsetHourAngle = MathF.Acos(Mathf.Clamp(x, -1f, 1f));

        return sun.Flux / MathF.PI
               * (sunsetHourAngle * sinLat * sinDecl + cosLat * cosDecl * MathF.Sin(sunsetHourAngle));
    }

    public static void Compute(Vector3[] positions, in SunState sun, float[] instant, float[] dailyMean)
    {
        var state = sun;
        System.Threading.Tasks.Parallel.For(0, positions.Length, cell =>
        {
            instant[cell] = Instant(positions[cell], state);
            dailyMean[cell] = DailyMean(positions[cell], state);
        });
    }
}
