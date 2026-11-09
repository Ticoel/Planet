using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Godot;
using Planet.Simulation.Gpu;

namespace Planet.Simulation.Climate;

/// <summary>
/// Simulation du climat sur un relief figé, avec sa propre horloge (en jours).
/// <list type="bullet">
/// <item>Étape 3 : ensoleillement (orbite, saisons, jour et nuit) ;</item>
/// <item>Étape 4 : températures (bilan d'énergie) ;</item>
/// <item>Étape 5 : pression et vents ;</item>
/// <item>Étape 6 : cycle de l'eau (évaporation, transport, pluie et neige) ;</item>
/// <item>Étape 7 : sols, neige, glaciers, rivières et lacs ;</item>
/// <item>Étape 8 : biomes, qui agissent en retour sur le climat.</item>
/// </list>
/// La physique rapide (ensoleillement, atmosphère, eau) tourne sur la carte graphique quand elle
/// est disponible, vérifiée au démarrage contre la version processeur ; les calculs annuels
/// (biomes, rivières) tournent sur le processeur, les rivières en tâche de fond.
/// </summary>
public sealed class ClimateSimulation : IDisposable
{
    // Écarts maximaux tolérés entre carte graphique et processeur lors des vérifications
    private const float InsolationToleranceWm2 = 0.5f;
    private const float TemperatureToleranceK = 0.05f;
    private const float WaterToleranceMm = 0.5f;

    // Au-delà de ce pas par image (jours), ensoleillement moyen sur 24 h plutôt qu'instantané
    private const double InstantInsolationMaxStepDays = 0.05;

    // Temps de calcul visé par image (ms) : le nombre de sous-pas s'adapte
    private const double ComputeBudgetMs = 18.0;
    private const int MinSubstepCap = 8;
    private const int MaxSubstepCap = 4000;

    // Les vents sont recalculés quand au moins ce temps a été simulé (jours)
    private const double WindUpdateIntervalDays = 0.1;
    // Les courants océaniques changent lentement : recalculés tous les 5 jours simulés
    private const double OceanUpdateIntervalDays = 5.0;
    private double _sinceOceanUpdateDays = double.MaxValue;

    public PlanetGrid Grid { get; }
    public OrbitalParameters Orbit { get; }

    /// <summary>Temps climatique en jours depuis le 1er janvier de l'an 1, à minuit.</summary>
    public double TimeDays { get; private set; }

    public SunState Sun { get; private set; }
    public float[] InstantInsolation { get; }
    public float[] DailyInsolation { get; }

    /// <summary>Température de surface (°C), altitude comprise.</summary>
    public float[] SurfaceTemperature { get; }
    /// <summary>Humidité relative de l'air (0 à 1, au-delà : sursaturé).</summary>
    public float[] RelativeHumidity { get; }
    /// <summary>Neige au sol (mm d'équivalent eau).</summary>
    public float[] Snow { get; }
    /// <summary>Eau du sol (mm).</summary>
    public float[] SoilWater { get; }

    public bool HasTemperature { get; private set; }
    public float GlobalMeanTemperature { get; private set; }
    /// <summary>Banquise qui dérive avec les courants et le vent (étape 11e).</summary>
    public SeaIce SeaIce { get; }
    /// <summary>Saisons de la végétation : feuillus, herbes (étape 12b).</summary>
    public VegetationSeasons Seasons { get; private set; }
    /// <summary>Niveau moyen de la mer (m) selon la glace des terres (étape 11d).</summary>
    public float SeaLevelMeters { get; private set; }
    /// <summary>Précipitations moyennes mondiales sur le dernier intervalle (mm/jour).</summary>
    public float GlobalPrecipitation { get; private set; }
    /// <summary>Part des terres enneigées (plus de 1 cm d'équivalent eau).</summary>
    public float SnowCoveredLandFraction { get; private set; }

    public bool UsesGpu => _insolationKernel != null;
    public string ComputeBackend { get; private set; } = "processeur";
    public int LastSubsteps { get; private set; }

    public WindModel Winds { get; }
    /// <summary>Courants océaniques (étape 11a), recalculés en tâche de fond après chaque mise à jour des vents.</summary>
    public OceanCurrents Ocean { get; }
    public ClimateStatistics Statistics { get; }
    public AtmosphereModel Model => _model;

    /// <summary>Biome de chaque cellule.</summary>
    public Biome[] Biomes { get; }
    /// <summary>Incrémenté à chaque nouvelle classification des biomes.</summary>
    public int BiomeVersion { get; private set; }

