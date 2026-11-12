using System;
using System.Linq;
using Planet.Ui;

namespace Planet.Tests;

/// <summary>Registre de l'interface : paramètres, courbes, journal.</summary>
public static class UiTests
{
    [Test]
    public static void RegistryGroupsParametersInOrder()
    {
        var registry = new UiRegistry();
        double tilt = 23.44;
        SimParameter Parameter(string id, string group) => new()
        {
            Id = id, Label = id, Group = group, Min = 0, Max = 60, Reference = 23.44, Unit = "°", Format = "0.0",
            Get = () => tilt, Set = v => tilt = v,
        };
        registry.Add(Parameter("a", "Orbite"));
        registry.Add(Parameter("b", "Étoile"));
        registry.Add(Parameter("c", "Orbite"));
        var groups = UiRegistry.Groups(registry.Parameters, p => p.Group);
        Assert.True(groups.SequenceEqual(new[] { "Orbite", "Étoile" }), "groupes dans l'ordre de première inscription");
        Assert.True(registry.Version == 3, "chaque inscription fait reconstruire l'interface");
        registry.Parameters[0].Set(30);
        Assert.True(registry.Parameters[2].Get() == 30, "le réglage agit sur la simulation");
        Assert.True(registry.Parameters[0].Display(23.44) == "23,4 °", $"affichage : {registry.Parameters[0].Display(23.44)}");
    }

    [Test]
    public static void SeriesKeepTheLastValues()
    {
        double value = 0;
        var series = new TimeSeries { Label = "t", Sample = () => value < 0 ? null : value };
        var registry = new UiRegistry();
        registry.Add(series);
        for (int i = 0; i < TimeSeries.Capacity + 50; i++)
        {
            value = i;
            registry.SampleSeries();
        }
        Assert.True(series.Count == TimeSeries.Capacity, "le tampon est plein");
        Assert.True(series[0] == 50 && series.Last == TimeSeries.Capacity + 49, "les plus anciennes valeurs sont oubliées");
        var (min, max) = series.Range();
        Assert.True(min == 50 && max == TimeSeries.Capacity + 49, "étendue");
        value = -1;
        registry.SampleSeries();
        Assert.True(series.Last == TimeSeries.Capacity + 49, "une grandeur indisponible n'ajoute pas de point");
        registry.ClearSeries();
        Assert.True(series.Count == 0, "remise à zéro");
    }

    [Test]
    public static void JournalSkipsRepeatsAndStaysBounded()
    {
        var registry = new UiRegistry();
        registry.Log("1 Ma", "Éruption");
        registry.Log("1 Ma", "Éruption");
        Assert.True(registry.Journal.Count == 1, "un message répété n'est noté qu'une fois");
        for (int i = 0; i < UiRegistry.JournalCapacity + 20; i++)
            registry.Log($"{i} Ma", $"événement {i}");
        Assert.True(registry.Journal.Count == UiRegistry.JournalCapacity, "le journal est borné");
        Assert.True(registry.Journal[^1].Text == $"événement {UiRegistry.JournalCapacity + 19}", "le plus récent est le dernier");
    }
}
