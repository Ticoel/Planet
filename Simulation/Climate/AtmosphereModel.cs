using System;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Godot;

namespace Planet.Simulation.Climate;

/// <summary>
/// Modèle d'atmosphère et d'hydrologie de surface. Chaque cellule porte quatre grandeurs :
/// <list type="bullet">
/// <item><b>T</b> : température de la colonne, ramenée au niveau de la mer (°C) ;</item>
/// <item><b>W</b> : vapeur d'eau de la colonne d'air (« eau précipitable », mm) ;</item>
/// <item><b>S</b> : eau du sol (mm, terres seulement), plafonnée par la réserve utile ;</item>
/// <item><b>N</b> : neige au sol en équivalent eau (mm, terres seulement) ; au-delà d'une
/// certaine épaisseur, c'est un glacier.</item>
/// </list>
/// <b>Températures</b> (étape 4) : bilan d'énergie Budyko-Sellers-North
/// C dT/dt = (1 − α) Q − (A + B T) + D ∇²T − C_air v·∇T.
/// <br/><b>Eau</b> (étapes 6 et 7) :
/// <list type="bullet">
/// <item>évaporation : d'autant plus forte que l'air est sec, chaud et venté ; sur les terres,
/// limitée par l'eau du sol ;</item>
/// <item>précipitations : air saturé (W au-delà de la capacité de l'air, qui double tous les
/// ~10 °C, loi de Clausius-Clapeyron), convergence des vents (zone intertropicale, dépressions),
/// soulèvement de l'air contre les reliefs (côté au vent), plus une pluie de fond ;</item>
/// <item>transport de la vapeur par le vent (forme en flux, qui conserve l'eau et l'accumule
/// là où les vents convergent) et par les tourbillons (diffusion) ;</item>
/// <item>en dessous de −3 à −5 °C (température de colonne) la pluie devient neige ; la neige fond
/// au-dessus de −2 °C (4 mm par jour et par degré) ; l'eau en excès dans le sol ruisselle vers les rivières ;
/// la neige accumulée au-delà de 10 m d'équivalent eau s'écoule en glacier ;</item>
/// <item>la neige au sol renvoie la lumière (albédo 0,6) : rétroaction neige-climat.</item>
/// </list>
/// Simplifications assumées : pas de chaleur latente (le transport de chaleur calibré l'inclut
/// en moyenne), nuages non représentés explicitement (compris dans l'albédo planétaire).
/// Cette classe est la version processeur de référence ; le calcul courant tourne sur la carte
/// graphique (AtmosphereKernel), avec exactement les mêmes formules et les mêmes constantes.
/// </summary>
public sealed class AtmosphereModel
{
    // --- Températures (calibrées à l'étape 4) --------------------------------------------------
    public const float OutgoingA = 188f;
    public const float OutgoingB = 2.09f;
    public const float Diffusivity = 0.35f;
    public const float OceanAlbedo = 0.29f;
    public const float LandAlbedo = 0.32f;
    public const float IceAlbedo = 0.62f;
    public const float IceStartCelsius = -5f;
    public const float IceFullCelsius = -15f;
    // Albédo plus fort aux hautes latitudes (soleil rasant, nuages bas) : terme en sin²(latitude)
    // de moyenne nulle sur la sphère
    public const float LatitudeAlbedo = 0.12f;
    public const float OceanHeatCapacity = 2.0e8f;
    public const float LandHeatCapacity = 1.0e7f;
    public const float SeaIceHeatCapacity = 2.0e7f;
    // Chaleur transportée par le vent de surface : capacité de la couche limite (~250 hPa d'air),
    // pas de toute la colonne. Avec la colonne entière (1e7), les alizés inondaient les tropiques
    // d'air froid : tropiques à 24 °C et moyennes latitudes à 19 °C (test LatitudeGradient)
    public const float AtmosphereHeatCapacity = 2.5e6f;
    public const float LapseRateKPerKm = 6.5f;
    // Part de la chaleur des eaux de surface effectivement transportée par les courants (le
    // modèle n'a qu'une couche : une partie de la chaleur transportée reste en profondeur)
    public const float OceanTransportEfficiency = 0.5f;

