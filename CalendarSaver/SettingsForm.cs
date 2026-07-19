using System.Diagnostics;

namespace CalendarSaver;

public class SettingsForm : Form
{
    private static readonly string[] Palette =
    {
        "#7aa2f7", "#f7768e", "#9ece6a", "#e0af68", "#bb9af7", "#7dcfff", "#ff9e64", "#c0caf5",
    };

    private readonly DataGridView _grid = new();
    private readonly ListBox _folders = new();
    private readonly NumericUpDown _photoInterval = new();
    private readonly NumericUpDown _refreshMinutes = new();
    private readonly ComboBox _theme = new();
    private readonly CheckBox _scheduleTheme = new();
    private readonly DateTimePicker _darkStart = new();
    private readonly DateTimePicker _darkEnd = new();
    private readonly Button _testButton = new();
    private AppSettings _settings;

    public SettingsForm()
    {
        _settings = AppSettings.Load();

        Text = "PactoTech Calendar Saver — Settings";
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(860, 780);
        MinimumSize = new Size(720, 640);

        BuildUi();
        LoadIntoUi();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(14),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // --- feeds section ---
        var feedsLabel = MakeHeading("Calendar feeds");

        var help = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = SystemColors.ControlDarkDark,
            Margin = new Padding(0, 2, 0, 6),
            Text =
                "How to get your Google Calendar address (repeat for each calendar you want to show):\n" +
                "   1.  Open calendar.google.com in a browser.\n" +
                "   2.  In the left sidebar under “My calendars”, hover the calendar and click its ⋮ menu\n" +
                "        (or right-click it) → “Settings and sharing”.\n" +
                "   3.  Scroll to the very bottom of that page, to “Integrate calendar”.\n" +
                "   4.  Copy the “Secret address in iCal format” and paste it below — it ends in basic.ics.\n" +
                "Share links (containing “?cid=…”) will not work — only the secret iCal address does.",
        };

