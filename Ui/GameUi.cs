using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Planet.Ui;

/// <summary>Ce que l'interface demande au jeu, en dehors du registre : le temps, la qualité, la sonde.</summary>
public sealed class UiHost
{
    public required Func<bool> IsRunning { get; init; }
    public required Action TogglePause { get; init; }
    /// <summary>+1 : plus vite ; −1 : moins vite.</summary>
    public required Action<int> ChangeSpeed { get; init; }
    public required Func<string> SpeedLabel { get; init; }
    public required Func<string> ClockLabel { get; init; }
    public required Action ToggleClock { get; init; }
    public required Func<string> TimeLabel { get; init; }
    /// <summary>Ligne d'état : position de la caméra, altitude, images par seconde.</summary>
    public required Func<string> StatusLabel { get; init; }
    public required string[] QualityNames { get; init; }
    public required Func<int> GetQuality { get; init; }
    public required Action<int> SetQuality { get; init; }
    /// <summary>Cellule sondée (−1 : aucune), son titre, et si la sonde est épinglée.</summary>
    public required Func<int> ProbeCell { get; init; }
    public required Func<string> ProbeTitle { get; init; }
    public required Func<bool> ProbePinned { get; init; }
    public required Action ReleaseProbe { get; init; }
    public required Action ToggleHelp { get; init; }
}

/// <summary>
/// Interface du jeu : une barre du temps en haut, les couches à gauche, les paramètres à droite,
/// et en bas la sonde, les graphiques et le journal. Tout est fabriqué à partir du
/// <see cref="UiRegistry"/> : inscrire un paramètre, une couche, une ligne de sonde ou une courbe
/// suffit à le faire apparaître. Les commandes ne prennent jamais le clavier (les touches
/// restent au jeu) ; chaque panneau se masque depuis la barre du haut.
/// </summary>
public sealed partial class GameUi : CanvasLayer
{
    private const int TopBarHeight = 34;
    private const int LeftWidth = 172, RightWidth = 280, BottomHeight = 150, Margin = 6;
    private const int FontSize = 12;
    private const double RefreshSeconds = 0.2;
    private static readonly Color PanelColor = new(0.05f, 0.07f, 0.09f, 0.8f);
    private static readonly Color AccentColor = new(0.45f, 0.75f, 1f);
    private static readonly Color DimColor = new(0.72f, 0.76f, 0.8f);

    public UiRegistry Registry { get; } = new();

    private UiHost _host;
    private Control _root;
    private Button _pauseButton, _clockButton;
    private Label _speedLabel, _timeLabel, _statusLabel;
    private OptionButton _quality;
    private PanelContainer _layersPanel, _parametersPanel, _probePanel, _graphPanel, _journalPanel;
    private VBoxContainer _layersBox, _parametersBox, _legendBox;
    private Label _probeTitle, _probeText, _journalText;
    private Button _releaseButton;
    private GraphControl _graph;
    private readonly List<(LayerEntry layer, Button button)> _layerButtons = new();
    private readonly List<(SimParameter parameter, HSlider slider, Label value)> _sliders = new();
    private readonly List<(SimToggle toggle, CheckBox box)> _checkBoxes = new();
    private readonly HashSet<string> _collapsed = new();
    private int _builtVersion = -1, _journalVersion = -1;
    private double _refreshTimer;

    /// <summary>Interface affichée (masquée pour contempler ou photographier).</summary>
    public bool Shown
    {
        get => _root.Visible;
        set => _root.Visible = value;
    }

    /// <summary>Le pointeur est au-dessus d'un panneau (le survol de la planète est alors ignoré).</summary>
    public bool PointerOverUi
    {
        get
        {
            if (!_root.Visible)
                return false;
            Vector2 mouse = _root.GetGlobalMousePosition();
            foreach (Node child in _root.GetChildren())
                if (child is Control { Visible: true } control && control.GetGlobalRect().HasPoint(mouse))
                    return true;
            return false;
        }
    }

