using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Godot;
using Planet.Simulation;
using Planet.Simulation.Climate;
using Planet.Simulation.Terrain;

namespace Planet.Rendering;

/// <summary>Coloration du terrain.</summary>
public enum TerrainColoring
{
    /// <summary>Couleurs naturelles de la géologie seule (sédiments, roche, neige, mer).</summary>
    Geology,
    /// <summary>Couleur de la couche de données affichée (plaques, températures…), drapée sur le relief.</summary>
    Layer,
    /// <summary>Couleur de la vue réaliste (biomes, neige, banquise), plus la roche nue des pentes raides.</summary>
    Realistic,
    /// <summary>Carte d'altitude, calculée point par point sur le relief détaillé (pas par cellule).</summary>
    Elevation,
}

/// <summary>
/// Affichage de la planète entière, de l'orbite jusqu'au sol, à l'échelle réelle (étape 9) :
/// <list type="bullet">
/// <item>le quadtree (<see cref="TerrainQuadtree"/>) choisit chaque image les morceaux utiles ;</item>
/// <item>les morceaux manquants sont calculés par des threads en arrière-plan, les plus urgents
/// d'abord ; seules les demandes de l'image en cours comptent (on n'attend pas un morceau que
/// la caméra a déjà dépassé) ;</item>
/// <item>chaque morceau devient un maillage et une instance du RenderingServer (sans nœud de
/// scène : plus léger), avec un matériau commun ; le morphing se fait dans le vertex shader ;</item>
/// <item>rendu relatif à la caméra : la caméra Godot reste à l'origine, chaque morceau est placé
/// à (son centre − position de la caméra), calculé en double précision : aucun tremblement,
/// même à 6 371 km du centre ;</item>
/// <item>cache des morceaux déjà calculés, les moins récemment utilisés étant libérés ;</item>
/// <item>quand le relief simulé change (tectonique), les morceaux sont recalculés en arrière-plan
/// et remplacés un à un : l'ancien relief reste affiché en attendant, sans clignotement ;</item>
/// <item>les couches de données (une couleur par cellule de la simulation) sont drapées sur le
/// relief : une texture par face du cube donne, pour chaque point, le numéro de la cellule la
/// plus proche, puis la couleur de cette cellule ;</item>
/// <item>l'eau (mers, lacs) est une seconde surface, partiellement transparente, posée au
/// niveau de l'eau par le vertex shader à partir du même maillage : on voit les fonds près des
/// côtes ;</item>
/// <item>le relief est toujours à l'échelle réelle (l'exagération automatique a été retirée à
/// la demande de Nicolas).</item>
/// </list>
/// </summary>
public partial class TerrainRenderer : Node3D
{
    // Temps de création de maillages par image (ms) et taille du cache (morceaux).
    // Chaque morceau a deux instances (relief + eau) avec un « instance uniform » (morph_range) :
    // Godot leur réserve 16 cases dans un tampon global dont la taille par défaut (65 536) ne
    // suffit que pour 4 096 instances. Après un saut de caméra, le cache, les morceaux de la
    // nouvelle vue et ceux en attente de libération dépassaient cette limite et faisaient
    // planter le jeu : le tampon est agrandi à 524 288 cases (32 768 instances) dans project.godot
    // (rendering/limits/global_shader_variables/buffer_size).
    private const double UploadBudgetMs = 3.0;

    /// <summary>Nombre de morceaux gardés en mémoire (selon la qualité).</summary>
    public int MaxCachedChunks { get; set; } = 3000;

    private bool _castShadows;

    /// <summary>Le relief projette des ombres (l'eau, jamais).</summary>
    public bool CastShadows
    {
        get => _castShadows;
        set
        {
            if (_castShadows == value)
                return;
            _castShadows = value;
            foreach (var entry in _cache.Values)
                RenderingServer.InstanceGeometrySetCastShadowsSetting(entry.Instance, ShadowSetting);
            foreach (var entry in _pending.Values)
                RenderingServer.InstanceGeometrySetCastShadowsSetting(entry.Instance, ShadowSetting);
        }
    }

    private RenderingServer.ShadowCastingSetting ShadowSetting =>
        _castShadows ? RenderingServer.ShadowCastingSetting.On : RenderingServer.ShadowCastingSetting.Off;
    // Plus haut sommet possible à l'échelle réelle (m)
    public const double MaxMountainHeight = 11000.0;
    // Résolution des textures « numéro de cellule » (pixels par côté de face)
    private const int CellIndexResolutionPixels = 512;

    private sealed class ChunkEntry
    {
        public ChunkKey Key;
        public Double3 Center;
        public Double3 BoundsCenter;
        public double BoundsRadius;
        public Rid Mesh;
        public Rid Instance;
        public Rid WaterInstance;   // vide si le morceau n'a pas d'eau
        public bool Visible;
        public long LastUsedFrame;
        public int Generation;
    }

    public ExplorerCamera Explorer { get; } = new();
    public Camera3D Camera { get; private set; }
    public TerrainQuadtree Quadtree { get; } = new();
    public TerrainHeightField Field { get; private set; }

    public int VisibleChunkCount { get; private set; }
    public int CachedChunkCount => _cache.Count;
    public int PendingChunkCount { get; private set; }
    public int DeepestVisibleLevel { get; private set; }
    public double LastSelectionMs { get; private set; }
    /// <summary>Morceaux du nouveau relief encore à calculer avant de l'afficher.</summary>
    public int StaleChunkCount { get; private set; }
    /// <summary>Vrai tant qu'un nouveau relief est en préparation (l'ancien reste affiché).</summary>
    public bool IsUpdating => _generation != _displayGeneration;

    public TerrainColoring Coloring { get; set; } = TerrainColoring.Geology;
    /// <summary>Surface de l'eau affichée (masquée : on voit les fonds marins et des lacs).</summary>
    public bool ShowWater { get; set; } = true;

    private static readonly Godot.Collections.Array EmptyBlendShapes = new();
    private static readonly Godot.Collections.Dictionary EmptyLods = new();
    // Morceaux du relief affiché
    private readonly Dictionary<long, ChunkEntry> _cache = new();
    // Morceaux du nouveau relief, calculés mais pas encore affichés
    private readonly Dictionary<long, ChunkEntry> _pending = new();
    private readonly List<ChunkEntry> _visible = new();
    private ShaderMaterial _material;
    private ShaderMaterial _waterMaterial;
    private Image _cellDataImage;
    private ImageTexture _cellDataTexture;
    private byte[] _cellDataBytes;
    private float[] _cellData;
    private long _frame;
    // Relief affiché (_generation : le plus récent, peut-être encore en préparation)
    private int _displayGeneration;
    // Durée de la préparation du nouveau relief
    private readonly Stopwatch _transitionClock = new();
    // Au-delà, on bascule même si le nouveau relief n'est pas encore aussi détaillé que l'ancien
    private const double MaxTransitionSeconds = 3.0;
    // Morceaux de l'ancien relief à libérer, et combien par image
    private readonly Queue<ChunkEntry> _toFree = new();
    private const int FreePerFrame = 150;
    private Image _cellColorImage;
    private ImageTexture _cellColorTexture;
    private byte[] _cellColorPixels;
    private int _cellColorHeight;

    // Threads de calcul
    private readonly object _queueLock = new();
    private readonly PriorityQueue<ChunkKey, double> _queue = new();
    private readonly HashSet<long> _queued = new();
    private readonly HashSet<long> _inProgress = new();
    private readonly ConcurrentQueue<(TerrainChunk chunk, int generation, int stamp)> _completed = new();
    // Croûte modulaire (étape 16a) : morceaux affichés dont le relief a changé, à recalculer
    // (numéro de la modification la plus récente qui les touche) ; le morceau périmé reste
    // affiché jusqu'à l'arrivée du nouveau
    private readonly Dictionary<long, int> _stale = new();
    private volatile int _editStamp;
    private Thread[] _workers;
    private volatile bool _stopping;
    private volatile int _generation;
    private volatile int _activeWorkers = int.MaxValue;

    /// <summary>
    /// Nombre de threads de calcul du terrain autorisés à travailler. Réduit pendant que la
    /// tectonique tourne, pour lui laisser des cœurs (sinon les deux se gênent et l'image saccade).
    /// </summary>
    public int ActiveWorkerLimit
    {
        get => _activeWorkers;
        set
        {
            if (_activeWorkers == value)
                return;
            _activeWorkers = value;
            lock (_queueLock)
                Monitor.PulseAll(_queueLock);
        }
    }

    public int WorkerCount => _workers?.Length ?? 0;

    public override void _Ready()
    {
        Camera = new Camera3D { Name = "ExplorerCamera", Near = 1f, Far = 1.0e6f, Fov = 70f };
        AddChild(Camera);
        _material = new ShaderMaterial { Shader = new Shader { Code = ShaderCode } };
        _material.SetShaderParameter("planet_radius", (float)TerrainHeightField.RadiusMeters);
        _waterMaterial = new ShaderMaterial { Shader = new Shader { Code = WaterShaderCode } };
        _waterMaterial.SetShaderParameter("planet_radius", (float)TerrainHeightField.RadiusMeters);

        int workerCount = Math.Max(1, System.Environment.ProcessorCount - 2);
        _workers = new Thread[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            int index = i;
            _workers[i] = new Thread(() => WorkerLoop(index)) { IsBackground = true, Name = $"TerrainWorker{i}", Priority = ThreadPriority.BelowNormal };
            _workers[i].Start();
        }
    }

    public override void _ExitTree()
    {
        _stopping = true;
        lock (_queueLock)
            Monitor.PulseAll(_queueLock);
        if (_workers != null)
            foreach (var worker in _workers)
                worker.Join(500);
        ClearCache();
        foreach (var entry in _pending.Values)
            FreeResources(entry);
        _pending.Clear();
    }

