using System;
using System.Linq;
using Godot;
using Planet.Rendering;
using Planet.Simulation;
using Planet.Simulation.Climate;
using Planet.Simulation.Tectonics;
using Planet.Ui;

namespace Planet;

/// <summary>
/// Branchement de l'interface (<see cref="GameUi"/>) sur la simulation : c'est ici qu'on inscrit
/// les paramètres réglables, les couches, les lignes de la sonde et les courbes. Chaque étape à
/// venir (milieux volcaniques, chimie, vie) ajoutera ses inscriptions ici, sans toucher aux
/// panneaux.
/// <para>Régler un paramètre ne recrée jamais la planète : la simulation continue avec la
/// nouvelle valeur, et l'on en voit l'effet sur les courbes.</para>
/// </summary>
public partial class Main
{
    private const double SeriesSampleSeconds = 0.5;

    private GameUi _ui;
    private int _probeCell = -1;
    private bool _probePinned;
    private Vector2 _pressPosition;
    private double _seriesTimer;
    private bool _hudCompact;

    /// <summary>Effet de serre ajouté par le joueur (W/m², positif : réchauffe).</summary>
    public float GreenhouseForcingWm2 { get; set; }

    private bool UiShown => _ui != null && _ui.Shown;

    private void SetupUi()
    {
        _ui = new GameUi { Name = "Ui" };
        AddChild(_ui);
        _ui.Setup(new UiHost
        {
            IsRunning = () => _running,
            TogglePause = TogglePause,
            ChangeSpeed = ChangeSpeed,
            SpeedLabel = () => _clock == ClockMode.Geological
                ? (_fastForward ? "avance rapide" : $"{French(GeologicalSpeeds[_geologicalSpeed])} Ma/s")
                : ClimateSpeedLabel(ClimateSpeeds[_climateSpeed]),
            ClockLabel = () => _clock == ClockMode.Geological ? "Géologie" : "Climat (géologie figée)",
            ToggleClock = () => { if (_clock == ClockMode.Geological) RequestClimate(); else RequestGeology(); },
            TimeLabel = TimeStamp,
            StatusLabel = StatusLabel,
            QualityNames = QualityLevels.Select(l => "Qualité " + l.Label.Split(' ')[0]).ToArray(),
            GetQuality = () => _quality,
            SetQuality = index => { _quality = index; ApplyQuality(); },
            ProbeCell = () => ProbeCell,
            ProbeTitle = () => ProbeTitle(ProbeCell),
            ProbePinned = () => _probePinned,
            ReleaseProbe = () => _probePinned = false,
            ToggleHelp = () => _showHelp = !_showHelp,
        });
        RegisterParameters();
        RegisterLayers();
        RegisterProbe();
        RegisterSeries();
        RegisterHabitats();
        RegisterCrust();
        RegisterChemistry();
    }

    private void TogglePause()
    {
        if (_gameOver)
            ShowNotice("L'étoile est morte : R pour une nouvelle planète, ou réglez la masse de l'étoile");
        else
            _running = !_running;
    }

    /// <summary>Date du moment dans l'horloge en cours (barre du haut, journal).</summary>
    private string TimeStamp() => _tectonics == null ? ""
        : _clock == ClockMode.Geological ? AgeLabel(_tectonics.TimeMyr) : DateLabel(_climate.TimeDays);