    /// <summary>Réseau hydrographique (null avant le premier calcul).</summary>
    public HydrologyResult Hydrology { get; private set; }
    /// <summary>Incrémenté à chaque nouveau réseau hydrographique.</summary>
    public int HydrologyVersion { get; private set; }

    private readonly AtmosphereModel _model;
    private float[] _state;
    private float[] _scratch;
    private readonly float[] _accumulation;
    private float[] _elevation;
    private readonly float[] _cellAreas;
    private readonly float[] _soilFraction;
    private int _substepCap = 400;
    private double _sinceWindUpdateDays;

    private readonly float[] _biomeAlbedo;
    private readonly float[] _biomeSoil;
    private readonly float[] _biomeEvapotranspiration;
    private readonly float[] _biomeSnowAlbedo;
    private Task<HydrologyResult> _hydrologyTask;
    private Task _oceanTask;

    private GpuComputeContext _gpu;
    private InsolationKernel _insolationKernel;
    private AtmosphereKernel _atmosphereKernel;

    public ClimateSimulation(PlanetGrid grid, OrbitalParameters orbit, bool useGpu, double startDay)
    {
        Grid = grid;
        Orbit = orbit;
        TimeDays = startDay;
        int count = grid.CellCount;
        InstantInsolation = new float[count];
        DailyInsolation = new float[count];
        SurfaceTemperature = new float[count];
        RelativeHumidity = new float[count];
        Snow = new float[count];
        SoilWater = new float[count];
        Biomes = new Biome[count];
        _state = new float[count * 4];
        _scratch = new float[count * 4];
        _accumulation = new float[count * 4];
        _biomeAlbedo = new float[count];
        _biomeSoil = new float[count];
        _biomeEvapotranspiration = new float[count];
        _biomeSnowAlbedo = new float[count];

        _model = new AtmosphereModel(grid);
        Winds = new WindModel(grid);
        Ocean = new OceanCurrents(grid);
        SeaIce = new SeaIce(grid);
        Seasons = new VegetationSeasons(count);
        _cellAreas = grid.ComputeCellAreas();
        _soilFraction = new float[count];
        Statistics = new ClimateStatistics(count, orbit.YearLengthDays);

        if (useGpu)
            SetUpGpu();
        UpdateInsolation();
    }

    private void SetUpGpu()
    {
        _gpu = GpuComputeContext.TryCreate();
        if (_gpu == null)
            return;
        try
        {
            _insolationKernel = new InsolationKernel(_gpu, Grid.Positions);
            if (!VerifyInsolation(out float error))
            {
                SimulationLog.Warning($"Climate: GPU insolation differs from CPU (max error {error} W/m2), falling back to CPU");
                DisposeGpu();
                return;
            }
            _atmosphereKernel = new AtmosphereKernel(_gpu, _model, _insolationKernel.InstantBuffer, _insolationKernel.DailyBuffer);
            ComputeBackend = $"carte graphique ({_gpu.DeviceName})";
            SimulationLog.Info($"Climate: GPU compute on {_gpu.DeviceName}, insolation check vs CPU OK (max error {error:0.0000} W/m2)");
        }
        catch (Exception exception)
        {
            SimulationLog.Warning($"Climate: GPU setup failed, falling back to CPU. {exception.Message}");
            DisposeGpu();
        }
    }

    // --- Relief ---------------------------------------------------------------------------------

