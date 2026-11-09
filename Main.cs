using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Planet.Rendering;
using Planet.Simulation;
using Planet.Simulation.Climate;
using Planet.Simulation.Tectonics;
using Planet.Simulation.Terrain;
using Environment = Godot.Environment;

namespace Planet;

/// <summary>Horloge qui fait avancer la simulation.</summary>
public enum ClockMode
{
    /// <summary>Millions d'années : plaques, relief.</summary>
    Geological,
    /// <summary>Heures et jours : soleil, saisons (relief figé).</summary>
    Climate,
}

/// <summary>
/// Scène principale : génère la planète, fait tourner l'horloge géologique ou climatique, et
/// l'affiche dans une vue unique, de l'orbite jusqu'au sol, à l'échelle réelle (le relief est
/// exagéré automatiquement vu de loin).
/// Souris : molette = zoom, clic gauche + glisser = déplacer, clic droit + glisser = regarder.
/// Clavier : flèches = avancer / tourner, Page préc. / suiv. = monter / descendre,
/// C = horloge géologique / climatique, 1 à 0 = couches, E = exagération du relief,
/// V = flèches (plaques ou vents), F = fleuves, G = vol / marche, X = retour en orbite,
/// P = pause, + / - = vitesse de l'horloge, R = nouvelle planète.
/// </summary>
public partial class Main : Node3D
{
    [ExportGroup("Planète")]
    [Export(PropertyHint.Range, "0,8")]
    public int Subdivisions { get; set; } = 5;

    [Export(PropertyHint.Range, "2,64")]
    public int PlateCount { get; set; } = 14;

    [Export]
    public int Seed { get; set; } = 1;

    [ExportGroup("Orbite et rotation")]
    [Export(PropertyHint.Range, "0,90,0.01")]
    public float AxialTiltDeg { get; set; } = 23.44f;

    [Export(PropertyHint.Range, "0,0.9,0.0001")]
    public float Eccentricity { get; set; } = 0.0167f;

    [Export(PropertyHint.Range, "10,2000,0.01")]
    public float YearLengthDays { get; set; } = 365.25f;

    [Export(PropertyHint.Range, "100,5000,1")]
    public float SolarConstant { get; set; } = 1361f;

    [ExportGroup("Calcul")]
    /// <summary>Calcul du climat sur la carte graphique (repli automatique sur le processeur).</summary>
    [Export]
    public bool UseGpu { get; set; } = true;

    // Vitesses de l'horloge géologique, en millions d'années par seconde réelle. Au-delà de
    // 50 Ma/s, la vitesse réelle est limitée par le calcul (voir FastSimulationBudgetMs)
    private static readonly float[] GeologicalSpeeds = { 0.5f, 1f, 2f, 5f, 10f, 20f, 50f, 100f, 200f, 500f };
    private const int DefaultGeologicalSpeed = 2;
    private const float FastGeologicalSpeed = 100f;
    // Âge de la Terre (Ma) : but de l'avance rapide (touche T)
    private const double EarthAgeMyr = 4540.0;

    // Vitesses de l'horloge climatique, en jours par seconde réelle : du temps réel à 3 mois/s
    private static readonly float[] ClimateSpeeds =
        { 1f / 86400f, 1f / 1440f, 10f / 1440f, 1f / 24f, 0.25f, 1f, 5f, 15f, 30f, 90f };
    // 1 minute par seconde : une journée dure 24 min, le soleil bouge à peine
    private const int DefaultClimateSpeed = 1;

    // Temps de calcul maximal consacré à la simulation par image, pour garder l'affichage fluide
    // (14 ms au lieu de 25 : à 50 Ma/s demandés, l'image saccadait ; la vitesse réelle baisse un peu)
    private const double SimulationBudgetMs = 14.0;
    // Aux vitesses rapides (100 Ma/s et plus, avance rapide) : bien plus de calcul par image,
    // au prix d'une image moins fluide (~15-20 img/s)
    private const double FastSimulationBudgetMs = 50.0;
    // Et le relief détaillé n'est rafraîchi que toutes les 3 s
    private const double FastTerrainRefreshSeconds = 3.0;

    // En horloge géologique, le climat moyen (qui pilote l'érosion) est réestimé tous les 25 Ma,
    // en tâche de fond
    private const double ClimateEstimateIntervalMyr = 25.0;

    // L'horloge climatique démarre à l'équinoxe de printemps (20 mars), à midi sur le méridien 0
    private const double ClimateStartDay = 79.5;

    private static readonly string[] MonthNames =
    {
        "janvier", "février", "mars", "avril", "mai", "juin",
        "juillet", "août", "septembre", "octobre", "novembre", "décembre",
    };
    private static readonly int[] MonthLengths = { 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };

    // Altitude de départ, comme l'ancien globe vu de loin (m)
    private const double OrbitAltitude = 1.3e7;
    // En horloge géologique, le relief affiché suit la tectonique : nouveau relief détaillé au
    // plus toutes les 0,3 s (calculé en tâche de fond)
    private const double TerrainRefreshSeconds = 0.8;
    // Couleurs des couches renvoyées à la carte graphique au plus 15 fois par seconde
    private const double ColorRefreshSeconds = 1.0 / 15.0;
    // Les tracés à l'échelle des cellules (fleuves, vents, flèches) ne s'affichent que de haut
    private const double OverlayMinAltitude = 150000.0;

    private PlanetGrid _grid;
    private TectonicPlates _tectonics;
    private ClimateSimulation _climate;
    private OrbitalParameters _orbit;

    // Estimation du climat pour l'érosion (horloge géologique)
    private Task<float[]> _estimateTask;
    private TectonicPlates _estimateSource;
    private double _nextEstimateMyr;

    // Relief sur lequel le climat a été initialisé : le climat ne repart de l'équilibre que si
    // le relief a changé depuis
    private TectonicPlates _climateSurfaceSource;
    private double _climateSurfaceTimeMyr = double.NaN;
    private DisplayMode _displayMode = DisplayMode.Geology;
    private ClockMode _clock = ClockMode.Geological;

    // Vue unique
    private TerrainRenderer _terrain;
    private Task<TerrainHeightField> _terrainTask;
    private TectonicPlates _terrainSource;
    private double _terrainTimeMyr = double.NaN;
    private double _terrainRefreshTimer;
    private bool _colorsDirty = true;
    private double _colorTimer;
    private double _climateDataTimer;

    // Tracés à l'échelle de la planète (placés et mis à l'échelle à chaque image)
    private Node3D _overlayRoot;
    private MeshInstance3D _arrowsMesh;
    private MeshInstance3D _riversMesh;
    private MeshInstance3D _windMesh;
    private bool _showArrows;
    private bool _showRivers = true;
    private bool _showWinds;
    private int _riversVersion = -1;
    private double _windRebuildTimer;
    private bool _arrowsDirty = true;

    private int _hoverCell = -1;
    private bool _showHelp;
    private bool _showDetails;
    // Ciel, atmosphère, Lune, nuages, météo (étape 10)
    private SkyRenderer _sky;
    private double _cloudTimer;
    private Task<byte[][]> _cloudTask;
    private bool _cloudsShown;
    private int _weatherHint = -1;
    // Qualité d'image (touche O) : rendu 3D à résolution réduite, agrandi par AMD FSR. FSR 1
    // (spatial) ne demande rien de plus ; FSR 2 (temporel) est plus net mais peut laisser des
    // traînées sur ce qui n'a pas de vecteurs de mouvement (nuages, pluie)
    private static readonly (string label, Viewport.Scaling3DModeEnum mode, float scale)[] QualityModes =
    {
        // Le premier mode est celui du démarrage
        ("FSR 2 qualité (67 %)", Viewport.Scaling3DModeEnum.Fsr2, 0.67f),
        ("FSR 2 performance (50 %)", Viewport.Scaling3DModeEnum.Fsr2, 0.5f),
        ("FSR 1 qualité (77 %)", Viewport.Scaling3DModeEnum.Fsr, 0.77f),
        ("FSR 1 équilibré (67 %)", Viewport.Scaling3DModeEnum.Fsr, 0.67f),
        ("FSR 1 performance (50 %)", Viewport.Scaling3DModeEnum.Fsr, 0.5f),
        ("pleine résolution", Viewport.Scaling3DModeEnum.Bilinear, 1f),
    };
    private int _quality;

