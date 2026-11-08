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
/// Scène principale : génère la planète, fait tourner l'horloge géologique ou climatique,
/// et affiche le globe.
/// Souris : clic gauche + glisser pour tourner, molette pour zoomer.
/// Clavier : C = horloge géologique/climatique, 1 à 0 = couches (appuyer de nouveau sur la même
/// touche passe à la variante suivante), E = relief 3D, V = flèches (plaques ou vents),
/// F = fleuves, Espace = pause, +/- = vitesse, R = nouvelle planète.
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

    private const float RotationSpeed = 0.005f;
    private const float MinDistance = 1.2f;
    private const float MaxDistance = 10f;

    // Vitesses de l'horloge géologique, en millions d'années par seconde réelle
    private static readonly float[] GeologicalSpeeds = { 0.5f, 1f, 2f, 5f, 10f, 20f, 50f };
    private const int DefaultGeologicalSpeed = 2;

    // Vitesses de l'horloge climatique, en jours par seconde réelle (1 h/s à 3 mois/s)
    private static readonly float[] ClimateSpeeds = { 1f / 24f, 0.25f, 1f, 5f, 15f, 30f, 90f };
    private const int DefaultClimateSpeed = 1;

    // Temps de calcul maximal consacré à la simulation par image, pour garder l'affichage fluide
    private const double SimulationBudgetMs = 25.0;

    // Exagérations du relief 3D (×1 = échelle réelle, invisible : 7 km pour 6371 km de rayon)
    private static readonly float[] ReliefPresets = { 0f, 10f, 25f, 50f, 100f };
    private const int DefaultReliefIndex = 2;

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

    private PlanetGrid _grid;
    private TectonicPlates _tectonics;
    private ClimateSimulation _climate;
    private OrbitalParameters _orbit;

    // Estimation du climat pour l'érosion (horloge géologique)
    private Task<float[]> _estimateTask;
    private TectonicPlates _estimateSource;
    private double _nextEstimateMyr;

    // Relief sur lequel le climat a été initialisé (planète et temps géologique) : le climat ne
    // repart de l'équilibre que si le relief a changé depuis
    private TectonicPlates _climateSurfaceSource;
    private double _climateSurfaceTimeMyr = double.NaN;
    private DisplayMode _displayMode = DisplayMode.Elevation;
    private ClockMode _clock = ClockMode.Geological;

    private MeshInstance3D _planetMesh;
    private CellColorLayer _colorLayer;
    private MeshInstance3D _arrowsMesh;
    private MeshInstance3D _riversMesh;
    private MeshInstance3D _windMesh;
    private bool _showRivers = true;
    private bool _showWinds;
    private int _riversVersion = -1;
    private double _windRebuildTimer;
    private int _hoverCell = -1;
    private DirectionalLight3D _sun;
    private Environment _environment;
    private Label _hud;

    private bool _running = true;
    private int _geologicalSpeed = DefaultGeologicalSpeed;
    private int _climateSpeed = DefaultClimateSpeed;
    private int _reliefIndex = DefaultReliefIndex;
    private double _pendingTime;
    private double _measureRealSeconds;
    private double _measureSimulated;
    private double _effectiveSpeed;

    private Node3D _cameraPivot;
    private Camera3D _camera;
    private float _yaw;
    private float _pitch;
    private float _distance = 3f;

    public override void _Ready()
    {
        var stopwatch = Stopwatch.StartNew();
        _grid = PlanetGrid.Create(Subdivisions);
        long gridMs = stopwatch.ElapsedMilliseconds;

        stopwatch.Restart();
        _colorLayer = new CellColorLayer(_grid.CellCount);
        _planetMesh = new MeshInstance3D
        {
            Name = "Planet",
            Mesh = PlanetMeshBuilder.Build(_grid),
            MaterialOverride = _colorLayer.Material,
        };
        AddChild(_planetMesh);
        _colorLayer.SetReliefExaggeration(ReliefPresets[_reliefIndex]);
        GD.Print($"Grid: {_grid.CellCount} cells (subdivisions {Subdivisions}) in {gridMs} ms, " +
                 $"mesh in {stopwatch.ElapsedMilliseconds} ms");

        _arrowsMesh = new MeshInstance3D
        {
            Name = "VelocityArrows",
            MaterialOverride = VelocityArrows.CreateMaterial(),
            Visible = false,
        };
        AddChild(_arrowsMesh);
        _riversMesh = new MeshInstance3D
        {
            Name = "Rivers",
            MaterialOverride = VelocityArrows.CreateMaterial(),
            Visible = false,
        };
        AddChild(_riversMesh);
        _windMesh = new MeshInstance3D
        {
            Name = "WindArrows",
            MaterialOverride = VelocityArrows.CreateMaterial(),
            Visible = false,
        };
        AddChild(_windMesh);

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

        SetupEnvironment();
        SetupCamera();
        SetupHud();
        GeneratePlanet();
    }

    public override void _ExitTree()
    {
        _estimateTask?.Wait();
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
            CollectClimateEstimate();
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

        var stopwatch = Stopwatch.StartNew();
        double simulated = 0;
        while (_pendingTime >= step && stopwatch.Elapsed.TotalMilliseconds < SimulationBudgetMs)
        {
            _tectonics.Step(step);
            _pendingTime -= step;
            simulated += step;
        }
        _pendingTime = Math.Min(_pendingTime, step);
        MeasureSpeed(delta, simulated);

        if (simulated > 0)
        {
            RefreshDisplay();
            if (_arrowsMesh.Visible)
                RebuildArrows();
        }
        if (_tectonics.TimeMyr >= _nextEstimateMyr)
            StartClimateEstimate();
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
        UpdateSunLight();
        RefreshDisplay();
        UpdateClimateOverlays(delta);
    }

    /// <summary>Fleuves (recalculés une fois par année simulée) et flèches de vent.</summary>
    private void UpdateClimateOverlays(double delta)
    {
        bool climate = _clock == ClockMode.Climate;
        _riversMesh.Visible = climate && _showRivers && _climate.Hydrology != null;
        if (_riversMesh.Visible && _riversVersion != _climate.HydrologyVersion)
            RebuildRivers();

        _windMesh.Visible = climate && _showWinds;
        _windRebuildTimer -= delta;
        if (_windMesh.Visible && _windRebuildTimer <= 0)
        {
            _windMesh.Mesh = ClimateOverlays.BuildWinds(_grid, _climate.Winds, _tectonics.CellElevation,
                                                        ReliefPresets[_reliefIndex]);
            _windRebuildTimer = 0.1;
        }
    }

    private void RebuildRivers()
    {
        _riversMesh.Mesh = ClimateOverlays.BuildRivers(_grid, _climate.Hydrology, _tectonics.CellElevation,
                                                       ReliefPresets[_reliefIndex]);
        _riversVersion = _climate.HydrologyVersion;
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
        _pendingTime = 0;
        _effectiveSpeed = 0;
        _measureRealSeconds = 0;
        _measureSimulated = 0;

        if (clock == ClockMode.Climate)
        {
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
            _arrowsMesh.Visible = false;
            // Nuit plus sombre pour bien voir le jour et la nuit
            _environment.AmbientLightEnergy = 0.12f;
            if (!IsClimateLayer(_displayMode) && _displayMode is not (DisplayMode.InstantInsolation or DisplayMode.DailyInsolation))
                _displayMode = DisplayMode.Realistic;
            UpdateSunLight();
        }
        else
        {
            _environment.AmbientLightEnergy = 0.35f;
            _sun.RotationDegrees = new Vector3(-30f, 40f, 0f);
            // Les couches du climat ne correspondent plus au relief qui va évoluer
            if (IsClimateLayer(_displayMode))
                _displayMode = DisplayMode.Elevation;
            _nextEstimateMyr = _tectonics.TimeMyr;
        }
        RefreshDisplay();
        UpdateClimateOverlays(0);
    }

    /// <summary>Couches calculées par la simulation du climat (sur un relief figé).</summary>
    private static bool IsClimateLayer(DisplayMode mode) => mode is DisplayMode.Temperature
        or DisplayMode.WindSpeed or DisplayMode.Pressure or DisplayMode.RecentPrecipitation
        or DisplayMode.AnnualPrecipitation or DisplayMode.Humidity or DisplayMode.Biomes or DisplayMode.Realistic;

    /// <summary>Oriente la lumière comme le vrai Soleil : la face éclairée du globe est le jour.</summary>
    private void UpdateSunLight()
    {
        Vector3 towardSun = _climate.Sun.Direction;
        Vector3 up = Mathf.Abs(towardSun.Y) > 0.99f ? Vector3.Right : Vector3.Up;
        // La lumière éclaire selon son axe −Z : on la fait regarder du Soleil vers la planète
        _sun.Basis = Basis.LookingAt(-towardSun, up);
    }

    // --- Planète -----------------------------------------------------------------------------

    /// <summary>(Re)génère les plaques pour la graine courante et remet l'horloge à zéro.</summary>
    private void GeneratePlanet()
    {
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
        RebuildArrows();
        RefreshDisplay();
    }

    /// <summary>Envoie à la carte graphique les couleurs de la couche affichée et les altitudes.</summary>
    private void RefreshDisplay()
    {
        _colorLayer.UpdateColors(PlanetColorizer.Compute(_tectonics, _climate, _displayMode));
        _colorLayer.UpdateElevation(_tectonics.CellElevation);
    }

    private void RebuildArrows()
    {
        _arrowsMesh.Mesh = VelocityArrows.Build(_tectonics, ReliefPresets[_reliefIndex]);
    }

    // --- Scène -------------------------------------------------------------------------------

    private void SetupEnvironment()
    {
        _sun = new DirectionalLight3D { Name = "Sun", RotationDegrees = new Vector3(-30f, 40f, 0f) };
        AddChild(_sun);

        _environment = new Environment
        {
            BackgroundMode = Environment.BGMode.Color,
            BackgroundColor = Colors.Black,
            AmbientLightSource = Environment.AmbientSource.Color,
            AmbientLightColor = Colors.White,
            AmbientLightEnergy = 0.35f,
        };
        AddChild(new WorldEnvironment { Name = "WorldEnvironment", Environment = _environment });
    }

    private void SetupCamera()
    {
        _cameraPivot = new Node3D { Name = "CameraPivot" };
        AddChild(_cameraPivot);
        _camera = new Camera3D { Name = "Camera", Position = new Vector3(0f, 0f, _distance) };
        _cameraPivot.AddChild(_camera);
        _camera.MakeCurrent();
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

    // --- Affichage du texte ------------------------------------------------------------------

    private void UpdateHud()
    {
        // Textes d'interface en français
        var lines = new List<string>();

        if (_clock == ClockMode.Geological)
        {
            string state = _running
                ? $"Vitesse : {French(GeologicalSpeeds[_geologicalSpeed])} Ma/s (réelle : {French(_effectiveSpeed)} Ma/s)"
                : "EN PAUSE";
            lines.Add($"HORLOGE GÉOLOGIQUE   Temps : {French(_tectonics.TimeMyr)} millions d'années   {state}");
            lines.Add($"Plaques : {_tectonics.AlivePlateCount} (cible {PlateCount})   Fracturations : {_tectonics.RiftCount}   " +
                      $"Fusions : {_tectonics.MergeCount}   Collisions continentales : {_tectonics.SutureCount}   Graine : {Seed}");
            lines.Add($"Continents : {French(_tectonics.ContinentalFraction * 100f)} % de la surface   " +
                      $"Point culminant : {French(_tectonics.CellElevation.Max())} km   " +
                      $"Fosse la plus profonde : {French(_tectonics.CellElevation.Min())} km");
        }
        else
        {
            var sun = _climate.Sun;
            string state = _running
                ? $"Vitesse : {ClimateSpeedLabel(ClimateSpeeds[_climateSpeed])}"
                : "EN PAUSE";
            lines.Add($"HORLOGE CLIMATIQUE   {DateLabel(_climate.TimeDays)}   {state}");
            lines.Add($"{SeasonLabel(sun.EclipticLongitude)}   Soleil au zénith à {SignedDegrees(sun.Declination)} de latitude   " +
                      $"Distance : {sun.DistanceAu:0.000} UA   Flux : {sun.Flux:0} W/m²".Replace('.', ','));
            if (_climate.HasTemperature)
                lines.Add($"Température moyenne : {French(_climate.GlobalMeanTemperature)} °C   " +
                          $"Extrêmes : {French(_climate.SurfaceTemperature.Min())} °C / {French(_climate.SurfaceTemperature.Max())} °C   " +
                          $"(vitesse réelle : {EffectiveClimateSpeed(_effectiveSpeed)}, {_climate.LastSubsteps} sous-pas par image)");
            if (_climate.HasTemperature)
            {
                var statistics = _climate.Statistics;
                string years = statistics.CompletedYears == 0
                    ? "climat annuel estimé (première année en cours)"
                    : $"{statistics.CompletedYears} année(s) simulée(s) depuis le gel du relief";
                lines.Add($"Précipitations : {French(_climate.GlobalPrecipitation)} mm/jour en moyenne   " +
                          $"Terres enneigées : {French(_climate.SnowCoveredLandFraction * 100f)} %   " +
                          $"Vent maximal : {French(_climate.Winds.MaxSpeed)} m/s   Biomes et fleuves : {years}");
                var hydrology = _climate.Hydrology;
                if (hydrology != null)
                    lines.Add($"Fleuves : {hydrology.RiverCellCount} cellules, le plus grand débite {hydrology.LargestDischarge:N0} m³/s   " +
                              $"Lacs : {hydrology.LakeCellCount} cellules   Glaciers : {hydrology.GlacierCellCount} cellules");
                if (_displayMode is DisplayMode.Biomes or DisplayMode.Realistic)
                    lines.Add(BiomeSummary());
            }
            lines.Add($"Relief figé à {French(_tectonics.TimeMyr)} Ma   Calcul : {_climate.ComputeBackend}");
        }

        lines.Add($"Affichage : {DisplayModeLabel()}");
        if (_hoverCell >= 0)
            lines.Add(HoverLabel(_hoverCell));
        lines.Add("[C] Horloge géologique / climatique   [1] Altitude   [2] Plaques   [3] Frontières   [4] Âge du plancher");
        lines.Add("[5] Ensoleillement (instantané / 24 h)   [6] Température   [7] Vents / pression   " +
                  "[8] Précipitations (du moment / annuelles) / humidité   [9] Biomes   [0] Vue réaliste");
        string arrows = _clock == ClockMode.Geological ? "déplacement des plaques" : "vents";
        lines.Add($"[V] Flèches ({arrows})   [F] Fleuves : {(_showRivers ? "affichés" : "masqués")}   " +
                  $"[E] Relief 3D : {ReliefLabel()}   [Espace] Pause   [+] / [-] Vitesse   [R] Nouvelle planète");
        lines.Add("Clic gauche + glisser : tourner   Molette : zoom");
        _hud.Text = string.Join("\n", lines);
    }

    private string DisplayModeLabel() => _displayMode switch
    {
        DisplayMode.Elevation => "Altitude (bleu : océans, vert : plaines, ocre : plateaux, brun : montagnes, blanc : sommets)",
        DisplayMode.Plates => "Plaques (clair : continents, foncé : océans)",
        DisplayMode.Boundaries => "Frontières (rouge : convergente, bleu : divergente, jaune : transformante)",
        DisplayMode.CrustAge => "Âge du plancher océanique (rouge : neuf, jaune, vert, bleu : ancien ; beige : continents)",
        DisplayMode.InstantInsolation => "Ensoleillement instantané (bleu nuit : nuit, jaune-blanc : Soleil au zénith, ~1360 W/m²)",
        DisplayMode.DailyInsolation => "Ensoleillement moyen sur 24 h (bleu nuit : nuit polaire, blanc : ~550 W/m²)",
        DisplayMode.Temperature => "Température de surface (violet : −50 °C, bleu, blanc : 0 °C, vert, jaune, rouge : +40 °C)",
        DisplayMode.WindSpeed => "Vitesse du vent (bleu sombre : calme, cyan, vert, jaune, rouge : plus de 18 m/s)",
        DisplayMode.Pressure => "Pression au niveau de la mer (bleu : dépressions, blanc : 1013 hPa, rouge : anticyclones)",
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

    /// <summary>Repère la cellule sous la souris (intersection du rayon avec la sphère).</summary>
    private void UpdateHover()
    {
        Vector2 mouse = GetViewport().GetMousePosition();
        Vector3 origin = _camera.ProjectRayOrigin(mouse);
        Vector3 direction = _camera.ProjectRayNormal(mouse);
        // |origin + t·direction| = 1 : on garde la première intersection
        float b = origin.Dot(direction);
        float c = origin.LengthSquared() - 1f;
        float discriminant = b * b - c;
        if (discriminant < 0f)
        {
            _hoverCell = -1;
            return;
        }
        float t = -b - Mathf.Sqrt(discriminant);
        Vector3 point = (origin + direction * t).Normalized();
        _hoverCell = _grid.NearestCell(point, _hoverCell >= 0 ? _hoverCell : 0);
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
        < 0.1f => "1 heure par seconde",
        < 0.5f => "6 heures par seconde",
        < 1.5f => "1 jour par seconde",
        < 20f => $"{daysPerSecond:0} jours par seconde",
        < 45f => "1 mois par seconde",
        _ => $"{daysPerSecond / 30f:0} mois par seconde",
    };

    /// <summary>Vitesse mesurée de l'horloge climatique, en heures ou jours par seconde.</summary>
    private static string EffectiveClimateSpeed(double daysPerSecond) => daysPerSecond < 1.0
        ? $"{daysPerSecond * 24.0:0.0} h/s".Replace('.', ',')
        : $"{daysPerSecond:0.0} jours/s".Replace('.', ',');

    private static string SignedDegrees(float radians)
    {
        float degrees = Mathf.RadToDeg(radians);
        return (degrees >= 0f ? "+" : "") + degrees.ToString("0.0").Replace('.', ',') + "°";
    }

    private string ReliefLabel()
    {
        float exaggeration = ReliefPresets[_reliefIndex];
        return exaggeration <= 0f ? "désactivé" : $"exagéré ×{exaggeration:0}";
    }

    /// <summary>Nombre à une décimale avec la virgule française.</summary>
    private static string French(double value) => value.ToString("0.0").Replace('.', ',');

    // --- Entrées -----------------------------------------------------------------------------

    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventKey { Pressed: true, Echo: false } key:
                HandleKey(key);
                break;

            case InputEventMouseMotion motion when (motion.ButtonMask & MouseButtonMask.Left) != 0:
                _yaw -= motion.Relative.X * RotationSpeed;
                // On bloque l'inclinaison juste avant les pôles pour ne pas retourner la caméra
                _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * RotationSpeed, -1.5f, 1.5f);
                _cameraPivot.Rotation = new Vector3(_pitch, _yaw, 0f);
                break;

            case InputEventMouseButton { Pressed: true } button:
                if (button.ButtonIndex == MouseButton.WheelUp)
                    _distance = Mathf.Max(MinDistance, _distance * 0.9f);
                else if (button.ButtonIndex == MouseButton.WheelDown)
                    _distance = Mathf.Min(MaxDistance, _distance * 1.1f);
                _camera.Position = new Vector3(0f, 0f, _distance);
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
            case Key.Key1 or Key.Kp1: SetDisplayMode(DisplayMode.Elevation); break;
            case Key.Key2 or Key.Kp2: SetDisplayMode(DisplayMode.Plates); break;
            case Key.Key3 or Key.Kp3: SetDisplayMode(DisplayMode.Boundaries); break;
            case Key.Key4 or Key.Kp4: SetDisplayMode(DisplayMode.CrustAge); break;
            case Key.Key5 or Key.Kp5:
                CycleDisplayMode(DisplayMode.InstantInsolation, DisplayMode.DailyInsolation);
                break;
            case Key.Key6 or Key.Kp6: CycleDisplayMode(DisplayMode.Temperature); break;
            case Key.Key7 or Key.Kp7: CycleDisplayMode(DisplayMode.WindSpeed, DisplayMode.Pressure); break;
            case Key.Key8 or Key.Kp8:
                CycleDisplayMode(DisplayMode.RecentPrecipitation, DisplayMode.AnnualPrecipitation, DisplayMode.Humidity);
                break;
            case Key.Key9 or Key.Kp9: CycleDisplayMode(DisplayMode.Biomes); break;
            case Key.Key0 or Key.Kp0: CycleDisplayMode(DisplayMode.Realistic); break;
            case Key.F:
                _showRivers = !_showRivers;
                UpdateClimateOverlays(0);
                break;
            case Key.C:
                SetClock(_clock == ClockMode.Geological ? ClockMode.Climate : ClockMode.Geological);
                break;
            case Key.V:
                if (_clock == ClockMode.Geological)
                {
                    _arrowsMesh.Visible = !_arrowsMesh.Visible;
                    if (_arrowsMesh.Visible)
                        RebuildArrows();
                }
                else
                {
                    _showWinds = !_showWinds;
                    _windRebuildTimer = 0;
                    UpdateClimateOverlays(0);
                }
                break;
            case Key.E:
                _reliefIndex = (_reliefIndex + 1) % ReliefPresets.Length;
                _colorLayer.SetReliefExaggeration(ReliefPresets[_reliefIndex]);
                if (_arrowsMesh.Visible)
                    RebuildArrows();
                _riversVersion = -1;
                _windRebuildTimer = 0;
                UpdateClimateOverlays(0);
                break;
            case Key.Space:
                _running = !_running;
                break;
            case Key.R:
                Seed++;
                GeneratePlanet();
                break;
        }
    }

    private void ChangeSpeed(int direction)
    {
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
        RefreshDisplay();
    }

    /// <summary>Une touche, plusieurs variantes : appuyer de nouveau passe à la suivante.</summary>
    private void CycleDisplayMode(params DisplayMode[] modes)
    {
        int index = Array.IndexOf(modes, _displayMode);
        SetDisplayMode(modes[(index + 1) % modes.Length]);
    }
}