    /// <summary>
    /// Reprend le relief (figé pendant la simulation du climat) et repart d'un état proche de
    /// l'équilibre : estimation rapide du climat moyen pour les biomes et les rivières, puis
    /// état initial de l'atmosphère. À appeler quand le relief a changé.
    /// </summary>
    public void SetSurface(float[] elevationKm)
    {
        var stopwatch = Stopwatch.StartNew();
        _elevation = (float[])elevationKm.Clone();
        _model.SetSurface(_elevation);
        // Courants : repartent de zéro sur le nouveau relief (calculés en tâche de fond)
        _oceanTask?.Wait();
        Ocean.SetSurface(_elevation);
        _sinceOceanUpdateDays = double.MaxValue;

        // Biomes initiaux d'après une estimation rapide du climat moyen
        var estimate = FastClimateEstimator.Estimate(Grid, _elevation, Orbit);
        Statistics.Reset(estimate);
        ApplyBiomes();

        _state = _model.ComputeInitialState(Orbit);
        _scratch = new float[_state.Length];
        UpdateInsolation();
        UpdateWinds();

        if (_atmosphereKernel != null)
        {
            _atmosphereKernel.UploadSurface();
            _atmosphereKernel.UploadWind(Winds.WindPacked);
            _atmosphereKernel.UploadCurrent(Ocean.CurrentPacked);
            if (VerifyAtmosphere(out float temperatureError, out float waterError))
            {
                SimulationLog.Info($"Climate: GPU atmosphere check vs CPU OK (max error {temperatureError:0.00000} K, {waterError:0.00000} mm)");
            }
            else
            {
                SimulationLog.Warning($"Climate: GPU atmosphere differs from CPU ({temperatureError} K, {waterError} mm), falling back to CPU");
                DisposeGpu();
            }
            _atmosphereKernel?.UploadState(_state);
        }

        HasTemperature = true;
        StartHydrology();
        UpdateDerivedFields(null, 0);
        SeaIce.Reset(_elevation, SurfaceTemperature);
        Seasons = new VegetationSeasons(Grid.CellCount);
        UpdateSurfaceStates(0);
        SimulationLog.Info($"Climate: initialised in {stopwatch.ElapsedMilliseconds} ms (substep {_model.MaxSubstepSeconds:0} s, " +
                           $"global mean {GlobalMeanTemperature:0.0} C)");
    }

    // --- Pas de temps ---------------------------------------------------------------------------

    /// <summary>Avance l'horloge climatique ; renvoie le temps réellement simulé (jours).</summary>
    public double Step(double dtDays)
    {
        CollectHydrology();
        CollectOcean();
        if (dtDays <= 0)
            return 0;

        if (!HasTemperature)
        {
            TimeDays += dtDays;
            UpdateInsolation();
            return dtDays;
        }

        double requestedSeconds = dtDays * 86400.0;
        int substeps = (int)Math.Ceiling(requestedSeconds / _model.MaxSubstepSeconds);
        float dtSeconds;
        if (substeps <= _substepCap)
        {
            dtSeconds = (float)(requestedSeconds / substeps);
        }
        else
        {
            substeps = _substepCap;
            dtSeconds = _model.MaxSubstepSeconds;
        }
        double simulatedDays = substeps * (double)dtSeconds / 86400.0;
        bool dailyMean = simulatedDays > InstantInsolationMaxStepDays;

        // Ensoleillement pris au milieu de l'intervalle simulé
        double midTime = TimeDays + simulatedDays / 2.0;
        TimeDays = midTime;
        UpdateInsolation();
        TimeDays = midTime + simulatedDays / 2.0;

        var stopwatch = Stopwatch.StartNew();
        if (_atmosphereKernel != null)
        {
            _atmosphereKernel.Run(substeps, dtSeconds, dailyMean);
            _atmosphereKernel.ReadState(_state);
            _atmosphereKernel.ReadAndResetAccumulation(_accumulation);
        }
        else
        {
            Array.Clear(_accumulation);
            float[] insolation = dailyMean ? DailyInsolation : InstantInsolation;
            for (int i = 0; i < substeps; i++)
            {
                _model.StepCpu(_state, _scratch, _accumulation, insolation, Winds.WindPacked, Ocean.CurrentPacked, dtSeconds);
                (_state, _scratch) = (_scratch, _state);
            }
        }
        AdaptSubstepCap(stopwatch.Elapsed.TotalMilliseconds, substeps);
        LastSubsteps = substeps;

        UpdateDerivedFields(_accumulation, simulatedDays);
        UpdateSurfaceStates(simulatedDays);
        Statistics.Add(SurfaceTemperature, _state, _accumulation, simulatedDays, midTime);
        if (Statistics.YearJustCompleted)
        {
            // Nouvelle année : biomes reclassés (et réinjectés dans le climat), rivières recalculées
            ApplyBiomes();
            _atmosphereKernel?.UploadSurface();
            StartHydrology();
        }

        _sinceWindUpdateDays += simulatedDays;
        if (_sinceWindUpdateDays >= WindUpdateIntervalDays)
            UpdateWinds();

        return simulatedDays;
    }

    private void AdaptSubstepCap(double elapsedMs, int substeps)
    {
        if (elapsedMs > ComputeBudgetMs)
            _substepCap = Math.Max(MinSubstepCap, (int)(substeps * ComputeBudgetMs / elapsedMs));
        else if (substeps >= _substepCap && elapsedMs < ComputeBudgetMs * 0.6)
            _substepCap = Math.Min(MaxSubstepCap, (int)(_substepCap * 1.25) + 1);
    }