    /// <summary>
    /// Prépare le drapage des couches : pour chaque face du cube, une texture qui donne le numéro
    /// de la cellule de la simulation la plus proche de chaque point (calculée une fois par grille).
    /// </summary>
    public void SetGrid(PlanetGrid grid)
    {
        int n = CellIndexResolutionPixels;
        var faces = new Godot.Collections.Array<Image>();
        var data = new float[CubeSphere.FaceCount][];
        for (int face = 0; face < CubeSphere.FaceCount; face++)
            data[face] = new float[n * n];
        System.Threading.Tasks.Parallel.For(0, CubeSphere.FaceCount * n, row =>
        {
            int face = row / n, j = row % n, hint = 0;
            for (int i = 0; i < n; i++)
            {
                Double3 direction = CubeSphere.Direction(face, (i + 0.5) / n * 2 - 1, (j + 0.5) / n * 2 - 1);
                hint = grid.NearestCell(direction.ToVector3(), hint);
                data[face][j * n + i] = hint;
            }
        });
        for (int face = 0; face < CubeSphere.FaceCount; face++)
        {
            var bytes = new byte[n * n * sizeof(float)];
            Buffer.BlockCopy(data[face], 0, bytes, 0, bytes.Length);
            faces.Add(Image.CreateFromData(n, n, false, Image.Format.Rf, bytes));
        }
        CellIndex = new int[CubeSphere.FaceCount][];
        for (int face = 0; face < CubeSphere.FaceCount; face++)
            CellIndex[face] = Array.ConvertAll(data[face], v => (int)v);
        var indexTexture = new Texture2DArray();
        indexTexture.CreateFromImages(faces);
        SetBoth("cell_index", indexTexture);
        // Fondu entre cellules à l'échelle des cellules de la simulation : un texel de la carte
        // des numéros vaut π/2 / 512 rad (~20 km) ; avec un fondu fixe de 2 texels, les grandes
        // cellules (~220 km à 10 242 cellules) restaient des hexagones bien nets vus de l'orbite
        float texelAngle = MathF.PI / 2f / CellIndexResolutionPixels;
        int blendStep = Math.Max(2, (int)MathF.Round(0.8f * grid.MeanEdgeAngle / texelAngle));
        SetBoth("cell_blend_step", blendStep);
        SetBoth("cell_warp", MathF.Max(0.0035f, 0.35f * grid.MeanEdgeAngle));

        _cellColorHeight = (grid.CellCount + CellColorLayer.TextureWidth - 1) / CellColorLayer.TextureWidth;
        _cellColorPixels = new byte[CellColorLayer.TextureWidth * _cellColorHeight * 4];
        _cellColorImage = Image.CreateFromData(CellColorLayer.TextureWidth, _cellColorHeight, false, Image.Format.Rgba8, _cellColorPixels);
        _cellColorTexture = ImageTexture.CreateFromImage(_cellColorImage);
        SetBoth("cell_colors", _cellColorTexture);
        SetBoth("texture_width", CellColorLayer.TextureWidth);

        _cellData = new float[CellColorLayer.TextureWidth * _cellColorHeight * 4];
        var dataBytes = new byte[_cellData.Length * sizeof(float)];
        _cellDataImage = Image.CreateFromData(CellColorLayer.TextureWidth, _cellColorHeight, false, Image.Format.Rgbaf, dataBytes);
        _cellDataTexture = ImageTexture.CreateFromImage(_cellDataImage);
        SetBoth("cell_data", _cellDataTexture);
        SetBoth("has_climate", false);
    }

    /// <summary>Numéro de la cellule la plus proche de chaque pixel des 6 faces du cube (CellIndexResolution² par face).</summary>
    public int[][] CellIndex { get; private set; }
    public int CellIndexResolution => CellIndexResolutionPixels;

    /// <summary>Paramètre commun aux shaders du sol et de l'eau (ciel, nuages : étape 10).</summary>
    public void SetShaderParameter(string name, Variant value) => SetBoth(name, value);

    private void SetBoth(string name, Variant value)
    {
        _material.SetShaderParameter(name, value);
        _waterMaterial.SetShaderParameter(name, value);
    }

    /// <summary>
    /// Climat du moment pour les couleurs du sol (étape 9e) : température de surface (°C, à
    /// l'altitude de la cellule), neige au sol (mm) et altitude de chaque cellule (m). Le shader
    /// en déduit la limite de la neige point par point (−6,5 °C par km). null : pas de climat
    /// simulé (horloge géologique), limite de la neige selon la latitude.
    /// </summary>
    /// <param name="canopy">Densité de la canopée des forêts (0 à 1) ou, en négatif, part de sable des déserts ; ou null.</param>
    public void UpdateCellClimate(float[] temperature, float[] snowMm, float[] elevationKm, float[] canopy = null)
    {
        if (_cellDataImage == null)
            return;
        if (temperature == null)
        {
            SetBoth("has_climate", false);
            return;
        }
        for (int cell = 0; cell < temperature.Length; cell++)
        {
            _cellData[cell * 4] = temperature[cell];
            _cellData[cell * 4 + 1] = snowMm[cell];
            _cellData[cell * 4 + 2] = elevationKm[cell] * 1000f;
            _cellData[cell * 4 + 3] = canopy?[cell] ?? 0f;
        }
        // (tampon réutilisé : 4 fois par seconde, une nouvelle copie de plusieurs Mo pesait sur le
        // ramasse-miettes)
        _cellDataBytes ??= new byte[_cellData.Length * sizeof(float)];
        Buffer.BlockCopy(_cellData, 0, _cellDataBytes, 0, _cellDataBytes.Length);
        _cellDataImage.SetData(CellColorLayer.TextureWidth, _cellColorHeight, false, Image.Format.Rgbaf, _cellDataBytes);
        _cellDataTexture.Update(_cellDataImage);
        SetBoth("has_climate", true);
    }

    /// <summary>
    /// Couleur de chaque cellule pour la couche affichée. La transparence (alpha) marque les
    /// cellules de mer (altitude &lt; 0, km), pour que le shader ne mélange que des cellules du
    /// même milieu que le point dessiné.
    /// </summary>
    public void UpdateCellColors(Color[] colors, float[] elevationKm = null)
    {
        if (_cellColorImage == null)
            return;
        for (int cell = 0; cell < colors.Length; cell++)
        {
            Color c = colors[cell];
            int offset = cell * 4;
            _cellColorPixels[offset] = (byte)c.R8;
            _cellColorPixels[offset + 1] = (byte)c.G8;
            _cellColorPixels[offset + 2] = (byte)c.B8;
            _cellColorPixels[offset + 3] = (byte)(elevationKm != null && elevationKm[cell] < 0 ? 0 : 255);
        }
        _cellColorImage.SetData(CellColorLayer.TextureWidth, _cellColorHeight, false, Image.Format.Rgba8, _cellColorPixels);
        _cellColorTexture.Update(_cellColorImage);
    }

    /// <summary>
    /// Nouveau relief. <paramref name="progressive"/> faux (nouvelle planète) : le cache est vidé
    /// et les 6 faces calculées tout de suite. Vrai (le relief a évolué) : l'ancien relief reste
    /// affiché pendant que les morceaux visibles sont recalculés en arrière-plan, puis tous sont
    /// remplacés d'un coup. (Les remplacer un à un mélangeait des morceaux d'époques différentes :
    /// à 2 Ma/s la côte bouge de dizaines de km entre deux reliefs, d'où des cassures droites
    /// le long des bords des morceaux.)
    /// </summary>
    public void SetField(TerrainHeightField field, bool progressive = false)
    {
        _generation++;
        lock (_queueLock)
        {
            _queue.Clear();
            _queued.Clear();
        }
        Field = field;
        _stale.Clear();
        // Un relief en préparation qui n'a pas abouti est abandonné
        foreach (var entry in _pending.Values)
            FreeResources(entry);
        _pending.Clear();
        if (progressive && _cache.Count > 0)
        {
            _transitionClock.Restart();
            return;
        }
        ClearCache();
        _displayGeneration = _generation;
        Explorer.SetField(field);
        for (int face = 0; face < CubeSphere.FaceCount; face++)
            _cache[new ChunkKey(face, 0, 0, 0).Packed] = CreateEntry(TerrainChunkBuilder.Build(field, new ChunkKey(face, 0, 0, 0)), _generation);
    }

    /// <summary>
    /// Croûte modulaire (étape 16a) : le relief a changé autour d'un point (glissement de terrain,
    /// éboulement, lave figée). Les morceaux affichés qui touchent la zone sont recalculés, les
    /// plus visibles d'abord ; chacun reste affiché jusqu'à l'arrivée de son remplaçant. Les
    /// morceaux trop grossiers pour montrer la modification (sommets plus espacés que la zone)
    /// ne sont pas refaits.
    /// </summary>
    public void InvalidateRegion(Double3 direction, double radiusM)
    {
        int stamp = Interlocked.Increment(ref _editStamp);
        direction = direction.Normalized();
        double planet = TerrainHeightField.RadiusMeters;
        foreach (var (packed, entry) in _cache)
        {
            double spacing = CubeSphere.ChunkSizeMeters(entry.Key.Level) / TerrainChunkBuilder.Resolution;
            if (spacing > 2 * radiusM && entry.Key.Level > 0)
                continue;
            double angle = Math.Acos(Math.Clamp(entry.BoundsCenter.Normalized().Dot(direction), -1, 1));
            if (angle * planet <= radiusM + entry.BoundsRadius)
                _stale[packed] = stamp;
        }
    }

    /// <summary>Morceaux en attente d'un nouveau calcul après une modification de la croûte.</summary>
    public int StaleRegionChunkCount => _stale.Count;

    /// <summary>Jour du climat, pour la repousse de la végétation sur le sol remanié.</summary>
    public void SetCrustDay(float day) => _material?.SetShaderParameter("crust_day", day);

    /// <summary>Bascule sur le nouveau relief : l'ancien est jeté, le nouveau devient le relief affiché.</summary>
    private void Swap()
    {
        foreach (var entry in _visible)
        {
            RenderingServer.InstanceSetVisible(entry.Instance, false);
            if (entry.WaterInstance.IsValid)
                RenderingServer.InstanceSetVisible(entry.WaterInstance, false);
        }
        _visible.Clear();
        // L'ancien relief est libéré petit à petit (des milliers de maillages d'un coup : saccade)
        foreach (var entry in _cache.Values)
            _toFree.Enqueue(entry);
        _cache.Clear();
        _stale.Clear();
        foreach (var (packed, entry) in _pending)
        {
            entry.LastUsedFrame = _frame;
            _cache[packed] = entry;
        }
        _pending.Clear();
        _displayGeneration = _generation;
        Explorer.SetField(Field);
    }

    /// <summary>Déplacement de la caméra puis mise à jour des morceaux affichés.</summary>
    public void Tick(double delta, ExplorerInput input)
    {
        if (Field == null)
            return;
        _frame++;
        Explorer.Update(delta, input);
        Camera.Basis = Explorer.Basis();
        var (near, far) = ClipPlanes(Explorer.Position.Length() - TerrainHeightField.RadiusMeters);
        Camera.Near = near;
        Camera.Far = far;
        SetBoth("coloring", (int)Coloring);

        UploadCompleted();
        UpdateSelection();
        Evict();
        for (int i = 0; i < FreePerFrame && _toFree.Count > 0; i++)
            FreeResources(_toFree.Dequeue());
    }

