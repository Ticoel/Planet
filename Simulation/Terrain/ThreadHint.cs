namespace Planet.Simulation.Terrain;

/// <summary>
/// Indice de cellule de départ pour <see cref="TerrainHeightField.Sample"/>, propre à chaque
/// thread : le relief est lu depuis plusieurs threads à la fois (terrain, relief en volume,
/// grottes, mouvements de terrain), et la recherche de la cellule la plus proche part de la
/// dernière trouvée. Un indice venu d'une autre grille est sans danger (la recherche repart de 0).
/// </summary>
public static class ThreadHint
{
    [System.ThreadStatic] private static int[] t_hint;

    public static ref int Value => ref (t_hint ??= new[] { -1 })[0];
}
