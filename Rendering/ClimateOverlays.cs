using System;
using System.Collections.Generic;
using Godot;
using Planet.Simulation;
using Planet.Simulation.Climate;

namespace Planet.Rendering;

/// <summary>
/// Tracés superposés au globe pendant la simulation du climat :
/// <list type="bullet">
/// <item>les fleuves (touche F) : un segment de chaque cellule de rivière vers la cellule où
/// son eau s'écoule, d'autant plus clair que le débit est fort ;</item>
/// <item>les vents (touche V) : flèches sur un échantillon de cellules bien réparties.</item>
/// </list>
/// </summary>
public static class ClimateOverlays
{
    private const float RiverLift = 1.003f;
    private const float WindLift = 1.006f;
    private const int WindSampleLevel = 3;           // 642 flèches
    // Longueur d'affichage : un vent de 10 m/s donne une flèche de 0,06 rayon
    private const float LengthPerMetrePerSecond = 0.006f;

    private static readonly Color SmallRiverColor = new(0.15f, 0.45f, 0.95f);
    private static readonly Color LargeRiverColor = new(0.6f, 0.9f, 1f);

    public static ImmediateMesh BuildRivers(PlanetGrid grid, HydrologyResult hydrology, float[] elevationKm)
    {
        var mesh = new ImmediateMesh();
        if (hydrology == null || hydrology.RiverCellCount == 0)
            return mesh;

        // Échelle logarithmique du débit : du seuil de dessin au plus grand fleuve
        float logMin = MathF.Log(HydrologyModel.RiverThreshold);
        float logMax = MathF.Log(MathF.Max(hydrology.LargestDischarge, HydrologyModel.RiverThreshold * 2f));

        var lines = new List<(Color, Vector3, Vector3)>();
        for (int cell = 0; cell < grid.CellCount; cell++)
        {
            if (!hydrology.River[cell])
                continue;
            int down = hydrology.Downstream[cell];
            if (down < 0)
                continue;
            float t = Mathf.Clamp((MathF.Log(hydrology.Discharge[cell]) - logMin) / (logMax - logMin), 0f, 1f);
            Vector3 from = grid.Positions[cell] * Lift(elevationKm[cell], RiverLift);
            // Le dernier segment s'arrête sur la côte, à mi-chemin de la cellule d'océan
            Vector3 toPosition = elevationKm[down] > 0f
                ? grid.Positions[down]
                : (grid.Positions[cell] + grid.Positions[down]).Normalized();
            Vector3 to = toPosition * Lift(MathF.Max(elevationKm[down], 0f), RiverLift);
            lines.Add((SmallRiverColor.Lerp(LargeRiverColor, t), from, to));
        }
        return Fill(mesh, lines);
    }

    public static ImmediateMesh BuildWinds(PlanetGrid grid, WindModel winds, float[] elevationKm)
    {
        var mesh = new ImmediateMesh();
        int sampleCount = Math.Min(grid.CellCount, PlanetGrid.ExpectedCellCount(WindSampleLevel));
        var lines = new List<(Color, Vector3, Vector3)>();
        for (int cell = 0; cell < sampleCount; cell++)
        {
            Vector3 wind = winds.Wind[cell];
            float speed = wind.Length();
            if (speed < 0.3f)
                continue;
            Vector3 position = grid.Positions[cell];
            Vector3 direction = wind / speed;
            float length = speed * LengthPerMetrePerSecond;
            Vector3 start = position * Lift(elevationKm[cell], WindLift) - direction * length * 0.5f;
            Vector3 tip = start + direction * length;
            Vector3 side = position.Cross(direction).Normalized();
            float head = length * 0.3f;

            // Couleur selon la vitesse : blanc pour le calme, jaune puis orange pour le vent fort
            Color color = Colors.White.Lerp(new Color(1f, 0.55f, 0.1f), Mathf.Clamp(speed / 15f, 0f, 1f));
            lines.Add((color, start, tip));
            lines.Add((color, tip, tip - direction * head + side * head * 0.5f));
            lines.Add((color, tip, tip - direction * head - side * head * 0.5f));
        }
        return Fill(mesh, lines);
    }

    /// <summary>
    /// Flèches d'un champ de vecteurs tangents (vents, courants) sur un échantillon de cellules,
    /// dans un maillage existant (vidé puis rempli). <paramref name="lengthScale"/> : facteur de
    /// longueur par rapport aux vents ; couleur du blanc à l'orange jusqu'à <paramref name="strong"/>.
    /// </summary>
    public static void BuildVectors(ImmediateMesh mesh, PlanetGrid grid, Vector3[] field, float[] elevationKm,
                                    float lengthScale, float minimum, float strong)
    {
        mesh.ClearSurfaces();
        int sampleCount = Math.Min(grid.CellCount, PlanetGrid.ExpectedCellCount(WindSampleLevel + 1));
        var lines = new List<(Color, Vector3, Vector3)>();
        for (int cell = 0; cell < sampleCount; cell++)
        {
            Vector3 value = field[cell];
            float speed = value.Length();
            if (speed < minimum)
                continue;
            Vector3 position = grid.Positions[cell];
            Vector3 direction = value / speed;
            float length = MathF.Min(speed * lengthScale * LengthPerMetrePerSecond, 0.08f);
            Vector3 start = position * Lift(elevationKm[cell], WindLift) - direction * length * 0.5f;
            Vector3 tip = start + direction * length;
            Vector3 side = position.Cross(direction).Normalized();
            float head = length * 0.3f;
            Color color = Colors.White.Lerp(new Color(1f, 0.55f, 0.1f), Mathf.Clamp(speed / strong, 0f, 1f));
            lines.Add((color, start, tip));
            lines.Add((color, tip, tip - direction * head + side * head * 0.5f));
            lines.Add((color, tip, tip - direction * head - side * head * 0.5f));
        }
        Fill(mesh, lines);
    }

    /// <summary>Remplit le maillage de segments (un maillage sans aucun sommet reste vide :
    /// Godot refuse une surface sans sommets).</summary>
    private static ImmediateMesh Fill(ImmediateMesh mesh, List<(Color color, Vector3 from, Vector3 to)> lines)
    {
        if (lines.Count == 0)
            return mesh;
        mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        foreach (var (color, from, to) in lines)
        {
            mesh.SurfaceSetColor(color);
            mesh.SurfaceAddVertex(from);
            mesh.SurfaceAddVertex(to);
        }
        mesh.SurfaceEnd();
        return mesh;
    }

    private static float Lift(float elevationKm, float baseLift) =>
        baseLift + CellColorLayer.ReliefOffset(elevationKm);
}
