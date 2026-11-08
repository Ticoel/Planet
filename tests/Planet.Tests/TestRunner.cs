using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace Planet.Tests;

/// <summary>Marque une méthode statique sans paramètre comme test.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute
{
    /// <summary>Simulation longue (plusieurs secondes), sautée avec --quick.</summary>
    public bool Slow { get; init; }
}

/// <summary>Échec d'une vérification.</summary>
public sealed class AssertionException : Exception
{
    public AssertionException(string message) : base(message) { }
}

/// <summary>Vérifications : chacune lève une exception explicite en cas d'échec.</summary>
public static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
            throw new AssertionException(message);
    }

    public static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"{label} : attendu {expected}, obtenu {actual}");
    }

    public static void Near(double actual, double expected, double tolerance, string label)
    {
        if (!(Math.Abs(actual - expected) <= tolerance))
            throw new AssertionException($"{label} : attendu {expected:G6} ± {tolerance:G3}, obtenu {actual:G6}");
    }

    public static void InRange(double actual, double min, double max, string label)
    {
        if (!(actual >= min && actual <= max))
            throw new AssertionException($"{label} : attendu entre {min:G6} et {max:G6}, obtenu {actual:G6}");
    }

    public static void Finite(float[] values, string label)
    {
        for (int i = 0; i < values.Length; i++)
            if (!float.IsFinite(values[i]))
                throw new AssertionException($"{label} : valeur non finie ({values[i]}) à l'indice {i}");
    }
}

/// <summary>Trouve les méthodes [Test], les exécute et affiche un bilan.</summary>
public static class TestRunner
{
    public static int Run(string[] args)
    {
        bool quick = args.Contains("--quick");
        string[] filters = args.Where(a => !a.StartsWith("--")).ToArray();

        var tests = Assembly.GetExecutingAssembly().GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Select(method => (method, attribute: method.GetCustomAttribute<TestAttribute>()))
            .Where(t => t.attribute != null)
            .Select(t => (name: $"{t.method.DeclaringType!.Name}.{t.method.Name}", t.method, t.attribute))
            .Where(t => filters.Length == 0 || filters.Any(f => t.name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(t => t.attribute.Slow)
            .ThenBy(t => t.name)
            .ToList();

        int passed = 0, failed = 0, skipped = 0;
        var total = Stopwatch.StartNew();
        foreach (var (name, method, attribute) in tests)
        {
            if (quick && attribute.Slow)
            {
                skipped++;
                continue;
            }
            var stopwatch = Stopwatch.StartNew();
            try
            {
                method.Invoke(null, null);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("PASS ");
                Console.ResetColor();
                Console.WriteLine($"{name} ({stopwatch.Elapsed.TotalSeconds:0.00} s)");
                passed++;
            }
            catch (TargetInvocationException exception)
            {
                var inner = exception.InnerException!;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("FAIL ");
                Console.ResetColor();
                Console.WriteLine($"{name} ({stopwatch.Elapsed.TotalSeconds:0.00} s)");
                Console.WriteLine(inner is AssertionException
                    ? $"     {inner.Message}"
                    : $"     {inner.GetType().Name} : {inner.Message}\n{inner.StackTrace}");
                failed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{passed} réussi(s), {failed} échoué(s), {skipped} sauté(s) en {total.Elapsed.TotalSeconds:0.0} s");
        return failed == 0 ? 0 : 1;
    }
}
