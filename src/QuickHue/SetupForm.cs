namespace QuickHue;

internal sealed class SetupForm : Form
{
    private const int RailWidth = 248;

    private readonly ConfigStore _store;
    private readonly AppConfig _draft;
    private readonly CancellationTokenSource _lifetime = new();

    private readonly ThemedComboBox _bridgeList = new() { EmptyText = "No bridge found yet" };
    private readonly TextBox _manualAddressText = new() { PlaceholderText = "192.168.1.42" };
    private readonly InputFrame _manualAddress;
    private readonly ThemedButton _discoverButton = new(ButtonKind.Secondary) { Text = "Search" };
    private readonly ThemedButton _pairButton = new(ButtonKind.Secondary) { Text = "Pair bridge" };
    private readonly ThemedComboBox _lightList = new() { EmptyText = "Pair a bridge first" };
    private readonly ThemedButton _testButton = new(ButtonKind.Secondary) { Text = "Blink" };
    private readonly HotkeyBox _hotkeyBox = new();
    private readonly ToggleSwitch _startWithWindows = new() { Text = "Launch QuickHue when I sign in" };
    private readonly StatusPill _status = new();
    private readonly StepList _steps = new();
    private readonly Label _subtitle = new();
    private readonly Label _hotkeyHint = new();
    private readonly ThemedButton _saveButton = new(ButtonKind.Primary) { Text = "Save changes", DialogResult = DialogResult.None };
    private readonly ThemedButton _cancelButton = new(ButtonKind.Secondary) { Text = "Cancel", DialogResult = DialogResult.Cancel };

    private readonly List<Control> _railControls = [];
    private bool _shown;
    private bool _busy;
    private string _idleGuidance = string.Empty;

