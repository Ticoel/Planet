namespace Planet.Rendering;

/// <summary>
/// Morceaux de code GLSL partagés par les shaders du terrain, de l'eau, du ciel et de la passe
/// atmosphère + nuages (étape 10). Toutes les distances sont en mètres ; les positions sont
/// relatives à la caméra, la planète étant centrée en <c>-camera</c>.
/// </summary>
public static class ShaderLibrary
{
    /// <summary>Projection d'une direction sur les 6 faces du cube (identique à CubeSphere.cs).</summary>
    public const string CubeFace = @"
const vec3 FACE_NORMAL[6] = { vec3(1, 0, 0), vec3(-1, 0, 0), vec3(0, 1, 0), vec3(0, -1, 0), vec3(0, 0, 1), vec3(0, 0, -1) };
const vec3 FACE_S[6] = { vec3(0, 0, -1), vec3(0, 0, 1), vec3(1, 0, 0), vec3(1, 0, 0), vec3(1, 0, 0), vec3(-1, 0, 0) };
const vec3 FACE_T[6] = { vec3(0, 1, 0), vec3(0, 1, 0), vec3(0, 0, -1), vec3(0, 0, 1), vec3(0, 1, 0), vec3(0, 1, 0) };

// Face du cube et coordonnées (0 à 1) d'une direction, en projection équi-angulaire
vec3 cube_face_uv(vec3 d) {
    vec3 a = abs(d);
    int f = a.x >= a.y && a.x >= a.z ? (d.x > 0.0 ? 0 : 1) : (a.y >= a.z ? (d.y > 0.0 ? 2 : 3) : (d.z > 0.0 ? 4 : 5));
    float n = dot(d, FACE_NORMAL[f]);
    vec2 st = vec2(atan(dot(d, FACE_S[f]) / n), atan(dot(d, FACE_T[f]) / n)) * 4.0 / PI;
    return vec3(st * 0.5 + 0.5, float(f));
}
";