    private void UpdateWinds()
    {
        var temperature = new float[Grid.CellCount];
        for (int cell = 0; cell < Grid.CellCount; cell++)
            temperature[cell] = _state[cell * 4];
        Winds.Update(temperature);
        _atmosphereKernel?.UploadWind(Winds.WindPacked);
        _sinceOceanUpdateDays += _sinceWindUpdateDays;
        _sinceWindUpdateDays = 0;
        if (_sinceOceanUpdateDays >= OceanUpdateIntervalDays)
            StartOcean();
    }

    /// <summary>Courants recalculés en tâche de fond à partir des nouveaux vents.</summary>
    private void StartOcean()
    {
        if (_oceanTask != null && !_oceanTask.IsCompleted)
            return;
        var wind = (Vector3[])Winds.Wind.Clone();
        _sinceOceanUpdateDays = 0;
        _oceanTask = Task.Run(() => Ocean.Compute(wind));
    }

    private void CollectOcean()
    {
        if (_oceanTask == null || !_oceanTask.IsCompleted)
            return;
        if (_oceanTask.IsFaulted)
            SimulationLog.Warning($"Ocean currents failed: {_oceanTask.Exception?.GetBaseException().Message}");
        _oceanTask = null;
        if (Ocean.Publish())
            _atmosphereKernel?.UploadCurrent(Ocean.CurrentPacked);
    }

    /// <summary>
    /// Voile d'aérosols volcaniques (étape 13a) : forçage radiatif en W/m² (positif = moins de
    /// Soleil). Appliqué comme une baisse du flux solaire, rapportée aux ~240 W/m² absorbés en
    /// moyenne : 3 W/m² (Pinatubo) retirent ~1,25 % du rayonnement.
    /// </summary>
    public float AerosolForcingWm2 { get; set; }

    private void UpdateInsolation()
    {
        Sun = Orbit.ComputeSun(TimeDays);
        if (AerosolForcingWm2 > 0f)
            Sun = Sun with { Flux = Sun.Flux * MathF.Max(0.5f, 1f - AerosolForcingWm2 / 240f) };
        if (_insolationKernel != null)
            _insolationKernel.Compute(Sun, InstantInsolation, DailyInsolation);
        else
            Insolation.Compute(Grid.Positions, Sun, InstantInsolation, DailyInsolation);
    }

    /// <summary>Grandeurs dérivées de l'état, pour l'affichage et les statistiques.</summary>
    private void UpdateDerivedFields(float[] accumulation, double simulatedDays)
    {
        double temperatureSum = 0, precipitationSum = 0;
        int land = 0, snowyLand = 0;
        for (int cell = 0; cell < Grid.CellCount; cell++)
        {
            int i = cell * 4;
            float surfaceT = _model.SurfaceTemperature(_state, cell);
            SurfaceTemperature[cell] = surfaceT;
            RelativeHumidity[cell] = _state[i + 1] / AtmosphereModel.Saturation(surfaceT);
            SoilWater[cell] = _state[i + 2];
            Snow[cell] = _state[i + 3];
            temperatureSum += surfaceT;
            if (accumulation != null)
                precipitationSum += accumulation[i];
            if (!_model.IsOcean(cell))
            {
                land++;
                if (_state[i + 3] > 10f)
                    snowyLand++;
            }
        }
        GlobalMeanTemperature = (float)(temperatureSum / Grid.CellCount);
        if (accumulation != null && simulatedDays > 0)
            GlobalPrecipitation = (float)(precipitationSum / Grid.CellCount / simulatedDays);
        SnowCoveredLandFraction = land > 0 ? (float)snowyLand / land : 0f;
    }

    /// <summary>Banquise, saisons de la végétation, niveau de la mer.</summary>
    private void UpdateSurfaceStates(double simulatedDays)
    {
        if (simulatedDays > 0)
            SeaIce.Step(simulatedDays, SurfaceTemperature, Ocean.Current, Winds.Wind);
        for (int cell = 0; cell < Grid.CellCount; cell++)
            _soilFraction[cell] = _biomeSoil[cell] > 0f ? SoilWater[cell] / _biomeSoil[cell] : 0f;
        Seasons.Step(simulatedDays, SurfaceTemperature, _soilFraction);
        SeaLevelMeters = SeaLevel.FromLandIce(Snow, _elevation, _cellAreas);
    }

    // --- Biomes et rivières ----------------------------------------------------------------------