    // --- Eau ------------------------------------------------------------------------------------
    // Capacité de l'air (mm d'eau précipitable à saturation) : 25 mm à 15 °C, ×e^(0,065 ΔT)
    public const float SaturationReferenceMm = 25f;
    public const float SaturationReferenceCelsius = 15f;
    public const float ClausiusClapeyron = 0.065f;
    public const float MaxSaturationMm = 90f;

    // Évaporation potentielle : E0 (1 + V/V0)(1 − HR)(Wsat / 25 mm), E0 = 4 mm/jour (calibré
    // pour ~2,7 mm/jour de précipitations en moyenne mondiale)
    public const float EvaporationMmPerDay = 4f;
    public const float EvaporationWindScale = 6f;

    // Précipitations : condensation de l'excès au-delà de la saturation (3 h), pluie de fond
    // (W HR² / 6 jours, averses de l'air humide), convergence (K × convergence × W), relief
    // (K × soulèvement × W / 2 km ; faible car le refroidissement de l'air en altitude fait déjà
    // condenser la vapeur sur les reliefs)
    public const float CondensationTimeHours = 12f;
    public const float BackgroundRainDays = 6f;
    public const float ConvergenceRainFactor = 0.25f;
    public const float OrographicRainFactor = 0.05f;
    public const float VaporScaleHeightMeters = 2000f;

    // Transport de la vapeur par les tourbillons (m²/s)
    public const float MoistureDiffusivity = 3.0e5f;

    // Pluie ou neige : tout en neige sous −5 °C, tout en pluie au-dessus de −3 °C ; fonte
    // au-dessus de −2 °C (4 mm par jour et par degré). Comme pour la glace (seuil −10 °C), ces
    // seuils sont décalés sous 0 °C car T représente une colonne d'air moyennée sur la journée :
    // une moyenne de −2 °C comporte des heures de dégel. Piège constaté : seuils à 0 °C → la neige
    // envahit les terres et la planète entière gèle (−13 °C en trois ans)
    public const float SnowAllCelsius = -5f;
    public const float RainAllCelsius = -3f;
    public const float MeltBaseCelsius = -2f;
    public const float MeltMmPerDayPerKelvin = 4f;

    // Neige : couverture complète dès 100 mm, albédo planétaire 0,5 (nuages et forêts masquent
    // en partie la neige vue de l'espace) ; glacier plafonné à 10 m (le surplus s'écoule) ;
    // un sol enneigé de plus de 2 m d'équivalent eau est compté comme glacier
    public const float SnowCoverMm = 100f;
    public const float SnowAlbedo = 0.5f;
    public const float GlacierMaxMm = 10000f;
    public const float GlacierThresholdMm = 2000f;

    // Réserve utile du sol (mm) et évapotranspiration (plantes comprises) par défaut ;
    // les biomes les modifient (étape 8)
    public const float DefaultSoilCapacityMm = 150f;
    public const float DefaultEvapotranspiration = 1.3f;

    // --- Pas de temps ---------------------------------------------------------------------------
    private const float StabilityFactor = 0.5f;

    public PlanetGrid Grid { get; }

    /// <summary>Couplage thermique entre voisines (W/m²/K) : D × 4 / h² (sphère unité).</summary>
    public float NeighborCoupling { get; }

    /// <summary>Couplage de la vapeur entre voisines (1/s) : D_vapeur × 4 / h² (h en mètres).</summary>
    public float MoistureCoupling { get; }

    /// <summary>Poids de l'advection, 6 cases par cellule (même ordre que <see cref="NeighborTable"/>) :
    /// longueur de la face commune / aire de la cellule (1/m), d'après la géométrie exacte.</summary>
    public float[] FaceWeightTable { get; }

    /// <summary>Plus long sous-pas stable (s).</summary>
    public float MaxSubstepSeconds { get; }

    /// <summary>Voisines de chaque cellule, 6 cases (−1 pour la 6e d'un pentagone).</summary>
    public int[] NeighborTable { get; }

    /// <summary>Direction tangente de chaque cellule vers chacune de ses voisines (6 cases par cellule).</summary>
    public Vector3[] NeighborDirections { get; }

    /// <summary>Surface (vec4) : capacité thermique, albédo hors glace/neige, altitude (km, ≥ 0), 1 si océan.</summary>
    public float[] Surface { get; }

    /// <summary>Couverture du sol (vec4) : réserve utile (mm), facteur d'évapotranspiration,
    /// albédo une fois enneigé (les forêts masquent la neige), 0.</summary>
    public float[] LandCover { get; }