    // Message affiché quelques secondes (lieu choisi avec F1–F7, F9–F11)
    private string _notice;
    private double _noticeTimer;
    private Label _hud;

    private bool _running = true;
    private int _geologicalSpeed = DefaultGeologicalSpeed;
    // Avance rapide jusqu'à l'âge de la Terre (touche T)
    private bool _fastForward;
    private int _climateSpeed = DefaultClimateSpeed;
    private double _pendingTime;
    private double _measureRealSeconds;
    private double _measureSimulated;
    private double _effectiveSpeed;

    public override void _Ready()
    {
        var stopwatch = Stopwatch.StartNew();
        _grid = PlanetGrid.Create(Subdivisions);
        GD.Print($"Grid: {_grid.CellCount} cells (subdivisions {Subdivisions}) in {stopwatch.ElapsedMilliseconds} ms");

        // Le climat est créé dès le départ : la vérification de la carte graphique s'affiche
        // ainsi tout de suite dans la console
        _orbit = new OrbitalParameters
        {
            AxialTiltDeg = AxialTiltDeg,
            Eccentricity = Eccentricity,
            YearLengthDays = YearLengthDays,
            SolarConstant = SolarConstant,
        };
        _climate = new ClimateSimulation(_grid, _orbit, UseGpu, ClimateStartDay);

        stopwatch.Restart();
        _terrain = new TerrainRenderer { Name = "Terrain" };
        AddChild(_terrain);
        _terrain.SetGrid(_grid);
        _terrain.Camera.MakeCurrent();
        GD.Print($"Terrain: cell index textures in {stopwatch.ElapsedMilliseconds} ms");
        SetupSky();
        ApplyQuality();
        SetupOverlays();
        SetupHud();
        GeneratePlanet();
        _terrain.Explorer.PlaceAbove(new Double3(0, 0, 1), OrbitAltitude);
    }

    public override void _ExitTree()
    {
        _estimateTask?.Wait();
        _terrainTask?.Wait();
        _climate?.Dispose();
    }

    public override void _Process(double delta)
    {
        if (_running)
        {
            if (_clock == ClockMode.Geological)
                AdvanceGeology(delta);
            else
                AdvanceClimate(delta);
        }
        if (_clock == ClockMode.Geological)
        {
            CollectClimateEstimate();
            UpdateTerrainField(delta);
        }

        // Pendant que la tectonique tourne, le terrain laisse des cœurs à la simulation
        bool tectonicsRunning = _running && _clock == ClockMode.Geological;
        _terrain.ActiveWorkerLimit = tectonicsRunning ? Math.Max(2, _terrain.WorkerCount / 3) : int.MaxValue;

        // Couleurs des cellules : inutiles en coloration géologique (calculée par le shader)
        _colorTimer -= delta;
        if (_colorsDirty && _colorTimer <= 0 && ColoringFor(_displayMode) is not (TerrainColoring.Geology or TerrainColoring.Elevation))
        {
            _terrain.UpdateCellColors(PlanetColorizer.Compute(_tectonics, _climate, _displayMode), _tectonics.CellElevation);
            _colorsDirty = false;
            _colorTimer = ColorRefreshSeconds;
        }
        // Climat du moment pour la neige du terrain (horloge climatique), 4 fois par seconde
        _climateDataTimer -= delta;
        if (_climateDataTimer <= 0)
        {
            bool climate = _clock == ClockMode.Climate && _climate.HasTemperature;
            _terrain.UpdateCellClimate(climate ? _climate.SurfaceTemperature : null, _climate.Snow, _tectonics.CellElevation);
            _climateDataTimer = 0.25;
        }
        _terrain.Coloring = ColoringFor(_displayMode);
        _terrain.Tick(delta, ReadInput());
        _noticeTimer -= delta;
        UpdateSky(delta);
        UpdateOverlays(delta);
        UpdateHover();
        UpdateHud();
    }

    // --- Horloges ----------------------------------------------------------------------------

    /// <summary>
    /// Fait avancer l'horloge géologique par pas de durée fixe (le pas maximal stable) ; le temps
    /// réel écoulé s'accumule jusqu'à couvrir un pas complet. Si le calcul ne suit pas, le retard
    /// est abandonné plutôt qu'accumulé, et la vitesse réellement obtenue est affichée.
    /// </summary>
    private void AdvanceGeology(double delta)
    {
        float step = _tectonics.MaxTimeStep();
        _pendingTime += delta * GeologicalSpeeds[_geologicalSpeed];
        double budget = IsFastGeology ? FastSimulationBudgetMs : SimulationBudgetMs;

        var stopwatch = Stopwatch.StartNew();
        double simulated = 0;
        while (_pendingTime >= step && stopwatch.Elapsed.TotalMilliseconds < budget)
        {
            if (_fastForward && _tectonics.TimeMyr >= EarthAgeMyr)
            {
                // Arrivé : retour à la vitesse normale, en pause pour regarder le résultat
                _fastForward = false;
                _geologicalSpeed = DefaultGeologicalSpeed;
                _running = false;
                _pendingTime = 0;
                break;
            }
            _tectonics.Step(step);
            _pendingTime -= step;
            simulated += step;
        }
        _pendingTime = Math.Min(_pendingTime, step);
        MeasureSpeed(delta, simulated);

        if (simulated > 0)
        {
            _colorsDirty = true;
            _arrowsDirty = true;
        }
        if (_tectonics.TimeMyr >= _nextEstimateMyr)
            StartClimateEstimate();
    }

    private bool IsFastGeology => _fastForward || GeologicalSpeeds[_geologicalSpeed] >= FastGeologicalSpeed;

    /// <summary>Touche T : avance rapide jusqu'à l'âge de la Terre (ou arrêt de l'avance rapide).</summary>
    private void ToggleFastForward()
    {
        if (_fastForward)
        {
            _fastForward = false;
            _geologicalSpeed = DefaultGeologicalSpeed;
            return;
        }
        if (_tectonics.TimeMyr >= EarthAgeMyr)
            return;
        if (_clock != ClockMode.Geological)
            SetClock(ClockMode.Geological);
        _fastForward = true;
        _geologicalSpeed = GeologicalSpeeds.Length - 1;
        _running = true;
    }

    /// <summary>
    /// Lance en tâche de fond une estimation rapide du climat moyen sur le relief actuel : les
    /// précipitations qui en sortent pilotent l'érosion (étape 7).
    /// </summary>
    private void StartClimateEstimate()
    {
        if (_estimateTask != null)
            return;
        var grid = _grid;
        var orbit = _orbit;
        var elevation = (float[])_tectonics.CellElevation.Clone();
        _estimateSource = _tectonics;
        _estimateTask = Task.Run(() => FastClimateEstimator.Estimate(grid, elevation, orbit).AnnualPrecipitation);
        _nextEstimateMyr = _tectonics.TimeMyr + ClimateEstimateIntervalMyr;
    }

    private void CollectClimateEstimate()
    {
        if (_estimateTask == null || !_estimateTask.IsCompleted)
            return;
        if (_estimateTask.IsCompletedSuccessfully && _estimateSource == _tectonics)
            _tectonics.AnnualPrecipitation = _estimateTask.Result;
        else if (!_estimateTask.IsCompletedSuccessfully)
            GD.PushWarning($"Climate estimate failed: {_estimateTask.Exception?.GetBaseException().Message}");
        _estimateTask = null;
    }

    /// <summary>
    /// Fait avancer l'horloge climatique. Le soleil se calcule pour n'importe quel instant :
    /// un seul calcul par image suffit, quelle que soit la vitesse.
    /// </summary>
    private void AdvanceClimate(double delta)
    {
        double dtDays = delta * ClimateSpeeds[_climateSpeed];
        double simulated = _climate.Step(dtDays);
        MeasureSpeed(delta, simulated);
        _colorsDirty = true;
    }