    private void ApplyBiomes()
    {
        Climate.Biomes.ClassifyAll(_model, Statistics, Biomes, _biomeAlbedo, _biomeSoil, _biomeEvapotranspiration, _biomeSnowAlbedo);
        _model.SetLandCover(_biomeAlbedo, _biomeSoil, _biomeEvapotranspiration, _biomeSnowAlbedo);
        BiomeVersion++;
    }

    /// <summary>Lance le calcul du réseau hydrographique en tâche de fond (copies des données).</summary>
    private void StartHydrology()
    {
        if (_hydrologyTask != null && !_hydrologyTask.IsCompleted)
            return;
        var elevation = (float[])_elevation.Clone();
        var runoff = (float[])Statistics.AnnualRunoff.Clone();
        var snow = (float[])Statistics.MaxSnow.Clone();
        var grid = Grid;
        _hydrologyTask = Task.Run(() => HydrologyModel.Compute(grid, elevation, runoff, snow));
    }

    /// <summary>Récupère le réseau hydrographique s'il est prêt.</summary>
    private void CollectHydrology()
    {
        if (_hydrologyTask == null || !_hydrologyTask.IsCompleted)
            return;
        if (_hydrologyTask.IsCompletedSuccessfully)
        {
            Hydrology = _hydrologyTask.Result;
            HydrologyVersion++;
        }
        else
        {
            SimulationLog.Warning($"Climate: hydrology failed: {_hydrologyTask.Exception?.GetBaseException().Message}");
        }
        _hydrologyTask = null;
    }

    /// <summary>Attend la fin du calcul des rivières (tests, premier affichage).</summary>
    public void WaitForHydrology()
    {
        _hydrologyTask?.Wait();
        CollectHydrology();
    }

    // --- Vérifications carte graphique / processeur ---------------------------------------------

    private bool VerifyInsolation(out float maxError)
    {
        maxError = 0f;
        var gpuInstant = new float[Grid.CellCount];
        var gpuDaily = new float[Grid.CellCount];
        var cpuInstant = new float[Grid.CellCount];
        var cpuDaily = new float[Grid.CellCount];
        foreach (double day in new[] { 0.3, 79.5, 171.7, 265.1, 355.9 })
        {
            var sun = Orbit.ComputeSun(day);
            _insolationKernel.Compute(sun, gpuInstant, gpuDaily);
            Insolation.Compute(Grid.Positions, sun, cpuInstant, cpuDaily);
            for (int cell = 0; cell < Grid.CellCount; cell++)
            {
                maxError = MathF.Max(maxError, MathF.Abs(gpuInstant[cell] - cpuInstant[cell]));
                maxError = MathF.Max(maxError, MathF.Abs(gpuDaily[cell] - cpuDaily[cell]));
            }
        }
        return maxError <= InsolationToleranceWm2;
    }

    /// <summary>Fait tourner 20 sous-pas sur les deux moteurs à partir du même état et compare.</summary>
    private bool VerifyAtmosphere(out float temperatureError, out float waterError)
    {
        const int substeps = 20;
        float dt = _model.MaxSubstepSeconds;

        _atmosphereKernel.UploadState(_state);
        _atmosphereKernel.Run(substeps, dt, dailyMeanInsolation: true);
        var gpu = new float[_state.Length];
        _atmosphereKernel.ReadState(gpu);

        var cpu = (float[])_state.Clone();
        var scratch = new float[_state.Length];
        var accumulation = new float[_state.Length];
        for (int i = 0; i < substeps; i++)
        {
            _model.StepCpu(cpu, scratch, accumulation, DailyInsolation, Winds.WindPacked, Ocean.CurrentPacked, dt);
            (cpu, scratch) = (scratch, cpu);
        }

        temperatureError = 0f;
        waterError = 0f;
        for (int i = 0; i < _state.Length; i += 4)
        {
            temperatureError = MathF.Max(temperatureError, MathF.Abs(gpu[i] - cpu[i]));
            for (int k = 1; k < 4; k++)
                waterError = MathF.Max(waterError, MathF.Abs(gpu[i + k] - cpu[i + k]));
        }
        return temperatureError <= TemperatureToleranceK && waterError <= WaterToleranceMm;
    }

    private void DisposeGpu()
    {
        _atmosphereKernel?.Dispose();
        _atmosphereKernel = null;
        _insolationKernel?.Dispose();
        _insolationKernel = null;
        _gpu?.Dispose();
        _gpu = null;
        ComputeBackend = "processeur";
    }

    public void Dispose()
    {
        _hydrologyTask?.Wait();
        _oceanTask?.Wait();
        DisposeGpu();
    }
}
