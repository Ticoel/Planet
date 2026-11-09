using System;

namespace Planet.Simulation.Climate;

/// <summary>
/// Niveau moyen de la mer selon la glace posée sur les terres (étape 11d). L'eau des neiges
/// et des glaciers qui s'accumulent sur les continents est prise à l'océan : la mer baisse
/// d'autant (volume de glace en équivalent eau / surface des océans). Sur Terre, les calottes
/// actuelles représentent ~65 m de mer ; au dernier maximum glaciaire, la mer était 120 m plus
/// bas qu'aujourd'hui.
/// <para>Référence (0 m) : une planète sans aucune glace sur les terres, celle dont le relief
/// donne le niveau de la mer. Le masque terre/mer du climat, lui, ne change pas.</para>
/// </summary>
public static class SeaLevel
{
    /// <summary>Plus forte montée affichée (m) : boîtes englobantes et côtes basses inondables.</summary>
    public const float MaxRiseMeters = 5f;

    /// <summary>Niveau de la mer (m) : neige et glace des terres en mm d'eau, aire des cellules en sr.</summary>
    public static float FromLandIce(float[] snowMm, float[] elevationKm, float[] cellAreas)
    {
        double ice = 0, ocean = 0;
        for (int c = 0; c < snowMm.Length; c++)
        {
            if (elevationKm[c] < 0f)
                ocean += cellAreas[c];
            else
                ice += snowMm[c] * 1e-3 * cellAreas[c];
        }
        // Volumes et surfaces sur la sphère unité : le rayon se simplifie
        return ocean > 0 ? (float)Math.Min(-ice / ocean, MaxRiseMeters) : 0f;
    }
}
