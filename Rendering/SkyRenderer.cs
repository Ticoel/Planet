using System;
using Environment = Godot.Environment;
using Godot;
using Planet.Simulation;
using Planet.Simulation.Climate;
using Planet.Simulation.Terrain;

namespace Planet.Rendering;

/// <summary>Ce dont le ciel a besoin à chaque image.</summary>
public struct SkyFrame
{
    /// <summary>Position de la caméra (m, repère de la planète).</summary>
    public Double3 CameraPosition;
    /// <summary>Hauteur de la caméra au-dessus du sol (m).</summary>
    public double AltitudeAboveGround;
    /// <summary>Direction du Soleil (repère de la planète).</summary>
    public Vector3 SunDirection;
    /// <summary>Lune (null : pas de Lune, horloge géologique).</summary>
    public MoonState? Moon;
    /// <summary>Rotation de la planète par rapport aux étoiles (radians).</summary>
    public double StarAngle;
    /// <summary>Temps simulé (s) qui fait dériver les nuages.</summary>
    public double CloudTime;
    /// <summary>Climat de la cellule sous la caméra (null : pas de climat simulé).</summary>
    public LocalWeather? Weather;
}

/// <summary>Climat du moment à l'endroit de la caméra (étape 10d).</summary>
public struct LocalWeather
{
    /// <summary>Précipitations (mm/jour).</summary>
    public float PrecipitationMmPerDay;
    /// <summary>Température de surface (°C).</summary>
    public float TemperatureC;
    /// <summary>Humidité relative (0 à 1).</summary>
    public float RelativeHumidity;
    /// <summary>Vent de surface (m/s, repère de la planète).</summary>
    public Vector3 Wind;
    /// <summary>Sécheresse du sol (0 : humide, 1 : désert).</summary>
    public float Aridity;
    /// <summary>Heure solaire locale (0 à 24).</summary>
    public float SolarHour;
}

/// <summary>
/// Ciel vivant (étape 10) :
/// <list type="bullet">
/// <item>ciel calculé par diffusion de la lumière (bleu du ciel, couchers rouges, halo vu de l'espace),
/// Soleil, Lune avec ses phases, étoiles et Voie lactée qui tournent avec la planète ;</item>
/// <item>passe plein écran après le relief : perspective atmosphérique, nuages en volume, brume et
/// poussière près du sol ;</item>
/// <item>pluie et neige autour de la caméra.</item>
/// </list>
/// Tout est relatif à la caméra (au centre du monde), la planète étant centrée en −caméra.
/// </summary>
public sealed partial class SkyRenderer : Node3D
{
    // Résolution des textures de couverture nuageuse (pixels par côté de face du cube)
    private const int CloudResolution = 256;
    /// <summary>
    /// Le temps des nuages (s) revient à zéro tous les 16 motifs météo de 100 000 s (shader :
    /// WEATHER_PERIOD × WEATHER_CYCLES) : en float, de plus grands nombres perdraient en précision.
    /// </summary>
    public const double CloudTimeWrapSeconds = 1.6e6;
    private const float MoonVisibilityBoost = 2.5f;   // l'œil s'habitue à la nuit
    // Intensité du Soleil pour la perspective atmosphérique (voir ShaderLibrary.Atmosphere)
    private const float AerialPerspectiveIntensity = 10f;

    public DirectionalLight3D Sun { get; private set; }
    public Environment Environment { get; private set; }
    /// <summary>Couverture nuageuse par face du cube (r : couverture, g : averses).</summary>
    public Texture2DArray CloudCover { get; private set; }
    public NoiseTexture3D CloudNoise { get; private set; }
    public bool HasClouds { get; private set; }

    private Camera3D _camera;
    private ShaderMaterial _skyMaterial;
    private ShaderMaterial _postMaterial;
    private MeshInstance3D _post;
    private Image[] _cloudImages;
    private byte[] _cloudPixels;
    private int[][] _cellIndex;
    private int _cellIndexResolution;
    private Node3D _weatherAnchor;
    private GpuParticles3D _rain, _snow, _dust;
    // Matériau et couleur de base de chaque sorte de particules : la couleur est assombrie selon
    // la lumière du moment (sinon, sans éclairage, elles brillaient en pleine nuit)
    private readonly System.Collections.Generic.List<(Material material, Color color)> _particleLooks = new();