    /// <summary>Pente du relief (vec4) : vecteur 3D tangent (m/m), 0.</summary>
    public float[] Slope { get; }

    public AtmosphereModel(PlanetGrid grid)
    {
        Grid = grid;
        float h = grid.MeanEdgeAngle;
        float radiusMeters = PlanetConstants.RadiusKm * 1000f;
        float hMeters = h * radiusMeters;
        NeighborCoupling = Diffusivity * 4f / (h * h);
        MoistureCoupling = MoistureDiffusivity * 4f / (hMeters * hMeters);
        FaceWeightTable = new float[grid.CellCount * 6];
        float[][] faceWeights = grid.ComputeFaceWeights();
        // Stabilité de la diffusion thermique sur les terres, et condensation non surcorrigée
        MaxSubstepSeconds = MathF.Min(
            StabilityFactor * LandHeatCapacity / (OutgoingB + NeighborCoupling),
            0.5f * CondensationTimeHours * 3600f);

        NeighborTable = new int[grid.CellCount * 6];
        NeighborDirections = new Vector3[grid.CellCount * 6];
        for (int cell = 0; cell < grid.CellCount; cell++)
        {
            int[] neighbors = grid.Neighbors[cell];
            Vector3 p = grid.Positions[cell];
            for (int k = 0; k < 6; k++)
            {
                NeighborTable[cell * 6 + k] = k < neighbors.Length ? neighbors[k] : -1;
                if (k < neighbors.Length)
                {
                    Vector3 d = grid.Positions[neighbors[k]] - p;
                    NeighborDirections[cell * 6 + k] = (d - p * d.Dot(p)).Normalized();
                }
                FaceWeightTable[cell * 6 + k] = k < neighbors.Length ? faceWeights[cell][k] / radiusMeters : 0f;
            }
        }
        Surface = new float[grid.CellCount * 4];
        LandCover = new float[grid.CellCount * 4];
        Slope = new float[grid.CellCount * 4];
    }

    // --- Surface -------------------------------------------------------------------------------

    /// <summary>Reprend le relief (figé pendant la simulation du climat) : terres/océans,
    /// altitudes et pentes. La couverture du sol revient aux valeurs par défaut.</summary>
    public void SetSurface(float[] elevationKm)
    {
        int count = Grid.CellCount;
        for (int cell = 0; cell < count; cell++)
        {
            bool ocean = elevationKm[cell] <= 0f;
            Surface[cell * 4] = ocean ? OceanHeatCapacity : LandHeatCapacity;
            Surface[cell * 4 + 1] = (ocean ? OceanAlbedo : LandAlbedo) + LatitudeAlbedoOffset(cell);
            Surface[cell * 4 + 2] = Mathf.Max(elevationKm[cell], 0f);
            Surface[cell * 4 + 3] = ocean ? 1f : 0f;
            LandCover[cell * 4] = DefaultSoilCapacityMm;
            LandCover[cell * 4 + 1] = DefaultEvapotranspiration;
            LandCover[cell * 4 + 2] = SnowAlbedo;
            LandCover[cell * 4 + 3] = 0f;
        }
        ComputeSlopes();
    }

    /// <summary>Applique les propriétés des biomes aux terres (étape 8).</summary>
    public void SetLandCover(float[] albedo, float[] soilCapacityMm, float[] evapotranspiration, float[] snowAlbedo)
    {
        for (int cell = 0; cell < Grid.CellCount; cell++)
        {
            if (Surface[cell * 4 + 3] > 0.5f)
                continue;
            Surface[cell * 4 + 1] = albedo[cell] + LatitudeAlbedoOffset(cell);
            LandCover[cell * 4] = soilCapacityMm[cell];
            LandCover[cell * 4 + 1] = evapotranspiration[cell];
            LandCover[cell * 4 + 2] = snowAlbedo[cell];
        }
    }

    private float LatitudeAlbedoOffset(int cell)
    {
        float sinLatitude = Grid.Positions[cell].Y;
        return LatitudeAlbedo * (sinLatitude * sinLatitude - 1f / 3f);
    }

    public bool IsOcean(int cell) => Surface[cell * 4 + 3] > 0.5f;