    private void MeasureSpeed(double delta, double simulated)
    {
        _measureRealSeconds += delta;
        _measureSimulated += simulated;
        if (_measureRealSeconds >= 1.0)
        {
            _effectiveSpeed = _measureSimulated / _measureRealSeconds;
            _measureRealSeconds = 0;
            _measureSimulated = 0;
        }
    }

    private void SetClock(ClockMode clock)
    {
        _clock = clock;
        _fastForward = false;
        _pendingTime = 0;
        _effectiveSpeed = 0;
        _measureRealSeconds = 0;
        _measureSimulated = 0;

        if (clock == ClockMode.Climate)
        {
            // Le relief se fige : le terrain affiché rattrape d'abord le dernier relief simulé
            if (_terrainTimeMyr != _tectonics.TimeMyr)
                RebuildTerrainNow();
            // Si le relief a changé depuis la dernière fois, le climat repart de ce nouveau relief ;
            // sinon il reprend là où il s'était arrêté
            if (_climateSurfaceSource != _tectonics || _climateSurfaceTimeMyr != _tectonics.TimeMyr)
            {
                _climate.SetSurface(_tectonics.CellElevation);
                _climateSurfaceSource = _tectonics;
                _climateSurfaceTimeMyr = _tectonics.TimeMyr;
                _climate.WaitForHydrology();
                _riversVersion = -1;
            }
            if (!IsClimateLayer(_displayMode) && _displayMode is not (DisplayMode.InstantInsolation or DisplayMode.DailyInsolation))
                _displayMode = DisplayMode.Realistic;
        }
        else
        {
            // Les couches du climat ne correspondent plus au relief qui va évoluer
            if (IsClimateLayer(_displayMode))
                _displayMode = DisplayMode.Geology;
            _nextEstimateMyr = _tectonics.TimeMyr;
        }
        _colorsDirty = true;
        _arrowsDirty = true;
        _riversVersion = -1;
    }

    /// <summary>Couches calculées par la simulation du climat (sur un relief figé).</summary>
    private static bool IsClimateLayer(DisplayMode mode) => mode is DisplayMode.Temperature
        or DisplayMode.WindSpeed or DisplayMode.Pressure or DisplayMode.RecentPrecipitation
        or DisplayMode.AnnualPrecipitation or DisplayMode.Humidity or DisplayMode.Biomes or DisplayMode.Realistic
        or DisplayMode.OceanCurrents;

    /// <summary>Coloration du terrain selon la couche choisie.</summary>
    private TerrainColoring ColoringFor(DisplayMode mode) => mode switch
    {
        DisplayMode.Geology => TerrainColoring.Geology,
        DisplayMode.Elevation => TerrainColoring.Elevation,
        DisplayMode.Realistic => _climate.HasTemperature && _clock == ClockMode.Climate
            ? TerrainColoring.Realistic
            : TerrainColoring.Geology,
        _ => TerrainColoring.Layer,
    };

    /// <summary>
    /// Direction du Soleil. Horloge climatique : le vrai Soleil (jour, nuit, saisons). Horloge
    /// géologique : une lumière qui suit la caméra, venue du nord-ouest et rasante (~22° au-dessus
    /// de l'horizon) : comme en fin d'après-midi, les pentes s'opposent nettement et le relief, à
    /// l'échelle réelle, reste lisible (avec une lumière haute, il paraissait tout plat).
    /// </summary>
    private Vector3 SunDirection()
    {
        if (_clock == ClockMode.Climate)
            return _climate.Sun.Direction;
        var (up, north, east) = _terrain.Explorer.LocalFrame();
        return (up * 0.36 + north * 0.75 - east * 0.45).Normalized().ToVector3();
    }

    // --- Planète et relief ------------------------------------------------------------------------

    /// <summary>(Re)génère les plaques pour la graine courante et remet l'horloge à zéro.</summary>
    private void GeneratePlanet()
    {
        _fastForward = false;
        var stopwatch = Stopwatch.StartNew();
        _tectonics = TectonicPlates.Generate(_grid, PlateCount, Seed);
        long platesMs = stopwatch.ElapsedMilliseconds;

        var counts = _tectonics.CellBoundary
            .GroupBy(b => b)
            .ToDictionary(g => g.Key, g => g.Count());
        GD.Print($"Plates: {PlateCount} (seed {Seed}) in {platesMs} ms, " +
                 $"boundary cells: convergent {counts.GetValueOrDefault(BoundaryType.Convergent)}, " +
                 $"divergent {counts.GetValueOrDefault(BoundaryType.Divergent)}, " +
                 $"transform {counts.GetValueOrDefault(BoundaryType.Transform)}, " +
                 $"time step {_tectonics.MaxTimeStep():0.000} Myr");

        // Premier climat moyen, pour que l'érosion tienne compte de la pluie dès le départ
        stopwatch.Restart();
        _estimateTask?.Wait();
        _estimateTask = null;
        _tectonics.AnnualPrecipitation = FastClimateEstimator.Estimate(_grid, _tectonics.CellElevation, _orbit).AnnualPrecipitation;
        _nextEstimateMyr = ClimateEstimateIntervalMyr;
        GD.Print($"Climate estimate for erosion in {stopwatch.ElapsedMilliseconds} ms");

        _pendingTime = 0;
        _effectiveSpeed = 0;
        if (_clock == ClockMode.Climate)
            SetClock(ClockMode.Geological);
        RebuildTerrainNow(progressive: false);
        _colorsDirty = true;
        _arrowsDirty = true;
    }

