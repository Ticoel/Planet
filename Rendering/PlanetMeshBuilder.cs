using Godot;
using Planet.Simulation;

namespace Planet.Rendering;

/// <summary>
/// Construit le maillage affichable de la planète : chaque cellule est dessinée comme un
/// polygone (hexagone ou pentagone). Les coins du polygone sont les centres des triangles
/// géodésiques qui entourent la cellule.
/// Le maillage ne contient ni couleurs ni altitudes : chaque sommet porte des numéros de
/// cellules, et le shader va chercher couleur et altitude dans des textures (voir CellDataLayer).
/// Ainsi, l'affichage change à chaque pas sans reconstruire la géométrie.
/// <list type="bullet">
/// <item>UV.x : cellule dont le sommet prend la couleur ;</item>
/// <item>UV.y, UV2.x, UV2.y : les trois cellules dont on moyenne l'altitude pour soulever le
/// sommet. Un coin est partagé par trois cellules : en moyennant les trois, deux cellules
/// voisines soulèvent leur coin commun de la même hauteur, et le relief n'a pas de fissures.</item>
/// </list>
/// </summary>
public static class PlanetMeshBuilder
{
    public static ArrayMesh Build(PlanetGrid grid, float radius = 1f)
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
        var cellIds = new Vector2[vertexCount];
        var reliefIds = new Vector2[vertexCount];
        var indices = new int[indexCount];

        int v = 0, i = 0;
        for (int cell = 0; cell < grid.CellCount; cell++)
        {
            int[] ringTriangles = grid.CellTriangles[cell];
            int corners = ringTriangles.Length;
            int centerIndex = v;
            // Les numéros de cellule tiennent exactement dans un float 32 bits (< 16 millions)

            // Le centre prend l'altitude de sa propre cellule
            Vector3 center = grid.Positions[cell];
            vertices[v] = center * radius;
            normals[v] = center;
            cellIds[v] = new Vector2(cell, cell);
            reliefIds[v] = new Vector2(cell, cell);
            v++;

            foreach (int triangle in ringTriangles)
            {
                Vector3 corner = grid.TriangleCenter(triangle);
                vertices[v] = corner * radius;
                normals[v] = corner;
                int t = triangle * 3;
                cellIds[v] = new Vector2(cell, grid.Triangles[t]);
                reliefIds[v] = new Vector2(grid.Triangles[t + 1], grid.Triangles[t + 2]);
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
        arrays[(int)Mesh.ArrayType.TexUV] = cellIds;
        arrays[(int)Mesh.ArrayType.TexUV2] = reliefIds;
        arrays[(int)Mesh.ArrayType.Index] = indices;

        // Pas d'option de compression : les UV doivent rester en float 32 bits pour coder
        // des numéros de cellule au-delà de 2048
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