    /// <summary>Pente du relief (terres ; océans à plat) par moindres carrés sur les voisines.</summary>
    private void ComputeSlopes()
    {
        float radiusMeters = PlanetConstants.RadiusKm * 1000f;
        Parallel.For(0, Grid.CellCount, cell =>
        {
            Vector3 p = Grid.Positions[cell];
            Vector3 east = Vector3.Up.Cross(p);
            east = east.LengthSquared() < 1e-8f ? Vector3.Right : east.Normalized();
            Vector3 north = p.Cross(east).Normalized();
            float h0 = Surface[cell * 4 + 2] * 1000f;

            float mxx = 0f, mxy = 0f, myy = 0f, bx = 0f, by = 0f;
            foreach (int n in Grid.Neighbors[cell])
            {
                Vector3 d = Grid.Positions[n] - p;
                float dx = d.Dot(east) * radiusMeters, dy = d.Dot(north) * radiusMeters;
                float dh = Surface[n * 4 + 2] * 1000f - h0;
                mxx += dx * dx; mxy += dx * dy; myy += dy * dy;
                bx += dh * dx; by += dh * dy;
            }
            float det = mxx * myy - mxy * mxy;
            float gx = (myy * bx - mxy * by) / det;
            float gy = (-mxy * bx + mxx * by) / det;
            Vector3 slope = east * gx + north * gy;
            Slope[cell * 4] = slope.X;
            Slope[cell * 4 + 1] = slope.Y;
            Slope[cell * 4 + 2] = slope.Z;
            Slope[cell * 4 + 3] = 0f;
        });
    }

    // --- Physique d'une cellule ----------------------------------------------------------------

    /// <summary>Capacité de l'air à saturation (mm d'eau précipitable).</summary>
    public static float Saturation(float surfaceTemperature) =>
        MathF.Min(MaxSaturationMm,
            SaturationReferenceMm * MathF.Exp(ClausiusClapeyron * (surfaceTemperature - SaturationReferenceCelsius)));

    /// <summary>
    /// Un sous-pas pour une cellule. Formules et ordre des opérations identiques au shader
    /// (AtmosphereKernel), pour que la vérification carte graphique / processeur soit fiable.
    /// </summary>
    public static Vector4 StepCell(
        Vector4 state, float meanT, float meanW, float advectionT, float advectionW, float oceanAdvectionT,
        float insolation, Vector4 surface, Vector4 landCover, Vector3 slope, Vector4 wind,
        float coupling, float moistureCoupling, float dt, out Vector4 accumulation)
    {
        float t = state.X, w = state.Y, soil = state.Z, snow = state.W;
        bool ocean = surface.W > 0.5f;

        // Température
        float surfaceT = t - LapseRateKPerKm * surface.Z;
        // Banquise sur l'océan ; sur les terres, seule la neige au sol éclaircit la surface
        float ice = ocean ? Mathf.Clamp((IceStartCelsius - surfaceT) / (IceStartCelsius - IceFullCelsius), 0f, 1f) : 0f;
        float snowCover = ocean ? 0f : Mathf.Clamp(snow / SnowCoverMm, 0f, 1f);
        float albedo = MathF.Max(Mathf.Lerp(surface.Y, IceAlbedo, ice), Mathf.Lerp(surface.Y, landCover.Z, snowCover));
        float capacity = ocean ? Mathf.Lerp(surface.X, SeaIceHeatCapacity, ice) : surface.X;
        float net = (1f - albedo) * insolation
                    - (OutgoingA + OutgoingB * t)
                    + coupling * (meanT - t)
                    + AtmosphereHeatCapacity * advectionT;
        // Courants océaniques : l'eau apporte sa température (K/s), même sous la banquise
        float newT = t + dt * net / capacity + (ocean ? dt * OceanTransportEfficiency * oceanAdvectionT : 0f);

        // Vapeur d'eau (taux en mm/s)
        float saturation = Saturation(surfaceT);
        float humidity = Mathf.Clamp(w / saturation, 0f, 2f);
        float speed = new Vector3(wind.X, wind.Y, wind.Z).Length();
        float potential = EvaporationMmPerDay / 86400f * (1f + speed / EvaporationWindScale)
                          * MathF.Max(0f, 1f - humidity) * (saturation / SaturationReferenceMm);
        float evaporation = ocean
            ? potential * (1f - ice)
            : potential * landCover.Y * Mathf.Clamp(soil / landCover.X, 0f, 1f);

        float uplift = MathF.Max(0f, wind.X * slope.X + wind.Y * slope.Y + wind.Z * slope.Z);
        float precipitation = MathF.Max(0f, w - saturation) / (CondensationTimeHours * 3600f)
                              + w * humidity * humidity / (BackgroundRainDays * 86400f)
                              + ConvergenceRainFactor * MathF.Max(0f, wind.W) * w
                              + OrographicRainFactor * uplift * w / VaporScaleHeightMeters;
        precipitation = MathF.Min(precipitation, w / dt + evaporation);

        float newW = MathF.Max(0f, w + dt * (evaporation - precipitation + advectionW + moistureCoupling * (meanW - w)));

        // Pluie ou neige, fonte, sol, ruissellement, glaciers
        float snowFraction = Mathf.Clamp((RainAllCelsius - surfaceT) / (RainAllCelsius - SnowAllCelsius), 0f, 1f);
        float snowfall = precipitation * snowFraction;
        float rain = precipitation - snowfall;
        float newSoil = 0f, newSnow = 0f, runoff = 0f;
        if (!ocean)
        {
            float melt = surfaceT > MeltBaseCelsius ? MeltMmPerDayPerKelvin / 86400f * (surfaceT - MeltBaseCelsius) : 0f;
            melt = MathF.Min(melt, snow / dt + snowfall);
            newSnow = snow + dt * (snowfall - melt);
            float glacierFlow = MathF.Max(0f, newSnow - GlacierMaxMm);
            newSnow -= glacierFlow;

            newSoil = soil + dt * (rain + melt - evaporation);
            float excess = MathF.Max(0f, newSoil - landCover.X);
            newSoil = MathF.Max(0f, newSoil - excess);
            runoff = excess + glacierFlow;
        }

        accumulation = new Vector4(precipitation * dt, evaporation * dt, runoff, snowfall * dt);
        return new Vector4(newT, newW, newSoil, newSnow);
    }

