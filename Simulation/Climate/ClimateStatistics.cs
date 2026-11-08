using System;

namespace Planet.Simulation.Climate;

/// <summary>
/// Statistiques climatiques de chaque cellule, à partir desquelles on déduit biomes et rivières :
/// moyennes mensuelles sur une année glissante (température, précipitations, ruissellement,
/// neige), plus des moyennes « récentes » (quelques jours) pour afficher la météo du moment.
/// Avant qu'une année complète ait été simulée, les valeurs annuelles viennent d'une estimation
/// rapide (<see cref="FastClimateEstimator"/>).
/// </summary>
public sealed class ClimateStatistics
{
    // Temps caractéristique des moyennes « récentes » (jours)
    private const double RecentTimescaleDays = 5.0;

    private readonly int _cellCount;
    private readonly float _yearLengthDays;

    // Cumuls de l'année en cours, par mois (12 cases par cellule)
    private readonly double[] _monthTemperatureDays;
    private readonly double[] _monthDays;
    private readonly float[] _yearPrecipitation;
    private readonly float[] _yearRunoff;
    private readonly float[] _yearSnowfall;
    private readonly float[] _yearMaxSnow;
    private double _yearDays;

    /// <summary>Température de surface moyenne annuelle (°C).</summary>
    public float[] MeanTemperature { get; }
    /// <summary>Moyenne du mois le plus froid / le plus chaud (°C).</summary>
    public float[] ColdestMonth { get; }
    public float[] WarmestMonth { get; }
    /// <summary>Précipitations annuelles (mm/an).</summary>
    public float[] AnnualPrecipitation { get; }
    /// <summary>Ruissellement annuel, qui alimente les rivières (mm/an).</summary>
    public float[] AnnualRunoff { get; }
    /// <summary>Chute de neige annuelle (mm d'équivalent eau par an).</summary>
    public float[] AnnualSnowfall { get; }
    /// <summary>Neige au sol maximale de l'année (mm d'équivalent eau).</summary>
    public float[] MaxSnow { get; }

    /// <summary>Précipitations récentes (mm/jour, moyenne sur quelques jours).</summary>
    public float[] RecentPrecipitation { get; }

    /// <summary>Nombre d'années complètes simulées depuis la dernière réinitialisation.</summary>
    public int CompletedYears { get; private set; }

    /// <summary>Vrai quand une nouvelle année vient d'être bouclée (à consommer par l'appelant).</summary>
    public bool YearJustCompleted { get; private set; }

    public ClimateStatistics(int cellCount, float yearLengthDays)
    {
        _cellCount = cellCount;
        _yearLengthDays = yearLengthDays;
        _monthTemperatureDays = new double[cellCount * 12];
        _monthDays = new double[12];
        _yearPrecipitation = new float[cellCount];
        _yearRunoff = new float[cellCount];
        _yearSnowfall = new float[cellCount];
        _yearMaxSnow = new float[cellCount];

        MeanTemperature = new float[cellCount];
        ColdestMonth = new float[cellCount];
        WarmestMonth = new float[cellCount];
        AnnualPrecipitation = new float[cellCount];
        AnnualRunoff = new float[cellCount];
        AnnualSnowfall = new float[cellCount];
        MaxSnow = new float[cellCount];
        RecentPrecipitation = new float[cellCount];
    }

    /// <summary>Remplace les valeurs annuelles par une estimation et repart d'une année vide.</summary>
    public void Reset(FastClimateEstimate estimate)
    {
        Array.Copy(estimate.MeanTemperature, MeanTemperature, _cellCount);
        Array.Copy(estimate.ColdestMonth, ColdestMonth, _cellCount);
        Array.Copy(estimate.WarmestMonth, WarmestMonth, _cellCount);
        Array.Copy(estimate.AnnualPrecipitation, AnnualPrecipitation, _cellCount);
        Array.Copy(estimate.AnnualRunoff, AnnualRunoff, _cellCount);
        Array.Copy(estimate.AnnualSnowfall, AnnualSnowfall, _cellCount);
        Array.Copy(estimate.Snow, MaxSnow, _cellCount);
        for (int cell = 0; cell < _cellCount; cell++)
            RecentPrecipitation[cell] = estimate.AnnualPrecipitation[cell] / 365.25f;
        ClearYear();
        CompletedYears = 0;
        YearJustCompleted = false;
    }

