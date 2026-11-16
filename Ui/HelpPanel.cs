using Godot;

namespace Planet.Ui;

/// <summary>
/// Aide des commandes (touche H ou bouton « ? ») : un panneau au centre de l'écran, les touches
/// dessinées comme des touches de clavier, rangées par thème en colonnes. Échap ou H pour fermer.
/// </summary>
public partial class HelpPanel : PanelContainer
{
    private static readonly Color Accent = new(0.45f, 0.75f, 1f);
    private static readonly Color Text = new(0.88f, 0.9f, 0.93f);
    private static readonly Color Dim = new(0.6f, 0.64f, 0.7f);

    private static readonly (string title, (string[] keys, string what)[] rows)[] Sections =
    {
        ("Se déplacer", new[]
        {
            (new[] { "↑", "↓" }, "Avancer, reculer (aussi Z et S)"),
            (new[] { "←", "→" }, "Tourner"),
            (new[] { "Q", "D" }, "Pas de côté"),
            (new[] { "Pg ↑", "Pg ↓" }, "Monter, descendre"),
            (new[] { "Origine", "Fin" }, "Regarder en haut, en bas"),
            (new[] { "Maj" }, "Plus vite"),
            (new[] { "G" }, "Marcher (atterrit d'abord), voler"),
            (new[] { "X" }, "Retour en orbite"),
            (new[] { "Molette" }, "Avancer dans l'axe du regard"),
            (new[] { "Clic gauche" }, "Glisser : déplacer"),
            (new[] { "Clic droit" }, "Glisser : regarder"),
        }),
        ("Le temps", new[]
        {
            (new[] { "P" }, "Pause"),
            (new[] { "+", "−" }, "Vitesse de l'horloge"),
            (new[] { "C" }, "Figer la géologie, passer au climat"),
            (new[] { "T" }, "Avance rapide jusqu'à 4,5 milliards d'années"),
            (new[] { "R" }, "Nouvelle planète"),
            (new[] { "E" }, "Étoile suivante"),
        }),
        ("Regarder", new[]
        {
            (new[] { "1" }, "Géologie, altitude"),
            (new[] { "2", "3", "4" }, "Plaques, frontières, âge du plancher"),
            (new[] { "5", "6" }, "Ensoleillement, température"),
            (new[] { "7", "8" }, "Vents et pression, pluie et humidité"),
            (new[] { "9", "0" }, "Climats, vue réaliste"),
            (new[] { "V", "F" }, "Flèches des vents, fleuves"),
            (new[] { "U", "N" }, "Masquer l'eau, les nuages"),
            (new[] { "O" }, "Qualité graphique"),
            (new[] { "K" }, "Couper le son"),
            (new[] { "Tab" }, "Masquer l'interface"),
        }),
        ("Aller voir", new[]
        {
            (new[] { "B" }, "Événement naturel suivant (Maj : précédent)"),
            (new[] { "J" }, "Source chaude ou geyser"),
            (new[] { "Y" }, "Grotte"),
            (new[] { "L" }, "Volcan en éruption"),
            (new[] { "F1", "F2", "F3" }, "Sommet, fleuve, lac"),
            (new[] { "F4", "F5" }, "Côte (Maj : falaise), désert venteux"),
            (new[] { "F6", "F7" }, "Averse, neige"),
            (new[] { "F9", "F10", "F11" }, "Nuages, coucher de soleil, Lune"),
        }),
        ("Garder une trace", new[]
        {
            (new[] { "F12" }, "Photo (sans l'interface)"),
            (new[] { "Maj", "F12" }, "Vidéo : marche, arrêt"),
            (new[] { "M" }, "Carte de la couche affichée"),
            (new[] { "²" }, "Rapport de bug (capture et état du jeu)"),
            (new[] { "Maj", "G" }, "Relief en volume : marche, arrêt"),
            (new[] { "Clic" }, "Épingler la sonde sur la planète"),
        }),
    };

    public HelpPanel()
    {
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsPreset(LayoutPreset.Center);
        GrowHorizontal = GrowDirection.Both;
        GrowVertical = GrowDirection.Both;
        AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.07f, 0.08f, 0.1f, 0.94f),
            CornerRadiusTopLeft = 14, CornerRadiusTopRight = 14, CornerRadiusBottomLeft = 14, CornerRadiusBottomRight = 14,
            BorderColor = new Color(0.3f, 0.45f, 0.65f, 0.8f),
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            ContentMarginLeft = 26, ContentMarginRight = 26, ContentMarginTop = 18, ContentMarginBottom = 18,
        });

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        AddChild(root);

        var header = new HBoxContainer();
        root.AddChild(header);
        header.AddChild(MakeLabel("Commandes", 22, Text));
        header.AddChild(MakeLabel($"   Planet {ProjectSettings.GetSetting("application/config/version")}", 13, Dim));
        header.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        header.AddChild(MakeLabel("H ou Échap pour fermer", 13, Dim));

        // Trois colonnes : déplacement ; temps et capture ; regarder et aller voir
        var columns = new HBoxContainer();
        columns.AddThemeConstantOverride("separation", 34);
        root.AddChild(columns);
        int[][] layout = { new[] { 0, 1 }, new[] { 2, 4 }, new[] { 3 } };
        foreach (var column in layout)
        {
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 16);
            columns.AddChild(box);
            foreach (int index in column)
                box.AddChild(MakeSection(Sections[index].title, Sections[index].rows));
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Visible && @event is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Visible = false;
            GetViewport().SetInputAsHandled();
        }
    }

    private static Control MakeSection(string title, (string[] keys, string what)[] rows)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 5);
        box.AddChild(MakeLabel(title.ToUpperInvariant(), 13, Accent));
        foreach (var (keys, what) in rows)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 4);
            var caps = new HBoxContainer { CustomMinimumSize = new Vector2(118, 0) };
            caps.AddThemeConstantOverride("separation", 3);
            foreach (string key in keys)
                caps.AddChild(MakeKey(key));
            row.AddChild(caps);
            row.AddChild(MakeLabel(what, 14, Text));
            box.AddChild(row);
        }
        return box;
    }

    /// <summary>Une touche de clavier : petit cadre arrondi, ombré en bas.</summary>
    private static Control MakeKey(string key)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.2f, 0.23f, 0.28f),
            CornerRadiusTopLeft = 5, CornerRadiusTopRight = 5, CornerRadiusBottomLeft = 5, CornerRadiusBottomRight = 5,
            BorderColor = new Color(0.1f, 0.11f, 0.13f),
            BorderWidthBottom = 3, BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1,
            ContentMarginLeft = 7, ContentMarginRight = 7, ContentMarginTop = 1, ContentMarginBottom = 2,
        });
        var label = MakeLabel(key, 13, Colors.White);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        panel.AddChild(label);
        return panel;
    }

    private static Label MakeLabel(string text, int size, Color color)
    {
        var label = new Label { Text = text, VerticalAlignment = VerticalAlignment.Center };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }
}