    /// <summary>Un sous-pas sur toute la grille (processeur, en parallèle).</summary>
    /// <param name="state">État courant (vec4 par cellule : T, W, sol, neige).</param>
    /// <param name="next">État suivant.</param>
    /// <param name="accumulation">Cumuls (vec4 : précipitations, évaporation, ruissellement,
    /// chute de neige, en mm), incrémentés.</param>
    /// <param name="wind">Vent (vec4 : x, y, z, convergence), ou null sans vent.</param>
    /// <param name="current">Courant océanique (vec4 : x, y, z, 0), ou null sans courants.</param>
    public void StepCpu(float[] state, float[] next, float[] accumulation, float[] insolation,
                        float[] wind, float[] current, float dt)
    {
        var positions = Grid.Positions;
        Parallel.For(0, Grid.CellCount, cell =>
        {
            int i = cell * 4;
            Vector4 here = new(state[i], state[i + 1], state[i + 2], state[i + 3]);
            Vector4 windHere = wind == null ? Vector4.Zero : new Vector4(wind[i], wind[i + 1], wind[i + 2], wind[i + 3]);
            Vector3 windVector = new(windHere.X, windHere.Y, windHere.Z);
            Vector3 p = positions[cell];

            float sumT = 0f, sumW = 0f, count = 0f, advectionT = 0f, advectionW = 0f, oceanAdvectionT = 0f;
            bool ocean = Surface[i + 3] > 0.5f;
            Vector3 currentHere = current == null ? Vector3.Zero : new Vector3(current[i], current[i + 1], current[i + 2]);
            for (int k = 0; k < 6; k++)
            {
                int n = NeighborTable[cell * 6 + k];
                if (n < 0)
                    continue;
                float tn = state[n * 4], wn = state[n * 4 + 1];
                sumT += tn;
                sumW += wn;
                count += 1f;
                Vector3 direction = NeighborDirections[cell * 6 + k];
                if (wind != null)
                {
                    // Advection amont : seul l'air qui arrive de la voisine compte
                    Vector3 windThere = new(wind[n * 4], wind[n * 4 + 1], wind[n * 4 + 2]);
                    float face = 0.5f * (windVector + windThere).Dot(direction) * FaceWeightTable[cell * 6 + k];
                    // Chaleur : forme advective amont (seul l'air entrant compte)
                    if (face < 0f)
                        advectionT += -face * (tn - here.X);
                    // Vapeur : forme en flux, conservative (ce qui sort d'une cellule entre
                    // dans sa voisine) ; la vapeur s'accumule là où les vents convergent
                    advectionW += face > 0f ? -face * here.Y : -face * wn;
                }
                // Courants : entre cellules de mer, forme advective amont
                if (current != null && ocean && Surface[n * 4 + 3] > 0.5f)
                {
                    Vector3 currentThere = new(current[n * 4], current[n * 4 + 1], current[n * 4 + 2]);
                    float flow = 0.5f * (currentHere + currentThere).Dot(direction) * FaceWeightTable[cell * 6 + k];
                    if (flow < 0f)
                        oceanAdvectionT += -flow * (tn - here.X);
                }
            }

            Vector4 result = StepCell(here, sumT / count, sumW / count, advectionT, advectionW, oceanAdvectionT,
                insolation[cell],
                new Vector4(Surface[i], Surface[i + 1], Surface[i + 2], Surface[i + 3]),
                new Vector4(LandCover[i], LandCover[i + 1], LandCover[i + 2], LandCover[i + 3]),
                new Vector3(Slope[i], Slope[i + 1], Slope[i + 2]),
                windHere, NeighborCoupling, MoistureCoupling, dt, out Vector4 accumulated);

            next[i] = result.X;
            next[i + 1] = result.Y;
            next[i + 2] = result.Z;
            next[i + 3] = result.W;
            accumulation[i] += accumulated.X;
            accumulation[i + 1] += accumulated.Y;
            accumulation[i + 2] += accumulated.Z;
            accumulation[i + 3] += accumulated.W;
        });
    }