    /// <summary>Relief détaillé du relief simulé actuel, calculé tout de suite.</summary>
    private void RebuildTerrainNow(bool progressive = true)
    {
        _terrainTask?.Wait();
        _terrainTask = null;
        var stopwatch = Stopwatch.StartNew();
        var field = new TerrainHeightField(_grid, (float[])_tectonics.CellElevation.Clone(), Seed, TerrainRunoff());
        _terrain.SetField(field, progressive);
        _terrainSource = _tectonics;
        _terrainTimeMyr = _tectonics.TimeMyr;
        GD.Print($"Terrain: height field in {stopwatch.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Horloge géologique : le relief affiché suit la tectonique. Un nouveau relief détaillé est
    /// préparé en tâche de fond, puis les morceaux de terrain sont remplacés progressivement.
    /// </summary>
    private void UpdateTerrainField(double delta)
    {
        _terrainRefreshTimer -= delta;
        if (_terrainTask != null)
        {
            if (!_terrainTask.IsCompleted)
                return;
            if (_terrainTask.IsCompletedSuccessfully && _terrainSource == _tectonics)
                _terrain.SetField(_terrainTask.Result, progressive: true);
            _terrainTask = null;
        }
        // Pas de nouveau relief tant que le précédent n'est pas entièrement affiché
        if (_terrainTimeMyr == _tectonics.TimeMyr || _terrainRefreshTimer > 0 || _terrain.IsUpdating)
            return;
        var grid = _grid;
        var elevation = (float[])_tectonics.CellElevation.Clone();
        int seed = Seed;
        float[] runoff = TerrainRunoff();
        _terrainSource = _tectonics;
        _terrainTimeMyr = _tectonics.TimeMyr;
        _terrainRefreshTimer = IsFastGeology ? FastTerrainRefreshSeconds : TerrainRefreshSeconds;
        _terrainTask = Task.Run(() => new TerrainHeightField(grid, elevation, seed, runoff));
    }

    /// <summary>
    /// Ruissellement qui creuse les vallées du terrain : la moitié des précipitations du climat
    /// moyen estimé (celui qui pilote l'érosion). Le même dans les deux horloges, pour que les
    /// fleuves ne changent pas de place quand on passe de l'une à l'autre.
    /// </summary>
    private float[] TerrainRunoff()
    {
        float[] precipitation = _tectonics.AnnualPrecipitation;
        if (precipitation == null)
            return null;
        var runoff = new float[precipitation.Length];
        for (int cell = 0; cell < runoff.Length; cell++)
            runoff[cell] = 0.5f * precipitation[cell];
        return runoff;
    }

    // --- Scène -------------------------------------------------------------------------------

    private void SetupSky()
    {
        _sky = new SkyRenderer { Name = "Sky" };
        AddChild(_sky);
        _sky.Setup(_terrain.Camera, _terrain.CellIndex, _terrain.CellIndexResolution);
        _terrain.SetShaderParameter("cloud_cover", _sky.CloudCover);
        _terrain.SetShaderParameter("cloud_noise", _sky.CloudNoise);
    }

    private void SetupOverlays()
    {
        _overlayRoot = new Node3D { Name = "Overlays" };
        AddChild(_overlayRoot);
        MeshInstance3D Create(string name)
        {
            var mesh = new MeshInstance3D { Name = name, MaterialOverride = VelocityArrows.CreateMaterial(), Visible = false };
            _overlayRoot.AddChild(mesh);
            return mesh;
        }
        _arrowsMesh = Create("VelocityArrows");
        _riversMesh = Create("Rivers");
        _windMesh = Create("WindArrows");
    }

    private void SetupHud()
    {
        var layer = new CanvasLayer { Name = "Hud" };
        AddChild(layer);
        _hud = new Label { Position = new Vector2(16f, 12f) };
        _hud.AddThemeColorOverride("font_outline_color", Colors.Black);
        _hud.AddThemeConstantOverride("outline_size", 4);
        layer.AddChild(_hud);
    }

    /// <summary>
    /// Ciel vivant (étape 10) : Soleil, Lune et étoiles (horloge climatique), nuages calculés
    /// depuis le climat deux fois par seconde, météo autour de la caméra.
    /// </summary>
    private void UpdateSky(double delta)
    {
        var explorer = _terrain.Explorer;
        bool climate = _clock == ClockMode.Climate && _climate.HasTemperature;
        Vector3 sun = SunDirection();
        _terrain.SetShaderParameter("sun_direction", sun);

        // Nuages : seulement avec un climat simulé, et pas sur les couches de données (ils
        // cacheraient ce qu'on veut lire)
        bool showClouds = climate && ColoringFor(_displayMode) is TerrainColoring.Geology or TerrainColoring.Realistic;
        // Calcul en tâche de fond (copies des champs du climat), envoi à la carte graphique ici
        _cloudTimer -= delta;
        if (_cloudTask != null && _cloudTask.IsCompleted)
        {
            if (_cloudTask.IsCompletedSuccessfully && showClouds)
            {
                _sky.ApplyCloudPixels(_cloudTask.Result);
                _cloudsShown = true;
            }
            _cloudTask = null;
        }
        if (showClouds && _cloudTimer <= 0 && _cloudTask == null)
        {
            var grid = _grid;
            var humidity = (float[])_climate.RelativeHumidity.Clone();
            var rain = (float[])_climate.Statistics.RecentPrecipitation.Clone();
            var convergence = (float[])_climate.Winds.Convergence.Clone();
            var sky = _sky;
            _cloudTask = Task.Run(() =>
            {
                var (cover, showers) = SkyRenderer.ComputeCloudCover(grid, humidity, rain, convergence);
                return sky.BuildCloudPixels(cover, showers);
            });
            _cloudTimer = 0.5;
        }
        else if (!showClouds && _cloudsShown)
        {
            _sky.UpdateCloudCover(null, null);
            _cloudsShown = false;
        }
        _terrain.SetShaderParameter("clouds_enabled", _sky.HasClouds && _cloudsShown);
        _terrain.SetShaderParameter("cloud_time", (float)((_climate.TimeDays * 86400.0) % 200000.0));

        var frame = new SkyFrame
        {
            CameraPosition = explorer.Position,
            AltitudeAboveGround = explorer.AltitudeAboveGround,
            SunDirection = sun,
            Moon = climate ? MoonOrbit.Compute(_climate.TimeDays, _orbit) : null,
            StarAngle = climate ? 2 * Math.PI * _climate.TimeDays * (1 + 1 / _orbit.YearLengthDays) : 0,
            CloudTime = _climate.TimeDays * 86400.0,
            Weather = climate ? LocalWeatherAtCamera() : null,
        };
        _sky.Update(frame, delta);
    }

    // --- Lieux de test (F1 à F7, F9 à F11) -------------------------------------------------------------

    private static readonly string[] PlaceNames =
    {
        "plus haut sommet", "plus grand fleuve", "grand lac", "côte", "désert venteux",
        "forêt sous la pluie", "neige qui tombe", "au-dessus des nuages", "coucher de soleil", "Lune",
    };

    /// <summary>
    /// Emmène la caméra vers un lieu choisi d'après la simulation, pour tester les nouveautés :
    /// relief, eau, météo, ciel. Les lieux liés au climat passent en horloge climatique.
    /// </summary>
    private void GoToPlace(int index)
    {
        if (index < 0 || index >= PlaceNames.Length)
            return;
        var explorer = _terrain.Explorer;
        bool needsClimate = index >= 4;
        if (needsClimate && _clock != ClockMode.Climate)
            SetClock(ClockMode.Climate);
        if (needsClimate && !_climate.HasTemperature)
        {
            ShowNotice("Climat pas encore prêt : réessayez dans un instant");
            return;
        }
        float[] elevation = _tectonics.CellElevation;
        int count = _grid.CellCount;
        Double3 Cell(int c) => Double3.From(_grid.Positions[c]).Normalized();
        int Best(Func<int, double> score)
        {
            int best = -1;
            double bestScore = double.MinValue;
            for (int c = 0; c < count; c++)
            {
                double value = score(c);
                if (value > bestScore)
                {
                    bestScore = value;
                    best = c;
                }
            }
            return best;
        }
        string detail = "";
        var hydrology = _terrain.Field?.Hydrology;
        switch (index)
        {
            case 0:
            {
                int c = Best(c => elevation[c]);
                Double3 east = Double3.UnitY.Cross(Cell(c)).Normalized();
                explorer.PlaceAbove((Cell(c) - east * 0.004).Normalized(), 2500);
                explorer.Face(east, -0.12);
                detail = $"{elevation[c] * 1000:N0} m (relief simulé)";
                break;
            }
            case 1 when hydrology != null:
            {
                int c = Best(c => elevation[c] > 0 && hydrology.Downstream[c] >= 0 && elevation[hydrology.Downstream[c]] > 0 ? hydrology.Discharge[c] : -1);
                Double3 down = Cell(hydrology.Downstream[c]);
                explorer.PlaceAbove((Cell(c) * 0.6 + down * 0.4).Normalized(), 500);
                explorer.Face(down - Cell(c), -0.15);
                detail = $"{hydrology.Discharge[c]:N0} m³/s";
                break;
            }
            case 2 when hydrology != null:
            {
                int c = Best(c => hydrology.Lake[c] ? hydrology.Discharge[c] : -1);
                if (c < 0 || !hydrology.Lake[c])
                {
                    ShowNotice("Pas de lac sur cette planète");
                    return;
                }
                explorer.PlaceAbove(Cell(c), 1200);
                explorer.Face(Double3.UnitY, -0.2);
                break;
            }
            case 3:
            {
                int c = Best(c =>
                {
                    if (elevation[c] <= 0 || elevation[c] > 0.15)
                        return -1;
                    int sea = 0;
                    foreach (int n in _grid.Neighbors[c])
                        if (elevation[n] < 0) sea++;
                    return sea == 0 ? -1 : sea - Math.Abs(Math.Asin(Cell(c).Y)) * 2;
                });
                int ocean = Array.Find(_grid.Neighbors[c], n => elevation[n] < 0);
                explorer.PlaceAbove(Cell(c), 150);
                explorer.Face(Cell(ocean) - Cell(c), -0.08);
                break;
            }
            case 4:
            {
                var annual = _climate.Statistics.AnnualPrecipitation;
                int c = Best(c => elevation[c] > 0 && elevation[c] < 1.5f && _climate.Snow[c] < 1
                    ? _climate.Winds.Wind[c].Length() * (1 - Mathf.SmoothStep(100f, 400f, annual[c])) : -1);
                explorer.PlaceAbove(Cell(c), 40);
                explorer.Face(Double3.From(_climate.Winds.Wind[c]), 0.02);
                detail = $"vent {_climate.Winds.Wind[c].Length():0} m/s, {annual[c]:N0} mm/an";
                break;
            }
            case 5:
            case 6:
            {
                bool snow = index == 6;
                var rain = _climate.Statistics.RecentPrecipitation;
                int c = Best(c => elevation[c] > 0 && (snow ? _climate.SurfaceTemperature[c] < -2 : _climate.SurfaceTemperature[c] > 4) ? rain[c] : -1);
                if (c < 0 || rain[c] < 0.5f)
                {
                    ShowNotice(snow ? "Il ne neige nulle part en ce moment" : "Il ne pleut nulle part en ce moment");
                    return;
                }
                explorer.PlaceAbove(Cell(c), 40);
                explorer.Face(Double3.UnitY, 0.05);
                detail = $"{rain[c]:0.0} mm/jour, {_climate.SurfaceTemperature[c]:0} °C";
                break;
            }
            case 7:
            {
                var (cover, _) = SkyRenderer.ComputeCloudCover(_climate);
                int c = Best(c => cover[c] - Math.Abs(Math.Asin(Cell(c).Y)) * 0.3 + Mathf.SmoothStep(-0.1f, 0.3f, Cell(c).ToVector3().Dot(_climate.Sun.Direction)));
                explorer.PlaceAbove(Cell(c), 13000);
                explorer.Face(Double3.UnitY, -0.12);
                break;
            }
            case 8:
            {
                // Le Soleil se couche à l'est du point où il est au zénith, à ~90° de lui
                Double3 sun = Double3.From(_climate.Sun.Direction);
                Double3 east = Double3.UnitY.Cross(sun).Normalized();
                explorer.PlaceAbove((sun * Math.Sin(0.06) + east * Math.Cos(0.06)).Normalized(), 1500);
                explorer.Face(sun, 0.03);
                break;
            }
            case 9:
            {
                var moon = MoonOrbit.Compute(_climate.TimeDays, _orbit);
                Double3 moonDirection = Double3.From(moon.Direction);
                Double3 sun = Double3.From(_climate.Sun.Direction);
                // De préférence là où il fait nuit et où la Lune est haute
                Double3 place = (moonDirection - sun * 0.5).Normalized();
                if (place.Dot(moonDirection) < 0.3)
                    place = moonDirection;
                explorer.PlaceAbove(place, 300);
                Double3 toMoon = moonDirection * (moon.DistanceKm * 1000) - explorer.Position;
                double elevationAngle = Math.Asin(toMoon.Normalized().Dot(explorer.Position.Normalized()));
                explorer.Face(toMoon, Math.Min(elevationAngle, 1.2));
                detail = $"{MoonOrbit.PhaseName(moon.Phase)}, éclairée à {moon.IlluminatedFraction * 100:0} %";
                break;
            }
            default:
                ShowNotice("Réseau des fleuves pas encore calculé");
                return;
        }
        ShowNotice($"Lieu : {PlaceNames[index]}{(detail.Length > 0 ? $" ({detail})" : "")}");
    }

    private void ApplyQuality()
    {
        var (_, mode, scale) = QualityModes[_quality];
        var viewport = GetViewport();
        viewport.Scaling3DMode = mode;
        viewport.Scaling3DScale = scale;
        viewport.FsrSharpness = 0.3f;
    }

    private void ShowNotice(string text)
    {
        _notice = text;
        _noticeTimer = 5;
    }

    /// <summary>Climat de la cellule sous la caméra : pluie, neige, vent, poussière, brume.</summary>
    private LocalWeather LocalWeatherAtCamera()
    {
        Double3 position = _terrain.Explorer.Position;
        Vector3 direction = position.Normalized().ToVector3();
        int cell = _grid.NearestCell(direction, Math.Max(_weatherHint, 0));
        _weatherHint = cell;
        bool land = _tectonics.CellElevation[cell] > 0;
        float annual = _climate.Statistics.AnnualPrecipitation[cell];
        float aridity = land ? 1f - Mathf.SmoothStep(150f, 450f, annual) : 0f;
        if (_climate.Snow[cell] > 5f)
            aridity = 0;
        // Heure solaire : écart de longitude entre la caméra et le point sous le Soleil
        Vector3 sun = _climate.Sun.Direction;
        double cameraLongitude = Math.Atan2(-direction.Z, direction.X);
        double sunLongitude = Math.Atan2(-sun.Z, sun.X);
        double hour = 12 + (cameraLongitude - sunLongitude) / (2 * Math.PI) * 24;
        hour = ((hour % 24) + 24) % 24;
        return new LocalWeather
        {
            PrecipitationMmPerDay = _climate.Statistics.RecentPrecipitation[cell],
            TemperatureC = _climate.SurfaceTemperature[cell],
            RelativeHumidity = _climate.RelativeHumidity[cell],
            Wind = _climate.Winds.Wind[cell],
            Aridity = aridity,
            SolarHour = (float)hour,
        };
    }

    /// <summary>
    /// Tracés à l'échelle des cellules (flèches des plaques, fleuves, vents) : construits sur la
    /// sphère unité, puis placés au centre de la planète
    /// et agrandis au rayon réel. Masqués près du sol, où le relief détaillé les traverserait.
    /// </summary>
    private void UpdateOverlays(double delta)
    {
        var explorer = _terrain.Explorer;
        bool high = explorer.AltitudeAboveGround > OverlayMinAltitude;
        _overlayRoot.Position = (-explorer.Position).ToVector3();
        _overlayRoot.Scale = Vector3.One * (float)TerrainHeightField.RadiusMeters;


        bool geological = _clock == ClockMode.Geological;
        _arrowsMesh.Visible = high && geological && _showArrows;
        if (_arrowsMesh.Visible && _arrowsDirty)
        {
            _arrowsMesh.Mesh = VelocityArrows.Build(_tectonics);
            _arrowsDirty = false;
        }

        _riversMesh.Visible = high && !geological && _showRivers && _climate.Hydrology != null;
        if (_riversMesh.Visible && _riversVersion != _climate.HydrologyVersion)
        {
            _riversMesh.Mesh = ClimateOverlays.BuildRivers(_grid, _climate.Hydrology, _tectonics.CellElevation);
            _riversVersion = _climate.HydrologyVersion;
        }

        _windMesh.Visible = high && !geological && _showWinds;
        _windRebuildTimer -= delta;
        if (_windMesh.Visible && _windRebuildTimer <= 0)
        {
            // Flèches des courants sur la couche des courants, des vents sinon (maillage réutilisé :
            // en recréer un dix fois par seconde accumulait de la mémoire graphique)
            var arrows = _windMesh.Mesh as ImmediateMesh ?? new ImmediateMesh();
            if (_displayMode == DisplayMode.OceanCurrents)
                ClimateOverlays.BuildVectors(arrows, _grid, _climate.Ocean.Current, _tectonics.CellElevation, 40f, 0.01f, 0.4f);
            else
                ClimateOverlays.BuildVectors(arrows, _grid, _climate.Winds.Wind, _tectonics.CellElevation, 1f, 0.3f, 15f);
            _windMesh.Mesh = arrows;
            _windRebuildTimer = 1.0;
        }
    }

    // --- Affichage du texte ------------------------------------------------------------------

    /// <summary>
    /// Texte à l'écran, volontairement court : horloge, position, couche, valeurs sous la souris.
    /// [H] affiche l'aide des touches, [I] les détails de la simulation.
    /// </summary>
    private void UpdateHud()
    {
        // Textes d'interface en français
        var lines = new List<string>();
        var explorer = _terrain.Explorer;

        if (_clock == ClockMode.Geological)
        {
            string speed = !_running ? "EN PAUSE"
                : _fastForward ? $"AVANCE RAPIDE jusqu'à 4,54 milliards d'années ({French(_effectiveSpeed)} Ma/s)"
                : $"{French(GeologicalSpeeds[_geologicalSpeed])} Ma/s (réel {French(_effectiveSpeed)})";
            lines.Add($"GÉOLOGIE   {AgeLabel(_tectonics.TimeMyr)}   {speed}");
        }
        else
        {
            string speed = _running ? ClimateSpeedLabel(ClimateSpeeds[_climateSpeed]) : "EN PAUSE";
            string temperature = _climate.HasTemperature ? $"   {French(_climate.GlobalMeanTemperature)} °C en moyenne" : "";
            lines.Add($"CLIMAT   {DateLabel(_climate.TimeDays)}   {speed}{temperature}");
        }

        Vector3 p = explorer.Position.Normalized().ToVector3();
        float latitude = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(p.Y, -1f, 1f)));
        float longitude = Mathf.RadToDeg(Mathf.Atan2(p.X, p.Z));
        string walking = explorer.Walking ? "à pied   " : "";
        lines.Add($"{walking}{Math.Abs(latitude):0.0}° {(latitude >= 0 ? "N" : "S")}  {Math.Abs(longitude):0.0}° {(longitude >= 0 ? "E" : "O")}   " +
                  $"hauteur {Distance(explorer.AltitudeAboveGround)}   sol {explorer.GroundHeight:N0} m   " +
                  $"{Engine.GetFramesPerSecond():0} img/s");
        lines.Add($"Couche : {LayerName()}");
        if (_hoverCell >= 0)
            lines.Add(ShortHoverLabel(_hoverCell));

        if (_showDetails)
        {
            lines.Add("");
            DetailLines(lines);
        }
        if (_noticeTimer > 0 && _notice != null)
            lines.Add(_notice);
        if (_showHelp)
        {
            lines.Add("");
            lines.Add("Molette : zoom   Clic gauche + glisser : déplacer   Clic droit + glisser : regarder");
            lines.Add("[↑ ↓] Avancer / reculer   [← →] Tourner   [Page préc. / suiv.] Monter / descendre   [Origine / Fin] Regard haut / bas");
            lines.Add("[Q D] Pas de côté   [Maj] Plus vite   [G] Vol / marche   [X] Retour en orbite");
            lines.Add("[C] Horloge géologique / climatique   [P] Pause   [+ / -] Vitesse de l'horloge   [T] Avance rapide (4,5 Ga)   [R] Nouvelle planète");
            lines.Add("[1] Géologie / altitude   [2] Plaques   [3] Frontières   [4] Âge du plancher   [5] Ensoleillement   [6] Température");
            lines.Add("[7] Vents / pression   [8] Pluie / humidité   [9] Biomes   [0] Vue réaliste   [V] Flèches   [F] Fleuves   [U] Masquer l'eau");
            lines.Add("[O] Qualité d'image (résolution réduite + AMD FSR)");
            lines.Add("Lieux : [F1] sommet  [F2] fleuve  [F3] lac  [F4] côte  [F5] désert venteux  [F6] pluie  [F7] neige  [F9] nuages  [F10] coucher de soleil  [F11] Lune");
        }
        else
        {
            lines.Add("[H] Aide   [I] Détails");
        }
        _hud.Text = string.Join("\n", lines);
    }