    /// <summary>
    /// Crée le ciel. <paramref name="cellIndex"/> : numéro de la cellule la plus proche de
    /// chaque pixel des 6 faces du cube (voir TerrainRenderer.SetGrid), pour la couverture nuageuse.
    /// </summary>
    public void Setup(Camera3D camera, int[][] cellIndex, int cellIndexResolution)
    {
        _camera = camera;
        _cellIndex = cellIndex;
        _cellIndexResolution = cellIndexResolution;

        Sun = new DirectionalLight3D { Name = "Sun", LightEnergy = 1.0f, ShadowEnabled = false };
        AddChild(Sun);

        _skyMaterial = new ShaderMaterial { Shader = new Shader { Code = SkyShaderCode } };
        var sky = new Sky { SkyMaterial = _skyMaterial, ProcessMode = Sky.ProcessModeEnum.Incremental, RadianceSize = Sky.RadianceSizeEnum.Size64 };
        Environment = new Environment
        {
            BackgroundMode = Environment.BGMode.Sky,
            Sky = sky,
            AmbientLightSource = Environment.AmbientSource.Color,
            AmbientLightColor = Colors.White,
            AmbientLightEnergy = 0.2f,
            ReflectedLightSource = Environment.ReflectionSource.Sky,
        };
        AddChild(new WorldEnvironment { Name = "WorldEnvironment", Environment = Environment });

        // Couverture nuageuse
        _cloudImages = new Image[CubeSphere.FaceCount];
        _cloudPixels = new byte[CloudResolution * CloudResolution * 2];
        var images = new Godot.Collections.Array<Image>();
        for (int face = 0; face < CubeSphere.FaceCount; face++)
        {
            _cloudImages[face] = Image.CreateFromData(CloudResolution, CloudResolution, false, Image.Format.Rg8, _cloudPixels);
            images.Add(_cloudImages[face]);
        }
        CloudCover = new Texture2DArray();
        CloudCover.CreateFromImages(images);
        CloudNoise = new NoiseTexture3D
        {
            Width = 128, Height = 128, Depth = 128, Seamless = true, Normalize = true,
            Noise = new FastNoiseLite
            {
                NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
                Frequency = 4.0f / 128.0f,
                FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
                // Deux octaves seulement : la texture n'a pas de niveaux de détail (mipmaps), ses
                // détails les plus fins scintilleraient vus de loin
                FractalOctaves = 2,
            },
        };

        // Passe plein écran : un quadrilatère collé à l'écran par le shader, dessiné en dernier
        _postMaterial = new ShaderMaterial { Shader = new Shader { Code = PostShaderCode }, RenderPriority = 127 };
        _postMaterial.SetShaderParameter("cloud_cover", CloudCover);
        _postMaterial.SetShaderParameter("cloud_noise", CloudNoise);
        _postMaterial.SetShaderParameter("sun_intensity", AerialPerspectiveIntensity);
        _post = new MeshInstance3D
        {
            Name = "AtmospherePass",
            Mesh = new QuadMesh { Size = new Vector2(2, 2) },
            MaterialOverride = _postMaterial,
            ExtraCullMargin = 16384,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        camera.AddChild(_post);
        _post.Position = new Vector3(0, 0, -1);

        SetupWeather();
    }

    // --- Nuages ----------------------------------------------------------------------------------

    /// <summary>
    /// Couverture nuageuse de chaque cellule d'après le climat du moment : air proche de la
    /// saturation, pluie récente, convergence du vent (l'air monte et se condense).
    /// </summary>
    public static (float[] cover, float[] showers) ComputeCloudCover(ClimateSimulation climate) =>
        ComputeCloudCover(climate.Grid, climate.RelativeHumidity, climate.Statistics.RecentPrecipitation, climate.Winds.Convergence);

    /// <summary>Même calcul à partir de copies des champs du climat (utilisable en tâche de fond).</summary>
    public static (float[] cover, float[] showers) ComputeCloudCover(PlanetGrid grid, float[] relativeHumidity, float[] rain, float[] convergence)
    {
        int count = grid.CellCount;
        var cover = new float[count];
        var showers = new float[count];
        for (int c = 0; c < count; c++)
        {
            // (moins couvrant qu'avant : le shader y ajoute le « temps qu'il fait », qui découpe
            // la couche en systèmes nuageux et en éclaircies ; avec une couverture proche de 1,
            // il n'y avait plus que des anneaux continus)
            float humidity = SmoothStep(0.6f, 0.97f, relativeHumidity[c]);
            float wet = SmoothStep(0.3f, 6f, rain[c]);
            float rising = SmoothStep(0f, 6e-6f, convergence[c]);
            cover[c] = Math.Clamp(0.6f * humidity + 0.45f * wet + 0.2f * rising, 0f, 1f);
            showers[c] = SmoothStep(3f, 25f, rain[c]);
        }
        // Lissage entre cellules voisines (deux passes) : contours moins anguleux
        Smooth(grid, cover);
        Smooth(grid, cover);
        Smooth(grid, showers);
        return (cover, showers);
    }

    private static void BoxBlur(byte[] pixels, int n)
    {
        var copy = (byte[])pixels.Clone();
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
                for (int channel = 0; channel < 2; channel++)
                {
                    int sum = 0, count = 0;
                    for (int dj = -1; dj <= 1; dj++)
                        for (int di = -1; di <= 1; di++)
                        {
                            int x = i + di, y = j + dj;
                            if (x < 0 || y < 0 || x >= n || y >= n)
                                continue;
                            sum += copy[(y * n + x) * 2 + channel];
                            count++;
                        }
                    pixels[(j * n + i) * 2 + channel] = (byte)(sum / count);
                }
    }

    public static void Smooth(PlanetGrid grid, float[] values)
    {
        var copy = (float[])values.Clone();
        for (int c = 0; c < values.Length; c++)
        {
            float sum = 0;
            int[] neighbors = grid.Neighbors[c];
            foreach (int n in neighbors)
                sum += copy[n];
            values[c] = 0.5f * copy[c] + 0.5f * sum / neighbors.Length;
        }
    }

    /// <summary>Met à jour la texture des nuages (null : pas de nuages, horloge géologique).</summary>
    public void UpdateCloudCover(float[] cover, float[] showers)
    {
        if (cover == null)
        {
            HasClouds = false;
            return;
        }
        ApplyCloudPixels(BuildCloudPixels(cover, showers));
    }

    /// <summary>
    /// Pixels des 6 faces de la texture des nuages (sans appel à Godot : utilisable en tâche de fond).
    /// </summary>
    public byte[][] BuildCloudPixels(float[] cover, float[] showers)
    {
        int n = CloudResolution, step = _cellIndexResolution / n;
        var faces = new byte[CubeSphere.FaceCount][];
        for (int face = 0; face < CubeSphere.FaceCount; face++)
        {
            var pixels = new byte[n * n * 2];
            int[] index = _cellIndex[face];
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int cell = index[(j * step + step / 2) * _cellIndexResolution + i * step + step / 2];
                    int o = (j * n + i) * 2;
                    pixels[o] = (byte)(Math.Clamp(cover[cell], 0f, 1f) * 255f);
                    pixels[o + 1] = (byte)(Math.Clamp(showers[cell], 0f, 1f) * 255f);
                }
            // Flou (deux passes 3×3) : chaque pixel prenait la valeur de la cellule la plus proche,
            // les contours des nuages suivaient les hexagones de la grille
            BoxBlur(pixels, n);
            BoxBlur(pixels, n);
            faces[face] = pixels;
        }
        return faces;
    }