    /// <summary>
    /// Diffusion de la lumière par l'atmosphère (diffusion simple) : Rayleigh (molécules : le
    /// bleu est diffusé ~5 fois plus que le rouge, d'où le ciel bleu et les couchers rouges) et
    /// Mie (aérosols : halo blanc autour du Soleil). Coefficients de la Terre au niveau de la mer,
    /// densités qui décroissent exponentiellement avec l'altitude.
    /// <para>Les altitudes le long d'un rayon sont calculées sans soustraire de grands nombres :
    /// en float, (R + a)² − R² perdrait toute précision près du sol.</para>
    /// </summary>
    public const string Atmosphere = @"
const float PLANET_R = 6371000.0;
const float ATMO_H = 100000.0;
const vec3 RAYLEIGH = vec3(5.8e-6, 13.5e-6, 33.1e-6);
const float MIE = 21e-6;
const float MIE_EXTINCTION = 1.11;
const float RAYLEIGH_H = 8000.0;
const float MIE_H = 1200.0;
const float MIE_G = 0.76;
// Ozone (absorbe surtout le vert et le rouge : bleu profond du crépuscule), couche vers 25 km
const vec3 OZONE = vec3(0.65e-6, 1.88e-6, 0.085e-6);
// Intensité du Soleil dans les unités de l'éclairage (sol blanc éclairé de face ≈ 1). La
// diffusion simple sous-estime la lumière du ciel : 20 pour le ciel, moins pour la
// perspective atmosphérique sur le relief (sinon la planète vue de l'espace est délavée)
uniform float sun_intensity = 20.0;

float ozone_density(float h) {
    return max(0.0, 1.0 - abs(h - 25000.0) / 15000.0);
}

// Altitude (m) au point situé à la distance t le long d'un rayon partant de l'altitude a,
// mu = cosinus de l'angle entre le rayon et la verticale du départ
float ray_height(float a, float mu, float t) {
    float r0 = PLANET_R + a;
    float r = sqrt(max(r0 * r0 + 2.0 * r0 * t * mu + t * t, 1.0));
    return (a * (2.0 * PLANET_R + a) + 2.0 * r0 * t * mu + t * t) / (r + PLANET_R);
}

// Distances d'entrée et de sortie du rayon dans la sphère d'altitude h (x > y : pas d'intersection)
vec2 ray_shell(float a, float mu, float h) {
    float r0 = PLANET_R + a;
    float b = r0 * mu;
    float d = b * b - (a - h) * (2.0 * PLANET_R + a + h);
    if (d < 0.0)
        return vec2(1e30, -1e30);
    float s = sqrt(d);
    return vec2(-b - s, -b + s);
}

// Épaisseur optique vers le Soleil depuis un point d'altitude h (mus : cosinus de la hauteur
// du Soleil) ; très grande si la planète cache le Soleil
vec3 sun_optical_depth(float h, float mus) {
    vec2 ground = ray_shell(h, mus, -50.0);
    if (ground.x > 0.0 && ground.y > ground.x)
        return vec3(1e4);
    float top = ray_shell(h, mus, ATMO_H).y;
    float dt = top / 3.0;
    float r = 0.0, m = 0.0, o = 0.0;
    for (int i = 0; i < 3; i++) {
        float hh = ray_height(h, mus, (float(i) + 0.5) * dt);
        r += exp(-hh / RAYLEIGH_H);
        m += exp(-hh / MIE_H);
        o += ozone_density(hh);
    }
    return (RAYLEIGH * r + MIE * MIE_EXTINCTION * m + OZONE * o) * dt;
}

// Lumière du Soleil qui arrive en un point (teintée par l'atmosphère traversée)
vec3 sun_transmittance(float h, float mus) {
    return exp(-sun_optical_depth(max(h, 0.0), mus));
}

float rayleigh_phase(float nu) {
    return 3.0 / (16.0 * PI) * (1.0 + nu * nu);
}

float mie_phase(float nu) {
    float g = MIE_G;
    return 3.0 / (8.0 * PI) * ((1.0 - g * g) * (1.0 + nu * nu)) / ((2.0 + g * g) * pow(1.0 + g * g - 2.0 * g * nu, 1.5));
}

// Lumière diffusée vers l'œil (inscatter) et transmission le long d'un rayon, jusqu'à t_max :
// départ à l'altitude a, verticale up, direction rd (unitaires), Soleil sun
void atmosphere(float a, vec3 up, vec3 rd, float t_max, vec3 sun, out vec3 inscatter, out vec3 transmittance) {
    inscatter = vec3(0.0);
    transmittance = vec3(1.0);
    float mu = dot(rd, up);
    vec2 shell = ray_shell(a, mu, ATMO_H);
    float t0 = max(shell.x, 0.0);
    float t1 = min(shell.y, t_max);
    vec2 ground = ray_shell(a, mu, 0.0);
    if (ground.x > 0.0 && ground.y > ground.x)
        t1 = min(t1, ground.x);
    if (t1 <= t0)
        return;
    const int STEPS = 10;
    float dt = (t1 - t0) / float(STEPS);
    float nu = dot(rd, sun);
    vec3 depth = vec3(0.0);
    vec3 sum_r = vec3(0.0), sum_m = vec3(0.0);
    vec3 origin = up * (PLANET_R + a);
    for (int i = 0; i < STEPS; i++) {
        float t = t0 + (float(i) + 0.5) * dt;
        float h = ray_height(a, mu, t);
        float dr = exp(-h / RAYLEIGH_H) * dt;
        float dm = exp(-h / MIE_H) * dt;
        vec3 step_depth = RAYLEIGH * dr + MIE * MIE_EXTINCTION * dm + OZONE * ozone_density(h) * dt;
        depth += step_depth * 0.5;
        float mus = dot(normalize(origin + rd * t), sun);
        vec3 light = exp(-(depth + sun_optical_depth(h, mus)));
        sum_r += light * dr;
        sum_m += light * dm;
        depth += step_depth * 0.5;
    }
    inscatter = sun_intensity * (sum_r * RAYLEIGH * rayleigh_phase(nu) + sum_m * MIE * mie_phase(nu));
    transmittance = exp(-depth);
}
";