    /// <summary>Détails de la simulation (touche I).</summary>
    private void DetailLines(List<string> lines)
    {
        if (_clock == ClockMode.Geological)
        {
            lines.Add($"Plaques : {_tectonics.AlivePlateCount} (cible {PlateCount})   Fracturations : {_tectonics.RiftCount}   " +
                      $"Fusions : {_tectonics.MergeCount}   Collisions continentales : {_tectonics.SutureCount}   Graine : {Seed}");
            lines.Add($"Continents : {French(_tectonics.ContinentalFraction * 100f)} % de la surface   " +
                      $"Point culminant : {French(_tectonics.CellElevation.Max())} km   " +
                      $"Fosse la plus profonde : {French(_tectonics.CellElevation.Min())} km");
        }
        else
        {
            var sun = _climate.Sun;
            lines.Add($"{SeasonLabel(sun.EclipticLongitude)}   Soleil au zénith à {SignedDegrees(sun.Declination)} de latitude   " +
                      $"Relief figé à {French(_tectonics.TimeMyr)} Ma   Calcul : {_climate.ComputeBackend}");
            var moon = MoonOrbit.Compute(_climate.TimeDays, _orbit);
            lines.Add($"Lune : {MoonOrbit.PhaseName(moon.Phase)}, éclairée à {moon.IlluminatedFraction * 100f:0} %, à {moon.DistanceKm:N0} km");
            if (_climate.HasTemperature)
            {
                lines.Add($"Extrêmes : {French(_climate.SurfaceTemperature.Min())} °C / {French(_climate.SurfaceTemperature.Max())} °C   " +
                          $"Précipitations : {French(_climate.GlobalPrecipitation)} mm/jour   " +
                          $"Terres enneigées : {French(_climate.SnowCoveredLandFraction * 100f)} %   Vent maximal : {French(_climate.Winds.MaxSpeed)} m/s");
                var hydrology = _climate.Hydrology;
                if (hydrology != null)
                    lines.Add($"Plus grand fleuve : {hydrology.LargestDischarge:N0} m³/s   Lacs : {hydrology.LakeCellCount} cellules   " +
                              $"Glaciers : {hydrology.GlacierCellCount} cellules   Années simulées : {_climate.Statistics.CompletedYears}");
                if (_displayMode is DisplayMode.Biomes or DisplayMode.Realistic)
                    lines.Add(BiomeSummary());
            }
        }
        lines.Add($"Légende : {DisplayModeLabel()}");
        if (_hoverCell >= 0)
            lines.Add(HoverLabel(_hoverCell));
        string stale = _terrain.StaleChunkCount > 0 ? $", {_terrain.StaleChunkCount} en mise à jour" : "";
        lines.Add($"Terrain : {_terrain.VisibleChunkCount} morceaux (niveau max {_terrain.DeepestVisibleLevel}{stale}), " +
                  $"{_terrain.CachedChunkCount} en mémoire, {_terrain.PendingChunkCount} en calcul   " +
                  $"Vitesse de la caméra : {SpeedLabel(_terrain.Explorer.Speed)}   Image : {QualityModes[_quality].label}");
    }

