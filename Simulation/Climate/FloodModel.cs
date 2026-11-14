using System;

namespace Planet.Simulation.Climate;

/// <summary>
/// Crues des fleuves (étape 17).
/// <list type="bullet">
/// <item><b>Débit du moment</b> : la pluie récente de chaque cellule (mm/jour) descend le réseau
/// hydrographique et s'additionne de l'amont vers l'aval, comme la pluie moyenne de l'année ; leur
/// rapport donne le débit du moment par rapport au débit moyen, en tout point du réseau.</item>
/// <item><b>Hauteur d'eau</b> : dans un lit naturel, la profondeur croît comme le débit à la
/// puissance ~0,4 (géométrie hydraulique de Leopold et Maddock 1953) ; un fleuve deux fois plus
/// gros que d'habitude monte de ~30 % de sa profondeur, déborde de son lit (plein bord, atteint
/// en moyenne tous les 1 à 2 ans) et inonde sa plaine. En sécheresse, il baisse.</item>
/// <item><b>Onde de crue</b> : la montée suit la pluie avec un retard de quelques jours (le temps
/// que l'eau du bassin arrive) : lissage exponentiel.</item>
/// </list>
/// </summary>
public sealed class FloodModel
{
    /// <summary>Profondeur d'eau d'un lit au débit moyen (m) : celle des lits du relief détaillé.</summary>
    public const double MeanDepthM = 2.6;
    public const double MaxRiseM = 6, MinRiseM = -1.5;
    private const double WaveDays = 2.5;
    private int[] _order;
    private int _orderCount;
    private float[] _recent, _mean;

    /// <summary>Débit du moment / débit moyen de chaque cellule (1 : débit habituel).</summary>
    public float[] Ratio { get; private set; }
    /// <summary>Montée du fleuve sous la caméra (m, négative en basses eaux), lissée.</summary>
    public double RiseM { get; private set; }
    /// <summary>Rapport de débit sous la caméra.</summary>
    public double LocalRatio { get; private set; } = 1;

    /// <summary>Montée de l'eau (m) pour un rapport de débit : h ∝ Q^0,4.</summary>
    public static double StageRise(double ratio) =>
        Math.Clamp(MeanDepthM * (Math.Pow(Math.Max(ratio, 0.05), 0.4) - 1), MinRiseM, MaxRiseM);

    /// <summary>Recalcule le rapport de débit de tout le réseau.</summary>
    /// <param name="downstream">Cellule aval (−1 : mer).</param>
    /// <param name="recentMmPerDay">Pluie récente (mm/jour).</param>
    /// <param name="annualMm">Pluie annuelle (mm/an).</param>
    public void UpdateNetwork(PlanetGrid grid, int[] downstream, float[] elevationKm, float[] recentMmPerDay, float[] annualMm)
    {
        int n = grid.CellCount;
        if (_order == null || _order.Length != n)
        {
            _order = new int[n];
            _recent = new float[n];
            _mean = new float[n];
            Ratio = new float[n];
        }
        // Ordre de l'amont vers l'aval (tri topologique du réseau)
        var inflow = new int[n];
        for (int c = 0; c < n; c++)
            if (elevationKm[c] > 0 && downstream[c] >= 0 && elevationKm[downstream[c]] > 0)
                inflow[downstream[c]]++;
        int head = 0, tail = 0;
        for (int c = 0; c < n; c++)
            if (elevationKm[c] > 0 && inflow[c] == 0)
                _order[tail++] = c;
        while (head < tail)
        {
            int c = _order[head++];
            int d = downstream[c];
            if (d >= 0 && elevationKm[d] > 0 && --inflow[d] == 0)
                _order[tail++] = d;
        }
        _orderCount = tail;
        for (int c = 0; c < n; c++)
        {
            // (la moitié de la pluie ruisselle, comme pour le réseau hydrographique)
            _recent[c] = Math.Max(0, recentMmPerDay[c]);
            _mean[c] = Math.Max(0.01f, annualMm[c] / 365.25f);
        }
        for (int k = 0; k < _orderCount; k++)
        {
            int c = _order[k];
            Ratio[c] = _recent[c] / _mean[c];
            int d = downstream[c];
            if (d >= 0 && elevationKm[d] > 0)
            {
                _recent[d] += _recent[c];
                _mean[d] += _mean[c];
            }
        }
    }

    /// <summary>Fait suivre à la montée locale le débit de la cellule, avec le retard de l'onde de crue.</summary>
    public void Follow(int cell, double days)
    {
        if (Ratio == null || cell < 0 || days <= 0)
            return;
        double target = Ratio[cell];
        double k = 1 - Math.Exp(-days / WaveDays);
        LocalRatio += (target - LocalRatio) * k;
        RiseM = StageRise(LocalRatio);
    }

    public void Reset()
    {
        RiseM = 0;
        LocalRatio = 1;
    }
}