    public void Setup(UiHost host)
    {
        _host = host;
        _root = new Control { Name = "Root", MouseFilter = Control.MouseFilterEnum.Ignore, Theme = BuildTheme() };
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);
        BuildTopBar();
        BuildSidePanels();
        BuildBottomPanels();
    }

    // --- Apparence ---------------------------------------------------------------------------------

    private static Theme BuildTheme()
    {
        var theme = new Theme { DefaultFontSize = FontSize };
        var panel = new StyleBoxFlat { BgColor = PanelColor };
        panel.SetCornerRadiusAll(6);
        panel.SetContentMarginAll(7);
        theme.SetStylebox("panel", "PanelContainer", panel);

        StyleBoxFlat Box(Color color)
        {
            var box = new StyleBoxFlat { BgColor = color };
            box.SetCornerRadiusAll(4);
            box.ContentMarginLeft = box.ContentMarginRight = 7;
            box.ContentMarginTop = box.ContentMarginBottom = 3;
            return box;
        }
        foreach (string type in new[] { "Button", "OptionButton" })
        {
            theme.SetStylebox("normal", type, Box(new Color(1f, 1f, 1f, 0.07f)));
            theme.SetStylebox("hover", type, Box(new Color(1f, 1f, 1f, 0.16f)));
            theme.SetStylebox("pressed", type, Box(new Color(AccentColor, 0.38f)));
            theme.SetStylebox("disabled", type, Box(new Color(1f, 1f, 1f, 0.03f)));
            theme.SetStylebox("focus", type, new StyleBoxEmpty());
        }
        // Cases à cocher : pas de fond (sinon une case cochée ressemble à un bouton enfoncé)
        foreach (string state in new[] { "normal", "hover", "pressed", "disabled", "focus", "hover_pressed" })
            theme.SetStylebox(state, "CheckBox", new StyleBoxEmpty());
        return theme;
    }

    private static PanelContainer Panel(string name) => new() { Name = name, MouseFilter = Control.MouseFilterEnum.Stop };

    private static Label Title(string text)
    {
        var label = new Label { Text = text.ToUpperInvariant() };
        label.AddThemeColorOverride("font_color", AccentColor);
        label.AddThemeFontSizeOverride("font_size", FontSize - 1);
        return label;
    }

    private static Button MakeButton(string text, string tooltip, Action pressed, bool toggle = false)
    {
        // (jamais de focus clavier : les flèches et la barre d'espace restent au jeu)
        var button = new Button { Text = text, TooltipText = tooltip, FocusMode = Control.FocusModeEnum.None, ToggleMode = toggle };
        button.Pressed += pressed;
        return button;
    }

    // --- Barre du haut -------------------------------------------------------------------------------

    private void BuildTopBar()
    {
        var bar = Panel("TopBar");
        bar.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);
        bar.OffsetLeft = Margin; bar.OffsetRight = -Margin; bar.OffsetTop = Margin; bar.OffsetBottom = Margin + TopBarHeight;
        _root.AddChild(bar);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        bar.AddChild(row);

        _pauseButton = MakeButton("Pause", "Pause / reprise (P)", () => _host.TogglePause());
        _pauseButton.CustomMinimumSize = new Vector2(62, 0);
        row.AddChild(_pauseButton);
        row.AddChild(MakeButton("−", "Ralentir l'horloge (-)", () => _host.ChangeSpeed(-1)));
        _speedLabel = new Label { CustomMinimumSize = new Vector2(132, 0), HorizontalAlignment = HorizontalAlignment.Center, ClipText = true };
        row.AddChild(_speedLabel);
        row.AddChild(MakeButton("+", "Accélérer l'horloge (+)", () => _host.ChangeSpeed(+1)));
        _clockButton = MakeButton("", "Horloge géologique (millions d'années) ou climatique (jours) (C)", () => _host.ToggleClock());
        row.AddChild(_clockButton);
        _timeLabel = new Label();
        _timeLabel.AddThemeColorOverride("font_color", AccentColor);
        row.AddChild(_timeLabel);
        _statusLabel = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, HorizontalAlignment = HorizontalAlignment.Right, ClipText = true };
        _statusLabel.AddThemeColorOverride("font_color", DimColor);
        row.AddChild(_statusLabel);

        _quality = new OptionButton { FocusMode = Control.FocusModeEnum.None, TooltipText = "Qualité graphique (O)" };
        foreach (string name in _host.QualityNames)
            _quality.AddItem(name);
        _quality.ItemSelected += index => _host.SetQuality((int)index);
        row.AddChild(_quality);

        // Afficher ou masquer chaque panneau
        void PanelToggle(string text, string tooltip, Func<Control> panel)
        {
            Button button = null;
            button = MakeButton(text, tooltip, () => panel().Visible = button.ButtonPressed, toggle: true);
            button.ButtonPressed = true;
            row.AddChild(button);
        }
        PanelToggle("Couches", "Ce qu'on regarde sur la planète", () => _layersPanel);
        PanelToggle("Paramètres", "Ce qu'on règle dans la simulation", () => _parametersPanel);
        PanelToggle("Sonde", "Ce qu'on lit en un point (clic sur la planète pour l'épingler)", () => _probePanel);
        PanelToggle("Courbes", "Évolution des grandeurs dans le temps", () => _graphPanel);
        PanelToggle("Journal", "Événements de la simulation", () => _journalPanel);
        row.AddChild(MakeButton("?", "Aide : touches du clavier (H)", () => _host.ToggleHelp()));
    }

    // --- Panneaux latéraux -----------------------------------------------------------------------------

    private void BuildSidePanels()
    {
        int top = Margin * 2 + TopBarHeight, bottom = -(BottomHeight + Margin * 2);
        _layersPanel = Panel("Layers");
        _layersPanel.AnchorBottom = 1;
        _layersPanel.OffsetLeft = Margin; _layersPanel.OffsetRight = Margin + LeftWidth;
        _layersPanel.OffsetTop = top; _layersPanel.OffsetBottom = bottom;
        _root.AddChild(_layersPanel);
        _layersBox = Scrolling(_layersPanel);

        _parametersPanel = Panel("Parameters");
        _parametersPanel.AnchorLeft = 1; _parametersPanel.AnchorRight = 1; _parametersPanel.AnchorBottom = 1;
        _parametersPanel.OffsetLeft = -(Margin + RightWidth); _parametersPanel.OffsetRight = -Margin;
        _parametersPanel.OffsetTop = top; _parametersPanel.OffsetBottom = bottom;
        _root.AddChild(_parametersPanel);
        _parametersBox = Scrolling(_parametersPanel);
    }

    private static VBoxContainer Scrolling(Control parent)
    {
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        parent.AddChild(scroll);
        var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 3);
        scroll.AddChild(box);
        return box;
    }

    /// <summary>Reconstruit les couches et les paramètres à partir du registre.</summary>
    private void Rebuild()
    {
        _builtVersion = Registry.Version;
        foreach (Node child in _layersBox.GetChildren()) child.QueueFree();
        foreach (Node child in _parametersBox.GetChildren()) child.QueueFree();
        foreach (Node child in _legendBox.GetChildren()) child.QueueFree();
        _layerButtons.Clear();
        _sliders.Clear();
        _checkBoxes.Clear();

        // Couches, par groupes
        foreach (string group in UiRegistry.Groups(Registry.Layers, l => l.Group))
        {
            _layersBox.AddChild(Title(group));
            foreach (var layer in Registry.Layers.Where(l => l.Group == group))
            {
                var button = MakeButton(layer.Label, layer.Help, () => layer.Activate(), toggle: true);
                button.Alignment = HorizontalAlignment.Left;
                button.ClipText = true;   // (le panneau garde sa largeur, quel que soit le nom)
                _layersBox.AddChild(button);
                _layerButtons.Add((layer, button));
            }
        }

        // Paramètres et bascules, par groupes repliables
        var groups = UiRegistry.Groups(Registry.Parameters, p => p.Group);
        foreach (string group in UiRegistry.Groups(Registry.Toggles, t => t.Group))
            if (!groups.Contains(group))
                groups.Add(group);
        foreach (string group in groups)
        {
            var content = new VBoxContainer { Visible = !_collapsed.Contains(group) };
            content.AddThemeConstantOverride("separation", 1);
            Button header = null;
            header = MakeButton((content.Visible ? "▾ " : "▸ ") + group.ToUpperInvariant(), "Replier / déplier", () =>
            {
                content.Visible = !content.Visible;
                if (content.Visible) _collapsed.Remove(group); else _collapsed.Add(group);
                header.Text = (content.Visible ? "▾ " : "▸ ") + group.ToUpperInvariant();
            });
            header.Alignment = HorizontalAlignment.Left;
            header.ClipText = true;
            header.AddThemeColorOverride("font_color", AccentColor);
            _parametersBox.AddChild(header);
            _parametersBox.AddChild(content);
            foreach (var parameter in Registry.Parameters.Where(p => p.Group == group))
                content.AddChild(ParameterRow(parameter));
            foreach (var toggle in Registry.Toggles.Where(t => t.Group == group))
            {
                var box = new CheckBox { Text = toggle.Label, TooltipText = toggle.Help, FocusMode = Control.FocusModeEnum.None, ClipText = true };
                box.Toggled += pressed => toggle.Set(pressed);
                content.AddChild(box);
                _checkBoxes.Add((toggle, box));
            }
        }

        // Légende des courbes : une case par grandeur
        foreach (var series in Registry.Series)
        {
            var box = new CheckBox { Text = series.Label, ButtonPressed = series.Shown, FocusMode = Control.FocusModeEnum.None, ClipText = true };
            box.AddThemeColorOverride("font_color", new Color(series.Color.R, series.Color.G, series.Color.B));
            box.AddThemeColorOverride("font_pressed_color", new Color(series.Color.R, series.Color.G, series.Color.B));
            box.Toggled += pressed => series.Shown = pressed;
            _legendBox.AddChild(box);
        }
        _graph.Series = Registry.Series;
    }

    /// <summary>Nom, valeur et bouton de retour à la valeur terrestre, puis le curseur.</summary>
    private Control ParameterRow(SimParameter parameter)
    {
        var box = new VBoxContainer { TooltipText = parameter.Help };
        box.AddThemeConstantOverride("separation", 0);
        var row = new HBoxContainer();
        var name = new Label { Text = parameter.Label, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true, TooltipText = parameter.Help, MouseFilter = Control.MouseFilterEnum.Pass };
        var value = new Label { HorizontalAlignment = HorizontalAlignment.Right };
        value.AddThemeColorOverride("font_color", AccentColor);
        var slider = new HSlider
        {
            MinValue = parameter.Min, MaxValue = parameter.Max, Step = parameter.Step, Value = parameter.Get(),
            FocusMode = Control.FocusModeEnum.None, TooltipText = parameter.Help,
            CustomMinimumSize = new Vector2(0, 16),
        };
        slider.ValueChanged += v =>
        {
            parameter.Set(v);
            value.Text = parameter.Display(v);
        };
        var reset = MakeButton("↺", $"Valeur de référence : {parameter.Display(parameter.Reference)}", () => slider.Value = parameter.Reference);
        reset.AddThemeFontSizeOverride("font_size", FontSize - 1);
        row.AddChild(name);
        row.AddChild(value);
        row.AddChild(reset);
        box.AddChild(row);
        box.AddChild(slider);
        _sliders.Add((parameter, slider, value));
        return box;
    }

    // --- Panneaux du bas -----------------------------------------------------------------------------------

    private void BuildBottomPanels()
    {
        var row = new HBoxContainer { Name = "Bottom", MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AnchorTop = 1; row.AnchorBottom = 1; row.AnchorRight = 1;
        row.OffsetLeft = Margin; row.OffsetRight = -Margin; row.OffsetTop = -(BottomHeight + Margin); row.OffsetBottom = -Margin;
        row.AddThemeConstantOverride("separation", Margin);
        _root.AddChild(row);

        // Sonde
        _probePanel = Panel("Probe");
        _probePanel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _probePanel.SizeFlagsStretchRatio = 1.1f;
        row.AddChild(_probePanel);
        var probe = new VBoxContainer();
        _probePanel.AddChild(probe);
        var probeHeader = new HBoxContainer();
        probe.AddChild(probeHeader);
        probeHeader.AddChild(Title("Sonde"));
        _probeTitle = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true };
        probeHeader.AddChild(_probeTitle);
        _releaseButton = MakeButton("Libérer", "La sonde suit de nouveau le pointeur", () => _host.ReleaseProbe());
        probeHeader.AddChild(_releaseButton);
        var probeScroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        probe.AddChild(probeScroll);
        _probeText = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        probeScroll.AddChild(_probeText);

        // Courbes
        _graphPanel = Panel("Graphs");
        _graphPanel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _graphPanel.SizeFlagsStretchRatio = 1.4f;
        row.AddChild(_graphPanel);
        var graphs = new HBoxContainer();
        _graphPanel.AddChild(graphs);
        _graph = new GraphControl { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        graphs.AddChild(_graph);
        var legendScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(150, 0) };
        graphs.AddChild(legendScroll);
        _legendBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _legendBox.AddThemeConstantOverride("separation", 0);
        legendScroll.AddChild(_legendBox);

        // Journal
        _journalPanel = Panel("Journal");
        _journalPanel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(_journalPanel);
        var journal = new VBoxContainer();
        _journalPanel.AddChild(journal);
        journal.AddChild(Title("Journal"));
        var journalScroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        journal.AddChild(journalScroll);
        _journalText = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _journalText.AddThemeColorOverride("font_color", DimColor);
        journalScroll.AddChild(_journalText);
    }

    // --- Mise à jour -------------------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        if (_host == null || !_root.Visible)
            return;
        if (_builtVersion != Registry.Version)
            Rebuild();
        _refreshTimer -= delta;
        if (_refreshTimer > 0)
            return;
        _refreshTimer = RefreshSeconds;

        _pauseButton.Text = _host.IsRunning() ? "Pause" : "Reprise";
        _speedLabel.Text = _host.SpeedLabel();
        _clockButton.Text = _host.ClockLabel();
        _timeLabel.Text = _host.TimeLabel();
        _statusLabel.Text = _host.StatusLabel();
        if (_quality.Selected != _host.GetQuality())
            _quality.Select(_host.GetQuality());

        foreach (var (layer, button) in _layerButtons)
        {
            button.SetPressedNoSignal(layer.IsActive());
            button.Disabled = layer.IsAvailable != null && !layer.IsAvailable();
        }
        foreach (var (parameter, slider, value) in _sliders)
        {
            // (la simulation ou une touche a pu changer la valeur)
            double current = parameter.Get();
            if (Math.Abs(slider.Value - current) > parameter.Step * 0.5)
                slider.SetValueNoSignal(current);
            value.Text = parameter.Display(current);
        }
        foreach (var (toggle, box) in _checkBoxes)
            box.SetPressedNoSignal(toggle.Get());

        // Sonde
        if (_probePanel.Visible)
        {
            int cell = _host.ProbeCell();
            _releaseButton.Visible = _host.ProbePinned();
            _probeTitle.Text = cell >= 0 ? _host.ProbeTitle() : "pointez la planète ; cliquez pour épingler";
            var lines = new List<string>();
            if (cell >= 0)
                foreach (var field in Registry.ProbeFields)
                    if (field.Read(cell) is { Length: > 0 } text)
                        lines.Add($"{field.Label} : {text}");
            _probeText.Text = string.Join("\n", lines);
        }
        if (_graphPanel.Visible)
            _graph.QueueRedraw();
        if (_journalVersion != Registry.JournalVersion)
        {
            _journalVersion = Registry.JournalVersion;
            // Les plus récents en haut
            _journalText.Text = string.Join("\n", Registry.Journal.AsEnumerable().Reverse().Take(60).Select(e => $"{e.Stamp}  {e.Text}"));
        }
    }
}