    /// <summary>
    /// Plans de découpe : le plan lointain juste au-delà du point le plus éloigné visible (horizon
    /// de la mer + sommets qui dépassent derrière), le plan proche un million de fois plus
    /// près. Au-delà de ce rapport, la projection en float perd toute précision de profondeur et
    /// plus rien ne s'affiche (vérifié : rapport 3·10⁷ → écran vide).
    /// </summary>
    public static (float near, float far) ClipPlanes(double altitude, double maxMountain = MaxMountainHeight)
    {
        double radius = TerrainHeightField.RadiusMeters;
        altitude = Math.Max(altitude, 1.0);
        double horizon = Math.Sqrt(altitude * (2 * radius + altitude));
        double beyond = Math.Sqrt(maxMountain * (2 * radius + maxMountain));
        double far = (horizon + beyond) * 1.1;
        return ((float)Math.Max(far * 1e-6, 0.05), (float)far);
    }

    // --- Sélection et affichage -----------------------------------------------------------------

    private void UpdateSelection()
    {
        var stopwatch = Stopwatch.StartNew();
        Double3 camera = Explorer.Position;
        var planes = new List<Plane>(Camera.GetFrustum()).ToArray();
        var requests = new List<(ChunkKey key, double priority)>();
        bool updating = IsUpdating;

        // Relief affiché. Pendant la préparation d'un nouveau relief, ses morceaux manquants ne
        // sont plus demandés : ils seraient calculés sur le nouveau relief
        bool inFrustum(Double3 relativeCenter, double radius)
        {
            Vector3 center = relativeCenter.ToVector3();
            foreach (Plane plane in planes)
                if (plane.DistanceTo(center) > radius)
                    return false;
            return true;
        }
        // Tout morceau consulté (y compris les parents, traversés mais non affichés) compte comme
        // utilisé : sinon le cache jetait les parents en premier, et l'affichage retombait d'un
        // coup sur les 6 faces entières (le relief « disparaissait » pendant une image)
        (Double3, double)? Lookup(Dictionary<long, ChunkEntry> chunks, ChunkKey key)
        {
            if (!chunks.TryGetValue(key.Packed, out var entry))
                return null;
            entry.LastUsedFrame = _frame;
            return (entry.BoundsCenter, entry.BoundsRadius);
        }
        var selection = Quadtree.Select(
            camera,
            key => Lookup(_cache, key),
            inFrustum,
            (key, priority) => { if (!updating) requests.Add((key, priority)); });

        // Nouveau relief en préparation : on choisit ses morceaux comme s'il était affiché (même
        // caméra, même découpage) et on calcule ceux qui manquent. Quand il n'en manque plus, il
        // remplace l'ancien d'un coup ; il est donc aussi détaillé que lui dès son apparition
        int stale = 0;
        if (updating)
        {
            var missing = new List<(ChunkKey key, double priority)>();
            var next = Quadtree.Select(
                camera,
                key => Lookup(_pending, key),
                inFrustum,
                (key, priority) => missing.Add((key, priority)));
            stale = missing.Count;
            requests.AddRange(missing);
            bool facesReady = true;
            for (int face = 0; face < CubeSphere.FaceCount; face++)
                facesReady &= _pending.ContainsKey(new ChunkKey(face, 0, 0, 0).Packed);
            if (missing.Count == 0 || (facesReady && _transitionClock.Elapsed.TotalSeconds > MaxTransitionSeconds))
            {
                Swap();
                selection = next;
            }
        }
        StaleChunkCount = stale;

        // Morceaux à refaire après une modification de la croûte : ceux qu'on voit d'abord
        if (!updating && _stale.Count > 0)
        {
            var shown = new HashSet<long>();
            foreach (var chunk in selection)
                shown.Add(chunk.Key.Packed);
            foreach (var (packed, _) in _stale)
            {
                if (!_cache.TryGetValue(packed, out var entry))
                    continue;
                double distance = Math.Max(0, entry.BoundsCenter.DistanceTo(camera) - entry.BoundsRadius);
                requests.Add((entry.Key, shown.Contains(packed) ? -1e9 + distance : entry.Key.Level * 1e9 + 5e8 + distance));
            }
        }

        // Demandes de l'image : elles remplacent celles de l'image précédente
        lock (_queueLock)
        {
            _queue.Clear();
            _queued.Clear();
            foreach (var (key, priority) in requests)
                if (!_inProgress.Contains(key.Packed) && !_pending.ContainsKey(key.Packed) && _queued.Add(key.Packed))
                    _queue.Enqueue(key, priority);
            PendingChunkCount = _queue.Count + _inProgress.Count;
            if (_queue.Count > 0)
                Monitor.PulseAll(_queueLock);
        }

        foreach (var entry in _visible)
            entry.Visible = false;
        var nowVisible = new List<ChunkEntry>(selection.Count);
        int deepest = 0;
        foreach (var chunk in selection)
        {
            var entry = _cache[chunk.Key.Packed];
            entry.LastUsedFrame = _frame;
            entry.Visible = true;
            nowVisible.Add(entry);
            deepest = Math.Max(deepest, chunk.Key.Level);
            var origin = (entry.Center - camera).ToVector3();
            RenderingServer.InstanceSetTransform(entry.Instance, new Transform3D(Godot.Basis.Identity, origin));
            RenderingServer.InstanceGeometrySetShaderParameter(entry.Instance, "morph_range",
                new Vector2(chunk.MorphStart, chunk.MorphEnd));
            RenderingServer.InstanceSetVisible(entry.Instance, true);
            if (entry.WaterInstance.IsValid)
            {
                RenderingServer.InstanceSetTransform(entry.WaterInstance, new Transform3D(Godot.Basis.Identity, origin));
                RenderingServer.InstanceGeometrySetShaderParameter(entry.WaterInstance, "morph_range",
                    new Vector2(chunk.MorphStart, chunk.MorphEnd));
                // Couches de données : pas d'eau par-dessus (on veut voir la couche sur les océans)
                RenderingServer.InstanceSetVisible(entry.WaterInstance, ShowWater && Coloring is not (TerrainColoring.Layer or TerrainColoring.Elevation));
            }
        }
        foreach (var entry in _visible)
            if (!entry.Visible)
            {
                RenderingServer.InstanceSetVisible(entry.Instance, false);
                if (entry.WaterInstance.IsValid)
                    RenderingServer.InstanceSetVisible(entry.WaterInstance, false);
            }
        _visible.Clear();
        _visible.AddRange(nowVisible);

        SetBoth("planet_center", (-camera).ToVector3());
        SetBoth("camera_planet", camera.ToVector3());
        SetBoth("noise_offset", new Vector3((float)PositiveModulo(camera.X), (float)PositiveModulo(camera.Y), (float)PositiveModulo(camera.Z)));
        VisibleChunkCount = _visible.Count;
        DeepestVisibleLevel = deepest;
        LastSelectionMs = stopwatch.Elapsed.TotalMilliseconds;
    }

    private static double PositiveModulo(double x)
    {
        const double period = 8192;   // NOISE_PERIOD du shader
        double r = x % period;
        return r < 0 ? r + period : r;
    }

    // --- Création des maillages -----------------------------------------------------------------