    /// <summary>Envoie les pixels préparés à la carte graphique (fil principal).</summary>
    public void ApplyCloudPixels(byte[][] faces)
    {
        int n = CloudResolution;
        for (int face = 0; face < CubeSphere.FaceCount; face++)
        {
            _cloudImages[face].SetData(n, n, false, Image.Format.Rg8, faces[face]);
            CloudCover.UpdateLayer(_cloudImages[face], face);
        }
        HasClouds = true;
    }

    // --- Image ------------------------------------------------------------------------------------

    public void Update(in SkyFrame frame, double delta)
    {
        Double3 camera = frame.CameraPosition;
        Vector3 up = camera.Normalized().ToVector3();
        float altitude = (float)(camera.Length() - TerrainHeightField.RadiusMeters);
        Vector3 sun = frame.SunDirection.Normalized();

        Vector3 reference = Mathf.Abs(sun.Y) > 0.99f ? Vector3.Right : Vector3.Up;
        // La lumière éclaire selon son axe −Z : on la fait regarder du Soleil vers la planète
        Sun.Basis = Basis.LookingAt(-sun, reference);

        // Lune : direction vue de la caméra (parallaxe : la Lune n'est qu'à 60 rayons)
        bool hasMoon = frame.Moon.HasValue;
        Vector3 moonDirection = Vector3.Up;
        float moonAngle = 0, moonLight = 0;
        if (hasMoon)
        {
            MoonState moon = frame.Moon.Value;
            Double3 moonPosition = Double3.From(moon.Direction) * (moon.DistanceKm * 1000.0);
            Double3 toMoon = moonPosition - camera;
            moonDirection = toMoon.Normalized().ToVector3();
            moonAngle = (float)Math.Asin(MoonOrbit.RadiusKm * 1000.0 / toMoon.Length());
            moonLight = moon.IlluminatedFraction;
        }

        foreach (var material in new[] { _skyMaterial, _postMaterial })
        {
            material.SetShaderParameter("local_up", up);
            material.SetShaderParameter("camera_altitude", altitude);
            material.SetShaderParameter("sun_direction", sun);
        }
        _skyMaterial.SetShaderParameter("moon_visible", hasMoon);
        _skyMaterial.SetShaderParameter("moon_direction", moonDirection);
        _skyMaterial.SetShaderParameter("moon_angle", moonAngle);
        _skyMaterial.SetShaderParameter("moon_boost", MoonVisibilityBoost);
        _skyMaterial.SetShaderParameter("star_angle", (float)(frame.StarAngle % (2 * Math.PI)));
        _postMaterial.SetShaderParameter("camera_planet", camera.ToVector3());
        float viewportHeight = Math.Max(GetViewport().GetVisibleRect().Size.Y, 1f);
        _postMaterial.SetShaderParameter("pixel_angle", 2f * Mathf.Tan(Mathf.DegToRad(_camera.Fov) / 2f) / viewportHeight);
        _postMaterial.SetShaderParameter("clouds_enabled", HasClouds);
        _postMaterial.SetShaderParameter("cloud_time", (float)(frame.CloudTime % CloudTimeWrapSeconds));

        // Lumière ambiante : ciel le jour (bleutée, plus faible en altitude et dans l'espace),
        // clair de lune la nuit
        float sunHeight = up.Dot(sun);
        float daylight = Mathf.SmoothStep(-0.1f, 0.2f, sunHeight);
        float inAir = 1f - Mathf.SmoothStep(15000f, 120000f, altitude);
        var dayAmbient = new Color(0.55f, 0.65f, 0.85f) * (0.16f * daylight * inAir + 0.05f * daylight);
        var nightAmbient = new Color(0.55f, 0.65f, 1.0f) * (0.012f + 0.05f * moonLight * (1f - daylight));
        Color ambient = dayAmbient + nightAmbient;
        float energy = Mathf.Max(ambient.R, Mathf.Max(ambient.G, ambient.B));
        Environment.AmbientLightColor = energy > 0 ? ambient / energy : Colors.White;
        Environment.AmbientLightEnergy = energy;

        UpdateWeather(frame, up, sun, delta);
    }

    // --- Météo autour de la caméra (étape 10d) -------------------------------------------------

    private void SetupWeather()
    {
        // Repère local (Y = verticale) centré sur la caméra, au centre du monde
        _weatherAnchor = new Node3D { Name = "Weather" };
        AddChild(_weatherAnchor);
        _rain = CreateParticles("Rain", 9000, 1.2f, new Vector2(0.015f, 0.7f), new Color(0.75f, 0.8f, 0.9f, 0.35f), 0f);
        _snow = CreateParticles("Snow", 7000, 6f, new Vector2(0.06f, 0.06f), new Color(1f, 1f, 1f, 0.85f), 0.6f);
        _dust = CreateParticles("Dust", 6000, 4f, new Vector2(0.08f, 0.08f), new Color(0.78f, 0.62f, 0.42f, 0.35f), 1.5f);
    }