        var helpLink = new LinkLabel
        {
            AutoSize = true,
            Text = "Open Google Calendar settings in your browser",
            Margin = new Padding(0, 0, 0, 8),
        };
        helpLink.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://calendar.google.com/calendar/r/settings") { UseShellExecute = true }); }
            catch { /* no browser — the text instructions still work */ }
        };

        ConfigureGrid();

        var feedButtons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 6, 0, 10),
        };
        feedButtons.Controls.Add(MakeButton("Add feed", (_, _) =>
            AddFeedRow(new FeedConfig { Color = Palette[_grid.Rows.Count % Palette.Length] })));
        feedButtons.Controls.Add(MakeButton("Remove feed", (_, _) =>
        {
            if (_grid.CurrentRow != null) _grid.Rows.Remove(_grid.CurrentRow);
        }));
        _testButton.Text = "Test feeds";
        _testButton.AutoSize = true;
        _testButton.Padding = new Padding(6, 2, 6, 2);
        _testButton.Click += async (_, _) => await ValidateFeedsAsync();
        feedButtons.Controls.Add(_testButton);

        // --- photo section ---
        var foldersLabel = MakeHeading("Photo folders  (scanned recursively for jpg / png / webp)");

        var folderPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Margin = new Padding(0, 2, 0, 8),
        };
        folderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _folders.Dock = DockStyle.Fill;
        _folders.IntegralHeight = false;
        folderPanel.Controls.Add(_folders, 0, 0);

        var folderButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(8, 0, 0, 0),
        };
        folderButtons.Controls.Add(MakeButton("Add folder…", (_, _) =>
        {
            using var browser = new FolderBrowserDialog();
            if (browser.ShowDialog(this) == DialogResult.OK && !_folders.Items.Contains(browser.SelectedPath))
                _folders.Items.Add(browser.SelectedPath);
        }));
        folderButtons.Controls.Add(MakeButton("Remove folder", (_, _) =>
        {
            if (_folders.SelectedIndex >= 0) _folders.Items.RemoveAt(_folders.SelectedIndex);
        }));
        folderPanel.Controls.Add(folderButtons, 1, 0);

        // --- intervals ---
        var intervals = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 4, 0, 6),
            WrapContents = true,
        };
        _photoInterval.Minimum = 3;
        _photoInterval.Maximum = 3600;
        _photoInterval.MinimumSize = new Size(80, 0);
        _photoInterval.AutoSize = true;
        _refreshMinutes.Minimum = 1;
        _refreshMinutes.Maximum = 1440;
        _refreshMinutes.MinimumSize = new Size(80, 0);
        _refreshMinutes.AutoSize = true;
        intervals.Controls.Add(MakeInlineLabel("Photo switch interval (seconds):"));
        intervals.Controls.Add(_photoInterval);
        intervals.Controls.Add(MakeInlineLabel("      Feed refresh interval (minutes):"));
        intervals.Controls.Add(_refreshMinutes);

        // --- theme ---
        var themeRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 6),
            WrapContents = true,
        };
        _theme.DropDownStyle = ComboBoxStyle.DropDownList;
        _theme.Items.AddRange(new object[] { "Dark", "Light" });
        _theme.Width = 90;
        _scheduleTheme.Text = "Switch on a schedule — dark from";
        _scheduleTheme.AutoSize = true;
        _scheduleTheme.Margin = new Padding(24, 5, 6, 0);
        foreach (var picker in new[] { _darkStart, _darkEnd })
        {
            picker.Format = DateTimePickerFormat.Custom;
            picker.CustomFormat = "HH:mm";
            picker.ShowUpDown = true;
            picker.Width = 70;
            picker.Enabled = false;
        }
        _scheduleTheme.CheckedChanged += (_, _) =>
        {
            _darkStart.Enabled = _darkEnd.Enabled = _scheduleTheme.Checked;
            _theme.Enabled = !_scheduleTheme.Checked;
        };
        themeRow.Controls.Add(MakeInlineLabel("Theme:"));
        themeRow.Controls.Add(_theme);
        themeRow.Controls.Add(_scheduleTheme);
        themeRow.Controls.Add(_darkStart);
        themeRow.Controls.Add(MakeInlineLabel("to"));
        themeRow.Controls.Add(_darkEnd);

        var note = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 2, 0, 8),
            Text = "Note: Google Calendar ICS feeds do not include Google Tasks. The task panel shows " +
                   "VTODO items from feeds that carry them (e.g. Nextcloud, Todoist ICS export).",
        };

        // --- dialog buttons ---
        var dialogButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0),
        };
        var cancel = MakeButton("Cancel", (_, _) => { });
        cancel.DialogResult = DialogResult.Cancel;
        var save = MakeButton("Save", async (_, _) => await SaveAsync());
        save.MinimumSize = new Size(96, 0);
        cancel.MinimumSize = new Size(96, 0);
        dialogButtons.Controls.Add(cancel);
        dialogButtons.Controls.Add(save);
        CancelButton = cancel;
        AcceptButton = save;

        // --- footer: support links on the left, dialog buttons on the right ---
        var supportRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
        };
        supportRow.Controls.Add(MakeLink("☕ Enjoying Calendar Saver? Leave a tip",
            "https://pactotech.com/products/calendar-saver-tip-jar"));
        supportRow.Controls.Add(MakeLink("calendarsaver.com", "https://calendarsaver.com/"));

        var skylightRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
        };
        var skylightLabel = MakeInlineLabel("Prefer a dedicated wall display?  Skylight Calendar (affiliate):");
        skylightLabel.ForeColor = SystemColors.ControlDarkDark;
        skylightRow.Controls.Add(skylightLabel);
        skylightRow.Controls.Add(MakeLink("10″", "https://www.amazon.com/dp/B0H37WG5F4?tag=flowerfrogmak-20"));
        skylightRow.Controls.Add(MakeLink("15″", "https://www.amazon.com/dp/B0G5ZX9WSW?tag=flowerfrogmak-20"));
        skylightRow.Controls.Add(MakeLink("27″", "https://www.amazon.com/dp/B0F1WYX8J6?tag=flowerfrogmak-20"));

        var supportLinks = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left,
            ColumnCount = 1,
            Margin = new Padding(0),
        };
        supportLinks.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        supportLinks.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        supportLinks.Controls.Add(supportRow, 0, 0);
        supportLinks.Controls.Add(skylightRow, 0, 1);

        var footer = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            ColumnCount = 2,
            Margin = new Padding(0),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(supportLinks, 0, 0);
        footer.Controls.Add(dialogButtons, 1, 0);

        // --- assemble rows ---
        void AddAuto(Control c) { root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.Controls.Add(c); }
        void AddFill(Control c, float percent)
        {
            root.RowStyles.Add(new RowStyle(SizeType.Percent, percent));
            c.Dock = DockStyle.Fill;
            root.Controls.Add(c);
        }

        AddAuto(feedsLabel);
        AddAuto(help);
        AddAuto(helpLink);
        AddFill(_grid, 58);
        AddAuto(feedButtons);
        AddAuto(foldersLabel);
        AddFill(folderPanel, 42);
        AddAuto(intervals);
        AddAuto(themeRow);
        AddAuto(note);
        AddAuto(footer);

        Controls.Add(root);
    }

    private void ConfigureGrid()
    {
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _grid.MultiSelect = false;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.BorderStyle = BorderStyle.FixedSingle;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.RowTemplate.Height = (int)(24 * DeviceDpi / 96f);
        _grid.Margin = new Padding(0, 2, 0, 0);

        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "colShow",
            HeaderText = "Show",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
            Width = (int)(48 * DeviceDpi / 96f),
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colName", HeaderText = "Name", FillWeight = 20 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colUrl", HeaderText = "ICS URL", FillWeight = 46 });
        var colorColumn = new DataGridViewButtonColumn
        {
            Name = "colColor",
            HeaderText = "Color",
            Text = "",
            UseColumnTextForButtonValue = true,
            FlatStyle = FlatStyle.Flat,   // required for the cell BackColor to actually show
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
            Width = (int)(56 * DeviceDpi / 96f),
        };
        _grid.Columns.Add(colorColumn);
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colStatus", HeaderText = "Status", FillWeight = 34, ReadOnly = true });
        _grid.CellClick += OnGridCellClick;
    }

    private static Label MakeHeading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 9.5f),
        Margin = new Padding(0, 4, 0, 2),
    };

    private static Label MakeInlineLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 5, 6, 0),
    };

    private static LinkLabel MakeLink(string text, string url)
    {
        var link = new LinkLabel
        {
            AutoSize = true,
            Text = text,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 8, 18, 0),
        };
        link.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* no browser available — nothing to do */ }
        };
        return link;
    }

    private static Button MakeButton(string text, EventHandler onClick)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            Padding = new Padding(6, 2, 6, 2),
            Margin = new Padding(0, 0, 8, 4),
        };
        button.Click += onClick;
        return button;
    }

    private void LoadIntoUi()
    {
        foreach (var feed in _settings.Feeds) AddFeedRow(feed);
        foreach (var folder in _settings.PhotoFolders) _folders.Items.Add(folder);
        _photoInterval.Value = Math.Clamp(_settings.PhotoIntervalSeconds, (int)_photoInterval.Minimum, (int)_photoInterval.Maximum);
        _refreshMinutes.Value = Math.Clamp(_settings.RefreshMinutes, (int)_refreshMinutes.Minimum, (int)_refreshMinutes.Maximum);

        _theme.SelectedIndex = _settings.Theme == "light" ? 1 : 0;
        var scheduled = TimeSpan.TryParse(_settings.DarkStart ?? "", out var darkStart) &
                        TimeSpan.TryParse(_settings.DarkEnd ?? "", out var darkEnd);
        _scheduleTheme.Checked = scheduled;
        _darkStart.Value = DateTime.Today + (scheduled ? darkStart : new TimeSpan(21, 0, 0));
        _darkEnd.Value = DateTime.Today + (scheduled ? darkEnd : new TimeSpan(7, 0, 0));
    }

    private void AddFeedRow(FeedConfig feed)
    {
        var rowIndex = _grid.Rows.Add(feed.Enabled, feed.Name, feed.Url, "", "");
        SetRowColor(_grid.Rows[rowIndex], feed.Color);
    }

    private static void SetRowColor(DataGridViewRow row, string hex)
    {
        var color = ParseHex(hex);
        var cell = row.Cells["colColor"];
        cell.Style.BackColor = color;
        cell.Style.SelectionBackColor = color;
        cell.Tag = hex;
        row.DataGridView?.InvalidateCell(cell);
    }

    private void OnGridCellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "colColor") return;
        var row = _grid.Rows[e.RowIndex];
        using var picker = new ColorDialog
        {
            Color = ParseHex(row.Cells["colColor"].Tag as string ?? "#7aa2f7"),
            FullOpen = true,
        };
        if (picker.ShowDialog(this) == DialogResult.OK)
            SetRowColor(row, $"#{picker.Color.R:x2}{picker.Color.G:x2}{picker.Color.B:x2}");
    }

    private static Color ParseHex(string hex)
    {
        try { return ColorTranslator.FromHtml(hex); }
        catch { return ColorTranslator.FromHtml("#7aa2f7"); }
    }

    private List<FeedConfig> CollectFeeds()
    {
        _grid.EndEdit();
        var feeds = new List<FeedConfig>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var url = (row.Cells["colUrl"].Value as string ?? "").Trim();
            var name = (row.Cells["colName"].Value as string ?? "").Trim();
            if (url.Length == 0 && name.Length == 0) continue;
            feeds.Add(new FeedConfig
            {
                Url = url,
                Name = name,
                Color = row.Cells["colColor"].Tag as string ?? Palette[0],
                Enabled = row.Cells["colShow"].Value is true,
            });
        }
        return feeds;
    }

    /// <summary>Live-tests every feed URL and writes pass/fail into the Status column
    /// (full message in the cell tooltip). Returns the number of failing feeds.</summary>
    private async Task<int> ValidateFeedsAsync()
    {
        _testButton.Enabled = false;
        var failures = 0;
        try
        {
            _grid.EndEdit();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                var url = (row.Cells["colUrl"].Value as string ?? "").Trim();
                var statusCell = row.Cells["colStatus"];
                if (row.Cells["colShow"].Value is not true)
                {
                    statusCell.Value = "hidden";
                    statusCell.Style.ForeColor = SystemColors.GrayText;
                    continue;
                }
                if (url.Length == 0)
                {
                    statusCell.Value = "no URL";
                    failures++;
                    continue;
                }
                statusCell.Value = "testing…";
                var (ok, message) = await FeedService.TestFeedAsync(url);
                statusCell.Value = (ok ? "✓ " : "✗ ") + message;
                statusCell.ToolTipText = message;
                statusCell.Style.ForeColor = ok ? Color.DarkGreen : Color.Firebrick;
                if (!ok) failures++;
            }
        }
        finally
        {
            _testButton.Enabled = true;
        }
        return failures;
    }

    private async Task SaveAsync()
    {
        var failures = _grid.Rows.Count > 0 ? await ValidateFeedsAsync() : 0;
        if (failures > 0)
        {
            var answer = MessageBox.Show(this,
                $"{failures} feed(s) failed validation (see the Status column). Save anyway?",
                "Feed validation", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;
        }

        _settings = new AppSettings
        {
            Feeds = CollectFeeds(),
            PhotoFolders = _folders.Items.Cast<string>().ToList(),
            PhotoIntervalSeconds = (int)_photoInterval.Value,
            RefreshMinutes = (int)_refreshMinutes.Value,
            Theme = _theme.SelectedIndex == 1 ? "light" : "dark",
            DarkStart = _scheduleTheme.Checked ? _darkStart.Value.ToString("HH:mm") : null,
            DarkEnd = _scheduleTheme.Checked ? _darkEnd.Value.ToString("HH:mm") : null,
        };
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Failed to save settings: " + ex.Message, "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