    private string StatusLabel()
    {
        var explorer = _terrain.Explorer;
        Vector3 p = explorer.Position.Normalized().ToVector3();
        float latitude = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(p.Y, -1f, 1f)));
        float longitude = Mathf.RadToDeg(Mathf.Atan2(-p.Z, p.X));
        return $"{Math.Abs(latitude):0.0}° {(latitude >= 0 ? "N" : "S")}  {Math.Abs(longitude):0.0}° {(longitude >= 0 ? "E" : "O")}   " +
               $"{Distance(explorer.AltitudeAboveGround)}   {Engine.GetFramesPerSecond():0} img/s";
    }

    private void UpdateUi(double delta)
    {
        EnsureHabitats(delta);
        if (_running)
        {
            _seriesTimer -= delta;
            if (_seriesTimer <= 0)
            {
                _seriesTimer = SeriesSampleSeconds;
                _ui.Registry.SampleSeries();
            }
        }
        // Texte d'aide et de détails : sous la barre, à droite du panneau des couches
        // (en plus petit et sur la largeur libre entre les deux panneaux)
        bool withUi = UiShown;
        if (withUi != _hudCompact)
        {
            _hudCompact = withUi;
            _hud.AddThemeFontSizeOverride("font_size", withUi ? 12 : 16);
            _hud.AutowrapMode = withUi ? TextServer.AutowrapMode.WordSmart : TextServer.AutowrapMode.Off;
        }
        _hud.Position = withUi ? new Vector2(190f, 50f) : new Vector2(16f, 12f);
        if (withUi)
            _hud.Size = new Vector2(Math.Max(200f, GetViewport().GetVisibleRect().Size.X - 190f - 300f), 0f);
    }

    // --- Paramètres --------------------------------------------------------------------------------

    /// <summary>L'orbite réglée s'applique tout de suite au climat en cours (la planète est conservée).</summary>
    private void ApplyOrbitNow()
    {
        if (_tectonics == null)
            return;
        ApplyOrbit(_orbit, _tectonics.TimeMyr);
        // Horloge géologique : le climat moyen (érosion, glaciations) est réestimé sans attendre
        _nextEstimateMyr = _tectonics.TimeMyr;
    }

    /// <summary>Change la masse de l'étoile sans toucher à la planète : durée de vie, lumière et année suivent.</summary>
    private void SetStarMass(float mass)
    {
        StarMass = mass;
        _star = new Star(mass);
        if (_tectonics == null)
            return;
        ApplyOrbitNow();
        StarPhase phase = _star.PhaseAt(_tectonics.TimeMyr / 1000.0);
        if (phase != StarPhase.Dead)
        {
            _gameOver = false;
            _starPhase = phase;
        }
        else
            CheckStar();
    }

    private void RegisterParameters()
    {
        var registry = _ui.Registry;
        registry.Add(new SimParameter
        {
            Id = "star.mass", Group = "Étoile", Label = "Masse", Unit = "M☉", Min = 0.7, Max = 1.5, Step = 0.05, Reference = 1, Format = "0.00",
            Help = "Masse de l'étoile (Soleil = 1). Une étoile massive brille plus mais vit moins longtemps : 10 milliards d'années × M^−2,5.",
            Get = () => StarMass, Set = v => SetStarMass((float)v),
        });
        registry.Add(new SimParameter
        {
            Id = "star.insolation", Group = "Étoile", Label = "Lumière reçue", Unit = "×", Min = 0.6, Max = 1.4, Step = 0.01, Reference = 1, Format = "0.00",
            Help = "Lumière reçue par la planète, par rapport à celle de la Terre (planète plus près ou plus loin de son étoile).",
            Get = () => InsolationFactor, Set = v => { InsolationFactor = (float)v; ApplyOrbitNow(); },
        });
        registry.Add(new SimParameter
        {
            Id = "orbit.tilt", Group = "Orbite", Label = "Inclinaison de l'axe", Unit = "°", Min = 0, Max = 60, Step = 0.5, Reference = EarthAxialTiltDeg, Format = "0.0",
            Help = "Inclinaison moyenne de l'axe de rotation : elle fait les saisons (0° : aucune ; forte : étés et hivers extrêmes).",
            Get = () => AxialTiltDeg, Set = v => { AxialTiltDeg = (float)v; ApplyOrbitNow(); },
        });
        registry.Add(new SimParameter
        {
            Id = "orbit.eccentricity", Group = "Orbite", Label = "Excentricité", Min = 0, Max = 0.4, Step = 0.005, Reference = EarthEccentricity, Format = "0.000",
            Help = "Allongement de l'orbite (0 : cercle). Une orbite allongée rapproche puis éloigne la planète de son étoile dans l'année.",
            Get = () => Eccentricity, Set = v => { Eccentricity = (float)v; ApplyOrbitNow(); },
        });
        registry.Add(new SimParameter
        {
            Id = "air.greenhouse", Group = "Atmosphère", Label = "Effet de serre ajouté", Unit = "W/m²", Min = -30, Max = 30, Step = 0.5, Reference = 0, Format = "0.0",
            Help = "Chaleur retenue en plus (ou en moins) par l'atmosphère. Doubler le gaz carbonique ajoute ~3,7 W/m². Agit dans l'horloge climatique.",
            Get = () => GreenhouseForcingWm2, Set = v => GreenhouseForcingWm2 = (float)v,
        });
        registry.Add(new SimParameter
        {
            Id = "volcano.activity", Group = "Volcanisme", Label = "Fréquence des éruptions", Unit = "×", Min = 0, Max = 5, Step = 0.1, Reference = 1, Format = "0.0",
            Help = "Fréquence des éruptions par rapport à la Terre (0 : volcans endormis). Agit dans l'horloge climatique.",
            Get = () => _activity.ActivityFactor, Set = v => _activity.ActivityFactor = v,
        });

        registry.Add(new SimToggle { Group = "Affichage", Label = "Nuages", Help = "Touche N", Get = () => !_hideClouds, Set = v => _hideClouds = !v });
        registry.Add(new SimToggle { Group = "Affichage", Label = "Eau", Help = "Masquée : on voit les fonds marins (touche U)", Get = () => _terrain.ShowWater, Set = v => _terrain.ShowWater = v });
        registry.Add(new SimToggle { Group = "Affichage", Label = "Fleuves (vue de haut)", Help = "Touche F", Get = () => _showRivers, Set = v => _showRivers = v });
        registry.Add(new SimToggle
        {
            Group = "Affichage", Label = "Vents (climat, vue de haut)", Help = "Touche V dans l'horloge climatique",
            Get = () => _showWinds, Set = v => { _showWinds = v; _windRebuildTimer = 0; },
        });
        registry.Add(new SimToggle
        {
            Group = "Affichage", Label = "Mouvement des plaques (géologie)", Help = "Touche V dans l'horloge géologique",
            Get = () => _showArrows, Set = v => { _showArrows = v; _arrowsDirty = true; },
        });
        registry.Add(new SimToggle { Group = "Affichage", Label = "Son", Help = "Touche K", Get = () => !_muted, Set = v => _muted = !v });
        registry.Add(new SimToggle { Group = "Affichage", Label = "Détails de la simulation", Help = "Touche I", Get = () => _showDetails, Set = v => _showDetails = v });
    }

    // --- Couches -------------------------------------------------------------------------------------

    private void RegisterLayers()
    {
        void Layer(string group, string label, DisplayMode mode, string help = "") => _ui.Registry.Add(new LayerEntry
        {
            Group = group, Label = label, Help = help,
            IsActive = () => _displayMode == mode,
            Activate = () => SetDisplayMode(mode),
        });
        Layer("Planète", "Vue réaliste", DisplayMode.Realistic, "Sol, végétation, neige et eau comme on les verrait");
        Layer("Planète", "Géologie", DisplayMode.Geology, "Roches et relief, sans végétation");
        Layer("Planète", "Altitude", DisplayMode.Elevation);
        Layer("Planète", "Plaques", DisplayMode.Plates);
        Layer("Planète", "Frontières de plaques", DisplayMode.Boundaries, "Rouge : convergente ; bleu : divergente ; jaune : transformante");
        Layer("Planète", "Âge des fonds marins", DisplayMode.CrustAge);
        const string climate = "Passe dans l'horloge climatique (géologie figée pour de bon, après confirmation)";
        Layer("Climat", "Ensoleillement", DisplayMode.InstantInsolation);
        Layer("Climat", "Ensoleillement sur 24 h", DisplayMode.DailyInsolation);
        Layer("Climat", "Température", DisplayMode.Temperature, climate);
        Layer("Climat", "Vent", DisplayMode.WindSpeed, climate);
        Layer("Climat", "Pression", DisplayMode.Pressure, climate);
        Layer("Climat", "Pluie récente", DisplayMode.RecentPrecipitation, climate);
        Layer("Climat", "Pluie annuelle", DisplayMode.AnnualPrecipitation, climate);
        Layer("Climat", "Humidité", DisplayMode.Humidity, climate);
        Layer("Climat", "Biomes", DisplayMode.Biomes, climate);
        Layer("Océan", "Courants", DisplayMode.OceanCurrents, climate);
        Layer("Océan", "Remontées d'eau froide", DisplayMode.Upwelling, climate);
    }

    // --- Sonde -----------------------------------------------------------------------------------------

    /// <summary>Cellule sondée : celle qu'on a épinglée d'un clic, sinon celle sous le pointeur.</summary>
    private int ProbeCell => _probePinned ? _probeCell : (_ui.PointerOverUi ? -1 : _hoverCell);

    private string ProbeTitle(int cell)
    {
        Vector3 p = _grid.Positions[cell];
        float latitude = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(p.Y, -1f, 1f)));
        float longitude = Mathf.RadToDeg(Mathf.Atan2(-p.Z, p.X));
        return $"{Math.Abs(latitude):0}° {(latitude >= 0 ? "N" : "S")}, {Math.Abs(longitude):0}° {(longitude >= 0 ? "E" : "O")}" +
               (_probePinned ? " (épinglée)" : "");
    }

    private void RegisterProbe()
    {
        bool Climate() => _clock == ClockMode.Climate && _climate.HasTemperature;
        bool Land(int c) => !_climate.Model.IsOcean(c);
        void Field(string label, Func<int, string> read) => _ui.Registry.Add(new ProbeField { Label = label, Read = read });

        Field("Altitude", c => $"{_tectonics.CellElevation[c] * 1000f:N0} m ({(_tectonics.CellElevation[c] > 0 ? "terre" : "océan")})");
        Field("Plaque", c => $"n° {_tectonics.CellPlate[c] + 1}, {French(_tectonics.VelocityAt(c).Length() * 100f)} cm/an");
        Field("Volcan", c =>
        {
            // Volcan actif le plus proche, à moins de 300 km
            Vector3 p = _grid.Positions[c];
            float cosRange = MathF.Cos(300f / (float)PlanetConstants.RadiusKm);
            Landform best = null;
            foreach (var l in _tectonics.Landforms)
                if (l.Active && l.Kind != LandformKind.ImpactCrater && l.Position.Dot(p) > cosRange
                    && (best == null || l.Position.Dot(p) > best.Position.Dot(p)))
                    best = l;
            if (best == null)
                return null;
            string kind = best.Kind == LandformKind.ShieldVolcano ? "bouclier" : "stratovolcan";
            return $"{kind} actif à {best.Position.AngleTo(p) * PlanetConstants.RadiusKm:N0} km";
        });
        Field("Température", c => Climate()
            ? $"{French(_climate.SurfaceTemperature[c])} °C (année : {French(_climate.Statistics.ColdestMonth[c])} à {French(_climate.Statistics.WarmestMonth[c])} °C)"
            : null);
        Field("Pluie", c => Climate() ? $"{_climate.Statistics.AnnualPrecipitation[c]:N0} mm/an" : null);
        Field("Vent", c => Climate() ? $"{French(_climate.Winds.Wind[c].Length())} m/s, {_climate.Winds.Pressure[c]:0} hPa" : null);
        Field("Milieu", c => Climate() && Land(c) ? Biomes.Name(_climate.Biomes[c]) : null);
        Field("Neige", c => Climate() && Land(c) && _climate.Snow[c] > 1f ? $"{_climate.Snow[c]:N0} mm d'eau" : null);
        Field("Eau douce", c =>
        {
            var hydrology = _climate.Hydrology;
            if (!Climate() || !Land(c) || hydrology == null)
                return null;
            if (hydrology.Lake[c])
                return "lac";
            return hydrology.Discharge[c] > HydrologyModel.RiverThreshold ? $"fleuve, {hydrology.Discharge[c]:N0} m³/s" : null;
        });
    }

    // --- Courbes ---------------------------------------------------------------------------------------

    private void RegisterSeries()
    {
        var registry = _ui.Registry;
        registry.Add(new TimeSeries
        {
            Label = "Température moyenne", Unit = "°C", Color = (1f, 0.55f, 0.35f),
            Sample = () => _clock == ClockMode.Climate && _climate.HasTemperature ? _climate.GlobalMeanTemperature : _estimatedMeanTemperature,
        });
        registry.Add(new TimeSeries
        {
            Label = "Lumière de l'étoile", Unit = "×", Format = "0.000", Color = (1f, 0.9f, 0.4f),
            Sample = () => _tectonics == null ? null : _star.EffectiveIrradiance(_tectonics.TimeMyr / 1000.0) * InsolationFactor,
        });
        registry.Add(new TimeSeries
        {
            Label = "Terres émergées", Unit = "%", Color = (0.55f, 0.85f, 0.45f),
            Sample = () => _tectonics == null ? null : 100.0 * _tectonics.CellElevation.Count(e => e > 0) / _tectonics.CellElevation.Length,
        });
        registry.Add(new TimeSeries
        {
            Label = "Glaces", Unit = "%", Format = "0", Color = (0.75f, 0.9f, 1f),
            Sample = () => _glacial.IceFraction * 100.0,
        });
        registry.Add(new TimeSeries
        {
            Label = "Niveau de la mer", Unit = "m", Format = "0", Color = (0.4f, 0.65f, 1f),
            Sample = () => _glacial.SeaLevelM,
        });
        registry.Add(new TimeSeries
        {
            Label = "Éruptions en cours", Format = "0", Color = (1f, 0.35f, 0.3f), Shown = false,
            Sample = () => _clock == ClockMode.Climate ? _activity.Eruptions.Count : null,
        });
    }
}