    private string LayerName() => _displayMode switch
    {
        DisplayMode.Geology => "géologie",
        DisplayMode.Elevation => "altitude",
        DisplayMode.Plates => "plaques",
        DisplayMode.Boundaries => "frontières de plaques (rouge : convergente, bleu : divergente, jaune : transformante)",
        DisplayMode.CrustAge => "âge du plancher océanique (rouge : récent, bleu : ancien)",
        DisplayMode.InstantInsolation => "ensoleillement instantané",
        DisplayMode.DailyInsolation => "ensoleillement moyen sur 24 h",
        DisplayMode.Temperature => "température (bleu : froid, rouge : chaud)",
        DisplayMode.WindSpeed => "vitesse du vent",
        DisplayMode.Pressure => "pression (bleu : dépression, rouge : anticyclone)",
        DisplayMode.OceanCurrents => "courants océaniques",
        DisplayMode.RecentPrecipitation => "pluie des derniers jours",
        DisplayMode.AnnualPrecipitation => "pluie annuelle",
        DisplayMode.Humidity => "humidité de l'air",
        DisplayMode.Biomes => "biomes",
        DisplayMode.Realistic => "vue réaliste",
        _ => "",
    };

    /// <summary>Valeurs essentielles de la cellule sous la souris.</summary>
    private string ShortHoverLabel(int cell)
    {
        string text = $"Sous la souris : altitude {_tectonics.CellElevation[cell] * 1000f:N0} m";
        if (_clock != ClockMode.Climate || !_climate.HasTemperature)
            return text;
        text += $", {French(_climate.SurfaceTemperature[cell])} °C, pluie {_climate.Statistics.AnnualPrecipitation[cell]:N0} mm/an";
        if (!_climate.Model.IsOcean(cell))
            text += $", {Biomes.Name(_climate.Biomes[cell])}";
        return text;
    }

    private static string Distance(double meters) => meters switch
    {
        < 1000 => $"{meters:0.0} m".Replace('.', ','),
        < 100000 => $"{meters / 1000:0.00} km".Replace('.', ','),
        _ => $"{meters / 1000:N0} km",
    };

    private static string SpeedLabel(double metersPerSecond) => metersPerSecond < 100
        ? $"{metersPerSecond * 3.6:0} km/h"
        : $"{metersPerSecond * 3.6:N0} km/h (Mach {metersPerSecond / 340:0.0})".Replace('.', ',');

