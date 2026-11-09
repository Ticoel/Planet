using System;

namespace Planet.Simulation.Audio;

/// <summary>Ce que la caméra entend, d'après la simulation (0 à 1 sauf mention contraire).</summary>
public struct SoundscapeInput
{
    /// <summary>Vent au niveau de la caméra (m/s).</summary>
    public float WindSpeed;
    /// <summary>Pluie (0 à 1 : bruine à averse).</summary>
    public float Rain;
    /// <summary>Neige (0 à 1) : elle étouffe les sons.</summary>
    public float Snow;
    /// <summary>Vagues : proximité d'un rivage et hauteur de la caméra (0 loin, 1 sur la plage).</summary>
    public float Surf;
    /// <summary>Orages : probabilité d'un coup de tonnerre par seconde.</summary>
    public float ThunderRate;
    /// <summary>Grondement volcanique (0 à 1 selon l'intensité et la distance).</summary>
    public float Volcano;
    /// <summary>Séisme ressenti (0 à 1).</summary>
    public float Quake;
    /// <summary>Hauteur de la caméra au-dessus du sol (m) : en altitude, seul le vent reste.</summary>
    public float AltitudeM;
}

/// <summary>
/// Sons de l'environnement, entièrement synthétisés (aucun enregistrement) : chaque son est du
/// bruit filtré dont la forme imite le phénomène physique.
/// <list type="bullet">
/// <item><b>Vent</b> : bruit grave dont le filtre s'ouvre et se ferme au rythme de rafales
/// aléatoires ; plus fort avec la vitesse (puissance acoustique ~ v³, amplitude ~ v^1,5).</item>
/// <item><b>Pluie</b> : bruit aigu plus milliers de petits impacts (clics qui s'amortissent en
/// quelques millisecondes), d'autant plus nombreux que la pluie est forte.</item>
/// <item><b>Vagues</b> : bruit très grave (rouleau) dont l'amplitude monte quand une vague
/// déferle, toutes les 6 à 12 s, puis s'étire en écume qui reflue.</item>
/// <item><b>Tonnerre</b> : craquement puis long grondement grave qui s'éteint en quelques
/// secondes (le son des parties lointaines de l'éclair arrive plus tard).</item>
/// <item><b>Volcan et séisme</b> : grondement de très basse fréquence, explosions sourdes.</item>
/// </list>
/// Échantillons stéréo (gauche, droite) entre −1 et 1. Déterministe pour une graine donnée.
/// </summary>
public sealed class Soundscape
{
    public int SampleRate { get; }
    private readonly Random _random;

    // États des filtres (passe-bas à un pôle) et des enveloppes, par voie gauche / droite
    private float _windLowL, _windLowR, _windLow2L, _windLow2R, _windBandL, _windBandR;
    private float _gust, _gustTarget, _gustTimer;
    private float _rainHighL, _rainHighR, _rainPrevL, _rainPrevR;
    private float _dropL, _dropR;
    private float _surfLow1, _surfLow2, _surfEnvelope, _surfTimer, _surfPan;
    private float _thunderEnvelope, _thunderLow1, _thunderLow2, _crack;
    private float _rumbleLow1, _rumbleLow2;
    private float _snowMuffleL, _snowMuffleR;
    // Paramètres lissés (sans à-coups quand la caméra change de lieu)
    private SoundscapeInput _smooth;

    public Soundscape(int sampleRate = 22050, int seed = 1)
    {
        SampleRate = sampleRate;
        _random = new Random(seed);
        _surfTimer = 2f;
    }

    /// <summary>Nombre de coups de tonnerre depuis le début (pour les tests).</summary>
    public int ThunderCount { get; private set; }