    public SetupForm(ConfigStore store, AppConfig current)
    {
        _store = store;
        _draft = current.Copy();
        _manualAddress = new InputFrame(_manualAddressText);

        Text = current.IsConfigured ? "QuickHue settings" : "Set up QuickHue";
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(844, 696);
        MinimumSize = Size;
        Font = Theme.Text(9.5F);
        Icon = IconFactory.Create(HueIconState.On);
        KeyPreview = true;

        BuildLayout();
        ApplyTheme();

        _hotkeyBox.Binding = current.Hotkey;
        _startWithWindows.Checked = current.StartWithWindows;
        if (!string.IsNullOrWhiteSpace(current.BridgeAddress))
        {
            _bridgeList.Items.Add(new BridgeInfo(current.BridgeId, current.BridgeAddress, "Saved"));
            _bridgeList.SelectedIndex = 0;
            _manualAddressText.Text = current.BridgeAddress;
        }

        _discoverButton.Click += async (_, _) => await DiscoverAsync();
        _pairButton.Click += async (_, _) => await PairAsync();
        _testButton.Click += async (_, _) => await BlinkAsync();
        _bridgeList.SelectedIndexChanged += (_, _) =>
        {
            if (_bridgeList.SelectedItem is BridgeInfo bridge)
            {
                _manualAddressText.Text = bridge.Address;
            }
        };
        _lightList.SelectedIndexChanged += (_, _) => RefreshDerivedState();
        _hotkeyBox.BindingChanged += (_, _) => OnHotkeyChanged();
        _saveButton.Click += (_, _) => Save();
        Theme.Changed += OnThemeChanged;

        RefreshDerivedState();

        Shown += async (_, _) =>
        {
            if (_shown) return;
            _shown = true;
            if (_draft.IsConfigured)
            {
                await LoadExistingLightsAsync();
            }
            else
            {
                await DiscoverAsync();
            }
        };
        FormClosed += (_, _) =>
        {
            Theme.Changed -= OnThemeChanged;
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        Theme.ApplyWindowChrome(this);
    }

    // ---------------------------------------------------------------- layout

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, RailWidth));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.Controls.Add(CreateRail(), 0, 0);
        root.Controls.Add(CreateContent(), 1, 0);
        Controls.Add(root);

        AcceptButton = _saveButton;
        CancelButton = _cancelButton;
    }

    private Control CreateRail()
    {
        var rail = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(28, 30, 16, 26),
            Margin = Padding.Empty
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 1,
            RowCount = 7,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        layout.Controls.Add(new BulbMark { Dock = DockStyle.Fill });

        var wordmark = new Label
        {
            Text = "QuickHue",
            Dock = DockStyle.Fill,
            Font = Theme.Display(16F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        layout.Controls.Add(wordmark);

        var tagline = new Label
        {
            Text = "Your PC light,\none shortcut away.",
            Dock = DockStyle.Fill,
            Font = Theme.Text(10F),
            TextAlign = ContentAlignment.TopLeft
        };
        layout.Controls.Add(tagline);

        var stepsCaption = new Label
        {
            Text = "SETUP",
            Dock = DockStyle.Fill,
            Font = Theme.Text(7.5F, FontStyle.Bold),
            TextAlign = ContentAlignment.BottomLeft,
            Padding = new Padding(0, 0, 0, 6)
        };
        layout.Controls.Add(stepsCaption);

        _steps.Dock = DockStyle.Fill;
        _steps.AccessibleName = "Setup progress";
        layout.Controls.Add(_steps);

        layout.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent }, 0, 5);

        var trust = new Label
        {
            Text = "LOCAL ONLY\nTLS PINNED",
            Dock = DockStyle.Fill,
            Font = Theme.Mono(7.5F, FontStyle.Bold),
            TextAlign = ContentAlignment.BottomLeft
        };
        layout.Controls.Add(trust, 0, 6);

        rail.Controls.Add(layout);
        _railControls.AddRange([rail, wordmark, tagline, stepsCaption, trust]);
        return rail;
    }

    private Control CreateContent()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(30, 24, 30, 22),
            Margin = Padding.Empty
        };
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 1,
            RowCount = 6,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        // Card rows = header (32) + field rows (40 each) + card padding (26) + margin (12).
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 158));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Controls.Add(CreateHeader());
        content.Controls.Add(CreateBridgeCard());
        content.Controls.Add(CreateLightCard());
        content.Controls.Add(CreateShortcutCard());
        content.Controls.Add(CreateStatusRow());
        content.Controls.Add(CreateFooter());
        panel.Controls.Add(content);
        return panel;
    }

    private Control CreateHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        header.Controls.Add(new Label
        {
            Text = _draft.IsConfigured ? "Light shortcut" : "Set up your light",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Colors.Ink,
            Font = Theme.Display(17F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Name = "Heading"
        });
        _subtitle.Dock = DockStyle.Fill;
        _subtitle.Font = Theme.Text(9.5F);
        _subtitle.TextAlign = ContentAlignment.TopLeft;
        _subtitle.Padding = new Padding(0, 3, 0, 0);
        header.Controls.Add(_subtitle);
        return header;
    }

    private Control CreateBridgeCard()
    {
        var card = new CardPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(18, 12, 18, 14) };
        var grid = CreateCardGrid();
        grid.ColumnCount = 3;
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        grid.RowCount = 4;
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.Controls.Add(CreateCardHeader("Hue Bridge", "Stays on your local network."), 0, 0);
        grid.SetColumnSpan(grid.GetControlFromPosition(0, 0)!, 3);

        grid.Controls.Add(CreateFieldLabel("Bridge"), 0, 1);
        // Anchored, not filled: a drop-down list keeps its own height, so stretching it
        // would leave the painted frame out of step with the real control.
        _bridgeList.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _bridgeList.Margin = new Padding(0, 5, 10, 5);
        _bridgeList.AccessibleName = "Discovered Hue Bridge";
        grid.Controls.Add(_bridgeList, 1, 1);
        _discoverButton.Dock = DockStyle.Fill;
        _discoverButton.Margin = new Padding(0, 4, 0, 4);
        _discoverButton.AccessibleDescription = "Search the local network for Hue Bridges";
        grid.Controls.Add(_discoverButton, 2, 1);

        grid.Controls.Add(CreateFieldLabel("Address"), 0, 2);
        _manualAddress.Dock = DockStyle.Fill;
        _manualAddress.Margin = new Padding(0, 5, 10, 5);
        _manualAddressText.AccessibleName = "Bridge IP address";
        grid.Controls.Add(_manualAddress, 1, 2);

        grid.Controls.Add(CreateFieldLabel("Pairing"), 0, 3);
        var hint = new Label
        {
            Text = "Press the round button first.",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Colors.Muted,
            Font = Theme.Text(9F),
            TextAlign = ContentAlignment.MiddleLeft,
            Name = "Muted"
        };
        grid.Controls.Add(hint, 1, 3);
        _pairButton.Dock = DockStyle.Fill;
        _pairButton.Margin = new Padding(0, 4, 0, 4);
        grid.Controls.Add(_pairButton, 2, 3);

        card.Controls.Add(grid);
        return card;
    }

    private Control CreateLightCard()
    {
        var card = new CardPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(18, 12, 18, 14) };
        var grid = CreateCardGrid();
        grid.ColumnCount = 3;
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        grid.RowCount = 2;
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.Controls.Add(CreateCardHeader("PC light", "Only this light responds to the shortcut."), 0, 0);
        grid.SetColumnSpan(grid.GetControlFromPosition(0, 0)!, 3);

        grid.Controls.Add(CreateFieldLabel("Light"), 0, 1);
        _lightList.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lightList.Margin = new Padding(0, 5, 10, 5);
        _lightList.AccessibleName = "Light controlled by QuickHue";
        grid.Controls.Add(_lightList, 1, 1);
        _testButton.Dock = DockStyle.Fill;
        _testButton.Margin = new Padding(0, 4, 0, 4);
        _testButton.AccessibleDescription = "Blink the selected light so you can identify it";
        grid.Controls.Add(_testButton, 2, 1);

        card.Controls.Add(grid);
        return card;
    }

    private Control CreateShortcutCard()
    {
        var card = new CardPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(18, 12, 18, 14) };
        var grid = CreateCardGrid();
        grid.ColumnCount = 2;
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowCount = 3;
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.Controls.Add(CreateCardHeader("Shortcut", "Works in any app, whenever Windows is running."), 0, 0);
        grid.SetColumnSpan(grid.GetControlFromPosition(0, 0)!, 2);

        grid.Controls.Add(CreateFieldLabel("Hotkey"), 0, 1);
        var hotkeyRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        hotkeyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 196));
        hotkeyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _hotkeyBox.Dock = DockStyle.Fill;
        _hotkeyBox.Margin = new Padding(0, 4, 12, 4);
        _hotkeyBox.AccessibleName = "Global shortcut";
        hotkeyRow.Controls.Add(_hotkeyBox, 0, 0);
        _hotkeyHint.Dock = DockStyle.Fill;
        _hotkeyHint.Font = Theme.Text(8.5F);
        _hotkeyHint.TextAlign = ContentAlignment.MiddleLeft;
        _hotkeyHint.Name = "Muted";
        hotkeyRow.Controls.Add(_hotkeyHint, 1, 0);
        grid.Controls.Add(hotkeyRow, 1, 1);

        grid.Controls.Add(CreateFieldLabel("Startup"), 0, 2);
        _startWithWindows.Dock = DockStyle.Fill;
        _startWithWindows.Margin = new Padding(0, 2, 0, 0);
        grid.Controls.Add(_startWithWindows, 1, 2);

        card.Controls.Add(grid);
        return card;
    }

    private Control CreateStatusRow()
    {
        _status.Dock = DockStyle.Fill;
        _status.Margin = new Padding(0, 0, 0, 8);
        _status.AccessibleName = "Status";
        return _status;
    }

    private Control CreateFooter()
    {
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = new Padding(0, 10, 0, 0)
        };
        _saveButton.Size = new Size(132, 40);
        _saveButton.Margin = new Padding(10, 0, 0, 0);
        _cancelButton.Size = new Size(96, 40);
        _cancelButton.Margin = Padding.Empty;
        footer.Controls.Add(_saveButton);
        footer.Controls.Add(_cancelButton);
        return footer;
    }

    private static TableLayoutPanel CreateCardGrid() => new()
    {
        Dock = DockStyle.Fill,
        BackColor = Color.Transparent,
        Margin = Padding.Empty,
        Padding = Padding.Empty
    };

    /// <summary>Auto-sized title plus a filler subtitle, so neither can clip the other.</summary>
    private static Control CreateCardHeader(string title, string subtitle)
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = Theme.Colors.Ink,
            Font = Theme.Text(10.5F, FontStyle.Bold),
            Margin = new Padding(0, 0, 14, 0),
            Name = "Ink"
        }, 0, 0);
        header.Controls.Add(new Label
        {
            Text = subtitle,
            Dock = DockStyle.Fill,
            ForeColor = Theme.Colors.Muted,
            Font = Theme.Text(8.5F),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Name = "Muted"
        }, 1, 0);
        return header;
    }

    private static Label CreateFieldLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = Theme.Colors.Muted,
        Font = Theme.Text(8.5F, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft,
        Name = "Muted"
    };

    // ----------------------------------------------------------------- theme

    private void OnThemeChanged(object? sender, EventArgs eventArgs)
    {
        if (IsDisposed)
        {
            return;
        }
        BeginInvoke(() =>
        {
            ApplyTheme();
            Theme.ApplyWindowChrome(this);
            Invalidate(true);
        });
    }

    private void ApplyTheme()
    {
        var palette = Theme.Colors;
        BackColor = palette.Canvas;
        foreach (var control in Descendants(this))
        {
            switch (control)
            {
                case ThemedComboBox combo:
                    combo.ApplyTheme();
                    break;
                case InputFrame frame:
                    frame.ApplyTheme();
                    break;
                case Label label:
                    label.ForeColor = label.Name switch
                    {
                        "Muted" => palette.Muted,
                        _ => palette.Ink
                    };
                    break;
                case ToggleSwitch toggle:
                    toggle.ForeColor = palette.Ink;
                    break;
            }
        }

        foreach (var control in _railControls)
        {
            switch (control)
            {
                case Panel rail:
                    rail.BackColor = palette.Rail;
                    break;
                case Label label when label.Text.StartsWith("LOCAL ONLY", StringComparison.Ordinal):
                    label.ForeColor = palette.Accent;
                    break;
                case Label label when label.Text == "SETUP":
                    label.ForeColor = palette.RailMuted;
                    break;
                case Label label when label.Text == "QuickHue":
                    label.ForeColor = palette.RailInk;
                    break;
                case Label label:
                    label.ForeColor = palette.RailMuted;
                    break;
            }
        }
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    // ------------------------------------------------------------- behaviour

    private void OnHotkeyChanged()
    {
        RefreshDerivedState();
        var binding = _hotkeyBox.Binding;
        using var probe = new HotkeyWindow();
        if (probe.TryRegister(binding, out _))
        {
            probe.Unregister();
            SetStatus($"{binding.DisplayText} is free to use.", StatusKind.Success);
        }
        else
        {
            SetStatus($"{binding.DisplayText} is already claimed by another app. Pick a different combination.", StatusKind.Error);
        }
    }

    /// <summary>Keeps the subtitle, step list, hints, and Save availability in sync with the draft.</summary>
    private void RefreshDerivedState()
    {
        var binding = _hotkeyBox.Binding;
        var paired = HasPairing();
        var lightChosen = _lightList.SelectedItem is HueLight;

        _subtitle.Text = $"Choose what {binding.DisplayText} controls from anywhere in Windows.";
        _hotkeyHint.Text = _hotkeyBox.IsRecording
            ? "Hold modifiers, then a letter."
            : "Click, then press your keys.";
        _testButton.Enabled = !_busy && paired && lightChosen;
        _saveButton.Enabled = !_busy && paired && lightChosen && binding.IsValid();

        _steps.SetSteps(
        [
            new StepList.Step("Connect bridge", paired),
            new StepList.Step("Pick your light", lightChosen),
            new StepList.Step("Set shortcut", binding.IsValid())
        ]);

        _idleGuidance = !paired
            ? "Press the round button on the bridge, then Pair bridge."
            : !lightChosen
                ? "Choose which light QuickHue should switch."
                : $"Ready. {binding.DisplayText} will toggle {SelectedLightName()}.";
    }

    private string SelectedLightName() =>
        _lightList.SelectedItem is HueLight light && !string.IsNullOrWhiteSpace(light.Name)
            ? light.Name
            : "your light";

    private bool HasPairing() =>
        !string.IsNullOrWhiteSpace(_draft.BridgeId) &&
        !string.IsNullOrWhiteSpace(_draft.ProtectedApplicationKey) &&
        _draft.CertificateSha256.Length == 64;

    private async Task DiscoverAsync()
    {
        SetBusy(true, "Searching the local network for Hue Bridges…");
        _discoverButton.Text = "Searching…";
        try
        {
            using var discovery = new BridgeDiscovery();
            var bridges = await discovery.DiscoverAsync(_lifetime.Token);
            var selectedId = (_bridgeList.SelectedItem as BridgeInfo)?.Id ?? _draft.BridgeId;
            _bridgeList.Items.Clear();
            foreach (var bridge in bridges)
            {
                _bridgeList.Items.Add(bridge);
            }
            if (_bridgeList.Items.Count > 0)
            {
                var index = Enumerable.Range(0, _bridgeList.Items.Count)
                    .FirstOrDefault(i => string.Equals(
                        (_bridgeList.Items[i] as BridgeInfo)?.Id,
                        selectedId,
                        StringComparison.OrdinalIgnoreCase));
                _bridgeList.SelectedIndex = index;
                _manualAddressText.Text = ((BridgeInfo)_bridgeList.SelectedItem!).Address;
                var count = _bridgeList.Items.Count;
                SetStatus(
                    $"Found {count} bridge{(count == 1 ? string.Empty : "s")}. Press its round button, then Pair bridge.",
                    StatusKind.Success);
            }
            else
            {
                SetStatus(
                    "No bridge answered. Type its IP address above, then pair.",
                    StatusKind.Error);
            }
        }
        catch (OperationCanceledException)
        {
            // Closing the form cancels discovery.
        }
        finally
        {
            _discoverButton.Text = "Search";
            SetBusy(false);
        }
    }

    private async Task PairAsync()
    {
        var selected = _bridgeList.SelectedItem as BridgeInfo;
        var address = _manualAddressText.Text.Trim();
        if (string.IsNullOrWhiteSpace(address))
        {
            address = selected?.Address ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(address))
        {
            SetStatus("Choose a discovered bridge or type its IP address first.", StatusKind.Error);
            return;
        }

        var isSelectedAddress = selected is not null &&
            address.Equals(selected.Address, StringComparison.OrdinalIgnoreCase);
        var bridge = new BridgeInfo(
            isSelectedAddress ? selected!.Id : string.Empty,
            address,
            isSelectedAddress ? selected!.Source : "Manual");
        SetBusy(true, "Pairing with the bridge…");
        _pairButton.Text = "Pairing…";
        try
        {
            var pairing = await new BridgePairingService().PairAsync(bridge, _lifetime.Token);
            _draft.BridgeId = pairing.BridgeId;
            _draft.BridgeAddress = pairing.Address;
            _draft.CertificateSha256 = pairing.CertificateSha256;
            _draft.ProtectedApplicationKey = DpapiProtector.Protect(pairing.ApplicationKey);

            using var client = new HueClient(_draft, pairing.ApplicationKey);
            var lights = await client.ListLightsAsync(_lifetime.Token);
            PopulateLights(lights);
            SetStatus(
                lights.Count == 0
                    ? "Paired, but the bridge reported no lights."
                    : $"Paired securely. Pick your PC light from {lights.Count} available light{(lights.Count == 1 ? string.Empty : "s")}.",
                lights.Count == 0 ? StatusKind.Error : StatusKind.Success);
        }
        catch (LinkButtonNotPressedException exception)
        {
            SetStatus(exception.Message, StatusKind.Error);
        }
        catch (Exception exception) when (exception is HttpRequestException or HueApiException or TaskCanceledException)
        {
            SetStatus(HueController.FriendlyMessage(exception), StatusKind.Error);
        }
        finally
        {
            _pairButton.Text = "Pair bridge";
            SetBusy(false);
        }
    }

    private async Task LoadExistingLightsAsync()
    {
        SetBusy(true, "Loading lights from the saved bridge…");
        try
        {
            var key = DpapiProtector.Unprotect(_draft.ProtectedApplicationKey);
            using var client = new HueClient(_draft, key);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var lights = await client.ListLightsAsync(timeout.Token);
            PopulateLights(lights);
            SetStatus(_idleGuidance, StatusKind.Idle);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or HueApiException or TaskCanceledException or IOException)
        {
            _lightList.Items.Clear();
            _lightList.Items.Add(new HueLight(_draft.Target.Id, _draft.Target.Name, false));
            _lightList.SelectedIndex = 0;
            RefreshDerivedState();
            SetStatus(
                $"Could not refresh the light list, so the saved light is still selected. {HueController.FriendlyMessage(exception)}",
                StatusKind.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Blinks the selected light twice and restores its original state.</summary>
    private async Task BlinkAsync()
    {
        if (_lightList.SelectedItem is not HueLight light)
        {
            SetStatus("Choose a light first.", StatusKind.Error);
            return;
        }

        SetBusy(true, $"Blinking {light.Name}…");
        _testButton.Text = "…";
        try
        {
            var key = DpapiProtector.Unprotect(_draft.ProtectedApplicationKey);
            using var client = new HueClient(_draft, key);
            var original = await client.GetLightStateAsync(light.Id, _lifetime.Token);
            for (var pulse = 0; pulse < 2; pulse++)
            {
                await client.SetLightStateAsync(light.Id, !original, _lifetime.Token);
                await Task.Delay(TimeSpan.FromMilliseconds(450), _lifetime.Token);
                await client.SetLightStateAsync(light.Id, original, _lifetime.Token);
                if (pulse == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(450), _lifetime.Token);
                }
            }
            SetStatus($"Blinked {light.Name}. If the wrong bulb flashed, pick another one.", StatusKind.Success);
        }
        catch (OperationCanceledException)
        {
            // The form is closing.
        }
        catch (Exception exception) when (
            exception is HttpRequestException or HueApiException or IOException)
        {
            SetStatus(HueController.FriendlyMessage(exception), StatusKind.Error);
        }
        finally
        {
            _testButton.Text = "Blink";
            SetBusy(false);
        }
    }

    private void PopulateLights(IReadOnlyList<HueLight> lights)
    {
        _lightList.Items.Clear();
        _lightList.EmptyText = lights.Count == 0 ? "The bridge reported no lights" : "Choose a light";
        foreach (var light in lights)
        {
            _lightList.Items.Add(light);
        }
        if (_lightList.Items.Count > 0)
        {
            _lightList.SelectedIndex = Enumerable.Range(0, _lightList.Items.Count)
                .FirstOrDefault(i => string.Equals(
                    (_lightList.Items[i] as HueLight)?.Id,
                    _draft.Target.Id,
                    StringComparison.OrdinalIgnoreCase));
        }
        RefreshDerivedState();
    }

    private void Save()
    {
        if (_lightList.SelectedItem is not HueLight selectedLight)
        {
            SetStatus("Pair with the bridge and choose your light first.", StatusKind.Error);
            return;
        }

        var binding = _hotkeyBox.Binding;
        if (!binding.IsValid())
        {
            SetStatus("Choose a shortcut with at least one modifier plus a letter or F1–F11 key.", StatusKind.Error);
            return;
        }

        _draft.Target = new LightTarget { Id = selectedLight.Id, Name = selectedLight.Name };
        _draft.Hotkey = binding;
        _draft.StartWithWindows = _startWithWindows.Checked;
        if (!_draft.IsConfigured)
        {
            SetStatus("Pair with the bridge before saving.", StatusKind.Error);
            return;
        }

        _store.Save(_draft);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        UseWaitCursor = busy;
        _discoverButton.Enabled = !busy;
        _pairButton.Enabled = !busy;
        if (status is not null)
        {
            SetStatus(status, StatusKind.Working);
        }
        RefreshDerivedState();
        if (!busy && status is null && _status.Kind == StatusKind.Working)
        {
            SetStatus(_idleGuidance, StatusKind.Idle);
        }
    }

    private void SetStatus(string message, StatusKind kind)
    {
        RefreshDerivedState();
        _status.Show(string.IsNullOrWhiteSpace(message) ? _idleGuidance : message, kind);
    }
}
