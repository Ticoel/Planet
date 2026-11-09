using System;
using Godot;
using Planet.Simulation;
using Planet.Simulation.Tectonics;

namespace Planet.Rendering;

/// <summary>
/// Flèches montrant le déplacement des plaques. On n'en dessine qu'un échantillon :
/// les cellules d'indice inférieur à 10 × 4^n + 2 sont exactement les sommets de la grille
/// de niveau n (la subdivision conserve les sommets existants), donc bien réparties.
/// </summary>
public static class VelocityArrows
{
    private const int SampleLevel = 3;            // 642 flèches
    private const float LiftAboveSurface = 1.004f;
    // Longueur d'affichage : une vitesse de 10 cm/an donne une flèche de 0,08 rayon
    private const float LengthPerCmPerYear = 0.008f;

    public static ImmediateMesh Build(TectonicPlates tectonics)
    {
        var grid = tectonics.Grid;
        int sampleCount = Math.Min(grid.CellCount, PlanetGrid.ExpectedCellCount(SampleLevel));
        var mesh = new ImmediateMesh();

        mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        for (int cell = 0; cell < sampleCount; cell++)
        {
            Vector3 position = grid.Positions[cell];
            float speed = tectonics.SpeedCmPerYear(cell);
            if (speed < 0.1f)
                continue;

            Vector3 direction = tectonics.VelocityAt(cell).Normalized();
            float length = speed * LengthPerCmPerYear;
            // Au-dessus du relief s'il est affiché en 3D
            float lift = LiftAboveSurface
                         + CellColorLayer.ReliefOffset(tectonics.CellElevation[cell]);
            Vector3 start = position * lift;
            Vector3 tip = start + direction * length;

            // Pointe de flèche : deux segments repliés vers l'arrière
            Vector3 side = position.Cross(direction).Normalized();
            float headLength = length * 0.3f;
            Vector3 headLeft = tip - direction * headLength + side * headLength * 0.5f;
            Vector3 headRight = tip - direction * headLength - side * headLength * 0.5f;

            mesh.SurfaceSetColor(Colors.White);
            AddLine(mesh, start, tip);
            AddLine(mesh, tip, headLeft);
            AddLine(mesh, tip, headRight);
        }
        mesh.SurfaceEnd();
        return mesh;
    }

    public static StandardMaterial3D CreateMaterial() => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
    };

    private static void AddLine(ImmediateMesh mesh, Vector3 from, Vector3 to)
    {
        mesh.SurfaceAddVertex(from);
        mesh.SurfaceAddVertex(to);
    }
}