    private string DisplayModeLabel() => _displayMode switch
    {
        DisplayMode.Geology => "Géologie (sédiments, sols nus, roche, neige au-dessus d'une limite selon la latitude, mer)",
        DisplayMode.Elevation => "Altitude (bleu : océans, vert : plaines, ocre : plateaux, brun : montagnes, blanc : sommets)",
        DisplayMode.Plates => "Plaques (clair : continents, foncé : océans)",
        DisplayMode.Boundaries => "Frontières (rouge : convergente, bleu : divergente, jaune : transformante)",
        DisplayMode.CrustAge => "Âge du plancher océanique (rouge : neuf, jaune, vert, bleu : ancien ; beige : continents)",
        DisplayMode.InstantInsolation => "Ensoleillement instantané (bleu nuit : nuit, jaune-blanc : Soleil au zénith, ~1360 W/m²)",
        DisplayMode.DailyInsolation => "Ensoleillement moyen sur 24 h (bleu nuit : nuit polaire, blanc : ~550 W/m²)",
        DisplayMode.Temperature => "Température de surface (violet : −50 °C, bleu, blanc : 0 °C, vert, jaune, rouge : +40 °C)",
        DisplayMode.WindSpeed => "Vitesse du vent (bleu sombre : calme, cyan, vert, jaune, rouge : plus de 18 m/s)",
        DisplayMode.Pressure => "Pression au niveau de la mer (bleu : dépressions, blanc : 1013 hPa, rouge : anticyclones)",
        DisplayMode.OceanCurrents => "Courants de surface (bleu nuit : calme, cyan, vert, jaune, rouge : plus de 0,4 m/s) ; flèches avec V",
        DisplayMode.RecentPrecipitation => "Précipitations des derniers jours, en rythme annuel (beige : sec, vert, bleu, violet : plus de 4 m/an)",
        DisplayMode.AnnualPrecipitation => "Précipitations annuelles (beige : désert, < 250 mm ; vert : 1 m ; bleu : 2 m et plus)",
        DisplayMode.Humidity => "Humidité relative de l'air (brun : sec, blanc : 60 %, bleu : saturé)",
        DisplayMode.Biomes => "Biomes (classification simplifiée de Köppen, mise à jour à chaque année simulée)",
        DisplayMode.Realistic => "Vue réaliste (végétation, déserts, neige, glaciers, banquise, lacs)",
        _ => "",
    };

    /// <summary>Part des terres occupée par chaque biome (les cinq plus étendus).</summary>
    private string BiomeSummary()
    {
        var counts = new int[Biomes.Count];
        int land = 0;
        for (int cell = 0; cell < _grid.CellCount; cell++)
        {
            if (_climate.Biomes[cell] == Biome.Ocean)
                continue;
            counts[(int)_climate.Biomes[cell]]++;
            land++;
        }
        if (land == 0)
            return "Aucune terre émergée";
        var top = Enumerable.Range(0, counts.Length)
            .Where(b => counts[b] > 0)
            .OrderByDescending(b => counts[b])
            .Take(5)
            .Select(b => $"{Biomes.Name((Biome)b)} {counts[b] * 100 / land} %");
        return "Terres : " + string.Join(", ", top);
    }

    /// <summary>Repère la cellule sous la souris (rayon depuis la caméra, sphère du niveau de la mer).</summary>
    private void UpdateHover()
    {
        Vector2 mouse = GetViewport().GetMousePosition();
        Vector3 ray = _terrain.Camera.ProjectRayNormal(mouse);
        Double3 direction = Double3.From(ray).Normalized();
        // Caméra à l'origine du rendu ; centre de la planète en −position de la caméra
        Double3 center = -_terrain.Explorer.Position;
        double radius = TerrainHeightField.RadiusMeters;
        double b = direction.Dot(center);
        double discriminant = b * b - center.LengthSquared() + radius * radius;
        if (discriminant < 0)
        {
            _hoverCell = -1;
            return;
        }
        double t = b - Math.Sqrt(discriminant);
        if (t < 0)
        {
            _hoverCell = -1;
            return;
        }
        Double3 point = (direction * t - center).Normalized();
        _hoverCell = _grid.NearestCell(point.ToVector3(), _hoverCell >= 0 ? _hoverCell : 0);
    }