    private GpuParticles3D CreateParticles(string name, int amount, float lifetime, Vector2 size, Color color, float turbulence)
    {
        var process = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(40, 18, 40),
            Gravity = Vector3.Zero,
            Direction = Vector3.Down,
            Spread = 3f,
            InitialVelocityMin = 1f,
            InitialVelocityMax = 1f,
            TurbulenceEnabled = turbulence > 0,
            TurbulenceNoiseStrength = turbulence,
            TurbulenceNoiseScale = 4f,
        };
        // Traits de pluie : orientés selon la vitesse de chaque goutte (le mode « Y fixe » de
        // Godot garde l'axe Y du monde, c'est-à-dire l'axe des pôles : à l'équateur la pluie
        // tombait à l'horizontale). Flocons et poussière : simples points tournés vers la caméra
        bool streak = size.Y > size.X * 2;
        process.ParticleFlagAlignY = streak;
        Material material;
        if (streak)
        {
            var shader = new ShaderMaterial { Shader = new Shader { Code = StreakShaderCode } };
            shader.SetShaderParameter("color", color);
            material = shader;
        }
        else
        {
            material = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                AlbedoColor = color,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                BillboardKeepScale = true,
            };
        }
        _particleLooks.Add((material, color));
        var particles = new GpuParticles3D
        {
            Name = name,
            Amount = amount,
            Lifetime = lifetime,
            Preprocess = lifetime,
            LocalCoords = true,
            Emitting = false,
            ProcessMaterial = process,
            DrawPass1 = new QuadMesh { Size = size, Material = material },
            VisibilityAabb = new Aabb(new Vector3(-60, -40, -60), new Vector3(120, 80, 120)),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        particles.Position = new Vector3(0, 8, 0);
        _weatherAnchor.AddChild(particles);
        return particles;
    }

    private void UpdateWeather(in SkyFrame frame, Vector3 up, Vector3 sun, double delta)
    {
        // Repère local : Y vers le haut, comme la gravité des particules
        Vector3 east = Vector3.Up.Cross(up);
        east = east.LengthSquared() < 1e-6f ? Vector3.Right : east.Normalized();
        Vector3 north = up.Cross(east);
        _weatherAnchor.Basis = new Basis(east, up, -north);

        float rain = 0, snow = 0, dust = 0, mist = 0;
        Vector3 localWind = Vector3.Zero;
        if (frame.Weather.HasValue && frame.AltitudeAboveGround < 2500)
        {
            LocalWeather w = frame.Weather.Value;
            float intensity = SmoothStep(0.5f, 20f, w.PrecipitationMmPerDay);
            float snowShare = SmoothStep(1.5f, -1.5f, w.TemperatureC);
            rain = intensity * (1 - snowShare);
            snow = intensity * snowShare;
            float windSpeed = w.Wind.Length();
            dust = w.Aridity * SmoothStep(7f, 15f, windSpeed) * (1 - intensity);
            // Brouillard matinal : air saturé, vent faible, de la nuit au milieu de matinée
            float morning = SmoothStep(2f, 4f, w.SolarHour) * (1 - SmoothStep(8f, 11f, w.SolarHour));
            mist = SmoothStep(0.88f, 0.98f, w.RelativeHumidity) * (1 - SmoothStep(2f, 5f, windSpeed)) * morning;
            localWind = new Vector3(w.Wind.Dot(east), 0, -w.Wind.Dot(north));
        }
        SetParticles(_rain, rain, localWind + new Vector3(0, -9f, 0));
        SetParticles(_snow, snow, localWind * 0.6f + new Vector3(0, -1.2f, 0));
        SetParticles(_dust, dust, localWind * 0.8f + new Vector3(0, -0.2f, 0));

        // Brume près du sol (passe plein écran) : averses, poussière, brouillard
        float daylight = Mathf.SmoothStep(-0.1f, 0.2f, up.Dot(sun));
        LightParticles(up.Dot(sun), daylight, rain + snow, frame.Moon);
        float density = 0.00025f * rain + 0.0002f * snow + 0.0006f * dust + 0.003f * mist;
        var color = new Color(0.62f, 0.66f, 0.72f);
        if (density > 0)
            color = (new Color(0.62f, 0.66f, 0.72f) * (0.00025f * rain + 0.0002f * snow)
                     + new Color(0.78f, 0.6f, 0.4f) * (0.0006f * dust)
                     + new Color(0.85f, 0.87f, 0.9f) * (0.003f * mist)) / density;
        color *= 0.08f + 0.6f * daylight;
        _postMaterial.SetShaderParameter("fog_density", density);
        _postMaterial.SetShaderParameter("fog_color", new Vector3(color.R, color.G, color.B));
        // Brouillard : nappe mince au fond des vallées ; poussière et averses : plus épaisses
        _postMaterial.SetShaderParameter("fog_height", mist > dust + rain + snow ? 120f : 900f);
        _postMaterial.SetShaderParameter("ground_altitude", (float)(frame.CameraPosition.Length() - TerrainHeightField.RadiusMeters - frame.AltitudeAboveGround));
    }

    /// <summary>
    /// Couleur des particules selon la lumière qui les éclaire : plein jour, Soleil bas (lumière
    /// orangée), ciel couvert sous les averses, nuit (clair de lune selon la phase, sinon presque noir).
    /// </summary>
    private void LightParticles(float sunHeight, float daylight, float precipitation, MoonState? moon)
    {
        float overcast = Math.Clamp(precipitation, 0f, 1f);
        var sunColor = new Color(1f, 0.62f, 0.38f).Lerp(Colors.White, SmoothStep(0.02f, 0.3f, sunHeight));
        float moonlight = moon.HasValue ? 0.015f + 0.06f * moon.Value.IlluminatedFraction : 0.015f;
        Color light = sunColor * (daylight * (1f - 0.45f * overcast)) + new Color(0.55f, 0.65f, 1f) * moonlight * (1f - daylight);
        foreach (var (material, color) in _particleLooks)
        {
            var lit = new Color(color.R * light.R, color.G * light.G, color.B * light.B, color.A);
            if (material is ShaderMaterial shader)
                shader.SetShaderParameter("color", lit);
            else if (material is StandardMaterial3D standard)
                standard.AlbedoColor = lit;
        }
    }

    private static void SetParticles(GpuParticles3D particles, float amount, Vector3 velocity)
    {
        bool on = amount > 0.02f;
        if (particles.Emitting != on)
            particles.Emitting = on;
        if (!on)
            return;
        particles.AmountRatio = Math.Clamp(amount, 0.05f, 1f);
        var process = (ParticleProcessMaterial)particles.ProcessMaterial;
        float speed = velocity.Length();
        process.Direction = speed > 1e-3f ? velocity / speed : Vector3.Down;
        process.InitialVelocityMin = speed * 0.9f;
        process.InitialVelocityMax = speed * 1.1f;
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3 - 2 * t);
    }

    // --- Shaders -----------------------------------------------------------------------------------

    /// <summary>
    /// Trait de pluie : quadrilatère allongé selon l'axe Y de la particule (aligné sur sa vitesse),
    /// tourné autour de cet axe pour faire face à la caméra.
    /// </summary>
    private const string StreakShaderCode = @"