    // --- État initial --------------------------------------------------------------------------

    /// <summary>
    /// État de départ proche de l'équilibre : températures d'équilibre de l'ensoleillement annuel
    /// moyen (calculées sur une grille grossière, puis reportées), air humide à 75 %, sols à
    /// moitié pleins, neige là où il gèle.
    /// </summary>
    public float[] ComputeInitialState(OrbitalParameters orbit)
    {
        float[] temperature = ComputeEquilibriumTemperature(orbit);
        var state = new float[Grid.CellCount * 4];
        for (int cell = 0; cell < Grid.CellCount; cell++)
        {
            int i = cell * 4;
            float surfaceT = temperature[cell] - LapseRateKPerKm * Surface[i + 2];
            state[i] = temperature[cell];
            state[i + 1] = 0.75f * Saturation(surfaceT);
            bool ocean = Surface[i + 3] > 0.5f;
            state[i + 2] = ocean ? 0f : 0.5f * LandCover[i];
            state[i + 3] = !ocean && surfaceT < -5f ? 200f : 0f;
        }
        return state;
    }

    /// <summary>Équilibre thermique de l'ensoleillement annuel moyen, sur une grille grossière
    /// (inertie réduite pour converger vite), reporté sur la grille complète.</summary>
    public float[] ComputeEquilibriumTemperature(OrbitalParameters orbit)
    {
        int coarseLevel = Math.Min(4, Grid.Subdivisions);
        var coarseGrid = coarseLevel == Grid.Subdivisions ? Grid : PlanetGrid.Create(coarseLevel);
        int coarseCount = coarseGrid.CellCount;
        float h = coarseGrid.MeanEdgeAngle;
        float coupling = Diffusivity * 4f / (h * h);
        float dt = StabilityFactor * LandHeatCapacity / (OutgoingB + coupling);

        // La grille grossière partage ses premiers sommets avec la grille fine (la subdivision
        // conserve les sommets) : mêmes indices, mêmes propriétés de surface
        var annual = AnnualMeanInsolation(coarseGrid, orbit);
        var a = new float[coarseCount];
        var b = new float[coarseCount];
        for (int c = 0; c < coarseCount; c++)
            a[c] = 30f - 50f * coarseGrid.Positions[c].Y * coarseGrid.Positions[c].Y;

        for (int iteration = 0; iteration < 20000; iteration++)
        {
            float maxChange = 0f;
            for (int c = 0; c < coarseCount; c++)
            {
                float sum = 0f;
                foreach (int n in coarseGrid.Neighbors[c])
                    sum += a[n];
                float mean = sum / coarseGrid.Neighbors[c].Length;
                float surfaceT = a[c] - LapseRateKPerKm * Surface[c * 4 + 2];
                float ice = Mathf.Clamp((IceStartCelsius - surfaceT) / (IceStartCelsius - IceFullCelsius), 0f, 1f);
                float albedo = Mathf.Lerp(Surface[c * 4 + 1], IceAlbedo, ice);
                float net = (1f - albedo) * annual[c] - (OutgoingA + OutgoingB * a[c]) + coupling * (mean - a[c]);
                b[c] = a[c] + dt * net / LandHeatCapacity;
                maxChange = MathF.Max(maxChange, MathF.Abs(b[c] - a[c]));
            }
            (a, b) = (b, a);
            if (maxChange < 1e-4f)
                break;
        }

        var result = new float[Grid.CellCount];
        int start = 0;
        for (int cell = 0; cell < Grid.CellCount; cell++)
        {
            start = ReferenceEquals(coarseGrid, Grid) ? cell : coarseGrid.NearestCell(Grid.Positions[cell], start);
            result[cell] = a[start];
        }
        return result;
    }