    /// <summary>
    /// Nuages (étape 10c) : couverture lue dans une texture par face du cube (calculée depuis le
    /// climat), formes par un bruit 3D qui dérive avec les vents dominants, couche entre 1,5 km et
    /// jusqu'à ~9 km (plus épaisse là où il pleut).
    /// </summary>
    public const string Clouds = @"
// Couverture (r) et averses (g) par face du cube
uniform sampler2DArray cloud_cover : filter_linear, repeat_disable;
uniform sampler3D cloud_noise : filter_linear, repeat_enable;
uniform bool clouds_enabled = false;
// Temps simulé (s) qui fait dériver les formes des nuages
uniform float cloud_time = 0.0;
// Position de la caméra dans le repère de la planète (m)
uniform vec3 camera_planet;

const float CLOUD_BASE = 1500.0;
const float CLOUD_TOP_MAX = 9500.0;
const float CLOUD_EXTINCTION = 0.004;

// Vent dominant (m/s, vers l'est > 0) selon la latitude : alizés, vents d'ouest, vents polaires
float prevailing_wind(float sin_latitude) {
    float lat = abs(asin(clamp(sin_latitude, -1.0, 1.0))) * 180.0 / PI;
    return lat < 30.0 ? -7.0 * sin(lat / 30.0 * PI) : (lat < 60.0 ? 11.0 * sin((lat - 30.0) / 30.0 * PI) : -4.0 * sin((lat - 60.0) / 30.0 * PI));
}

// Systèmes nuageux (0 à 1, moyenne 0,5) : dépressions, fronts, amas d'orages, de quelques
// centaines à ~1500 km. Le climat simulé ne connaît que des moyennes sur des jours : sans ce
// « temps qu'il fait », la couverture formait des anneaux continus le long des latitudes.
// Le motif dérive avec les vents dominants et se renouvelle : deux motifs en fondu enchaîné
// (période WEATHER_PERIOD), chacun repartant d'un nouveau tirage quand son poids est nul
// (sinon le cisaillement des vents l'étirerait en traînées)
const float WEATHER_PERIOD = 100000.0;
const float WEATHER_CYCLES = 16.0;   // cloud_time revient à zéro tous les 16 motifs

float weather_pattern(vec3 d, float phase, float index) {
    vec3 east = normalize(vec3(d.z, 0.0, -d.x) + vec3(1e-6, 0.0, 0.0));
    vec3 q = d + east * (prevailing_wind(d.y) * phase * WEATHER_PERIOD / PLANET_R);
    float k = mod(index, WEATHER_CYCLES);
    vec3 offset = fract(vec3(0.371, 0.613, 0.157) * k * 7.13) * 4.0;
    // Grands systèmes (~1500 km) aux bords déchiquetés par une échelle plus fine (~500 km)
    return 0.68 * texture(cloud_noise, q * 1.7 + offset).r + 0.32 * texture(cloud_noise, q * 4.9 + offset.yzx).r;
}

float weather_at(vec3 d) {
    float t = cloud_time / WEATHER_PERIOD;
    float phase_a = fract(t), phase_b = fract(t + 0.5);
    float weight_a = 1.0 - abs(2.0 * phase_a - 1.0);
    float weight_b = 1.0 - weight_a;
    float w = weight_a * weather_pattern(d, phase_a, floor(t))
            + weight_b * weather_pattern(d, phase_b, floor(t + 0.5) + 0.5 * WEATHER_CYCLES);
    // Le fondu de deux bruits atténue les contrastes : on les rétablit
    return 0.5 + (w - 0.5) * 2.4 / sqrt(weight_a * weight_a + weight_b * weight_b);
}

// Couverture en un point. La direction est d'abord déformée par le bruit (~60 km, ±50 km) :
// sinon les bords des cellules de la simulation (hexagones de ~60 km) se voyaient de haut
vec2 cloud_cover_raw(vec3 direction) {
    // (une seule lecture du bruit : la passe des nuages lit la couverture des dizaines de fois par pixel)
    float n = texture(cloud_noise, direction * 25.0).r - 0.5;
    vec3 warp = n * vec3(1.0, -0.7, 0.45) + (n * n - 0.08) * vec3(-0.6, 0.9, 1.1);
    return texture(cloud_cover, cube_face_uv(normalize(direction + warp * 0.02))).rg;
}

// Le climat dit où l'air est humide ; le temps qu'il fait (w, de weather_at) y découpe nuages
// et éclaircies. Les averses se regroupent en amas d'orages
vec2 apply_weather(vec2 cover, float w) {
    cover.r = clamp(cover.r + 1.2 * (w - 0.5) - 0.15, 0.0, 1.0);
    cover.g *= smoothstep(0.35, 0.7, w) * 1.5;
    return cover;
}

vec2 cloud_cover_at(vec3 direction) {
    return apply_weather(cloud_cover_raw(direction), weather_at(direction));
}

// Sommet de la couche nuageuse (m) selon la couverture et les averses
float cloud_top(vec2 cover) {
    return CLOUD_BASE + 2000.0 + 6000.0 * clamp(max(cover.g, cover.r * cover.r * 0.6), 0.0, 1.0);
}

// Densité (0 à 1) en un point p (repère de la planète, m) d'altitude h ; footprint : taille
// d'un pixel à cette distance (m). Les détails plus petits que quelques pixels sont remplacés
// par leur moyenne (sinon ils scintillent en grains de sel, vus de loin)
float cloud_density(vec3 p, float h, vec2 cover, float footprint) {
    float top = cloud_top(cover);
    float hn = (h - CLOUD_BASE) / (top - CLOUD_BASE);
    if (hn <= 0.0 || hn >= 1.0 || cover.r < 0.02)
        return 0.0;
    vec3 d = normalize(p);
    vec3 east = normalize(vec3(d.z, 0.0, -d.x) + vec3(1e-6, 0.0, 0.0));
    vec3 q = p + east * prevailing_wind(d.y) * cloud_time;
    // Trois échelles : grands ensembles (~120 km, toujours là, même vus de l'espace), masses
    // nuageuses (~14 km) et détail (~3 km), estompées quand elles deviennent plus petites qu'un pixel
    float large = texture(cloud_noise, q / 120000.0 + vec3(0.53, 0.29, 0.07)).r;
    float base = mix(texture(cloud_noise, q / 14000.0).r, 0.5, smoothstep(450.0, 1200.0, footprint));
    float detail = mix(texture(cloud_noise, q / 3100.0 + vec3(0.37, 0.11, 0.73)).r, 0.5, smoothstep(100.0, 260.0, footprint));
    float shape = smoothstep(0.0, 0.1, hn) * (1.0 - smoothstep(0.45, 1.0, hn));
    float n = large * 0.35 + base * 0.45 + detail * 0.2;
    return clamp((n - (1.0 - cover.r)) * 2.5, 0.0, 1.0) * shape;
}
";