shader_type spatial;
render_mode unshaded, blend_mix, cull_disabled, depth_draw_never, shadows_disabled;
uniform vec4 color : source_color = vec4(0.8, 0.85, 0.9, 0.35);

void vertex() {
    vec3 center = (MODEL_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
    vec3 axis = normalize((MODEL_MATRIX * vec4(0.0, 1.0, 0.0, 0.0)).xyz);
    vec3 to_camera = normalize(CAMERA_POSITION_WORLD - center);
    vec3 side = cross(axis, to_camera);
    side = length(side) > 1e-4 ? normalize(side) : normalize(cross(axis, vec3(0.0, 0.0, 1.0)));
    vec3 world = center + side * VERTEX.x + axis * VERTEX.y;
    POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world, 1.0);
}

void fragment() {
    // Plus transparent aux deux bouts du trait
    float fade = 1.0 - abs(UV.y * 2.0 - 1.0);
    ALBEDO = color.rgb;
    ALPHA = color.a * fade;
}
";

    /// <summary>
    /// Ciel (fond de l'image) : diffusion de la lumière jusqu'au bord de l'atmosphère (ou jusqu'au
    /// sol pour les rayons qui le touchent), disque du Soleil, Lune et ses phases, étoiles.
    /// </summary>
    public static string SkyShaderCode => @"
shader_type sky;
" + ShaderLibrary.CubeFace + ShaderLibrary.Atmosphere + @"
uniform vec3 local_up = vec3(0.0, 1.0, 0.0);
uniform float camera_altitude = 2.0;
uniform vec3 sun_direction = vec3(0.0, 1.0, 0.0);
uniform bool moon_visible = false;
uniform vec3 moon_direction = vec3(0.0, 1.0, 0.0);
uniform float moon_angle = 0.0045;
uniform float moon_boost = 2.5;
uniform float star_angle = 0.0;
// Taille apparente (rayon, radians) et teinte de l'étoile : elle enfle et rougit en géante rouge
uniform float sun_radius = 0.00465;
uniform vec3 sun_tint = vec3(1.0);

