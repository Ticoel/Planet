using System;
using System.Collections.Generic;
using System.Globalization;

namespace Planet.Ui;

/// <summary>
/// Un paramètre réglable de la simulation. Déclaré une seule fois ; le panneau « Paramètres »
/// fabrique son curseur tout seul (nom, unité, bornes, repère de la valeur terrestre).
/// </summary>
public sealed class SimParameter
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    /// <summary>Groupe repliable du panneau (« Étoile », « Orbite », « Vie »…).</summary>
    public required string Group { get; init; }
    public string Unit { get; init; } = "";
    public required double Min { get; init; }
    public required double Max { get; init; }
    public double Step { get; init; } = 0.01;
    /// <summary>Valeur de référence (celle de la Terre) : repère sur le curseur, bouton de retour.</summary>
    public required double Reference { get; init; }
    /// <summary>Explication courte (info-bulle) : ce que le paramètre change.</summary>
    public string Help { get; init; } = "";
    public required Func<double> Get { get; init; }
    /// <summary>Applique la valeur à la simulation en cours (la planète est conservée).</summary>
    public required Action<double> Set { get; init; }
    /// <summary>Format d'affichage (« 0.0 », « 0.000 »…).</summary>
    public string Format { get; init; } = "0.##";

    public string Display(double value) =>
        value.ToString(Format, CultureInfo.InvariantCulture).Replace('.', ',') + (Unit.Length > 0 ? " " + Unit : "");
}

/// <summary>Une bascule (case à cocher) : nuages, eau, fleuves, son…</summary>
public sealed class SimToggle
{
    public required string Label { get; init; }
    public required string Group { get; init; }
    public string Help { get; init; } = "";
    public required Func<bool> Get { get; init; }
    public required Action<bool> Set { get; init; }
}

/// <summary>Une couche d'affichage de la planète (réaliste, température, chimie, vie…).</summary>
public sealed class LayerEntry
{
    public required string Label { get; init; }
    public required string Group { get; init; }
    public string Help { get; init; } = "";
    public required Func<bool> IsActive { get; init; }
    public required Action Activate { get; init; }
    /// <summary>Disponible en ce moment (sinon le bouton est grisé) ; null : toujours.</summary>
    public Func<bool> IsAvailable { get; init; }
}

/// <summary>
/// Une ligne de la fiche de la sonde : ce qu'on lit en un point de la planète (cellule de la
/// grille). Le texte null masque la ligne (grandeur sans objet ici ou pas encore calculée).
/// </summary>
public sealed class ProbeField
{
    public required string Label { get; init; }
    public required Func<int, string> Read { get; init; }
}

/// <summary>
/// Une grandeur suivie dans le temps (courbe des graphiques) : globale (température moyenne,
/// biomasse…). Les valeurs sont gardées dans un tampon circulaire.
/// </summary>
public sealed class TimeSeries
{
    public const int Capacity = 600;

    public required string Label { get; init; }
    public string Unit { get; init; } = "";
    /// <summary>Valeur du moment ; null : rien à enregistrer (grandeur indisponible).</summary>
    public required Func<double?> Sample { get; init; }
    /// <summary>Couleur de la courbe (rouge, vert, bleu : 0 à 1).</summary>
    public (float R, float G, float B) Color { get; init; } = (1f, 1f, 1f);
    public string Format { get; init; } = "0.0";
    public bool Shown { get; set; } = true;

    private readonly double[] _values = new double[Capacity];
    private int _start;
    public int Count { get; private set; }

    public double this[int index] => _values[(_start + index) % Capacity];
    public double Last => Count > 0 ? this[Count - 1] : double.NaN;

    public void Add(double value)
    {
        if (Count < Capacity)
            _values[(_start + Count++) % Capacity] = value;
        else
        {
            _values[_start] = value;
            _start = (_start + 1) % Capacity;
        }
    }

    public void Clear()
    {
        Count = 0;
        _start = 0;
    }

    public (double min, double max) Range()
    {
        double min = double.MaxValue, max = double.MinValue;
        for (int i = 0; i < Count; i++)
        {
            min = Math.Min(min, this[i]);
            max = Math.Max(max, this[i]);
        }
        return Count == 0 ? (0, 1) : (min, max);
    }
}

/// <summary>Un événement du journal, daté dans le temps de la simulation.</summary>
public readonly record struct JournalEntry(string Stamp, string Text);

/// <summary>
/// Tout ce que l'interface montre, déclaré par la simulation : paramètres, bascules, couches,
/// lignes de la sonde, courbes, journal. L'interface se construit à partir de ce registre ; pour
/// ajouter un réglage ou une grandeur (un élément chimique, une espèce), il suffit de l'y
/// inscrire : aucun panneau à modifier.
/// </summary>
public sealed class UiRegistry
{
    public const int JournalCapacity = 200;

    public List<SimParameter> Parameters { get; } = new();
    public List<SimToggle> Toggles { get; } = new();
    public List<LayerEntry> Layers { get; } = new();
    public List<ProbeField> ProbeFields { get; } = new();
    public List<TimeSeries> Series { get; } = new();
    public List<JournalEntry> Journal { get; } = new();

    /// <summary>Augmente à chaque inscription : l'interface se reconstruit quand il change.</summary>
    public int Version { get; private set; }
    /// <summary>Augmente à chaque événement du journal.</summary>
    public int JournalVersion { get; private set; }

    public void Add(SimParameter parameter) { Parameters.Add(parameter); Version++; }
    public void Add(SimToggle toggle) { Toggles.Add(toggle); Version++; }
    public void Add(LayerEntry layer) { Layers.Add(layer); Version++; }
    public void Add(ProbeField field) { ProbeFields.Add(field); Version++; }
    public void Add(TimeSeries series) { Series.Add(series); Version++; }

    /// <summary>Groupes dans l'ordre de première inscription.</summary>
    public static List<string> Groups<T>(IEnumerable<T> items, Func<T, string> group)
    {
        var groups = new List<string>();
        foreach (var item in items)
            if (!groups.Contains(group(item)))
                groups.Add(group(item));
        return groups;
    }

    public void Log(string stamp, string text)
    {
        // (un même message répété coup sur coup n'est noté qu'une fois)
        if (Journal.Count > 0 && Journal[^1].Text == text)
            return;
        Journal.Add(new JournalEntry(stamp, text));
        if (Journal.Count > JournalCapacity)
            Journal.RemoveAt(0);
        JournalVersion++;
    }

    /// <summary>Enregistre un point de chaque courbe.</summary>
    public void SampleSeries()
    {
        foreach (var series in Series)
            if (series.Sample() is { } value && !double.IsNaN(value))
                series.Add(value);
    }

    public void ClearSeries()
    {
        foreach (var series in Series)
            series.Clear();
    }
}