    /// <summary>
    /// Forme des panaches volcaniques, partagée par le ciel (le volume lui-même) et le terrain
    /// (son ombre portée) : une colonne convective qui monte en s'élargissant et que le vent
    /// penche, puis un nuage qui s'étale à l'altitude où la colonne n'est plus plus légère que
    /// l'air (l'ombrelle) et que le vent étire en une longue traînée qui s'élargit, s'épaissit et
    /// s'éclaircit en s'éloignant (Sparks 1986 ; Bonadonna et Phillips 2003). Repère local : axe
    /// vertical à la bouche, direction du vent, et la perpendiculaire.
    /// </summary>
    public const string Plumes = @"
// Éruptions en cours : direction de la bouche + intensité ; rayon du volcan (m), hauteur du
// panache (m), VEI (−1 : vapeur d'un volcan ; −2 : petit évent, source chaude ou geyser, dont
// le premier nombre est alors le rayon en m), altitude de la bouche (m) ; vent (direction, m/s)
uniform int eruption_count = 0;
uniform vec4 eruption_vents[8];
uniform vec4 eruption_shapes[8];
uniform vec4 eruption_winds[8];

struct Plume {
    vec3 vent, up, wind, side;
    float height, base, bend, top, thick, spread, len, x0, speed, vei;
    bool steam, small;
};

Plume plume_params(int k) {
    Plume P;
    P.up = eruption_vents[k].xyz;
    P.vent = P.up * (PLANET_R + eruption_shapes[k].w);
    vec3 w = eruption_winds[k].xyz - P.up * dot(eruption_winds[k].xyz, P.up);
    P.wind = dot(w, w) > 1e-8 ? normalize(w) : normalize(cross(vec3(0.0, 1.0, 0.0), P.up) + vec3(1e-6, 0.0, 0.0));
    P.side = cross(P.up, P.wind);
    P.speed = eruption_winds[k].w;
    float f = clamp(P.speed / 10.0, 0.2, 3.0);
    P.steam = eruption_shapes[k].z < -0.5;
    P.small = eruption_shapes[k].z < -1.5;
    P.vei = max(eruption_shapes[k].z, 0.0);
    P.height = eruption_shapes[k].y;
    float volcano = eruption_shapes[k].x;
    // Base de la colonne à la mesure du volcan ; la vapeur, légère, se couche vite dans le vent
    P.base = P.small ? volcano : P.steam ? max(100.0, 0.01 * volcano) : max(450.0, 0.015 * volcano);
    P.bend = (P.steam ? 0.7 : 0.15) * f;
    P.top = (P.steam ? 0.85 : 0.8) * P.height;
    P.thick = (P.steam ? 0.1 : 0.14) * P.height;
    P.spread = (P.steam ? 0.15 : 0.45) * P.height;
    // Traînée d'autant plus longue que l'éruption est forte et le vent rapide
    P.len = P.height * (P.steam ? 3.0 : 5.0) * (0.4 + 0.6 * eruption_vents[k].w) * clamp(f, 0.5, 2.0);
    P.x0 = P.height * P.bend * (P.top / P.height) * (P.top / P.height);
    return P;
}

// Forme sans les volutes : 1 au bord, < 1 dedans ; feature = taille des volutes (m), thin =
// densité relative (la traînée s'éclaircit), trail = 0 dans la colonne, 1 dans la traînée ;
// safe = distance (m) qu'on peut parcourir sans risquer d'entrer dans le panache, volutes comprises
float plume_envelope(Plume P, vec3 p, out float feature, out float thin, out float trail, out float safe) {
    vec3 w = p - P.vent;
    float s = dot(w, P.up), x = dot(w, P.wind), y = dot(w, P.side);
    feature = P.base; thin = 1.0; trail = 0.0;
    safe = max(max(-50.0 - s, s - 1.15 * P.height), P.base);
    if (s < -50.0 || s > 1.15 * P.height)
        return 9.0;
    // Colonne : penchée par le vent (décalage qui croît comme le carré de la hauteur)
    float sc = clamp(s, 0.0, P.height);
    float offset = P.height * P.bend * (sc / P.height) * (sc / P.height);
    float r = P.base + (P.steam ? 0.22 : 0.2) * sc;
    float column_top = P.steam ? P.height : P.top + 0.5 * P.thick;
    float e_column = length(vec2(x - offset, y)) / r + smoothstep(0.9 * column_top, 1.05 * column_top, s) * 4.0;
    // Ombrelle et traînée : demi-ellipsoïde au-dessus de la colonne côté amont, puis section
    // elliptique qui s'élargit (~0,3 m par m) et s'épaissit vers l'aval, en descendant un peu
    float xt = x - P.x0;
    float along = max(xt, 0.0);
    float half_width = P.spread + 0.3 * along;
    float half_thick = P.thick * (1.0 + 0.6 * along / P.len);
    float z = s - (P.top - 0.06 * P.height * along / P.len);
    float e_trail = xt < 0.0 ? length(vec3(xt / P.spread, y / P.spread, z / P.thick))
                             : length(vec2(y / half_width, z / half_thick));
    e_trail += smoothstep(0.5, 1.0, along / P.len) * 3.0;
    // Nuage de cendres au pied de la colonne (éruptions explosives) : la colonne aspire l'air
    // autour, des bouffées retombent et roulent sur les flancs
    float e_skirt = 9.0;
    if (P.vei >= 2.0) {
        float skirt_radius = 3.0 * P.base + 0.04 * P.height;
        e_skirt = length(vec2(length(vec2(x - offset, y)) / skirt_radius, s / (0.05 * P.height + 300.0)));
        e_column = min(e_column, e_skirt);
    }
    trail = smoothstep(-0.2, 0.2, e_column - e_trail);
    // (les distances se mesurent séparément : la colonne, fine, ne doit pas être sautée en
    // avançant au pas de la traînée, épaisse)
    safe = max(min(max(e_column - 1.45, 0.0) * r, max(e_trail - 1.45, 0.0) * min(half_thick, P.spread)), 0.0);
    feature = mix(r, 1.4 * half_thick, trail);
    thin = mix(1.0, mix(1.0, 0.3, smoothstep(0.0, 1.0, along / P.len)), trail);
    return min(e_column, e_trail);
}
";
}