float hash12(vec2 p) {
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

// Étoiles : une étoile au plus par case d'une grille posée sur les faces du cube, éclat très
// inégal (beaucoup de faibles, peu de brillantes), plus la Voie lactée (bande diffuse)
vec3 stars(vec3 dir) {
    vec3 uvf = cube_face_uv(dir);
    vec2 cell = floor(uvf.xy * 700.0);
    vec2 key = cell + uvf.z * 1000.0;
    float h = hash12(key);
    vec3 color = vec3(0.0);
    if (h > 0.994) {
        vec2 center = (cell + vec2(hash12(key + 7.1), hash12(key + 3.7))) / 700.0;
        float d = length((uvf.xy - center) * 700.0);
        float brightness = pow(hash12(key + 1.3), 6.0) * 1.5 + 0.03;
        vec3 tint = mix(vec3(1.0, 0.8, 0.65), vec3(0.75, 0.85, 1.0), hash12(key + 9.2));
        color = tint * brightness * exp(-d * d * 6.0);
    }
    // Voie lactée : grand cercle incliné de 60° sur l'équateur céleste
    vec3 galactic = normalize(vec3(0.0, 0.5, 0.866));
    float band = exp(-pow(dot(dir, galactic) / 0.1, 2.0));
    float clouds = 0.6 + 0.4 * sin(dir.x * 13.0 + dir.z * 7.0) * sin(dir.y * 11.0 - dir.x * 5.0);
    color += vec3(0.6, 0.62, 0.7) * 0.008 * band * clouds;
    return color;
}

void sky() {
    vec3 up = normalize(local_up);
    vec3 dir = EYEDIR;
    vec3 sun = normalize(sun_direction);
    vec3 inscatter, transmittance;
    atmosphere(camera_altitude, up, dir, 1e30, sun, inscatter, transmittance);
    vec3 color = inscatter;

    // Le sol cache ce qui est sous l'horizon
    vec2 ground = ray_shell(camera_altitude, dot(dir, up), 0.0);
    bool below = ground.x > 0.0 && ground.y > ground.x;
    if (!below && !AT_CUBEMAP_PASS) {
        // Étoiles, fixes dans l'espace : la planète tourne sous elles
        float c = cos(star_angle), s = sin(star_angle);
        vec3 inertial = vec3(c * dir.x + s * dir.z, dir.y, -s * dir.x + c * dir.z);
        vec3 background = stars(inertial);
        // Lune : sphère éclairée par le Soleil (phases), grisée de mers sombres
        float cos_moon = dot(dir, moon_direction);
        if (moon_visible && cos_moon > cos(moon_angle)) {
            vec3 tu = normalize(cross(moon_direction, abs(moon_direction.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0)));
            vec3 tv = cross(moon_direction, tu);
            vec2 xy = vec2(dot(dir, tu), dot(dir, tv)) / sin(moon_angle);
            float z = sqrt(max(1.0 - dot(xy, xy), 0.0));
            vec3 n = normalize(tu * xy.x + tv * xy.y - moon_direction * z);
            float maria = 0.75 + 0.25 * sin(n.x * 9.0 + 1.3) * sin(n.y * 7.0 - 0.4) * sin(n.z * 8.0 + 2.1);
            float lit = max(dot(n, sun), 0.0);
            background = vec3(0.13, 0.125, 0.12) * maria * (lit + 0.004) * moon_boost;
        }
        // Disque du Soleil (assombri vers le bord)
        float cos_sun = dot(dir, sun);
        if (cos_sun > cos(sun_radius)) {
            float r = acos(clamp(cos_sun, -1.0, 1.0)) / sun_radius;
            background += 60.0 * sun_tint * (1.0 - 0.6 * r * r);
        }
        // Les étoiles et la Lune pâlissent dans un ciel lumineux (l'œil ne les distingue plus)
        float sky_brightness = dot(inscatter, vec3(0.3, 0.5, 0.2));
        background *= mix(1.0, 0.0, smoothstep(0.0, 0.06, sky_brightness)) * 0.85 + 0.15 * step(0.5, dot(background, vec3(1.0)));
        color += background * transmittance;
    }
    COLOR = color;
}
";

    /// <summary>
    /// Passe plein écran, après le relief et l'eau : perspective atmosphérique (la lumière
    /// diffusée entre l'œil et le sol), nuages en volume, brume près du sol. Distance de chaque
    /// pixel lue dans le tampon de profondeur.
    /// </summary>
    /// <summary>Disque de l'étoile : rayon apparent (radians) et teinte.</summary>
    public void SetStar(float angularRadius, Vector3 tint)
    {
        _skyMaterial.SetShaderParameter("sun_radius", angularRadius);
        _skyMaterial.SetShaderParameter("sun_tint", tint);
    }

    /// <summary>Éruptions proches de la caméra, pour les panaches (même format que le terrain).</summary>
    public void SetEruptions(int count, Vector4[] vents, Vector4[] shapes, Vector4[] winds)
    {
        _postMaterial.SetShaderParameter("eruption_count", count);
        _postMaterial.SetShaderParameter("eruption_vents", vents);
        _postMaterial.SetShaderParameter("eruption_shapes", shapes);
        _postMaterial.SetShaderParameter("eruption_winds", winds);
    }

    public static string PostShaderCode => @"
shader_type spatial;
render_mode unshaded, cull_disabled, depth_test_disabled, depth_draw_never, blend_premul_alpha, fog_disabled, shadows_disabled;
" + ShaderLibrary.CubeFace + ShaderLibrary.Atmosphere + ShaderLibrary.Clouds + ShaderLibrary.Plumes + @"
uniform sampler2D depth_texture : hint_depth_texture, filter_nearest;
uniform vec3 local_up = vec3(0.0, 1.0, 0.0);
uniform float camera_altitude = 2.0;
uniform vec3 sun_direction = vec3(0.0, 1.0, 0.0);
// Brume près du sol (averses, poussière, brouillard)
uniform float fog_density = 0.0;
uniform vec3 fog_color = vec3(0.7);
uniform float fog_height = 500.0;
uniform float ground_altitude = 0.0;
// Angle couvert par un pixel (radians), pour estomper les détails trop fins au loin
uniform float pixel_angle = 0.0012;

// Panaches volcaniques, en volume (forme : ShaderLibrary.Plumes) : colonne qui bouillonne,
// ombrelle et longue traînée étirée par le vent.
// Volutes « en chou-fleur » par un bruit à la taille de la forme ; éclairage par le Soleil avec
// ombre propre (deux échantillons vers le Soleil), lueur de la lave à la base, éclairs la nuit.
// Marche adaptative : grands pas dans le vide (d'après la forme sans volutes), petits dedans.
// Renvoie la couleur (prémultipliée) et l'opacité ; t_hit : distance du premier panache touché.

float hash12(vec2 p) {
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

// Densité (0 à 1) au point p
float plume_density(int k, Plume P, vec3 p, float boil, out float feature) {
    float thin, trail, safe;
    float e = plume_envelope(P, p, feature, thin, trail, safe);
    float density = 0.0;
    if (e < 1.45) {
        // Volutes : trois échelles de bruit, qui montent dans la colonne et dérivent avec le vent
        // (échelle des volutes fixe dans la colonne : une échelle qui grandirait avec la hauteur
        // étirerait le bruit en traînées verticales, comme une flamme de chalumeau)
        vec3 drift = (P.up * 8.0 + P.wind * (0.5 * P.speed)) * cloud_time;
        float scale = mix(P.base + 0.1 * P.height, feature, trail);
        vec3 q = (p - P.vent - drift) / scale;
        float n1 = texture(cloud_noise, q * 0.3 + vec3(float(k) * 0.37)).r;
        float n2 = texture(cloud_noise, q * 0.9 + vec3(0.21, boil, 0.43)).r;
        float n3 = texture(cloud_noise, q * 2.7 + vec3(0.77, 0.13, boil)).r;
        float billow = n1 * 0.55 + n2 * 0.3 + n3 * 0.15;
        density = (1.0 - smoothstep(0.35, 0.8, e + (0.5 - billow) * 1.3)) * thin;
    }
    return density;
}

vec4 plumes(vec3 rd, float t_scene, vec3 sun, vec2 pixel, out float t_hit) {
    vec3 color = vec3(0.0);
    float transmittance = 1.0;
    t_hit = 1e30;
    // Décalage du premier pas par pixel (bruit à gradient entrelacé : grain fin et régulier)
    float jitter = fract(52.9829189 * fract(dot(pixel, vec2(0.06711056, 0.00583715))));
    for (int k = 0; k < eruption_count; k++) {
        float intensity = eruption_vents[k].w;
        if (eruption_shapes[k].y <= 0.0 || intensity <= 0.0)
            continue;
        Plume P = plume_params(k);
        float vei = max(eruption_shapes[k].z, 0.0);
        // Boîte englobante dans le repère du panache (vent, côté, verticale)
        vec3 o = camera_planet - P.vent;
        vec3 ol = vec3(dot(o, P.wind), dot(o, P.side), dot(o, P.up));
        vec3 dl = vec3(dot(rd, P.wind), dot(rd, P.side), dot(rd, P.up));
        float half_width = (P.spread + 0.3 * P.len) * 1.3;
        float upwind = max(P.spread, P.base + 0.22 * P.height) * 1.3;
        vec3 box_min = vec3(-upwind, -half_width, -50.0);
        vec3 box_max = vec3(P.x0 + P.len + P.spread, half_width, 1.15 * P.height);
        vec3 inv = 1.0 / (dl + vec3(1e-9));
        vec3 ta = (box_min - ol) * inv, tb = (box_max - ol) * inv;
        vec3 near3 = min(ta, tb), far3 = max(ta, tb);
        float t0 = max(max(near3.x, near3.y), max(near3.z, 0.0));
        float t1 = min(min(far3.x, far3.y), min(far3.z, t_scene));
        if (t1 <= t0)
            continue;
        // Éclairage : lumière du Soleil à mi-hauteur (rougie au couchant), ciel, nuit
        float mus = dot(P.up, sun);
        vec3 sunlight = sun_transmittance(eruption_shapes[k].w + 0.5 * P.height, mus) * smoothstep(-0.12, 0.05, mus);
        vec3 skylight = vec3(0.32, 0.38, 0.5) * smoothstep(-0.15, 0.25, mus) + vec3(0.004);
        vec3 albedo = P.steam ? vec3(0.9, 0.91, 0.93) : vec3(0.38, 0.35, 0.315);
        float night = 1.0 - 0.75 * smoothstep(-0.1, 0.2, mus);
        // Éclairs dans les colonnes explosives : brefs, irréguliers, visibles surtout la nuit
        float flash_slot = floor(cloud_time * 0.5 + float(k) * 13.0);
        float flash = (!P.steam && vei >= 3.0 && fract(sin(flash_slot * 91.7 + float(k)) * 4375.85) > 0.93)
                      ? (1.0 - fract(cloud_time * 0.5 + float(k) * 13.0)) * 6.0 : 0.0;
        float boil = cloud_time * 0.002;
        float extinction_scale = (P.steam ? 0.004 : 0.01) * (1.0 + 0.5 * vei) * intensity;
        float t = t0;
        bool first = true;
        for (int i = 0; i < 256 && t < t1; i++) {
            vec3 p = camera_planet + rd * t;
            float feature, thin, trail, safe;
            float e = plume_envelope(P, p, feature, thin, trail, safe);
            // Hors de la forme, volutes comprises (elles décalent le bord d'au plus ~0,65) : grand
            // pas, proportionné à la distance au bord
            float lod = 1.0 + t / 60000.0;
            if (e > 1.45) {
                t += max(safe * 0.8, 0.08 * feature);
                continue;
            }
            float dt = 0.08 * feature * lod;
            if (first) {
                t += dt * jitter;
                first = false;
                p = camera_planet + rd * t;
            }
            float density = plume_density(k, P, p, boil, feature);
            if (density > 0.01) {
                float s = dot(p - P.vent, P.up);
                // Ombre propre : épaisseur vers le Soleil (près et loin), plus une part de lumière
                // diffusée plusieurs fois, qui éclaire un peu le cœur des volutes
                float f2;
                float near_sun = plume_density(k, P, p + sun * feature * 0.3, boil, f2);
                float far_sun = plume_density(k, P, p + sun * feature * 0.9, boil, f2);
                float lit = exp(-near_sun * 1.2 - far_sun * 1.8) + 0.12;
                // Le ciel éclaire mieux le haut que la base, dans l'ombre de l'ombrelle
                float sky_view = 0.55 + 0.45 * smoothstep(0.1, 0.9, s / P.height);
                // (cendre gris-brun ; un peu plus claire en haut, où elle est plus fine)
                vec3 light = albedo * mix(0.7, 1.05, smoothstep(0.15, 0.9, s / P.height)) * (sunlight * lit * 1.3 + skylight * sky_view);
                // Lueur de la lave sur les ~300 premiers mètres (éclatante la nuit) et éclairs
                light += vec3(1.0, 0.33, 0.06) * (1.0 - smoothstep(0.0, 300.0, s)) * night * (vei <= 1.0 ? 1.4 : 0.8) * float(!P.steam);
                light += vec3(0.75, 0.7, 1.0) * flash * (1.0 - smoothstep(0.2, 0.8, s / P.height)) * smoothstep(0.3, 0.0, mus + 0.2);
                float step_t = exp(-density * extinction_scale * dt);
                color += transmittance * (1.0 - step_t) * light;
                transmittance *= step_t;
                t_hit = min(t_hit, t);
                if (transmittance < 0.02)
                    break;
            }
            t += dt;
        }
    }
    return vec4(color, 1.0 - transmittance);
}

void vertex() {
    POSITION = vec4(VERTEX.xy, 1.0, 1.0);
}


// Nuages le long du rayon : couleur (prémultipliée), opacité, distance du premier nuage
void march_clouds(vec3 up, vec3 rd, float t_scene, vec3 sun, vec2 pixel, out vec3 color, out float alpha, out float t_first) {
    color = vec3(0.0);
    alpha = 0.0;
    t_first = 1e30;
    float a = camera_altitude;
    float mu = dot(rd, up);
    vec2 top = ray_shell(a, mu, CLOUD_TOP_MAX);
    vec2 base = ray_shell(a, mu, CLOUD_BASE);
    float t0 = max(top.x, 0.0);
    float t1 = min(top.y, t_scene);
    if (a < CLOUD_BASE && base.y > 0.0)
        t0 = max(t0, base.y);
    if (a > CLOUD_BASE && base.x > 0.0 && base.y > base.x)
        t1 = min(t1, base.x);
    t1 = min(t1, t0 + 120000.0);
    if (t1 <= t0)
        return;
    // Le « temps qu'il fait » varie sur des centaines de km : une seule valeur par rayon (le
    // trajet dans la couche fait au plus 120 km), au lieu de 4 lectures de bruit par pas
    float weather = weather_at(normalize(camera_planet + rd * (0.5 * (t0 + t1))));
    // Rien à faire si aucun nuage aux deux bouts ni au milieu du trajet (cas le plus courant)
    float probe = max(max(apply_weather(cloud_cover_raw(normalize(camera_planet + rd * t0)), weather).r,
                          apply_weather(cloud_cover_raw(normalize(camera_planet + rd * t1)), weather).r),
                      apply_weather(cloud_cover_raw(normalize(camera_planet + rd * (0.5 * (t0 + t1)))), weather).r);
    if (probe < 0.03)
        return;
    const int STEPS = 36;
    float dt = (t1 - t0) / float(STEPS);
    float t = t0 + dt * hash12(pixel);
    float transmittance = 1.0;
    float nu = dot(rd, sun);
    // Diffusion vers l'avant (halo lumineux des nuages à contre-jour)
    float phase = mix(0.8, 2.2, pow(max(nu, 0.0), 6.0));
    vec3 sky_light = vec3(0.35, 0.45, 0.65);
    // Lumière du Soleil au niveau des nuages : calculée une fois par rayon (elle varie peu)
    vec3 sunlight = vec3(-1.0);
    for (int i = 0; i < STEPS; i++) {
        vec3 p = camera_planet + rd * t;
        float h = ray_height(a, mu, t);
        vec3 d = normalize(p);
        vec2 cover = apply_weather(cloud_cover_raw(d), weather);
        float footprint = t * pixel_angle;
        float density = cloud_density(p, h, cover, footprint);
        if (density > 0.003) {
            if (t_first > 1e29)
                t_first = t;
            // Lumière du Soleil : atmosphère au-dessus du nuage, puis le nuage lui-même
            float mus = dot(d, sun);
            if (sunlight.x < 0.0)
                sunlight = sun_transmittance(h, mus);
            // Épaisseur de nuage vers le Soleil : un échantillon à 700 m
            float light_depth = cloud_density(p + sun * 700.0, h + mus * 700.0, cover, footprint) * 1400.0;
            float beer = exp(-CLOUD_EXTINCTION * light_depth);
            float powder = 1.0 - exp(-CLOUD_EXTINCTION * light_depth * 2.0);
            float day = smoothstep(-0.1, 0.15, mus);
            // Nuages d'orage sombres vus d'en dessous (leur épaisseur arrête la lumière) ; vus
            // d'au-dessus, leur sommet éclairé par le Soleil reste blanc
            float below = 1.0 - smoothstep(CLOUD_BASE, CLOUD_TOP_MAX, a);
            float storm = mix(1.0, 0.45, cover.g * below);
            vec3 light = (sunlight * beer * mix(1.0, powder, 0.5) * phase * 1.2 + sky_light * day * 0.5 + vec3(0.01)) * storm;
            float extinction = CLOUD_EXTINCTION * density;
            float step_t = exp(-extinction * dt);
            color += transmittance * (1.0 - step_t) * light;
            transmittance *= step_t;
            if (transmittance < 0.02)
                break;
        }
        t += dt;
    }
    alpha = 1.0 - transmittance;
}

void fragment() {
    float depth = texture(depth_texture, SCREEN_UV).r;
    vec4 view = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, depth, 1.0);
    view.xyz /= view.w;
    vec3 rd = normalize((INV_VIEW_MATRIX * vec4(view.xyz, 0.0)).xyz);
    bool sky = depth <= 1e-9;
    float t_scene = sky ? 1e30 : length(view.xyz);
    vec3 up = normalize(local_up);
    vec3 sun = normalize(sun_direction);

    // Perspective atmosphérique sur le relief (le ciel a déjà la sienne)
    vec3 inscatter = vec3(0.0), transmittance = vec3(1.0);
    if (!sky)
        atmosphere(camera_altitude, up, rd, t_scene, sun, inscatter, transmittance);
    float scene_t = dot(transmittance, vec3(1.0 / 3.0));

    // Brume près du sol : densité qui décroît avec la hauteur au-dessus du sol sous la caméra
    if (fog_density > 0.0) {
        float mu = dot(rd, up);
        float range = min(t_scene, 60000.0);
        float h0 = camera_altitude - ground_altitude;
        float k = mu * range / fog_height;
        float integral = abs(k) < 1e-3 ? range : fog_height / mu * (1.0 - exp(-k));
        float fog_t = exp(-fog_density * exp(-max(h0, 0.0) / fog_height) * integral);
        inscatter = inscatter * fog_t + fog_color * (1.0 - fog_t);
        scene_t *= fog_t;
    }

    vec3 color = inscatter;
    float alpha = 1.0 - scene_t;
    // Panaches volcaniques et nuages composés dans l'ordre des distances : un panache sous une
    // mer de nuages est caché par elle, un panache qui la perce passe devant
    float t_plume = 1e30;
    vec4 plume = vec4(0.0);
    if (eruption_count > 0)
        plume = plumes(rd, t_scene, sun, FRAGCOORD.xy, t_plume);
    vec3 cloud_color = vec3(0.0);
    float cloud_alpha = 0.0, t_first = 1e30;
    if (clouds_enabled) {
        march_clouds(up, rd, t_scene, sun, FRAGCOORD.xy, cloud_color, cloud_alpha, t_first);
        if (cloud_alpha > 0.0) {
            // Atmosphère entre l'œil et le nuage
            vec3 ci, ct;
            atmosphere(camera_altitude, up, rd, t_first, sun, ci, ct);
            cloud_color = cloud_color * dot(ct, vec3(1.0 / 3.0)) + ci * cloud_alpha;
        }
    }
    bool plume_in_front = t_plume < t_first;
    if (plume.a > 0.0 && !plume_in_front) {
        color = plume.rgb + (1.0 - plume.a) * color;
        alpha = 1.0 - (1.0 - plume.a) * (1.0 - alpha);
    }
    if (cloud_alpha > 0.0) {
        color = cloud_color + (1.0 - cloud_alpha) * color;
        alpha = 1.0 - (1.0 - cloud_alpha) * (1.0 - alpha);
    }
    if (plume.a > 0.0 && plume_in_front) {
        color = plume.rgb + (1.0 - plume.a) * color;
        alpha = 1.0 - (1.0 - plume.a) * (1.0 - alpha);
    }
    ALBEDO = color;
    ALPHA = alpha;
}
";
}