    private void UploadCompleted()
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed.TotalMilliseconds < UploadBudgetMs && _completed.TryDequeue(out var item))
        {
            long packed = item.chunk.Key.Packed;
            if (item.generation == _displayGeneration)
            {
                if (!_cache.TryGetValue(packed, out var old))
                    _cache[packed] = CreateEntry(item.chunk, item.generation);
                else if (_stale.TryGetValue(packed, out int since))
                {
                    // Croûte modifiée : le nouveau morceau remplace l'ancien (affiché dès cette
                    // image par la sélection) ; s'il a été calculé avant la dernière
                    // modification, il reste à refaire
                    if (item.stamp >= since)
                        _stale.Remove(packed);
                    var entry = CreateEntry(item.chunk, item.generation);
                    entry.LastUsedFrame = old.LastUsedFrame;
                    if (old.Visible)
                        _visible.Remove(old);
                    FreeResources(old);
                    _cache[packed] = entry;
                }
            }
            else if (item.generation == _generation)
            {
                // Nouveau relief : mis de côté jusqu'à la bascule
                if (!_pending.ContainsKey(packed))
                    _pending[packed] = CreateEntry(item.chunk, item.generation);
            }
            // Sinon : calculé sur un relief abandonné
        }
    }

    private ChunkEntry CreateEntry(TerrainChunk chunk, int generation)
    {
        int count = TerrainChunkBuilder.VertexCount;
        var vertices = new Vector3[count];
        var normals = new Vector3[count];
        var water = new Vector2[count];
        var debris = new Vector2[count];
        for (int v = 0; v < count; v++)
        {
            debris[v] = new Vector2(chunk.Debris[v * 2], chunk.Debris[v * 2 + 1]);
            vertices[v] = new Vector3(chunk.Positions[v * 3], chunk.Positions[v * 3 + 1], chunk.Positions[v * 3 + 2]);
            normals[v] = new Vector3(chunk.Normals[v * 3], chunk.Normals[v * 3 + 1], chunk.Normals[v * 3 + 2]);
            water[v] = new Vector2(chunk.Water[v * 2], chunk.Water[v * 2 + 1]);
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Custom0] = chunk.Custom0;
        arrays[(int)Mesh.ArrayType.Custom1] = chunk.Custom1;
        arrays[(int)Mesh.ArrayType.TexUV] = water;
        arrays[(int)Mesh.ArrayType.TexUV2] = debris;
        arrays[(int)Mesh.ArrayType.Index] = TerrainChunkBuilder.Indices;
        long format = ((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift)
                      | ((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom1Shift);

        Rid mesh = RenderingServer.MeshCreate();
        RenderingServer.MeshAddSurfaceFromArrays(mesh, RenderingServer.PrimitiveType.Triangles, arrays,
                                                 EmptyBlendShapes, EmptyLods, (RenderingServer.ArrayFormat)format);
        RenderingServer.MeshSurfaceSetMaterial(mesh, 0, _material.GetRid());

        // Boîte englobante qui contient aussi ce que les shaders déplacent : sommets « morphés »
        // et surface de l'eau (posée jusqu'à des centaines de mètres au-dessus du fond d'un lac).
        // Avec la boîte du seul maillage, Godot jetait l'eau dès que le fond sortait du champ :
        // des carrés d'eau disparaissaient sous la caméra
        Aabb bounds = new(vertices[0], Vector3.Zero);
        for (int v = 0; v < count; v++)
        {
            Vector3 p = vertices[v];
            bounds = bounds.Expand(p);
            bounds = bounds.Expand(p + new Vector3(chunk.Custom0[v * 4], chunk.Custom0[v * 4 + 1], chunk.Custom0[v * 4 + 2]));
            float rise = chunk.Water[v * 2] - chunk.Custom0[v * 4 + 3];
            if (rise > 0)
            {
                Vector3 up = (chunk.Center + new Double3(p.X, p.Y, p.Z)).Normalized().ToVector3();
                bounds = bounds.Expand(p + up * rise);
            }
        }
        // (plus la marée et les variations du niveau de la mer, qui soulèvent la surface de la mer)
        bounds = bounds.Grow(1f + Tides.MaxHeightMeters + SeaLevel.MaxRiseMeters);

        Rid instance = RenderingServer.InstanceCreate2(mesh, GetWorld3D().Scenario);
        RenderingServer.InstanceSetCustomAabb(instance, bounds);
        RenderingServer.InstanceSetVisible(instance, false);
        RenderingServer.InstanceGeometrySetCastShadowsSetting(instance, ShadowSetting);
        // Surface de l'eau : même maillage, autre matériau (le shader pose les sommets au niveau de l'eau)
        Rid waterInstance = default;
        if (chunk.HasWater)
        {
            waterInstance = RenderingServer.InstanceCreate2(mesh, GetWorld3D().Scenario);
            RenderingServer.InstanceSetCustomAabb(waterInstance, bounds);
            RenderingServer.InstanceGeometrySetMaterialOverride(waterInstance, _waterMaterial.GetRid());
            RenderingServer.InstanceSetVisible(waterInstance, false);
            RenderingServer.InstanceGeometrySetCastShadowsSetting(waterInstance, RenderingServer.ShadowCastingSetting.Off);
        }

        var entry = new ChunkEntry
        {
            Key = chunk.Key,
            Center = chunk.Center,
            BoundsCenter = chunk.BoundsCenter,
            BoundsRadius = chunk.BoundsRadius,
            Mesh = mesh,
            Instance = instance,
            WaterInstance = waterInstance,
            LastUsedFrame = _frame,
            Generation = generation,
        };
        return entry;
    }

    private void Evict()
    {
        if (_cache.Count <= MaxCachedChunks)
            return;
        var candidates = new List<ChunkEntry>();
        foreach (var entry in _cache.Values)
            if (!entry.Visible && entry.Key.Level > 0 && entry.LastUsedFrame < _frame)
                candidates.Add(entry);
        candidates.Sort((a, b) => a.LastUsedFrame.CompareTo(b.LastUsedFrame));
        int toRemove = Math.Min(candidates.Count, _cache.Count - MaxCachedChunks + MaxCachedChunks / 10);
        for (int i = 0; i < toRemove; i++)
            FreeEntry(candidates[i]);
    }

    private void FreeEntry(ChunkEntry entry)
    {
        FreeResources(entry);
        _cache.Remove(entry.Key.Packed);
        _stale.Remove(entry.Key.Packed);
    }

    private static void FreeResources(ChunkEntry entry)
    {
        RenderingServer.FreeRid(entry.Instance);
        if (entry.WaterInstance.IsValid)
            RenderingServer.FreeRid(entry.WaterInstance);
        RenderingServer.FreeRid(entry.Mesh);
    }

    private void ClearCache()
    {
        while (_toFree.Count > 0)
            FreeResources(_toFree.Dequeue());
        foreach (var entry in _cache.Values)
            FreeResources(entry);
        _cache.Clear();
        _visible.Clear();
        _stale.Clear();
    }

    // --- Threads de calcul ----------------------------------------------------------------------

    private void WorkerLoop(int index)
    {
        while (!_stopping)
        {
            ChunkKey key;
            TerrainHeightField field;
            int generation, stamp;
            lock (_queueLock)
            {
                while (!_stopping && (_queue.Count == 0 || index >= _activeWorkers))
                    Monitor.Wait(_queueLock, 200);
                if (_stopping)
                    return;
                key = _queue.Dequeue();
                _queued.Remove(key.Packed);
                _inProgress.Add(key.Packed);
                field = Field;
                generation = _generation;
                stamp = _editStamp;
            }
            try
            {
                if (field != null)
                    _completed.Enqueue((TerrainChunkBuilder.Build(field, key), generation, stamp));
            }
            catch (Exception exception)
            {
                GD.PushError($"Terrain chunk {key} failed: {exception.Message}");
            }
            finally
            {
                lock (_queueLock)
                    _inProgress.Remove(key.Packed);
            }
        }
    }

    // --- Shader -----------------------------------------------------------------------------------

    // Uniformes et fonctions communs au sol et à l'eau
    private const string CommonShaderCode = @"
// Zone de morphing du morceau (distances à la caméra, m)
instance uniform vec2 morph_range = vec2(1e30, 2e30);
// Centre de la planète, relatif à la caméra (m)
uniform vec3 planet_center;
uniform float planet_radius;
// 0 : géologie ; 1 : couche de données ; 2 : vue réaliste ; 3 : carte d'altitude
uniform int coloring = 0;
// Numéro de la cellule la plus proche (une texture par face du cube), couleur des cellules,
// et climat des cellules (x : température °C, y : neige mm, z : altitude de la cellule m,
// w : densité de la canopée des forêts, 0 à 1, ou part de sable des déserts en négatif)
uniform sampler2DArray cell_index : filter_nearest, repeat_disable;
uniform sampler2D cell_colors : source_color, filter_nearest;
uniform sampler2D cell_data : filter_nearest;
uniform int texture_width = 1024;
// Pas du fondu entre cellules (texels de cell_index) et amplitude de la déformation des
// limites (radians), selon la taille des cellules de la simulation
uniform int cell_blend_step = 2;
uniform float cell_warp = 0.0035;
uniform bool has_climate = false;
// Position de la caméra modulo NOISE_PERIOD (m), calculée en double sur le processeur : les
// bruits de texture, périodiques, sont calculés en coordonnées relatives à la caméra (précises
// en float) mais restent fixés au sol
uniform vec3 noise_offset;
const int NOISE_PERIOD = 8192;

// Repère de chaque face du cube (identique à CubeSphere.cs)
" + ShaderLibrary.CubeFace + ShaderLibrary.Atmosphere + ShaderLibrary.Clouds + ShaderLibrary.Plumes + @"
// Direction du Soleil (repère de la planète)
uniform vec3 sun_direction = vec3(0.0, 1.0, 0.0);
// Marée (étape 11b) : direction de chaque astre (xyz) et amplitude K en m (w) ; la mer monte
// de K·(cos²θ − 1/3). Niveau moyen de la mer (m), qui suit le volume des glaces (étape 11d)
uniform vec4 tide_moon = vec4(0.0, 1.0, 0.0, 0.0);
uniform vec4 tide_sun = vec4(0.0, 1.0, 0.0, 0.0);
uniform float sea_level = 0.0;
// Plus haute mer possible au point le plus favorable (m) : limite haute de l'estran
uniform float tide_high = 0.0;
// Glaciations (étape 13c) : refroidissement global (°C, négatif) qui abaisse la limite des neiges
uniform float glacial_cooling = 0.0;
// (éruptions en cours : uniformes eruption_* de ShaderLibrary.Plumes)
// Volcans proches (direction, rayon de la base en m) : flancs de lave refroidie et de cendres
uniform int volcano_count = 0;
uniform vec4 volcano_sites[16];
// Coulée simulée (LavaFlow) autour de la bouche la plus proche : rouge = lave incandescente,
// vert = sol recouvert de lave ; grille posée sur le plan tangent (axes x, y), demi-côté en m
uniform sampler2D lava_map : filter_linear, repeat_disable;
uniform float lava_active = 0.0;
uniform vec3 lava_center = vec3(0.0, 1.0, 0.0);
uniform vec3 lava_axis_x = vec3(1.0, 0.0, 0.0);
uniform vec3 lava_axis_y = vec3(0.0, 0.0, 1.0);
uniform float lava_half_extent = 5120.0;
// Milieux hydrothermaux proches (étape 14a) : position par rapport à la caméra (m) et rayon (m) ;
// type (0 fumerolles, 1 source chaude, 2 mare de boue, 3 geyser), température (°C), pH, variation
uniform int habitat_count = 0;
uniform vec4 habitat_sites[16];
uniform vec4 habitat_kinds[16];
// Croûte modulaire (étape 16a) : jour du climat (le sol remanié reverdit en quelques dizaines
// d'années ; le jour de l'événement est stocké dans le maillage)
uniform float crust_day = 0.0;
uniform float crust_regrow_days = 9000.0;

float sea_surface(vec3 up) {
    float m = dot(up, tide_moon.xyz), s = dot(up, tide_sun.xyz);
    return sea_level + tide_moon.w * (m * m - 1.0 / 3.0) + tide_sun.w * (s * s - 1.0 / 3.0);
}

// Niveau de l'eau : celui du lac s'il y en a un (stocké dans le maillage, > 0), sinon la mer
float water_surface(vec3 up, float stored) {
    return stored > 0.01 ? stored : sea_surface(up);
}
// Lumière du Soleil arrivée jusqu'au point (atmosphère traversée), ombre des nuages
varying vec3 v_sun;
varying float v_shade;

varying float v_height;
varying vec3 v_world;
varying vec3 v_world_normal;
varying vec2 v_water;

// --- Bruit de texture (bruit de gradient périodique, avec ses dérivées : Inigo Quilez) ---
vec3 lattice_gradient(ivec3 c, int period) {
    c = ((c % period) + period) % period;
    uint h = uint(c.x) * 73856093u ^ uint(c.y) * 19349663u ^ uint(c.z) * 83492791u;
    h ^= h >> 15u; h *= 2246822519u; h ^= h >> 13u; h *= 3266489917u; h ^= h >> 16u;
    vec3 g = vec3(float(h & 1023u), float((h >> 10u) & 1023u), float((h >> 20u) & 1023u)) / 511.5 - 1.0;
    return g / max(length(g), 1e-3);
}

// Valeur (x, environ −1 à 1) et dérivées (yzw) ; période en mailles du réseau
vec4 gradient_noise(vec3 x, int period) {
    ivec3 i = ivec3(floor(x));
    vec3 f = fract(x);
    vec3 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    vec3 du = 30.0 * f * f * (f * (f - 2.0) + 1.0);
    vec3 ga = lattice_gradient(i, period);
    vec3 gb = lattice_gradient(i + ivec3(1, 0, 0), period);
    vec3 gc = lattice_gradient(i + ivec3(0, 1, 0), period);
    vec3 gd = lattice_gradient(i + ivec3(1, 1, 0), period);
    vec3 ge = lattice_gradient(i + ivec3(0, 0, 1), period);
    vec3 gf = lattice_gradient(i + ivec3(1, 0, 1), period);
    vec3 gg = lattice_gradient(i + ivec3(0, 1, 1), period);
    vec3 gh = lattice_gradient(i + ivec3(1, 1, 1), period);
    float va = dot(ga, f);
    float vb = dot(gb, f - vec3(1, 0, 0));
    float vc = dot(gc, f - vec3(0, 1, 0));
    float vd = dot(gd, f - vec3(1, 1, 0));
    float ve = dot(ge, f - vec3(0, 0, 1));
    float vf = dot(gf, f - vec3(1, 0, 1));
    float vg = dot(gg, f - vec3(0, 1, 1));
    float vh = dot(gh, f - vec3(1, 1, 1));
    float value = va + u.x * (vb - va) + u.y * (vc - va) + u.z * (ve - va)
        + u.x * u.y * (va - vb - vc + vd) + u.y * u.z * (va - vc - ve + vg)
        + u.z * u.x * (va - vb - ve + vf) + u.x * u.y * u.z * (-va + vb + vc - vd + ve - vf - vg + vh);
    vec3 derivative = ga + u.x * (gb - ga) + u.y * (gc - ga) + u.z * (ge - ga)
        + u.x * u.y * (ga - gb - gc + gd) + u.y * u.z * (ga - gc - ge + gg)
        + u.z * u.x * (ga - gb - ge + gf) + u.x * u.y * u.z * (-ga + gb + gc - gd + ge - gf - gg + gh)
        + du * (vec3(vb - va, vc - va, ve - va)
                + u.yzx * vec3(va - vb - vc + vd, va - vc - ve + vg, va - vb - ve + vf)
                + u.zxy * vec3(va - vb - ve + vf, va - vb - vc + vd, va - vc - ve + vg)
                + u.yzx * u.zxy * (-va + vb + vc - vd + ve - vf - vg + vh));
    return vec4(value * 1.6, derivative * 1.6);
}

// Bruit à l'échelle « scale » (m, diviseur de NOISE_PERIOD) ; dérivées en 1/m × scale
vec4 texture_noise(vec3 p, float scale) {
    return gradient_noise(p / scale, NOISE_PERIOD / int(scale));
}

// Poids d'un détail de taille « scale » selon la taille d'un pixel au sol : on efface ce qui
// serait plus petit que quelques pixels (sinon il scintille)
float detail_weight(float scale, float footprint) {
    return 1.0 - smoothstep(scale * 0.12, scale * 0.35, footprint);
}

// Couleur de la couche adoucie, pour la vue réaliste. La texture des numéros de cellules est en
// carrés de ~20 km : lue telle quelle, elle dessinait des damiers. On mélange les cellules des 4
// texels voisins (interpolation bilinéaire), en ne gardant que celles du même milieu que le
// point (alpha : 1 terre, 0 mer) : la côte détaillée ne suit pas celle des cellules, et sans ce
// tri la mer « débordait » en carrés bleus sur la terre. Le point est d'abord déplacé par un
// bruit (~20 km) : les limites entre biomes ondulent au lieu de suivre la grille.
// Les 4 cellules autour d'un point et leurs poids (interpolation bilinéaire sur un réseau de
// cell_blend_step texels de pas, ~0,8 fois l'écart entre cellules, après déplacement du point
// par un bruit)
void smooth_cells(vec3 d, out ivec4 cells, out vec4 weights) {
    // (taille des ondulations proportionnelle à leur amplitude : 300 et 1100 par radian pour
    // les petites cellules)
    float wf = 1.05 / cell_warp;
    vec4 w1 = gradient_noise(d * wf, 1 << 20);
    vec4 w2 = gradient_noise(d * wf + vec3(31.7, -12.3, 7.9), 1 << 20);
    vec4 w3 = gradient_noise(d * wf + vec3(-5.1, 19.4, -27.6), 1 << 20);
    vec4 w4 = gradient_noise(d * wf * 3.7, 1 << 20);
    d = normalize(d + cell_warp * vec3(w1.x, w2.x, w3.x) + cell_warp * 0.34 * w4.yzw);
    vec3 a = abs(d);
    int f = a.x >= a.y && a.x >= a.z ? (d.x > 0.0 ? 0 : 1) : (a.y >= a.z ? (d.y > 0.0 ? 2 : 3) : (d.z > 0.0 ? 4 : 5));
    float n = dot(d, FACE_NORMAL[f]);
    vec2 st = vec2(atan(dot(d, FACE_S[f]) / n), atan(dot(d, FACE_T[f]) / n)) * 4.0 / PI;
    int size = textureSize(cell_index, 0).x;
    float step_texels = float(cell_blend_step);
    vec2 texel = ((st * 0.5 + 0.5) * float(size) - 0.5) / step_texels;
    ivec2 base = ivec2(floor(texel)) * cell_blend_step;
    vec2 t = fract(texel);
    for (int k = 0; k < 4; k++) {
        ivec2 o = ivec2(k & 1, k >> 1);
        ivec2 c = clamp(base + o * cell_blend_step, ivec2(0), ivec2(size - 1));
        weights[k] = (o.x == 1 ? t.x : 1.0 - t.x) * (o.y == 1 ? t.y : 1.0 - t.y);
        cells[k] = int(texelFetch(cell_index, ivec3(c, f), 0).r + 0.5);
    }
}

ivec2 cell_pixel(int cell) {
    return ivec2(cell % texture_width, cell / texture_width);
}

// Couche de données (plaques, températures…) : les 4 cellules voisines mélangées
// (les cellules et leurs poids sont calculés une seule fois par pixel : smooth_cells coûte
// quatre bruits de gradient)
vec3 layer_color_blend(ivec4 cells, vec4 weights) {
    vec3 sum = vec3(0.0);
    for (int k = 0; k < 4; k++)
        sum += texelFetch(cell_colors, cell_pixel(cells[k]), 0).rgb * weights[k];
    return sum;
}

// Palette d'altitude (m), identique à celle de PlanetColorizer : abysses bleu nuit, plateau
// continental bleu clair, plaines vertes, plateaux ocre, montagnes brunes puis enneigées
const float ELEVATION_STOPS[13] = { -10000.0, -6000.0, -3000.0, -500.0, 0.0, 10.0, 300.0, 700.0, 1200.0, 2000.0, 3000.0, 4200.0, 5500.0 };
const vec3 ELEVATION_COLORS[13] = {
    vec3(0.02, 0.03, 0.15), vec3(0.05, 0.1, 0.35), vec3(0.1, 0.3, 0.6), vec3(0.3, 0.6, 0.85),
    vec3(0.55, 0.8, 0.9), vec3(0.2, 0.48, 0.22), vec3(0.35, 0.6, 0.28), vec3(0.6, 0.68, 0.35),
    vec3(0.78, 0.7, 0.42), vec3(0.7, 0.52, 0.3), vec3(0.52, 0.36, 0.22), vec3(0.55, 0.5, 0.47),
    vec3(0.97, 0.97, 1.0) };

vec3 elevation_color(float h) {
    if (h <= ELEVATION_STOPS[0])
        return ELEVATION_COLORS[0];
    for (int k = 1; k < 13; k++) {
        if (h <= ELEVATION_STOPS[k]) {
            float t = (h - ELEVATION_STOPS[k - 1]) / (ELEVATION_STOPS[k] - ELEVATION_STOPS[k - 1]);
            return mix(ELEVATION_COLORS[k - 1], ELEVATION_COLORS[k], t);
        }
    }
    return ELEVATION_COLORS[12];
}

vec3 layer_color_smooth_cells(ivec4 cells, vec4 weights, bool land) {
    vec3 sum = vec3(0.0);
    float total = 0.0;
    vec3 fallback = vec3(0.0);
    float fallbackTotal = 0.0;
    for (int k = 0; k < 4; k++) {
        vec4 color = texelFetch(cell_colors, cell_pixel(cells[k]), 0);
        if ((color.a > 0.5) == land) {
            sum += color.rgb * weights[k];
            total += weights[k];
        } else {
            fallback += color.rgb * weights[k];
            fallbackTotal += weights[k];
        }
    }
    if (total > 1e-4)
        return sum / total;
    // Aucune cellule du bon milieu tout près : teinte neutre plutôt que la couleur de l'autre
    return land ? vec3(0.45, 0.42, 0.34) : fallback / max(fallbackTotal, 1e-4);
}

vec3 layer_color_smooth(vec3 d, bool land) {
    ivec4 cells;
    vec4 weights;
    smooth_cells(d, cells, weights);
    return layer_color_smooth_cells(cells, weights, land);
}



// Climat des cellules voisines, mélangé (lu cellule par cellule, la limite de la neige
// dessinait des marches d'escalier)
vec4 climate_data(ivec4 cells, vec4 weights) {
    if (!has_climate)
        return vec4(0.0);
    vec4 data = vec4(0.0);
    for (int k = 0; k < 4; k++)
        data += texelFetch(cell_data, cell_pixel(cells[k]), 0) * weights[k];
    return data;
}

// Part du sol enneigée : climat simulé (température corrigée de l'altitude, neige de la cellule)
// ou, sans climat, limite de la neige selon la latitude
// (sans tenir compte de la pente : voir snow_on_slope)
float snow_cover(vec4 data, vec3 up, float h) {
    float snow;
    if (has_climate) {
        float t = data.x - 0.0065 * (h - max(data.z, 0.0)) + glacial_cooling;
        float lying = data.y > 10.0 ? 1.0 : smoothstep(-2.0, -6.0, t);
        snow = smoothstep(0.5, -2.5, t) * lying;
    } else {
        float latitude = abs(up.y);
        float snowline = mix(5200.0, 400.0, smoothstep(0.25, 0.95, latitude)) + glacial_cooling / 0.0065;
        snow = smoothstep(snowline, snowline + 400.0, h);
    }
    return snow;
}

// Lave des éruptions en cours. Autour de la bouche la plus proche : la coulée simulée comme un
// fluide (LavaFlow, sur le processeur), qui suit le relief. Aux autres bouches : un petit lac de
// lave dans le cratère. Renvoie (part de lave incandescente, part du sol couverte de lave).
vec2 lava_state(vec3 up, float h, vec4 n128) {
    vec2 lava = vec2(0.0);
    if (lava_active > 0.5 && dot(up, lava_center) > 0.0) {
        vec2 uv = vec2(dot(up, lava_axis_x), dot(up, lava_axis_y)) * planet_radius / (2.0 * lava_half_extent) + 0.5;
        if (all(greaterThan(uv, vec2(0.0))) && all(lessThan(uv, vec2(1.0)))) {
            vec2 texel = texture(lava_map, uv).rg;
            // Bords nets (la texture est interpolée entre cellules de 40 m) et ondulés par le bruit
            float edge = (n128.x - 0.5) * 0.25;
            lava.y = smoothstep(0.35, 0.6, texel.g + edge);
            lava.x = smoothstep(0.05, 0.5, texel.r + edge * 0.5) * lava.y;
        }
    }
    for (int k = 0; k < eruption_count; k++) {
        vec3 vent = eruption_vents[k].xyz;
        float radius = eruption_shapes[k].x;
        // (pas de lave sous les fumerolles ni pour la bouche simulée)
        if (radius <= 0.0 || eruption_shapes[k].z < -0.5 || (lava_active > 0.5 && dot(vent, lava_center) > 0.9999999))
            continue;
        // (corde plutôt qu'arc : en 32 bits, acos d'un cosinus proche de 1 ne distingue pas
        // les distances de moins de ~2 km ; à cette échelle, corde et arc sont égaux)
        float distance = length(up - vent) * planet_radius;
        float pool = (1.0 - smoothstep(0.5, 1.0, distance / 350.0 + (n128.y - 0.5) * 0.4)) * eruption_vents[k].w;
        lava = max(lava, vec2(pool));
    }
    return clamp(lava, 0.0, 1.0);
}

// La neige ne tient pas sur les pentes trop raides
float snow_on_slope(float snow, float slope) {
    return snow * (1.0 - smoothstep(0.35, 0.55, slope));
}

// Morphing : 0 près de la caméra, 1 à la distance où le morceau parent prend le relais
float morph_factor(vec3 vertex, mat4 model, vec3 camera) {
    float d = length((model * vec4(vertex, 1.0)).xyz - camera);
    return clamp((d - morph_range.x) / max(morph_range.y - morph_range.x, 1e-3), 0.0, 1.0);
}

// Ombre des nuages : densité des nuages là où le rayon du Soleil traverse la couche
float cloud_shadow(vec3 world, vec3 up, float h) {
    if (!clouds_enabled)
        return 1.0;
    float mus = dot(up, sun_direction);
    if (mus <= 0.03)
        return 1.0;
    vec3 base_point = camera_planet + world;
    vec2 cover = cloud_cover_at(normalize(base_point + sun_direction * ((CLOUD_BASE + 1000.0 - h) / mus)));
    float height = mix(CLOUD_BASE, cloud_top(cover), 0.3);
    vec3 q = base_point + sun_direction * ((height - h) / mus);
    // (détails estompés selon la distance, comme pour les nuages eux-mêmes)
    float density = cloud_density(q, height, cover, length(world) * 0.0015);
    return 1.0 - 0.8 * smoothstep(0.02, 0.4, density);
}

// Ombre des panaches volcaniques : on remonte vers le Soleil jusqu'à l'altitude de la traînée,
// puis jusqu'à mi-hauteur de la colonne, et on regarde si l'on tombe dans la forme du panache
float plume_shadow(vec3 world, vec3 up, float h) {
    float mus = dot(up, sun_direction);
    if (eruption_count == 0 || mus <= 0.05)
        return 1.0;
    vec3 base_point = camera_planet + world;
    float shade = 1.0;
    for (int k = 0; k < eruption_count; k++) {
        // (les petits évents ne font pas d'ombre notable)
        if (eruption_vents[k].w <= 0.0 || eruption_shapes[k].y <= 0.0 || eruption_shapes[k].z < -1.5)
            continue;
        Plume P = plume_params(k);
        float feature, thin, trail, safe;
        float vent_height = eruption_shapes[k].w;
        float e = plume_envelope(P, base_point + sun_direction * ((vent_height + P.top - h) / mus), feature, thin, trail, safe);
        float density = (1.0 - smoothstep(0.6, 1.1, e)) * thin;
        e = plume_envelope(P, base_point + sun_direction * ((vent_height + 0.4 * P.height - h) / mus), feature, thin, trail, safe);
        density = max(density, 1.0 - smoothstep(0.6, 1.1, e));
        shade *= 1.0 - (P.steam ? 0.3 : 0.75) * density * eruption_vents[k].w;
    }
    return shade;
}

// Éclairage : Lambert et reflet de Blinn-Phong, la lumière du Soleil étant teintée par
// l'atmosphère traversée (rouge au coucher, nulle derrière la planète) et par l'ombre des nuages
void light() {
    float ndl = clamp(dot(NORMAL, LIGHT), 0.0, 1.0);
    vec3 tint = v_sun * v_shade;
    DIFFUSE_LIGHT += ndl * ATTENUATION * LIGHT_COLOR * tint / PI;
    vec3 half_vector = normalize(VIEW + LIGHT);
    float shininess = 2.0 / max(pow(ROUGHNESS, 4.0), 1e-4) - 2.0;
    float spec = pow(max(dot(NORMAL, half_vector), 0.0), shininess) * (shininess + 8.0) / (8.0 * PI);
    float fresnel = SPECULAR_AMOUNT + (1.0 - SPECULAR_AMOUNT) * pow(1.0 - clamp(dot(VIEW, half_vector), 0.0, 1.0), 5.0);
    SPECULAR_LIGHT += spec * fresnel * ndl * ATTENUATION * LIGHT_COLOR * tint / PI;
}
";

    public const string ShaderCode = @"
shader_type spatial;
render_mode cull_back;
" + CommonShaderCode + @"
// Sol remanié par la croûte modulaire : x (−1 arraché, +1 éboulis), y : jour de l'événement
varying vec2 v_debris;

void vertex() {
    float m = morph_factor(VERTEX, MODEL_MATRIX, vec3(0.0));
    VERTEX += CUSTOM0.xyz * m;
    vec3 n = normalize(mix(NORMAL, CUSTOM1.xyz, m));
    v_height = mix(CUSTOM0.w, CUSTOM1.w, m);
    v_water = UV;
    v_debris = UV2;
    NORMAL = n;
    v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    v_world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
    v_sun = sun_transmittance(v_height, dot(normalize(v_world - planet_center), sun_direction));
}

void fragment() {
    vec3 up = normalize(v_world - planet_center);
    vec3 normal = normalize(v_world_normal);
    float h = v_height;
    float depth = water_surface(up, v_water.x) - h;   // > 0 : sous l'eau (mer ou lac)
    vec3 p = v_world + noise_offset;       // position (m), modulo la période des bruits
    float footprint = length(fwidth(v_world));

    // Bruits de texture : régions (2 km, 512 m), taches (128 m), grain (32 m, 8 m, 2 m)
    vec4 n2048 = texture_noise(p, 2048.0);
    vec4 n512 = texture_noise(p, 512.0);
    vec4 n128 = texture_noise(p, 128.0) * detail_weight(128.0, footprint);
    vec4 n32 = texture_noise(p, 32.0) * detail_weight(32.0, footprint);
    vec4 n8 = texture_noise(p, 8.0) * detail_weight(8.0, footprint);
    vec4 n2 = texture_noise(p, 2.0) * detail_weight(2.0, footprint);

    float slope0 = 1.0 - clamp(dot(normal, up), 0.0, 1.0);
    float rockiness = smoothstep(0.12, 0.3, slope0 + 0.08 * n128.x);
    // Micro-relief plus fin que le maillage : on penche la normale selon la pente des bruits
    // (roche rugueuse, sédiments plus doux, rien sous la neige ni au fond de l'eau)
    float roughGround = mix(0.10, 0.28, rockiness) * (depth > 0.0 ? 0.3 : 1.0);
    vec3 bump = (n32.yzw * 0.8 + n8.yzw * 0.6 + n2.yzw * 0.4) * roughGround;
    // Cellules voisines et poids du fondu, une fois pour toutes (couleurs, neige, canopée)
    ivec4 cells = ivec4(0);
    vec4 weights = vec4(0.0);
    if (coloring == 1 || coloring == 2 || (coloring == 0 && has_climate))
        smooth_cells(up, cells, weights);
    vec4 cdata = coloring == 2 || coloring == 0 ? climate_data(cells, weights) : vec4(0.0);
    float snowBase = coloring == 1 || coloring == 3 ? 0.0 : snow_cover(cdata, up, h);
    // Canopée des forêts (w > 0) ou part de sable des déserts (w < 0), vue réaliste (étape 12a)
    float canopy = coloring == 2 ? max(cdata.w, 0.0) : 0.0;
    float desertSand = coloring == 2 ? max(-cdata.w, 0.0) : 0.0;
    // Sol remanié par la croûte modulaire, encore frais (étape 16a) : ni forêt ni sable dessus
    // (x < 0 : sol arraché ; 0 à 1 : éboulis ; 2 : lave figée, qui reste nue des siècles)
    float crustFresh = abs(v_debris.x) > 0.02
        ? clamp(abs(v_debris.x), 0.0, 1.0)
          * (1.0 - smoothstep(0.0, crust_regrow_days * (v_debris.x > 1.2 ? 30.0 : 1.0), crust_day - v_debris.y)) : 0.0;
    canopy *= 1.0 - crustFresh;
    desertSand *= 1.0 - crustFresh;
    float snowEstimate = snow_on_slope(snowBase, slope0);
    // Sable (plages, déserts) : dunes (~32 m) et rides (~0,5 m), crêtes ondulées par les bruits.
    // Coordonnée x + z : les deux sont périodiques de NOISE_PERIOD, multiple des longueurs
    // d'onde, donc pas de saut quand la position de la caméra repart de zéro
    float sandy = max(desertSand * (1.0 - rockiness),
                      (1.0 - smoothstep(1.0, 6.0, h)) * (1.0 - smoothstep(0.1, 0.25, slope0)) * (coloring == 1 || coloring == 3 ? 0.0 : 1.0));
    sandy *= depth > 0.0 ? 0.0 : 1.0 - snowEstimate;
    vec3 ridge_axis = vec3(1.0, 0.0, 1.0) - up * dot(vec3(1.0, 0.0, 1.0), up);
    float dune_phase = 6.2832 * (p.x + p.z + 14.0 * n128.x + 45.0 * n512.x) / 32.0;
    float ripple_phase = 6.2832 * (p.x + p.z + 0.25 * n2.x + 1.5 * n8.x) / 0.5;
    // Profil dissymétrique des dunes : pente douce au vent, raide sous le vent
    float dune_slope = cos(dune_phase) + 0.35 * cos(2.0 * dune_phase);
    bump += ridge_axis * sandy * (0.22 * dune_slope * desertSand * detail_weight(32.0, footprint)
                                  + 0.12 * cos(ripple_phase) * detail_weight(0.5, footprint));
    // Canopée : houppiers des arbres (~8 m) vus d'avion
    bump += (n8.yzw * 0.9 + n2.yzw * 0.3) * canopy * (1.0 - snowEstimate);
    bump *= 1.0 - 0.8 * snowEstimate;
    normal = normalize(normal - (bump - up * dot(bump, up)));

    float flatness = clamp(dot(normal, up), 0.0, 1.0);
    float slope = 1.0 - flatness;          // 0 : plat ; 0,3 : ~45° ; 1 : vertical
    vec3 color;
    float roughness = 0.95;
    float specular = 0.25;
    vec3 sand = vec3(0.76, 0.70, 0.55);
    // Deux familles de roches selon la région (granite gris, grès ocre), en strates sur les pentes
    vec3 rock = mix(vec3(0.42, 0.41, 0.40), vec3(0.52, 0.42, 0.32), smoothstep(-0.3, 0.4, n2048.x));
    float strata = sin(6.2832 * (h + 35.0 * n512.x + 6.0 * n32.x) / 23.0)
                   + 0.5 * sin(6.2832 * (h + 10.0 * n128.x) / 6.5);
    rock *= 1.0 + 0.10 * strata * detail_weight(10.0, footprint);
    vec3 cliff = rock * 0.72;

    if (coloring == 1) {
        color = layer_color_blend(cells, weights);
    } else if (coloring == 3) {
        color = elevation_color(h);
    } else if (depth > 0.0) {
        // Fonds : sable près des côtes, vase sombre au large (l'eau est dessinée par-dessus)
        color = mix(sand, vec3(0.30, 0.30, 0.26), smoothstep(5.0, 200.0, depth));
        color = mix(color, rock, smoothstep(0.25, 0.5, slope));
        color *= 1.0 + 0.08 * n128.x + 0.06 * n8.x;
    } else {
        if (coloring == 2) {
            // Vue réaliste : couleur du biome, roche nue sur les pentes raides
            color = layer_color_smooth_cells(cells, weights, true);
            color *= 1.0 + 0.10 * n512.x + 0.08 * n128.x;
            // Déserts de sable (étape 12a) : dunes claires, crêtes et creux
            // (sable des déserts plus ocre que celui des plages : vus de l'espace, ils viraient au blanc)
            vec3 desert = mix(vec3(0.66, 0.50, 0.32), vec3(0.72, 0.60, 0.42), smoothstep(-0.4, 0.4, n2048.x));
            color = mix(color, desert * (1.0 + 0.06 * dune_slope * detail_weight(32.0, footprint)), desertSand * (1.0 - rockiness) * 0.7);
            // Forêts : houppiers et trouées d'ombre entre les arbres ; de loin, une teinte plus sombre
            float crowns = 0.5 + 0.5 * clamp(n8.x * 1.4 + n32.x * 0.6, -1.0, 1.0);
            color *= mix(1.0, mix(0.55, 1.12, crowns), canopy * detail_weight(16.0, footprint));
            color *= mix(1.0, 0.85, canopy);
            color = mix(color, sand, (1.0 - smoothstep(1.0, 6.0, h)) * (1.0 - smoothstep(0.1, 0.25, slope)));
        } else {
            // Géologie seule (pas de végétation) : sédiments, sols nus, roche
            vec3 lowland = mix(vec3(0.50, 0.43, 0.33), vec3(0.45, 0.42, 0.36), smoothstep(-0.4, 0.4, n2048.x));
            vec3 upland = vec3(0.56, 0.49, 0.39);
            color = mix(lowland, upland, smoothstep(200.0, 1500.0, h + 150.0 * n512.x));
            color = mix(color, rock, smoothstep(1500.0, 3500.0, h + 300.0 * n512.x));
            color = mix(sand, color, smoothstep(2.0, 25.0, h) + smoothstep(0.08, 0.2, slope));
            // Taches de sol plus ou moins clair, cailloux
            color *= 1.0 + 0.09 * n512.x + 0.07 * n128.x + 0.05 * n8.x;
        }
        // Roche fissurée : réseau de diaclases (lignes où un bruit s'annule)
        float cracks = 1.0 - smoothstep(0.0, 0.06, abs(n8.x + 0.5 * n32.x));
        vec3 cracked = rock * (1.0 - 0.35 * cracks * detail_weight(4.0, footprint));
        color = mix(color, cracked, rockiness);
        color = mix(color, cliff * (1.0 - 0.3 * cracks * detail_weight(4.0, footprint)), smoothstep(0.38, 0.62, slope));
        // Éboulis : grain clair et sombre sur la roche
        color *= 1.0 + rockiness * (0.08 * n2.x + 0.06 * n8.x);
        // Croûte modulaire (étape 16a) : niche d'arrachement et couloir en sol frais (terre et
        // roche à nu, sans végétation), dépôt en éboulis de blocs ; la végétation reprend ensuite
        if (crustFresh > 0.0) {
            vec3 soil = mix(vec3(0.33, 0.24, 0.16), rock * 0.85, smoothstep(0.15, 0.4, slope));
            soil *= 1.0 + 0.10 * n8.x + 0.06 * n2.x;
            // Blocs : taches claires et sombres de tailles variées, ombres entre les blocs
            float blocks = clamp(n2.x * 1.6 + n8.x * 0.8, -1.0, 1.0);
            vec3 rubble = mix(rock * 0.5, rock * 1.0, 0.5 + 0.5 * blocks);
            rubble = mix(rubble, vec3(0.34, 0.28, 0.21), 0.3);
            vec3 basalt = vec3(0.075, 0.07, 0.068) * (1.0 + 0.35 * n2.x + 0.2 * n8.x);
            color = mix(color, v_debris.x < 0.0 ? soil : v_debris.x > 1.2 ? basalt : rubble, crustFresh);
        }
        float snow = snow_on_slope(snowBase, slope);
        // La neige s'accroche d'abord dans les creux du micro-relief
        snow = clamp(snow + (snow * (1.0 - snow)) * 1.5 * n32.x, 0.0, 1.0);
        // Sol tout juste remanié (croûte modulaire) : la neige a glissé avec lui ; elle ne le
        // recouvre qu'au fil des chutes suivantes (quelques semaines)
        if (abs(v_debris.x) > 0.02)
            snow *= mix(1.0, smoothstep(0.0, 60.0, crust_day - v_debris.y), clamp(abs(v_debris.x), 0.0, 1.0));
        color = mix(color, vec3(0.86, 0.88, 0.92) * (1.0 + 0.03 * n8.x), snow);
        roughness = mix(0.95, 0.6, snow);
        // Estran (étape 11b) : sable mouillé, plus sombre et brillant, là où la mer vient de se
        // retirer ; un peu humide jusqu'à la limite des plus hautes mers
        if (v_water.x <= 0.01) {
            float above = h - sea_surface(up);
            float wet = (1.0 - smoothstep(0.0, 0.7, above)) * (1.0 - smoothstep(0.25, 0.45, slope));
            float damp = 1.0 - smoothstep(tide_high + sea_level, tide_high + sea_level + 0.4, h);
            color *= mix(1.0, 0.86, damp * (1.0 - snowEstimate));
            color *= mix(1.0, 0.62, wet);
            roughness = mix(roughness, 0.3, wet);
            specular = mix(specular, 0.5, wet);
        }
        // Fleuves (étape 12c) : l'eau coule vers l'aval (le sens de la pente du maillage), plus
        // vite là où ça descend ; vaguelettes entraînées par le courant (deux motifs en fondu
        // enchaîné, chacun repartant de zéro quand son poids est nul : sinon le bruit s'étirerait
        // à l'infini) ; eaux blanches dans les rapides, cascades sur les pentes raides
        float river = v_water.y;
        if (river > 0.01) {
            vec3 n0 = normalize(v_world_normal);
            vec3 downhill = n0 - up * dot(n0, up);
            float steep = length(downhill);
            vec3 flow = steep > 1e-4 ? downhill / steep : vec3(0.0);
            float speed = mix(0.7, 4.0, smoothstep(0.0, 0.25, steep));
            const float FLOW_CYCLE = 2.0;
            float t1 = fract(TIME / FLOW_CYCLE), t2 = fract(TIME / FLOW_CYCLE + 0.5);
            float w1 = 1.0 - abs(2.0 * t1 - 1.0);
            vec4 r1 = texture_noise(p - flow * speed * t1 * FLOW_CYCLE, 2.0);
            vec4 r2 = texture_noise(p - flow * speed * t2 * FLOW_CYCLE + vec3(3.7, 1.3, 5.1), 2.0);
            vec4 ripples = (r1 * w1 + r2 * (1.0 - w1)) * detail_weight(2.0, footprint);
            float rapids = smoothstep(0.12, 0.32, steep) * (0.55 + 0.45 * ripples.x);
            vec3 water_color = mix(vec3(0.12, 0.30, 0.38), vec3(0.85, 0.9, 0.92), clamp(rapids, 0.0, 1.0));
            color = mix(color, water_color, river);
            roughness = mix(roughness, mix(0.12, 0.6, rapids), river);
            specular = mix(specular, 0.5, river);
            vec3 water_normal = normalize(up - (ripples.yzw - up * dot(ripples.yzw, up)) * (0.08 + 0.25 * rapids));
            normal = normalize(mix(normal, water_normal, river));
        }
    }

    // Volcans : flancs sombres (lave refroidie, cendres), ravines claires et sombres
    if (volcano_count > 0 && depth <= 0.0 && coloring != 1 && coloring != 3) {
        float volcanic = 0.0;
        for (int k = 0; k < volcano_count; k++) {
            float distance = length(up - volcano_sites[k].xyz) * planet_radius;
            // (bord découpé par le bruit : des langues de lave et de cendres, pas un disque)
            volcanic = max(volcanic, 1.0 - smoothstep(0.45, 0.95, distance / volcano_sites[k].w + (n128.x - 0.5) * 0.6));
        }
        if (volcanic > 0.0) {
            vec3 ash = mix(vec3(0.17, 0.15, 0.14), vec3(0.33, 0.29, 0.26), smoothstep(-0.3, 0.4, n128.x + 0.5 * n32.x));
            color = mix(color, ash, volcanic * 0.8 * (1.0 - snowEstimate));
        }
    }
    // Milieux hydrothermaux : bassins, dépôts minéraux et tapis microbiens
    if (habitat_count > 0 && depth <= 0.0 && coloring != 1 && coloring != 3) {
        for (int k = 0; k < habitat_count; k++) {
            vec3 r = v_world - habitat_sites[k].xyz;
            r -= up * dot(r, up);
            float radius = habitat_sites[k].w;
            float d = length(r);
            if (d > 3.6 * radius)
                continue;
            // Contour irrégulier (lobes à l'échelle du bassin, festons fins)
            float angle = atan(r.z, r.x) + habitat_kinds[k].w * 6.28;
            float q = d / radius * (1.0 + 0.16 * sin(3.0 * angle) + 0.09 * sin(7.0 * angle + 1.7)) + (n8.x - 0.5) * 0.12;
            float kind = habitat_kinds[k].x, heat = clamp((habitat_kinds[k].y - 40.0) / 55.0, 0.0, 1.0), ph = habitat_kinds[k].z;
            if (kind < 0.5) {
                // Fumerolles : roche blanchie par les gaz acides, plaques de soufre jaune autour des évents
                float field = 1.0 - smoothstep(0.5, 1.0, q);
                float sulfur = field * smoothstep(0.5, 0.62, n32.x * 0.6 + n8.y * 0.4 + 0.25 * (1.0 - q));
                color = mix(color, vec3(0.74, 0.71, 0.64), field * 0.6);
                color = mix(color, vec3(0.86, 0.74, 0.16), sulfur * 0.85);
            } else if (kind < 1.5) {
                // Source chaude : terrasse de dépôts clairs, puis anneaux de tapis microbiens (bruns,
                // orange, jaunes : chaque couleur vit à sa température), puis l'eau, turquoise au
                // bord et bleu profond au centre, là où elle est trop chaude pour la vie
                float terrace = (1.0 - smoothstep(1.2, 3.2, q)) * 0.75;
                color = mix(color, ph < 5.0 ? vec3(0.72, 0.7, 0.6) : vec3(0.84, 0.8, 0.72), terrace);
                if (ph >= 5.0) {
                    float mats = smoothstep(0.72, 0.84, q) * (1.0 - smoothstep(1.15, 1.45, q)) * mix(0.5, 1.0, heat);
                    vec3 mat = mix(vec3(0.93, 0.78, 0.2), mix(vec3(0.9, 0.45, 0.08), vec3(0.42, 0.2, 0.08), smoothstep(1.0, 1.35, q)),
                                   smoothstep(0.8, 0.98, q));
                    color = mix(color, mat * (0.85 + 0.3 * n8.z), mats);
                }
                float water = 1.0 - smoothstep(0.74, 0.8, q);
                // (source acide : eau laiteuse vert-gris, sans tapis colorés)
                vec3 pool = ph < 5.0 ? mix(vec3(0.42, 0.56, 0.5), vec3(0.6, 0.7, 0.62), smoothstep(0.2, 0.75, q))
                                     : mix(vec3(0.02, 0.13, 0.4), vec3(0.1, 0.62, 0.68), smoothstep(0.15, 0.72, q));
                color = mix(color, pool, water);
                roughness = mix(roughness, 0.06, water);
                specular = mix(specular, 0.6, water);
                normal = normalize(mix(normal, up, water));
            } else if (kind < 2.5) {
                // Mare de boue : argile grise qui bouillonne (bulles en anneaux)
                float mud = 1.0 - smoothstep(0.85, 1.0, q);
                float bubbles = smoothstep(0.55, 0.7, n2.x + n8.x * 0.5);
                color = mix(color, vec3(0.5, 0.48, 0.44), (1.0 - smoothstep(1.0, 2.2, q)) * 0.6);
                color = mix(color, vec3(0.4, 0.38, 0.35) * (1.0 - 0.25 * bubbles), mud);
                roughness = mix(roughness, 0.3, mud);
                normal = normalize(mix(normal, up, mud * 0.8));
            } else {
                // Geyser : cône et tablier de geysérite (silice blanche), bouche sombre, flaques
                float apron = 1.0 - smoothstep(1.5, 3.4, q);
                color = mix(color, vec3(0.86, 0.85, 0.81) * (0.9 + 0.2 * n8.y), apron * 0.85);
                float puddles = apron * smoothstep(0.6, 0.7, n32.y + 0.3 * n8.x) * smoothstep(1.0, 1.6, q);
                color = mix(color, vec3(0.25, 0.5, 0.58), puddles * 0.7);
                roughness = mix(roughness, 0.1, puddles);
                color = mix(color, vec3(0.08, 0.1, 0.12), 1.0 - smoothstep(0.25, 0.45, q));
            }
        }
    }
    // Éruptions : basalte noir sous la lave, lave incandescente qui éclaire d'elle-même
    if ((eruption_count > 0 || lava_active > 0.5) && depth <= 0.0) {
        vec2 lava = lava_state(up, h, n128);
        if (lava.y > 0.0) {
            // Croûte sombre qui se craquelle : la lumière sort par les fissures et les fronts
            float cracks = smoothstep(0.45, 0.6, n32.x * 0.6 + n128.z * 0.4 + lava.x * 0.35);
            float heat = lava.x * mix(0.12, 1.0, cracks);
            color = mix(color, vec3(0.05, 0.04, 0.035), lava.y);
            EMISSION = vec3(1.0, 0.32, 0.05) * heat * 3.0;
            roughness = mix(roughness, 0.7, lava.y);
        }
    }
    NORMAL = normalize((VIEW_MATRIX * vec4(normal, 0.0)).xyz);
    v_shade = cloud_shadow(v_world, up, h) * plume_shadow(v_world, up, h);
    ALBEDO = color;
    ROUGHNESS = roughness;
    SPECULAR = specular;
}
";

    /// <summary>
    /// Surface de l'eau : le même maillage que le sol, chaque sommet étant déplacé à la verticale
    /// jusqu'au niveau de l'eau (calcul en coordonnées locales du morceau : pas de perte de
    /// précision). Là où le sol est plus haut que l'eau, la surface passe sous le sol et reste
    /// cachée. Transparence selon la profondeur : on devine les fonds près des côtes.
    /// </summary>
    public static string WaterShaderCode => @"
shader_type spatial;
render_mode blend_mix, cull_back, depth_draw_opaque;
const int GRID_VERTICES = " + (TerrainChunkBuilder.Side * TerrainChunkBuilder.Side) + @";
" + CommonShaderCode + @"
varying float v_depth;
// 1 sur les jupes (sommets ajoutés après la grille) : pas d'eau dessus, sinon on les voyait
// en traits sombres à travers l'eau transparente, le long des bords des morceaux
varying float v_skirt;

void vertex() {
    v_skirt = VERTEX_ID >= GRID_VERTICES ? 1.0 : 0.0;
    float m = morph_factor(VERTEX, MODEL_MATRIX, vec3(0.0));
    VERTEX += CUSTOM0.xyz * m;
    vec3 n = normalize(mix(NORMAL, CUSTOM1.xyz, m));
    v_height = mix(CUSTOM0.w, CUSTOM1.w, m);
    v_water = UV;
    vec3 up = normalize((MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz - planet_center);
    v_depth = water_surface(up, v_water.x) - v_height;
    VERTEX += up * v_depth;
    NORMAL = up;
    v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    v_sun = sun_transmittance(v_water.x, dot(up, sun_direction));
}

void fragment() {
    if (v_depth <= 0.0 || v_skirt > 0.001) {
        discard;
    }
    vec3 up = normalize(v_world - planet_center);
    // Vaguelettes : deux bruits qui dérivent avec le temps
    float footprint = length(fwidth(v_world));
    vec3 p = v_world + noise_offset;
    vec3 waves = texture_noise(p + vec3(TIME * 0.6, 0.0, TIME * 0.4), 8.0).yzw * detail_weight(8.0, footprint)
               + texture_noise(p - vec3(TIME * 0.5, TIME * 0.2, 0.0), 2.0).yzw * 0.6 * detail_weight(2.0, footprint)
               + texture_noise(p + vec3(0.0, TIME * 0.3, TIME * 1.1), 32.0).yzw * 0.5 * detail_weight(32.0, footprint);
    // Houle du large (~128 m et ~512 m), plus marquée en eau profonde, qui s'atténue sur les
    // hauts-fonds (étape 12c)
    float open_sea = smoothstep(2.0, 30.0, v_depth) * (v_water.x <= 0.01 ? 1.0 : 0.3);
    vec3 swell = (texture_noise(p + vec3(TIME * 2.5, 0.0, TIME * 1.2), 128.0).yzw * detail_weight(128.0, footprint)
                + texture_noise(p - vec3(TIME * 1.1, 0.0, TIME * 3.0), 512.0).yzw * 0.7) * open_sea;
    waves += swell * 1.4;
    vec3 water_normal = normalize(up - (waves - up * dot(waves, up)) * 0.06);
    NORMAL = normalize((VIEW_MATRIX * vec4(water_normal, 0.0)).xyz);
    // Couleur de l'eau, plus sombre et plus saturée que le sol (avant, de loin, eau peu
    // profonde et terre se confondaient) : turquoise sur les hauts-fonds, bleu profond au large
    vec3 color = mix(vec3(0.05, 0.36, 0.40), vec3(0.02, 0.14, 0.26), smoothstep(0.0, 40.0, v_depth));
    color = mix(color, vec3(0.008, 0.04, 0.11), smoothstep(40.0, 2000.0, v_depth));
    if (coloring == 2) {
        // Vue réaliste : banquise et teinte de la couche (océans gelés en blanc)
        color = mix(color, layer_color_smooth(up, false), 0.35);
    }
    // Écume sur les côtes de la mer (étape 12c) : les vagues déferlent quand le fond remonte,
    // en rouleaux qui avancent vers le rivage ; plus une frange d'écume au bord de l'eau
    float foam = 0.0;
    if (v_water.x <= 0.01) {
        float shore = 1.0 - smoothstep(0.0, 1.8, v_depth);
        shore *= shore;
        float breaker = smoothstep(0.55, 0.95, 0.5 + 0.5 * sin(v_depth * 5.0 + TIME * 1.7
                         + 2.0 * texture_noise(p, 32.0).x * detail_weight(32.0, footprint)));
        float lace = smoothstep(0.0, 0.6, texture_noise(p + vec3(0.0, 0.0, TIME * 0.4), 2.0).x) * detail_weight(2.0, footprint);
        foam = shore * max(breaker * (0.4 + 0.6 * lace), 1.0 - smoothstep(0.0, 0.35, v_depth));
        // vu de loin, les rouleaux se fondent en une frange blanche
        foam = mix(shore * 0.35, foam, detail_weight(4.0, footprint));
    }
    color = mix(color, vec3(0.92, 0.95, 0.97), foam);
    v_shade = cloud_shadow(v_world, up, v_water.x) * plume_shadow(v_world, up, v_water.x);
    ALBEDO = color;
    ROUGHNESS = mix(0.08, 0.7, foam);
    SPECULAR = 0.6;
    // Plus opaque qu'avant : les fonds sableux ne transparaissent que sur les premiers mètres
    ALPHA = max(clamp(0.62 + v_depth / 25.0, 0.62, 0.98), foam);
}
";
}