    /// <summary>
    /// Ajoute un intervalle simulé.
    /// </summary>
    /// <param name="surfaceTemperature">Température de surface en fin d'intervalle (°C).</param>
    /// <param name="state">État de l'atmosphère (vec4 : T, vapeur, sol, neige).</param>
    /// <param name="accumulation">Cumuls de l'intervalle (vec4 : précipitations, évaporation,
    /// ruissellement, chute de neige, en mm).</param>
    /// <param name="dtDays">Durée de l'intervalle (jours).</param>
    /// <param name="midTimeDays">Milieu de l'intervalle (jours depuis le 1er janvier de l'an 1).</param>
    public void Add(float[] surfaceTemperature, float[] state, float[] accumulation, double dtDays, double midTimeDays)
    {
        YearJustCompleted = false;
        if (dtDays <= 0)
            return;

        int month = MonthOf(midTimeDays);
        _monthDays[month] += dtDays;
        float recentBlend = (float)(1.0 - Math.Exp(-dtDays / RecentTimescaleDays));

        for (int cell = 0; cell < _cellCount; cell++)
        {
            int i = cell * 4;
            _monthTemperatureDays[cell * 12 + month] += surfaceTemperature[cell] * dtDays;
            _yearPrecipitation[cell] += accumulation[i];
            _yearRunoff[cell] += accumulation[i + 2];
            _yearSnowfall[cell] += accumulation[i + 3];
            _yearMaxSnow[cell] = MathF.Max(_yearMaxSnow[cell], state[i + 3]);

            float rate = (float)(accumulation[i] / dtDays);
            RecentPrecipitation[cell] += (rate - RecentPrecipitation[cell]) * recentBlend;
        }

        _yearDays += dtDays;
        if (_yearDays >= _yearLengthDays)
            CompleteYear();
    }

    private void CompleteYear()
    {
        float scale = (float)(_yearLengthDays / _yearDays);
        for (int cell = 0; cell < _cellCount; cell++)
        {
            double sum = 0, days = 0;
            float coldest = float.MaxValue, warmest = float.MinValue;
            for (int month = 0; month < 12; month++)
            {
                if (_monthDays[month] <= 0)
                    continue;
                double monthSum = _monthTemperatureDays[cell * 12 + month];
                float monthMean = (float)(monthSum / _monthDays[month]);
                coldest = MathF.Min(coldest, monthMean);
                warmest = MathF.Max(warmest, monthMean);
                sum += monthSum;
                days += _monthDays[month];
            }
            MeanTemperature[cell] = (float)(sum / days);
            ColdestMonth[cell] = coldest;
            WarmestMonth[cell] = warmest;
            AnnualPrecipitation[cell] = _yearPrecipitation[cell] * scale;
            AnnualRunoff[cell] = _yearRunoff[cell] * scale;
            AnnualSnowfall[cell] = _yearSnowfall[cell] * scale;
            MaxSnow[cell] = _yearMaxSnow[cell];
        }
        CompletedYears++;
        YearJustCompleted = true;
        ClearYear();
    }

    private void ClearYear()
    {
        Array.Clear(_monthTemperatureDays);
        Array.Clear(_monthDays);
        Array.Clear(_yearPrecipitation);
        Array.Clear(_yearRunoff);
        Array.Clear(_yearSnowfall);
        Array.Clear(_yearMaxSnow);
        _yearDays = 0;
    }

    private int MonthOf(double timeDays)
    {
        double dayOfYear = timeDays - Math.Floor(timeDays / _yearLengthDays) * _yearLengthDays;
        return Math.Clamp((int)(dayOfYear / _yearLengthDays * 12.0), 0, 11);
    }
}