    /// <summary>Remplit left/right (même longueur) avec les sons du moment.</summary>
    public void Render(in SoundscapeInput input, float[] left, float[] right)
    {
        int count = left.Length;
        float dt = 1f / SampleRate;
        // Lissage des paramètres sur ~0,5 s
        float follow = 1f - MathF.Exp(-count * dt / 0.5f);
        Lerp(ref _smooth.WindSpeed, input.WindSpeed, follow);
        Lerp(ref _smooth.Rain, input.Rain, follow);
        Lerp(ref _smooth.Snow, input.Snow, follow);
        Lerp(ref _smooth.Surf, input.Surf, follow);
        Lerp(ref _smooth.Volcano, input.Volcano, follow);
        Lerp(ref _smooth.Quake, input.Quake, 1f - MathF.Exp(-count * dt / 0.1f));
        Lerp(ref _smooth.AltitudeM, input.AltitudeM, follow);

        // En altitude, les sons du sol s'éteignent (au-delà de ~1,5 km, il ne reste que le vent)
        float ground = 1f - SmoothStep(300f, 1500f, _smooth.AltitudeM);
        float wind = MathF.Pow(Math.Clamp(_smooth.WindSpeed / 25f, 0f, 1.2f), 1.5f);
        float rain = Math.Clamp(_smooth.Rain, 0f, 1f) * ground;
        float surf = Math.Clamp(_smooth.Surf, 0f, 1f) * ground;
        float volcano = Math.Clamp(_smooth.Volcano, 0f, 1f);
        float quake = Math.Clamp(_smooth.Quake, 0f, 1f);
        // La neige étouffe : filtre passe-bas plus fermé
        float muffle = 1f - 0.75f * Math.Clamp(_smooth.Snow, 0f, 1f) * ground;

        float windCoefficient = Coefficient(40f);
        float rainCoefficient = Coefficient(2500f);
        float surfCoefficient = Coefficient(320f);
        float rumbleCoefficient = Coefficient(55f);
        float thunderCoefficient = Coefficient(90f);
        float muffleCoefficient = Coefficient(800f + 9000f * muffle);
        float dropsPerSample = rain * rain * 2500f * dt;
        float thunderChance = input.ThunderRate * ground * count * dt;
        if (_random.NextSingle() < thunderChance)
        {
            _thunderEnvelope = 0.6f + 0.4f * _random.NextSingle();
            _crack = 1f;
            ThunderCount++;
        }

        for (int i = 0; i < count; i++)
        {
            float whiteL = _random.NextSingle() * 2f - 1f, whiteR = _random.NextSingle() * 2f - 1f;

            // Rafales : cible tirée au hasard toutes les ~1 à 4 s, suivie en douceur
            _gustTimer -= dt;
            if (_gustTimer <= 0f)
            {
                _gustTarget = 0.4f + 0.9f * _random.NextSingle();
                _gustTimer = 1f + 3f * _random.NextSingle();
            }
            _gust += (_gustTarget - _gust) * 1.5f * dt;
            // Deux passe-bas en cascade (le filtre s'ouvre avec les rafales), moins le très grave
            float gustCoefficient = Coefficient((150f + 500f * wind) * (0.6f + 0.6f * _gust));
            _windLowL += (whiteL - _windLowL) * gustCoefficient;
            _windLowR += (whiteR - _windLowR) * gustCoefficient;
            _windLow2L += (_windLowL - _windLow2L) * gustCoefficient;
            _windLow2R += (_windLowR - _windLow2R) * gustCoefficient;
            _windBandL += (_windLow2L - _windBandL) * windCoefficient;
            _windBandR += (_windLow2R - _windBandR) * windCoefficient;
            float windL = (_windLow2L - _windBandL) * wind * _gust * 5f;
            float windR = (_windLow2R - _windBandR) * wind * _gust * 5f;

            // Pluie : souffle aigu (passe-haut) et gouttes
            _rainHighL += (whiteL - _rainHighL) * rainCoefficient;
            _rainHighR += (whiteR - _rainHighR) * rainCoefficient;
            float hissL = (whiteL - _rainHighL) * 0.12f * rain, hissR = (whiteR - _rainHighR) * 0.12f * rain;
            if (_random.NextSingle() < dropsPerSample)
            {
                float strength = 0.15f + 0.35f * _random.NextSingle();
                if (_random.NextSingle() < 0.5f) _dropL += strength; else _dropR += strength;
            }
            _dropL *= 0.93f;
            _dropR *= 0.93f;
            float clickL = _dropL * (whiteL - _rainPrevL), clickR = _dropR * (whiteR - _rainPrevR);
            _rainPrevL = whiteL;
            _rainPrevR = whiteR;

            // Vagues : une vague déferle toutes les 6 à 12 s ; l'écume reflue ensuite
            _surfTimer -= dt;
            if (_surfTimer <= 0f)
            {
                _surfTimer = 6f + 6f * _random.NextSingle();
                _surfEnvelope = 1f;
                _surfPan = _random.NextSingle() * 2f - 1f;
            }
            _surfEnvelope *= 1f - 0.35f * dt;
            _surfLow1 += (whiteL - _surfLow1) * surfCoefficient;
            _surfLow2 += (_surfLow1 - _surfLow2) * surfCoefficient;
            float wave = (_surfLow2 * 3f) * surf * (0.15f + 0.85f * _surfEnvelope);
            float waveL = wave * (1f - 0.3f * _surfPan), waveR = wave * (1f + 0.3f * _surfPan);

            // Tonnerre : craquement bref puis grondement
            _thunderEnvelope *= 1f - 0.45f * dt;
            _crack *= 1f - 40f * dt;
            _thunderLow1 += (whiteR - _thunderLow1) * thunderCoefficient;
            _thunderLow2 += (_thunderLow1 - _thunderLow2) * thunderCoefficient;
            float thunder = _thunderLow2 * 9f * _thunderEnvelope + whiteL * 0.5f * _crack * _thunderEnvelope;

            // Volcan et séisme : grondement très grave
            _rumbleLow1 += (whiteL - _rumbleLow1) * rumbleCoefficient;
            _rumbleLow2 += (_rumbleLow1 - _rumbleLow2) * rumbleCoefficient;
            float rumble = _rumbleLow2 * 14f * MathF.Max(volcano * 0.8f, quake);

            float mixL = windL + hissL + clickL + waveL + thunder + rumble;
            float mixR = windR + hissR + clickR + waveR + thunder * 0.9f + rumble;
            _snowMuffleL += (mixL - _snowMuffleL) * muffleCoefficient;
            _snowMuffleR += (mixR - _snowMuffleR) * muffleCoefficient;
            left[i] = SoftClip(_snowMuffleL);
            right[i] = SoftClip(_snowMuffleR);
        }
    }

    /// <summary>Coefficient d'un passe-bas à un pôle pour une fréquence de coupure (Hz).</summary>
    private float Coefficient(float cutoffHz) => 1f - MathF.Exp(-2f * MathF.PI * cutoffHz / SampleRate);

    /// <summary>Écrêtage doux : jamais au-delà de ±1, même quand tout se cumule.</summary>
    private static float SoftClip(float x) => MathF.Tanh(x);

    private static void Lerp(ref float value, float target, float t) => value += (target - value) * t;

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3 - 2 * t);
    }
}