    /// <summary>Ensoleillement moyen annuel de chaque cellule (moyenne des moyennes journalières).</summary>
    public static float[] AnnualMeanInsolation(PlanetGrid grid, OrbitalParameters orbit)
    {
        var annual = new float[grid.CellCount];
        const int samples = 146;
        for (int i = 0; i < samples; i++)
        {
            var sun = orbit.ComputeSun((i + 0.5) * orbit.YearLengthDays / samples);
            for (int c = 0; c < grid.CellCount; c++)
                annual[c] += Insolation.DailyMean(grid.Positions[c], sun) / samples;
        }
        return annual;
    }

    /// <summary>Température de surface (°C) d'une cellule : niveau de la mer moins l'effet de l'altitude.</summary>
    public float SurfaceTemperature(float[] state, int cell) =>
        state[cell * 4] - LapseRateKPerKm * Surface[cell * 4 + 2];

    // --- Constantes pour le shader -------------------------------------------------------------

    /// <summary>Déclarations GLSL des constantes physiques, générées depuis ce fichier pour que
    /// le shader et la référence processeur ne puissent pas diverger.</summary>
    public string ShaderConstants()
    {
        var sb = new StringBuilder();
        void Const(string name, float value) =>
            sb.Append("const float ").Append(name).Append(" = ")
              .Append(value.ToString("R", CultureInfo.InvariantCulture)).Append(";\n");

        Const("OLR_A", OutgoingA);
        Const("OLR_B", OutgoingB);
        Const("ICE_ALBEDO", IceAlbedo);
        Const("ICE_START", IceStartCelsius);
        Const("ICE_FULL", IceFullCelsius);
        Const("SEA_ICE_CAPACITY", SeaIceHeatCapacity);
        Const("ATMOSPHERE_CAPACITY", AtmosphereHeatCapacity);
        Const("LAPSE_RATE", LapseRateKPerKm);
        Const("OCEAN_TRANSPORT", OceanTransportEfficiency);
        Const("SATURATION_REF", SaturationReferenceMm);
        Const("SATURATION_REF_T", SaturationReferenceCelsius);
        Const("CLAUSIUS_CLAPEYRON", ClausiusClapeyron);
        Const("MAX_SATURATION", MaxSaturationMm);
        Const("EVAPORATION_RATE", EvaporationMmPerDay / 86400f);
        Const("EVAPORATION_WIND", EvaporationWindScale);
        Const("CONDENSATION_TIME", CondensationTimeHours * 3600f);
        Const("BACKGROUND_RAIN_TIME", BackgroundRainDays * 86400f);
        Const("CONVERGENCE_RAIN", ConvergenceRainFactor);
        Const("OROGRAPHIC_RAIN", OrographicRainFactor);
        Const("VAPOR_SCALE_HEIGHT", VaporScaleHeightMeters);
        Const("SNOW_ALL", SnowAllCelsius);
        Const("RAIN_ALL", RainAllCelsius);
        Const("MELT_RATE", MeltMmPerDayPerKelvin / 86400f);
        Const("MELT_BASE", MeltBaseCelsius);
        Const("SNOW_COVER", SnowCoverMm);
        Const("SNOW_ALBEDO", SnowAlbedo);
        Const("GLACIER_MAX", GlacierMaxMm);
        Const("COUPLING", NeighborCoupling);
        Const("MOISTURE_COUPLING", MoistureCoupling);
        return sb.ToString();
    }
}
