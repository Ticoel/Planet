using Godot;
using Planet.Simulation;

namespace Planet.Rendering;

/// <summary>
/// Construit le maillage affichable de la planète : chaque cellule est dessinée comme un
/// polygone (hexagone ou pentagone) d'une couleur unie. Les coins du polygone sont les
/// centres des triangles géodésiques qui entourent la cellule.
/// </summary>
public static class PlanetMeshBuilder
{
    public static ArrayMesh Build(PlanetGrid grid, Color[] cellColors, float radius = 1f)
    {
        // Comptage préalable pour allouer les tableaux en une fois
        int vertexCount = 0, indexCount = 0;
        for (int cell = 0; cell < grid.CellCount; cell++)
        {
            int corners = grid.CellTriangles[cell].Length;
            vertexCount += corners + 1;   // centre + coins
            indexCount += corners * 3;    // un triangle en éventail par coin
        }

        var vertices = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var colors = new Color[vertexCount];
        var indices = new int[indexCount];

        int v = 0, i = 0;
        for (int cell = 0; cell < grid.CellCount; cell++)
        {
            int[] ringTriangles = grid.CellTriangles[cell];
            int corners = ringTriangles.Length;
            int centerIndex = v;
            Color color = cellColors[cell];

            Vector3 center = grid.Positions[cell];
            vertices[v] = center * radius;
            normals[v] = center;
            colors[v] = color;
            v++;

            foreach (int triangle in ringTriangles)
            {
                Vector3 corner = grid.TriangleCenter(triangle);
                vertices[v] = corner * radius;
                normals[v] = corner;
                colors[v] = color;
                v++;
            }

            // Les coins tournent dans le sens anti-horaire vu de l'extérieur ;
            // Godot considère les faces horaires comme faces avant, d'où l'ordre (k+1, k)
            for (int k = 0; k < corners; k++)
            {
                indices[i++] = centerIndex;
                indices[i++] = centerIndex + 1 + (k + 1) % corners;
                indices[i++] = centerIndex + 1 + k;
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.Index] = indices;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>Matériau qui affiche simplement la couleur de chaque cellule.</summary>
    public static StandardMaterial3D CreateCellMaterial() => new()
    {
        VertexColorUseAsAlbedo = true,
        Roughness = 1f,
        // Sécurité tant que l'orientation des faces n'est pas validée à l'écran
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };
}