    /// <summary>Valeurs de la cellule sous la souris.</summary>
    private string HoverLabel(int cell)
    {
        Vector3 p = _grid.Positions[cell];
        float latitude = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(p.Y, -1f, 1f)));
        float longitude = Mathf.RadToDeg(Mathf.Atan2(p.X, p.Z));
        string position = $"Sous la souris : {Math.Abs(latitude):0}° {(latitude >= 0 ? "N" : "S")}, " +
                          $"{Math.Abs(longitude):0}° {(longitude >= 0 ? "E" : "O")}, " +
                          $"altitude {_tectonics.CellElevation[cell] * 1000f:N0} m";
        if (_clock != ClockMode.Climate || !_climate.HasTemperature)
            return position;

        var statistics = _climate.Statistics;
        string text = position +
                      $"   {French(_climate.SurfaceTemperature[cell])} °C (moyenne annuelle {French(statistics.MeanTemperature[cell])} °C, " +
                      $"{French(statistics.ColdestMonth[cell])} à {French(statistics.WarmestMonth[cell])} °C)" +
                      $"   Pluie : {statistics.AnnualPrecipitation[cell]:N0} mm/an" +
                      $"   Vent : {French(_climate.Winds.Wind[cell].Length())} m/s, {_climate.Winds.Pressure[cell]:0} hPa";
        if (!_climate.Model.IsOcean(cell))
        {
            text += $"   {Biomes.Name(_climate.Biomes[cell])}";
            if (_climate.Snow[cell] > 1f)
                text += $", neige {_climate.Snow[cell]:N0} mm";
            var hydrology = _climate.Hydrology;
            if (hydrology != null && hydrology.Discharge[cell] > HydrologyModel.RiverThreshold)
                text += $", débit {hydrology.Discharge[cell]:N0} m³/s";
            if (hydrology != null && hydrology.Lake[cell])
                text += ", lac";
        }
        return text;
    }

    /// <summary>Date et heure (au méridien 0) à partir du temps climatique en jours.</summary>
    private string DateLabel(double timeDays)
    {
        float yearLength = _climate.Orbit.YearLengthDays;
        int year = (int)Math.Floor(timeDays / yearLength) + 1;
        double dayOfYear = timeDays - (year - 1) * yearLength;

        // Calendrier terrestre étiré ou comprimé à la durée de l'année de la planète
        double day365 = dayOfYear * 365.0 / yearLength;
        int month = 0;
        while (month < 11 && day365 >= MonthLengths[month])
        {
            day365 -= MonthLengths[month];
            month++;
        }
        int dayOfMonth = Math.Min((int)day365 + 1, MonthLengths[month]);

        double hours = (timeDays - Math.Floor(timeDays)) * 24.0;
        int hour = (int)hours;
        int minute = (int)((hours - hour) * 60.0);
        string first = dayOfMonth == 1 ? "1er" : dayOfMonth.ToString();
        return $"Année {year}, {first} {MonthNames[month]}, {hour} h {minute:00} (méridien 0)";
    }

    private static string SeasonLabel(float eclipticLongitude)
    {
        float degrees = Mathf.RadToDeg(eclipticLongitude);
        string season = degrees switch
        {
            < 90f => "Printemps",
            < 180f => "Été",
            < 270f => "Automne",
            _ => "Hiver",
        };
        return $"{season} dans l'hémisphère nord";
    }

    private static string ClimateSpeedLabel(float daysPerSecond) => daysPerSecond switch
    {
        < 1f / 50000f => "temps réel",
        < 1f / 1000f => "1 minute par seconde",
        < 0.01f => "10 minutes par seconde",
        < 0.1f => "1 heure par seconde",
        < 0.5f => "6 heures par seconde",
        < 1.5f => "1 jour par seconde",
        < 20f => $"{daysPerSecond:0} jours par seconde",
        < 45f => "1 mois par seconde",
        _ => $"{daysPerSecond / 30f:0} mois par seconde",
    };

    /// <summary>Vitesse mesurée de l'horloge climatique, en heures ou jours par seconde.</summary>
    private static string EffectiveClimateSpeed(double daysPerSecond) => daysPerSecond < 1.0 / 24.0
        ? $"{daysPerSecond * 1440.0:0.0} min/s".Replace('.', ',')
        : daysPerSecond < 1.0
        ? $"{daysPerSecond * 24.0:0.0} h/s".Replace('.', ',')
        : $"{daysPerSecond:0.0} jours/s".Replace('.', ',');

    private static string SignedDegrees(float radians)
    {
        float degrees = Mathf.RadToDeg(radians);
        return (degrees >= 0f ? "+" : "") + degrees.ToString("0.0").Replace('.', ',') + "°";
    }

    /// <summary>Nombre à une décimale avec la virgule française.</summary>
    private static string French(double value) => value.ToString("0.0").Replace('.', ',');

    /// <summary>Âge en millions d'années, ou en milliards au-delà de 1000 Ma.</summary>
    private static string AgeLabel(double myr) => myr < 1000
        ? $"{French(myr)} millions d'années"
        : $"{(myr / 1000).ToString("0.00").Replace('.', ',')} milliards d'années";

    // --- Entrées -----------------------------------------------------------------------------

    /// <summary>
    /// Commandes continues : flèches haut/bas pour avancer/reculer, gauche/droite pour tourner,
    /// Page préc./suiv. (ou Espace/Ctrl) pour monter/descendre, Origine/Fin pour lever ou baisser
    /// les yeux, Maj pour aller plus vite. Z/S et Q/D (positions physiques) marchent aussi.
    /// </summary>
    private static ExplorerInput ReadInput()
    {
        static float Axis(bool negative, bool positive) => (positive ? 1f : 0f) - (negative ? 1f : 0f);
        static bool Down(Key key) => Input.IsPhysicalKeyPressed(key);
        return new ExplorerInput(
            Forward: Axis(Down(Key.Down) || Down(Key.S), Down(Key.Up) || Down(Key.W)),
            Strafe: Axis(Down(Key.A), Down(Key.D)),
            Vertical: Axis(Down(Key.Pagedown) || Down(Key.Ctrl), Down(Key.Pageup) || Down(Key.Space)),
            Turn: Axis(Down(Key.Left), Down(Key.Right)),
            Pitch: Axis(Down(Key.End), Down(Key.Home)),
            Run: Down(Key.Shift),
            Jump: Down(Key.Space));
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        var explorer = _terrain.Explorer;
        switch (@event)
        {
            case InputEventKey { Pressed: true, Echo: false } key:
                HandleKey(key);
                break;
            case InputEventMouseMotion motion when (motion.ButtonMask & MouseButtonMask.Left) != 0:
                explorer.Pan(motion.Relative, GetViewport().GetVisibleRect().Size.Y);
                break;
            case InputEventMouseMotion motion when (motion.ButtonMask & MouseButtonMask.Right) != 0:
                explorer.Look(motion.Relative);
                break;
            case InputEventMouseButton { Pressed: true } button:
                if (button.ButtonIndex == MouseButton.WheelUp)
                    explorer.Zoom(+1);
                else if (button.ButtonIndex == MouseButton.WheelDown)
                    explorer.Zoom(-1);
                break;
        }
    }

    private void HandleKey(InputEventKey key)
    {
        // + et - : caractère tapé (en AZERTY, « - » est sur la touche 6 et « + » sur Maj + =)
        switch (key.Keycode)
        {
            case Key.Plus:
            case Key.Equal:
            case Key.KpAdd:
                ChangeSpeed(+1);
                return;
            case Key.Minus:
            case Key.KpSubtract:
                ChangeSpeed(-1);
                return;
        }

        // Le reste : touche physique, pour que la rangée des chiffres marche en AZERTY sans Maj
        switch (key.PhysicalKeycode)
        {
            case Key.Key1 or Key.Kp1: CycleDisplayMode(DisplayMode.Geology, DisplayMode.Elevation); break;
            case Key.Key2 or Key.Kp2: SetDisplayMode(DisplayMode.Plates); break;
            case Key.Key3 or Key.Kp3: SetDisplayMode(DisplayMode.Boundaries); break;
            case Key.Key4 or Key.Kp4: SetDisplayMode(DisplayMode.CrustAge); break;
            case Key.Key5 or Key.Kp5:
                CycleDisplayMode(DisplayMode.InstantInsolation, DisplayMode.DailyInsolation);
                break;
            case Key.Key6 or Key.Kp6: CycleDisplayMode(DisplayMode.Temperature); break;
            case Key.Key7 or Key.Kp7: CycleDisplayMode(DisplayMode.WindSpeed, DisplayMode.Pressure, DisplayMode.OceanCurrents); break;
            case Key.Key8 or Key.Kp8:
                CycleDisplayMode(DisplayMode.RecentPrecipitation, DisplayMode.AnnualPrecipitation, DisplayMode.Humidity);
                break;
            case Key.Key9 or Key.Kp9: CycleDisplayMode(DisplayMode.Biomes); break;
            case Key.Key0 or Key.Kp0: CycleDisplayMode(DisplayMode.Realistic); break;
            case Key.F: _showRivers = !_showRivers; break;
            // Pas de F8 : lancé depuis l'éditeur, le jeu s'arrête sur F8 (raccourci « Arrêter » de Godot)
            case >= Key.F1 and <= Key.F7:
                GoToPlace((int)(key.Keycode - Key.F1));
                break;
            case >= Key.F9 and <= Key.F11:
                GoToPlace(7 + (int)(key.Keycode - Key.F9));
                break;
            case Key.C:
                SetClock(_clock == ClockMode.Geological ? ClockMode.Climate : ClockMode.Geological);
                break;
            case Key.V:
                if (_clock == ClockMode.Geological)
                {
                    _showArrows = !_showArrows;
                    _arrowsDirty = true;
                }
                else
                {
                    _showWinds = !_showWinds;
                    _windRebuildTimer = 0;
                }
                break;
            case Key.G:
                _terrain.Explorer.ToggleWalking();
                break;
            case Key.X:
                // Retour en orbite au-dessus du point actuel
                _terrain.Explorer.PlaceAbove(_terrain.Explorer.Position, OrbitAltitude);
                break;
            case Key.P:
                _running = !_running;
                break;
            case Key.T:
                ToggleFastForward();
                break;
            case Key.O:
                _quality = (_quality + 1) % QualityModes.Length;
                ApplyQuality();
                ShowNotice($"Qualité d'image : {QualityModes[_quality].label}");
                break;
            case Key.U:
                // Eau masquée : on voit les fonds marins et le fond des lacs
                _terrain.ShowWater = !_terrain.ShowWater;
                break;
            case Key.H:
                _showHelp = !_showHelp;
                break;
            case Key.I:
                _showDetails = !_showDetails;
                break;
            case Key.R:
                Seed++;
                GeneratePlanet();
                break;
        }
    }

    private void ChangeSpeed(int direction)
    {
        _fastForward = false;
        if (_clock == ClockMode.Geological)
            _geologicalSpeed = Math.Clamp(_geologicalSpeed + direction, 0, GeologicalSpeeds.Length - 1);
        else
            _climateSpeed = Math.Clamp(_climateSpeed + direction, 0, ClimateSpeeds.Length - 1);
    }

    private void SetDisplayMode(DisplayMode mode)
    {
        _displayMode = mode;
        // Les couches du climat demandent l'horloge climatique (relief figé)
        if (IsClimateLayer(mode) && _clock == ClockMode.Geological)
            SetClock(ClockMode.Climate);
        _colorsDirty = true;
        _colorTimer = 0;
    }

    /// <summary>Une touche, plusieurs variantes : appuyer de nouveau passe à la suivante.</summary>
    private void CycleDisplayMode(params DisplayMode[] modes)
    {
        int index = Array.IndexOf(modes, _displayMode);
        SetDisplayMode(modes[(index + 1) % modes.Length]);
    }
}
