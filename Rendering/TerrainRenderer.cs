using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Godot;
using Planet.Simulation;
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
    // Temps de création de maillages par image (ms) et taille du cache (morceaux)
    private const double UploadBudgetMs = 3.0;
    private const int MaxCachedChunks = 3000;
    // Plus haut sommet possible à l'échelle réelle (m)
    public const double MaxMountainHeight = 11000.0;
    // Résolution des textures « numéro de cellule » (pixels par côté de face)
    private const int CellIndexResolution = 512;

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
    private readonly ConcurrentQueue<(TerrainChunk chunk, int generation)> _completed = new();
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
        int n = CellIndexResolution;
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
        var indexTexture = new Texture2DArray();
        indexTexture.CreateFromImages(faces);
        SetBoth("cell_index", indexTexture);

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
    public void UpdateCellClimate(float[] temperature, float[] snowMm, float[] elevationKm)
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
        }
        var bytes = new byte[_cellData.Length * sizeof(float)];
        Buffer.BlockCopy(_cellData, 0, bytes, 0, bytes.Length);
        _cellDataImage.SetData(CellColorLayer.TextureWidth, _cellColorHeight, false, Image.Format.Rgbaf, bytes);
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
                if (!_cache.ContainsKey(packed))
                    _cache[packed] = CreateEntry(item.chunk, item.generation);
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
        for (int v = 0; v < count; v++)
        {
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
        bounds = bounds.Grow(1f);

        Rid instance = RenderingServer.InstanceCreate2(mesh, GetWorld3D().Scenario);
        RenderingServer.InstanceSetCustomAabb(instance, bounds);
        RenderingServer.InstanceSetVisible(instance, false);
        RenderingServer.InstanceGeometrySetCastShadowsSetting(instance, RenderingServer.ShadowCastingSetting.Off);
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
    }

    // --- Threads de calcul ----------------------------------------------------------------------

    private void WorkerLoop(int index)
    {
        while (!_stopping)
        {
            ChunkKey key;
            TerrainHeightField field;
            int generation;
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
            }
            try
            {
                if (field != null)
                    _completed.Enqueue((TerrainChunkBuilder.Build(field, key), generation));
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
// et climat des cellules (x : température °C, y : neige mm, z : altitude de la cellule m)
uniform sampler2DArray cell_index : filter_nearest, repeat_disable;
uniform sampler2D cell_colors : source_color, filter_nearest;
uniform sampler2D cell_data : filter_nearest;
uniform int texture_width = 1024;
uniform bool has_climate = false;
// Position de la caméra modulo NOISE_PERIOD (m), calculée en double sur le processeur : les
// bruits de texture, périodiques, sont calculés en coordonnées relatives à la caméra (précises
// en float) mais restent fixés au sol
uniform vec3 noise_offset;
const int NOISE_PERIOD = 8192;

// Repère de chaque face du cube (identique à CubeSphere.cs)
const vec3 FACE_NORMAL[6] = { vec3(1, 0, 0), vec3(-1, 0, 0), vec3(0, 1, 0), vec3(0, -1, 0), vec3(0, 0, 1), vec3(0, 0, -1) };
const vec3 FACE_S[6] = { vec3(0, 0, -1), vec3(0, 0, 1), vec3(1, 0, 0), vec3(1, 0, 0), vec3(1, 0, 0), vec3(-1, 0, 0) };
const vec3 FACE_T[6] = { vec3(0, 1, 0), vec3(0, 1, 0), vec3(0, 0, -1), vec3(0, 0, 1), vec3(0, 1, 0), vec3(0, 1, 0) };

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
// 2 texels de pas, soit un fondu sur ~40 km, après déplacement du point par un bruit)
void smooth_cells(vec3 d, out ivec4 cells, out vec4 weights) {
    vec4 w1 = gradient_noise(d * 300.0, 1 << 20);
    vec4 w2 = gradient_noise(d * 300.0 + vec3(31.7, -12.3, 7.9), 1 << 20);
    vec4 w3 = gradient_noise(d * 300.0 + vec3(-5.1, 19.4, -27.6), 1 << 20);
    vec4 w4 = gradient_noise(d * 1100.0, 1 << 20);
    d = normalize(d + 0.0035 * vec3(w1.x, w2.x, w3.x) + 0.0012 * w4.yzw);
    vec3 a = abs(d);
    int f = a.x >= a.y && a.x >= a.z ? (d.x > 0.0 ? 0 : 1) : (a.y >= a.z ? (d.y > 0.0 ? 2 : 3) : (d.z > 0.0 ? 4 : 5));
    float n = dot(d, FACE_NORMAL[f]);
    vec2 st = vec2(atan(dot(d, FACE_S[f]) / n), atan(dot(d, FACE_T[f]) / n)) * 4.0 / PI;
    int size = textureSize(cell_index, 0).x;
    vec2 texel = ((st * 0.5 + 0.5) * float(size) - 0.5) * 0.5;
    ivec2 base = ivec2(floor(texel)) * 2;
    vec2 t = fract(texel);
    for (int k = 0; k < 4; k++) {
        ivec2 o = ivec2(k & 1, k >> 1);
        ivec2 c = clamp(base + o * 2, ivec2(0), ivec2(size - 1));
        weights[k] = (o.x == 1 ? t.x : 1.0 - t.x) * (o.y == 1 ? t.y : 1.0 - t.y);
        cells[k] = int(texelFetch(cell_index, ivec3(c, f), 0).r + 0.5);
    }
}

ivec2 cell_pixel(int cell) {
    return ivec2(cell % texture_width, cell / texture_width);
}

// Couche de données (plaques, températures…) : les 4 cellules voisines mélangées
vec3 layer_color_blend(vec3 d) {
    ivec4 cells;
    vec4 weights;
    smooth_cells(d, cells, weights);
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

vec3 layer_color_smooth(vec3 d, bool land) {
    ivec4 cells;
    vec4 weights;
    smooth_cells(d, cells, weights);
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


// Part du sol enneigée : climat simulé (température corrigée de l'altitude, neige de la cellule)
// ou, sans climat, limite de la neige selon la latitude
// (sans tenir compte de la pente : voir snow_on_slope)
float snow_cover(vec3 up, float h) {
    float snow;
    if (has_climate) {
        // Climat des cellules voisines, mélangé (lu cellule par cellule, la limite de la neige
        // dessinait des marches d'escalier)
        ivec4 cells;
        vec4 weights;
        smooth_cells(up, cells, weights);
        vec4 data = vec4(0.0);
        for (int k = 0; k < 4; k++)
            data += texelFetch(cell_data, cell_pixel(cells[k]), 0) * weights[k];
        float t = data.x - 0.0065 * (h - max(data.z, 0.0));
        float lying = data.y > 10.0 ? 1.0 : smoothstep(-2.0, -6.0, t);
        snow = smoothstep(0.5, -2.5, t) * lying;
    } else {
        float latitude = abs(up.y);
        float snowline = mix(5200.0, 400.0, smoothstep(0.25, 0.95, latitude));
        snow = smoothstep(snowline, snowline + 400.0, h);
    }
    return snow;
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

// Brume atmosphérique, seulement dans l'atmosphère ou juste au-dessus
vec3 haze(vec3 color, vec3 world, vec3 camera) {
    float distance_to_camera = length(world - camera);
    float camera_altitude = length(camera - planet_center) - planet_radius;
    float scale = mix(120000.0, 600000.0, smoothstep(0.0, 60000.0, camera_altitude));
    float amount = (1.0 - exp(-distance_to_camera / scale)) * 0.55
                   * (1.0 - smoothstep(40000.0, 400000.0, camera_altitude));
    return mix(color, vec3(0.62, 0.72, 0.86), amount);
}
";

    public const string ShaderCode = @"
shader_type spatial;
render_mode cull_back, diffuse_burley, specular_schlick_ggx;
" + CommonShaderCode + @"
void vertex() {
    float m = morph_factor(VERTEX, MODEL_MATRIX, CAMERA_POSITION_WORLD);
    VERTEX += CUSTOM0.xyz * m;
    vec3 n = normalize(mix(NORMAL, CUSTOM1.xyz, m));
    v_height = mix(CUSTOM0.w, CUSTOM1.w, m);
    v_water = UV;
    NORMAL = n;
    v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    v_world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
    vec3 up = normalize(v_world - planet_center);
    vec3 normal = normalize(v_world_normal);
    float h = v_height;
    float depth = v_water.x - h;           // > 0 : sous l'eau (mer ou lac)
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
    float snowBase = coloring == 1 || coloring == 3 ? 0.0 : snow_cover(up, h);
    float snowEstimate = snow_on_slope(snowBase, slope0);
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
        color = layer_color_blend(up);
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
            color = layer_color_smooth(up, true);
            color *= 1.0 + 0.10 * n512.x + 0.08 * n128.x;
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
        color = mix(color, rock, rockiness);
        color = mix(color, cliff, smoothstep(0.38, 0.62, slope));
        // Éboulis : grain clair et sombre sur la roche
        color *= 1.0 + rockiness * (0.08 * n2.x + 0.06 * n8.x);
        float snow = snow_on_slope(snowBase, slope);
        // La neige s'accroche d'abord dans les creux du micro-relief
        snow = clamp(snow + (snow * (1.0 - snow)) * 1.5 * n32.x, 0.0, 1.0);
        color = mix(color, vec3(0.86, 0.88, 0.92) * (1.0 + 0.03 * n8.x), snow);
        roughness = mix(0.95, 0.6, snow);
        // Lit des fleuves
        float river = v_water.y;
        color = mix(color, vec3(0.12, 0.30, 0.38), river);
        roughness = mix(roughness, 0.15, river);
        specular = mix(specular, 0.5, river);
    }

    NORMAL = normalize((VIEW_MATRIX * vec4(normal, 0.0)).xyz);
    ALBEDO = haze(color, v_world, CAMERA_POSITION_WORLD);
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
render_mode blend_mix, cull_back, depth_draw_opaque, diffuse_burley, specular_schlick_ggx;
const int GRID_VERTICES = " + (TerrainChunkBuilder.Side * TerrainChunkBuilder.Side) + @";
" + CommonShaderCode + @"
varying float v_depth;
// 1 sur les jupes (sommets ajoutés après la grille) : pas d'eau dessus, sinon on les voyait
// en traits sombres à travers l'eau transparente, le long des bords des morceaux
varying float v_skirt;

void vertex() {
    v_skirt = VERTEX_ID >= GRID_VERTICES ? 1.0 : 0.0;
    float m = morph_factor(VERTEX, MODEL_MATRIX, CAMERA_POSITION_WORLD);
    VERTEX += CUSTOM0.xyz * m;
    vec3 n = normalize(mix(NORMAL, CUSTOM1.xyz, m));
    v_height = mix(CUSTOM0.w, CUSTOM1.w, m);
    v_water = UV;
    vec3 up = normalize((MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz - planet_center);
    v_depth = v_water.x - v_height;
    VERTEX += up * v_depth;
    NORMAL = up;
    v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
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
    vec3 water_normal = normalize(up - (waves - up * dot(waves, up)) * 0.06);
    NORMAL = normalize((VIEW_MATRIX * vec4(water_normal, 0.0)).xyz);
    vec3 color = mix(vec3(0.10, 0.42, 0.48), vec3(0.03, 0.12, 0.30), smoothstep(0.0, 200.0, v_depth));
    color = mix(color, vec3(0.01, 0.04, 0.14), smoothstep(200.0, 5000.0, v_depth));
    if (coloring == 2) {
        // Vue réaliste : banquise et teinte de la couche (océans gelés en blanc)
        color = mix(color, layer_color_smooth(up, false), 0.5);
    }
    ALBEDO = haze(color, v_world, CAMERA_POSITION_WORLD);
    ROUGHNESS = 0.08;
    SPECULAR = 0.6;
    ALPHA = clamp(0.45 + v_depth / 60.0, 0.45, 0.97);
}
";
}
