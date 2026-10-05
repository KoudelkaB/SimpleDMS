using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SimpleDms.Core;

namespace SimpleDms.App;

public sealed class MainWindow : Window
{
    readonly LocalStore store;
    readonly AppSettings settings;
    readonly ArchiveService service;
    GoogleAuth auth = null!;
    WorkbookCatalog? catalog;
    bool busy, linking, cached, driveOnline, filling, switching;
    DateTime stamp;
    int ticks;
    CancellationTokenSource? operation;
    readonly TextBlock archiveTitle = new() { Text = "SimpleDMS", FontSize = 24, FontWeight = FontWeight.SemiBold };
    readonly TextBlock mode = new() { Text = "Vyberte složku archivu", FontSize = 13, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBlock details = new() { Text = "Vyberte záznam.", TextWrapping = TextWrapping.Wrap };
    readonly TextBlock counter = new() { VerticalAlignment = VerticalAlignment.Center };
    // Shows only the records whose register row points at another document; toggles back to all.
    readonly Button problemsButton = new() { IsVisible = false };
    readonly TextBlock detailProblem = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick, FontWeight = FontWeight.SemiBold, IsVisible = false };
    bool onlyProblems;
    readonly TextBlock syncClient = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    readonly TextBox rootPath = new() { PlaceholderText = "Místní složka, ve které leží registr XLSX a složka dokumentů" };
    readonly TextBox archiveName = new() { PlaceholderText = "Název nového archivu", Width = 300 };
    readonly ComboBox archiveChoices = new() { MinWidth = 300, PlaceholderText = "Nalezené archivy" };
    readonly TextBox driveUrl = new() { PlaceholderText = "https://drive.google.com/drive/folders/… (složka, ve které leží registr)" };
    readonly TextBlock driveStatus = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBox query = new() { PlaceholderText = "Číslo, název, autor, poznámky…" };
    readonly ComboBox category = new() { MinWidth = 260 };
    readonly ComboBox state = new() { ItemsSource = new[] { "Všechny stavy", "Rozpracované", "Dokončené" }, SelectedIndex = 0, MinWidth = 140 };
    readonly ComboBox electronic = new() { ItemsSource = new[] { "Všechny dokumenty", "S elektronickou přílohou", "Pouze papír" }, SelectedIndex = 0, MinWidth = 180 };
    readonly ListBox records = new() { MinHeight = 120 };
    readonly ComboBox newCategory = new() { MinWidth = 360, PlaceholderText = "Vyberte kategorii" };
    readonly TextBlock nextCode = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new(12, 0, 0, 0) };
    readonly TextBox newTitle = new() { PlaceholderText = "Název dokumentu" };
    readonly AutoCompleteBox newAuthor = new() { PlaceholderText = "Autor / účastníci", FilterMode = AutoCompleteFilterMode.Contains, MinimumPrefixLength = 1 };
    readonly TextBox newReference = new() { PlaceholderText = "Reference (např. číslo smlouvy)" };
    readonly TextBox newValidity = new() { PlaceholderText = "d.M.rrrr", Width = 140 };
    readonly TextBox newNotes = new() { PlaceholderText = "Klíčová slova, poznámky a fyzické umístění (skříň / šanon)", AcceptsReturn = true, MinHeight = 70, TextWrapping = TextWrapping.Wrap };
    readonly CheckBox newPending = new() { Content = "Rozpracovaný dokument" };
    readonly ListBox filesList = new() { MinHeight = 80, MaxHeight = 200 };
    readonly List<string> attachmentPaths = [];
    readonly ListBox queueList = new() { Height = 220, SelectionMode = SelectionMode.Multiple };
    readonly Grid labelGrid = new();
    readonly TextBlock sheetStatus = new() { TextWrapping = TextWrapping.Wrap };
    readonly Dictionary<string, TextBox> dimensions = [];
    readonly TextBox profileName = new() { PlaceholderText = "Jméno profilu" };
    readonly ComboBox profileChoice = new() { MinWidth = 180 };
    readonly ComboBox sheetChoice = new() { MinWidth = 220 };
    readonly CheckBox includeQr = new() { Content = "QR kód" };
    readonly ComboBox printerChoice = new() { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Tiskárna" };
    readonly TextBox clientId = new() { PlaceholderText = "Google OAuth Client ID" };
    readonly TextBox clientSecret = new() { PlaceholderText = "Desktop client secret (z JSON klienta)", PasswordChar = '●' };
    readonly CheckBox readOnly = new() { Content = "Pouze čtení: neupravovat registr ani složku dokumentů" };
    readonly TabControl tabs = new();
    // Shown only while an operation runs; it bypasses Run, which ignores clicks while busy.
    readonly Button cancel = new() { Content = "Zrušit probíhající operaci", IsVisible = false };
    readonly Button saveDocument;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(15) };
    public MainWindow() : this(new LocalStore()) { }
    public MainWindow(LocalStore local)
    {
        store = local; settings = store.Read<AppSettings>("settings.json") ?? new(); service = new(store);
        var shipped = Path.Combine(AppContext.BaseDirectory, "oauth-client.json");
        if (settings.ClientId.Length == 0 && File.Exists(shipped)) ImportOAuth(File.ReadAllText(shipped));
        InitializeServices();
        Title = "SimpleDMS"; Width = 1180; Height = 840; MinWidth = 900; MinHeight = 600;
        var body = new DockPanel { Margin = new Thickness(20) };
        var header = Stack(archiveTitle, mode); header.Margin = new(0, 0, 0, 14); DockPanel.SetDock(header, Dock.Top); body.Children.Add(header);
        cancel.Click += (_, _) => operation?.Cancel();
        var bottom = Stack(status, Row(cancel)); bottom.Margin = new(0, 12, 0, 0); DockPanel.SetDock(bottom, Dock.Bottom); body.Children.Add(bottom);
        // Messages belong to the action on the current tab; switching tabs clears them unless an operation is running.
        tabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.Source, tabs) && !busy && !switching) status.Text = ""; };
        body.Children.Add(tabs); Content = body;

        syncClient.Text = OperatingSystem.IsWindows() ? "Hledám Google Drive for desktop…" : "Na Linuxu použijte Insync nebo rclone (rclone bisync / rclone mount) se složkou archivu.";
        rootPath.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Run(DiscoverAsync); } };
        var archivePanel = Stack(
            Heading("1. Synchronizovaná složka"),
            Text("SimpleDMS pracuje s místní složkou, kterou na pozadí synchronizuje Google Drive for desktop (případně Insync nebo rclone). Úpravy jsou okamžité a fungují i offline. Nahrání na Google Drive obstará synchronizační klient, takže aplikace nepotřebuje přihlášení ke Google."),
            syncClient,
            Row(Action("Stáhnout Google Drive for desktop", () => { GoogleAuth.OpenBrowser("https://www.google.com/drive/download/"); return Task.CompletedTask; })),
            Text("Sdílenou složku archivu nejprve na webu Google Drive přidejte do Můj disk (pravé tlačítko → Uspořádat → Přidat zástupce do Můj disk). Pak se objeví na disku G: ve složce Můj disk. Chcete-li archiv používat i bez internetu, nastavte u složky v Průzkumníku: pravé tlačítko → Offline přístup → Dostupné offline."),
            Heading("2. Složka archivu"),
            Text("Vyberte složku, ve které leží registr (např. G:\\Můj disk\\Databáze dokumentů), nebo cestu vložte a stiskněte Enter. Pokud v ní je jediný archiv, otevře se automaticky."),
            Grid2(rootPath, Row(Action("Načíst", DiscoverAsync), Action("Vybrat složku…", ChooseRootAsync))),
            Row(archiveChoices, Action("Otevřít vybraný archiv", OpenChosenAsync)),
            Row(archiveName, Action("Založit nový archiv v této složce", CreateArchiveAsync)),
            Heading("3. Volitelně: Google ID pro odkazy a QR kódy"),
            Text("Po propojení aplikace na pozadí doplní do sloupce Q Google ID složek, které synchronizační klient už nahrál. Odkaz ve sloupci M a QR kód na štítku pak vedou přímo na Google Drive. Aplikace žádá jen oprávnění číst názvy a ID souborů."),
            driveUrl, driveStatus,
            Row(Action("Přihlásit Google a propojit", LinkDriveAsync), Action("Doplnit Google ID nyní", () => LinkIdsAsync(true)), Action("Zrušit propojení", UnlinkDriveAsync)));
        AddTab("Archiv", archivePanel);

        var searchPanel = new Grid { RowDefinitions = new("Auto,Auto,*"), ColumnDefinitions = new("2*,*") };
        var filters = Stack(query, Row(category, state, electronic)); Grid.SetColumnSpan(filters, 2); searchPanel.Children.Add(filters);
        var counterRow = Row(counter, problemsButton); counterRow.Margin = new(0, 4); Grid.SetRow(counterRow, 1); Grid.SetColumnSpan(counterRow, 2); searchPanel.Children.Add(counterRow);
        problemsButton.Click += (_, _) => { onlyProblems = !onlyProblems; Filter(); };
        Grid.SetRow(records, 2); searchPanel.Children.Add(records);
        var detailPanel = Stack(Heading("Detail dokumentu"), detailProblem, details, Action("Otevřít přílohy", OpenSelectedAsync), Action("Otevřít na Google Drive", OpenDriveAsync),
            Action("Přepnout Rozpracovaný / Dokončený", TogglePendingAsync), Text("Doplnit přílohy ke stejnému číslu:"), Row(Action("Soubory…", () => AttachAsync(false)), Action("Složky…", () => AttachAsync(true))), Action("Přidat štítek do fronty", QueueSelectedAsync));
        detailPanel.Margin = new(18, 0, 0, 0); searchPanel.Children.Add(new ScrollViewer { Content = detailPanel, [Grid.RowProperty] = 2, [Grid.ColumnProperty] = 1 });
        AddTab("Dokumenty", searchPanel, false);

        saveDocument = Action("Uložit dokument a připravit štítek", SaveDocumentAsync);
        saveDocument.Classes.Add("accent");
        var attachments = Stack(Row(Action("Vybrat soubory", () => PickAttachmentsAsync(false)), Action("Vybrat složky", () => PickAttachmentsAsync(true)), Action("Vyprázdnit", () => { attachmentPaths.Clear(); UpdateFiles(); return Task.CompletedTask; })),
            filesList, Text("Soubory i složky (i najednou) lze také přetáhnout z Průzkumníku do seznamu. Jedna příloha se uloží pod číslem dokumentu (např. 100242.pdf nebo složka 100242), více příloh do složky pojmenované číslem. Papírový dokument může být bez příloh."));
        AddTab("Přidat dokument", Stack(Heading("Nový dokument"),
            Form(("Kategorie", Row(newCategory, nextCode)), ("Název", newTitle), ("Autor / účastníci", newAuthor), ("Reference", newReference),
                ("Platnost", DateField(newValidity)), ("Poznámky", newNotes), ("", newPending), ("Přílohy", attachments)),
            saveDocument));
        newCategory.SelectionChanged += (_, _) => _ = UpdateNextCodeAsync();
        DragDrop.SetAllowDrop(filesList, true);
        DragDrop.AddDragOverHandler(filesList, (_, e) => { e.DragEffects = e.DataTransfer.TryGetFiles() != null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; });
        DragDrop.AddDropHandler(filesList, (_, e) =>
        {
            foreach (var path in (e.DataTransfer.TryGetFiles() ?? []).Select(x => x.TryGetLocalPath()).OfType<string>())
                if (!attachmentPaths.Contains(path)) attachmentPaths.Add(path);
            UpdateFiles(); e.Handled = true;
        });

        // Queue and print actions stay visible on the left; the sheet layout scrolls on the right.
        var printPanel = Stack(Heading("Fronta štítků"), queueList, Row(Action("Odebrat vybrané", RemoveLabelAsync), Action("Vyprázdnit frontu", ClearQueueAsync)),
            Heading("Tisk"), Text("Tiskárna"), Grid2(printerChoice, Action("↻", LoadPrintersAsync)),
            Row(Action("Tisknout", () => ExportAsync(true)), Action("Náhled PDF", () => ExportAsync(false))),
            sheetStatus,
            Text("Náhled i tisk ukazují přesně frontu. Štítky z ní odejdou a pozice na archu se spotřebují, až po tisku potvrdíte, co se vytisklo. Tiskněte v měřítku 100 %."));
        printPanel.Margin = new(0, 0, 18, 0);
        // The profile is the paper type (geometry); a sheet is one physical piece of label paper whose used positions are remembered.
        ToolTip.SetTip(profileChoice, "Typ papíru s nálepkami: rozměry, mřížka, okraje a kalibrace."); ToolTip.SetTip(sheetChoice, "Konkrétní list nálepek tohoto typu. Aplikace si pamatuje jeho použité pozice, takže se k částečně použitému listu můžete vrátit.");
        var sheetPanel = Stack(Heading("Arch nálepek"), Form(("Typ archu", profileChoice), ("List", Row(sheetChoice, Action("Nový list", NewSheetAsync))), ("Název typu", profileName)),
            Text("List je konkrétní papír s nálepkami; vyberte ten, který vkládáte do tiskárny. Nový list založte, když vložíte nepoužitý papír."));
        var inputs = new Grid { ColumnDefinitions = new("*,*,*,*"), RowDefinitions = new("Auto,Auto,Auto") };
        var fields = new[] { ("Rows", "Řádky"), ("Columns", "Sloupce"), ("PaperWidth", "Papír šířka mm"), ("PaperHeight", "Papír výška mm"), ("Width", "Nálepka šířka mm"), ("Height", "Nálepka výška mm"), ("Left", "Levý okraj mm"), ("Top", "Horní okraj mm"), ("GapX", "Mezera X mm"), ("GapY", "Mezera Y mm"), ("OffsetX", "Posun X mm"), ("OffsetY", "Posun Y mm") };
        for (var i = 0; i < fields.Length; i++) { var (key, title) = fields[i]; var input = new TextBox(); dimensions[key] = input; var group = Stack(Text(title), input); group.Margin = new(0, 0, 8, 8); Grid.SetRow(group, i / 4); Grid.SetColumn(group, i % 4); inputs.Children.Add(group); }
        sheetPanel.Children.Add(inputs); sheetPanel.Children.Add(Row(includeQr, Action("Uložit typ archu", SaveProfileAsync)));
        sheetPanel.Children.Add(Text("Kliknutím určíte začátek; zaškrtávátko označuje již použitou nebo chybějící nálepku.")); sheetPanel.Children.Add(labelGrid);
        var labelPanel = new Grid { ColumnDefinitions = new("380,*") };
        labelPanel.Children.Add(new ScrollViewer { Content = printPanel });
        labelPanel.Children.Add(new ScrollViewer { Content = sheetPanel, [Grid.ColumnProperty] = 1 });
        AddTab("Štítky", labelPanel, false);
        printerChoice.SelectionChanged += (_, _) => { if (printerChoice.SelectedItem is string p && p != settings.Printer) { settings.Printer = p; Save(); } };

        clientId.Text = settings.ClientId; clientSecret.Text = settings.ClientSecret; readOnly.IsChecked = settings.ReadOnly;
        readOnly.IsCheckedChanged += (_, _) => { settings.ReadOnly = readOnly.IsChecked == true; Save(); UpdateArchive(); };
        // QR is not part of the sheet geometry, so it applies at once without saving the profile.
        includeQr.IsCheckedChanged += (_, _) => { var qr = includeQr.IsChecked == true; if (settings.Labels.Qr == qr) return; settings.Labels.Qr = qr; if (settings.LabelProfiles.TryGetValue(settings.Labels.Name, out var stored)) stored.Qr = qr; Save(); };
        AddTab("Nastavení", Stack(Heading("Režim"), readOnly,
            Heading("Google OAuth klient (jen pro volitelné Google ID)"), Text("Běžný uživatel používá OAuth klienta dodaného vydavatelem. Toto nastavení slouží pro vlastní sestavení nebo první konfiguraci správce."), clientId, clientSecret,
            Row(Action("Importovat Google client JSON", ImportOAuthAsync), Action("Uložit nastavení Google", SaveOAuthAsync), Action("Návod pro správce", () => { GoogleAuth.OpenBrowser("https://github.com/KoudelkaB/SimpleDMS/blob/main/docs/google-setup.md"); return Task.CompletedTask; })),
            Heading("Údržba"), Action("Otevřít místní zálohy registru", () => { if (settings.Archive != null) GoogleAuth.OpenBrowser(Path.Combine(store.ArchiveDirectory(settings.Archive), "backups")); return Task.CompletedTask; }),
            Text("Před každou změnou registru se uloží jeho kopie (posledních 50). Google Drive navíc uchovává historii verzí. Registr neupravujte současně v Excelu; když je otevřený, aplikace zápis odmítne.")));

        query.TextChanged += (_, _) => Filter(); category.SelectionChanged += (_, _) => Filter(); state.SelectionChanged += (_, _) => Filter(); electronic.SelectionChanged += (_, _) => Filter(); records.SelectionChanged += (_, _) => UpdateDetail();
        records.DoubleTapped += async (_, _) => await Run(OpenSelectedAsync);
        records.ItemTemplate = new FuncDataTemplate<DocumentRecord>((r, _) =>
        {
            if (r == null) return new TextBlock();
            var row = new Grid { ColumnDefinitions = new("80,*,220,110"), Margin = new Thickness(8, 5) };
            row.Children.Add(new TextBlock { Text = r.Code, FontWeight = FontWeight.SemiBold });
            var title = new TextBlock { Text = (r.Problem.Length > 0 ? "⚠ " : "") + r.Title, TextTrimming = TextTrimming.CharacterEllipsis, [Grid.ColumnProperty] = 1 };
            if (r.Problem.Length > 0) { title.Foreground = Brushes.Firebrick; ToolTip.SetTip(title, r.Problem); }
            row.Children.Add(title);
            row.Children.Add(new TextBlock { Text = CategoryName(r.Category), Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(8, 0), [Grid.ColumnProperty] = 2 });
            var stateText = new TextBlock { Text = r.State, [Grid.ColumnProperty] = 3 }; if (r.Pending) stateText.Foreground = Brushes.DarkOrange; row.Children.Add(stateText);
            return row;
        });
        profileChoice.SelectionChanged += (_, _) => { if (!filling && profileChoice.SelectedItem is string name && settings.LabelProfiles.TryGetValue(name, out var p)) { settings.Labels = p; settings.Sheet = settings.LabelSheets.Values.LastOrDefault(s => s.ProfileKey == p.Key) ?? new() { ProfileKey = p.Key }; FillProfile(); UpdateLabels(); Save(); } };
        sheetChoice.SelectionChanged += (_, _) => { if (!filling && sheetChoice.SelectedItem is SheetOption s) { settings.Sheet = s.Sheet; UpdateLabels(false); Save(); } };
        if (settings.LabelProfiles.Count == 0) settings.LabelProfiles[settings.Labels.Name] = settings.Labels;
        FillProfile(); UpdateLabels();
        if (settings.Archive != null)
        {
            rootPath.Text = settings.Archive.Root; driveUrl.Text = settings.Archive.DriveRootId is { Length: > 0 } id ? "https://drive.google.com/drive/folders/" + id : "";
            status.Text = "Načítám registr…";
        }
        else status.Text = "Na kartě Archiv vyberte složku archivu.";
        UpdateArchive();
        // Everything touching the synchronized drive runs off the UI thread: a streaming Drive
        // or a disconnected network disk can take a long time to answer.
        Opened += async (_, _) =>
        {
            if (settings.Archive != null) { await LoadCatalogAsync(); if (catalog != null) ShowDocuments(); }
            if (OperatingSystem.IsWindows())
            {
                var drive = await DetectGoogleDriveAsync();
                syncClient.Text = drive != null ? $"Google Drive for desktop je připojen jako {drive}" : "Google Drive for desktop nebyl nalezen. Po instalaci a přihlášení aplikaci restartujte.";
                if (drive != null && settings.Archive == null && string.IsNullOrWhiteSpace(rootPath.Text)) rootPath.Text = Path.Combine(drive, "Můj disk");
            }
            await LoadPrintersAsync();
            var p = settings.Archive;
            if (p?.DriveLinked != true || settings.ClientId.Length == 0) return;
            try { await auth.RestoreAsync(p.AccountId, p.AccountEmail); driveOnline = true; UpdateArchive(); await BackgroundLinkAsync(); }
            catch (Exception) { driveOnline = false; UpdateArchive(); }
        };
        timer.Tick += async (_, _) => await TickAsync(); timer.Start();
        Closed += (_, _) => { timer.Stop(); operation?.Cancel(); };
    }
    sealed record SheetOption(LabelSheet Sheet, int Number, int Capacity) { public override string ToString() => $"List {Number} · použito {Sheet.Used.Count} z {Capacity}"; }
    // Sheets are numbered in the order they were started for the profile; the random ID means nothing to the user.
    int SheetNumber(LabelSheet sheet) { var sheets = settings.LabelSheets.Values.Where(x => x.ProfileKey == settings.Labels.Key).ToList(); var index = sheets.FindIndex(x => x.Id == sheet.Id); return (index < 0 ? sheets.Count : index) + 1; }
    sealed record CategoryOption(string Code, string Name) { public override string ToString() => Name.Length > 0 ? $"{Code} – {Name}" : Code; }
    CancellationToken Token => operation?.Token ?? CancellationToken.None;
    // Czech count with the right noun form: 1 štítek, 2–4 štítky, 5 a více štítků.
    static string Count(int n, string one, string few, string many) => $"{n} {(n == 1 ? one : n is >= 2 and <= 4 ? few : many)}";
    static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    static TextBlock Heading(string text) => new() { Text = text, FontSize = 18, FontWeight = FontWeight.SemiBold, Margin = new(0, 12, 0, 4) };
    static StackPanel Stack(params Control[] controls) { var p = new StackPanel { Spacing = 8 }; foreach (var c in controls) p.Children.Add(c); return p; }
    static StackPanel Row(params Control[] controls) { var p = Stack(controls); p.Orientation = Orientation.Horizontal; return p; }
    // A field that stretches next to a fixed-width button.
    static Grid Grid2(Control stretch, Control fixedWidth)
    {
        var grid = new Grid { ColumnDefinitions = new("*,Auto") }; fixedWidth.Margin = new(8, 0, 0, 0);
        grid.Children.Add(stretch); Grid.SetColumn(fixedWidth, 1); grid.Children.Add(fixedWidth); return grid;
    }
    static Grid Form(params (string Label, Control Field)[] rows)
    {
        var grid = new Grid { ColumnDefinitions = new("150,*") };
        for (var i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = rows[i].Label, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 10, 12, 4) }; Grid.SetRow(label, i); grid.Children.Add(label);
            var field = rows[i].Field; field.Margin = new(0, 4); Grid.SetRow(field, i); Grid.SetColumn(field, 1); grid.Children.Add(field);
        }
        return grid;
    }
    Button Action(string title, Func<Task> work) { var b = new Button { Content = title }; b.Click += async (_, _) => await Run(work); return b; }
    void AddTab(string title, Control content, bool scroll = true) => tabs.Items.Add(new TabItem { Header = title, Content = scroll ? new ScrollViewer { Content = content, Margin = new Thickness(10) } : new Border { Child = content, Margin = new Thickness(10) } });
    async Task Run(Func<Task> work)
    {
        if (busy) return; busy = true; operation = new(); saveDocument.IsEnabled = false; cancel.IsVisible = true;
        try { await work(); }
        catch (OperationCanceledException) { status.Text = "Operace přerušena."; }
        catch (Exception e) { status.Text = e.Message; }
        finally { busy = false; operation.Dispose(); operation = null; saveDocument.IsEnabled = CanWrite; cancel.IsVisible = false; }
    }
    // A switch made by the application keeps the message that explains it.
    void ShowDocuments() { switching = true; tabs.SelectedIndex = 1; switching = false; }
    bool CanWrite => settings.Archive != null && catalog != null && !cached && !settings.ReadOnly;
    void InitializeServices() => auth = new(settings, new OsSecretStore(store));
    void Save()
    {
        if (settings.Archive != null) settings.ArchiveLabelQueues[settings.Archive.Key] = settings.LabelQueue;
        settings.Sheet.ProfileKey = settings.Labels.Key; settings.LabelSheets[settings.Sheet.Id] = settings.Sheet; store.Write("settings.json", settings);
    }
    void SetArchive(ArchiveProfile p)
    {
        Save(); settings.Archive = p; settings.LabelQueue = settings.ArchiveLabelQueues.GetValueOrDefault(p.Key) ?? []; Save(); UpdateLabels();
    }
    // Runs a file-system probe in the background; a drive that does not answer counts as unavailable.
    static async Task<T?> Probe<T>(Func<T> work, int seconds = 5)
    {
        try { return await Task.Run(work).WaitAsync(TimeSpan.FromSeconds(seconds)); }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException) { return default; }
    }
    static Task<string?> DetectGoogleDriveAsync() => Probe(() =>
    {
        // Network drives are skipped: a disconnected one blocks IsReady for a long time.
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType is not (DriveType.Network or DriveType.CDRom)))
            try { if (drive.IsReady && drive.VolumeLabel == "Google Drive") return drive.RootDirectory.FullName; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return null;
    });
    async Task LoadCatalogAsync()
    {
        var p = settings.Archive;
        if (p == null) { catalog = null; UpdateArchive(); return; }
        try
        {
            var result = await Task.Run(() => (Load: service.Load(p), Stamp: ArchiveService.Stamp(p)));
            if (settings.Archive != p) return;
            (catalog, cached) = result.Load; stamp = result.Stamp;
            status.Text = cached ? "Složka archivu není dostupná. Zobrazena poslední načtená kopie registru, úpravy nejsou možné." : status.Text == "Načítám registr…" ? "" : status.Text;
        }
        catch (Exception e) { catalog = null; status.Text = e.Message; }
        UpdateArchive();
    }
    async Task TickAsync()
    {
        var p = settings.Archive;
        if (busy || p == null) return;
        // The sync client replaces the workbook when someone else changes it; reload when it does.
        var current = await Task.Run(() => ArchiveService.Stamp(p));
        if (!busy && settings.Archive == p && (current != stamp || cached))
        {
            try { var result = await Task.Run(() => service.Load(p)); if (settings.Archive == p) { (catalog, cached) = result; stamp = current; UpdateArchive(); } }
            catch (Exception) { }
        }
        if (++ticks % 20 == 0) await BackgroundLinkAsync();
    }
    async Task BackgroundLinkAsync()
    {
        if (linking || busy || !driveOnline || settings.Archive?.DriveLinked != true || settings.ReadOnly || cached) return;
        linking = true;
        try { var p = settings.Archive; if (await service.LinkDriveIdsAsync(p, new DriveClient(auth)) > 0 && settings.Archive == p) await LoadCatalogAsync(); }
        catch (Exception e) { if (!busy) driveStatus.Text = "Doplnění Google ID se nezdařilo: " + e.Message; }
        finally { linking = false; }
    }
    IReadOnlyList<CategoryOption> Categories()
    {
        var known = catalog?.Categories ?? new Dictionary<string, string>();
        // A new archive without "Kódování dokumentů" still needs some category to start with.
        return (known.Count > 0 ? known.Keys : Enumerable.Range(0, 100).Select(i => i.ToString("D2", CultureInfo.InvariantCulture)))
            .Order().Select(code => new CategoryOption(code, known.GetValueOrDefault(code) ?? "")).ToList();
    }
    string CategoryName(string code) => catalog?.Categories.GetValueOrDefault(code) is { Length: > 0 } name ? code + " – " + name : code;
    void UpdateArchive()
    {
        var p = settings.Archive; archiveTitle.Text = p?.Name ?? "SimpleDMS"; Title = p == null ? "SimpleDMS" : p.Name + " — SimpleDMS";
        mode.Text = p == null ? "Vyberte složku archivu" : $"{p.Root} · " + (cached ? "složka nedostupná, zobrazena poslední kopie" : settings.ReadOnly ? "pouze čtení" : "úpravy povoleny")
            + (p.DriveLinked ? $" · Google ID: {p.AccountEmail}" + (driveOnline ? "" : " (přihlášení je třeba obnovit)") : "");
        driveStatus.Text = p == null ? "Nejprve otevřete archiv." : !p.DriveLinked ? "Google ID se nedoplňují (propojení je volitelné)."
            : $"Propojeno s účtem {p.AccountEmail}. " + (driveOnline ? "Google ID se doplňují automaticky každých 5 minut." : "Přihlášení vypršelo, použijte Přihlásit Google a propojit.");
        var options = Categories();
        var selected = (category.SelectedItem as CategoryOption)?.Code;
        var filterItems = new List<object> { "Všechny kategorie" }; filterItems.AddRange(options);
        category.ItemsSource = filterItems; category.SelectedItem = options.FirstOrDefault(x => x.Code == selected) ?? (object)filterItems[0];
        var chosen = (newCategory.SelectedItem as CategoryOption)?.Code;
        newCategory.ItemsSource = options; newCategory.SelectedItem = options.FirstOrDefault(x => x.Code == chosen) ?? options.FirstOrDefault();
        newAuthor.ItemsSource = catalog?.Records.Select(r => r.Author.Trim()).Where(x => x.Length > 0).Distinct().Order().ToList() ?? [];
        saveDocument.IsEnabled = CanWrite && !busy;
        _ = UpdateNextCodeAsync(); Filter();
    }
    int previewVersion;
    // Listing the documents folder may wait for the sync client, so the preview is computed in the background.
    async Task UpdateNextCodeAsync()
    {
        var version = ++previewVersion; var p = settings.Archive; var current = catalog;
        var code = p != null && current != null && newCategory.SelectedItem is CategoryOption c ? await Probe(() => ArchiveService.PreviewCode(current, p, c.Code), 30) ?? "" : "";
        if (version == previewVersion) nextCode.Text = code.Length > 0 ? "Přidělí se číslo " + code : "";
    }
    void Filter()
    {
        var chosen = (category.SelectedItem as CategoryOption)?.Code;
        var list = catalog?.Records.Where(r => r.Matches(query.Text ?? "") && (chosen == null || r.Category == chosen) && (state.SelectedIndex <= 0 || r.Pending == (state.SelectedIndex == 1)) && (electronic.SelectedIndex <= 0 || r.Electronic == (electronic.SelectedIndex == 1)) && (!onlyProblems || r.Problem.Length > 0)).ToList() ?? [];
        var selectedCode = (records.SelectedItem as DocumentRecord)?.Code;
        records.ItemsSource = list; records.SelectedItem = list.FirstOrDefault(x => x.Code == selectedCode); counter.Text = $"{list.Count} z {catalog?.Records.Count ?? 0} dokumentů";
        var problems = catalog?.Records.Count(r => r.Problem.Length > 0) ?? 0;
        if (problems == 0) onlyProblems = false;
        problemsButton.IsVisible = problems > 0;
        problemsButton.Content = onlyProblems ? "Zobrazit všechny dokumenty" : $"⚠ {Count(problems, "záznam", "záznamy", "záznamů")} s chybou v registru – zobrazit";
        // Rows without a complete number are not records; they can only be listed.
        var rows = catalog?.Warnings.Where(w => w.Contains("neúplné evidenční číslo")).ToList() ?? [];
        if (onlyProblems && rows.Count > 0) status.Text = string.Join(" ", rows);
    }
    DocumentRecord Selected() => records.SelectedItem as DocumentRecord ?? throw new InvalidOperationException("Vyberte dokument v seznamu.");
    ArchiveProfile Profile() => settings.Archive ?? throw new InvalidOperationException("Nejprve otevřete archiv.");
    void RequireWrite()
    {
        Profile();
        if (settings.ReadOnly) throw new InvalidOperationException("Aplikace je v režimu pouze pro čtení (Nastavení).");
        if (cached || catalog == null) throw new InvalidOperationException("Složka archivu není dostupná. Zkontrolujte synchronizačního klienta.");
    }
    void UpdateDetail()
    {
        detailProblem.IsVisible = records.SelectedItem is DocumentRecord { Problem.Length: > 0 };
        if (records.SelectedItem is not DocumentRecord r) { details.Text = "Vyberte záznam."; return; }
        detailProblem.Text = "⚠ Chyba v registru: " + r.Problem;
        details.Text = $"{r.Code}\n{r.Title}\n\nKategorie: {CategoryName(r.Category)}\n{r.State}\nAutor: {r.Author}\nReference: {r.Reference}\nPlatnost: {r.Validity}\nElektronická forma: {(r.Electronic ? "ano" : "ne")}"
            + (r.RelativePath.Length > 0 ? $"\nUmístění: {r.RelativePath}" : "") + $"\nGoogle ID: {(r.DriveId.Length > 0 ? r.DriveId : "zatím nedoplněno")}\n\n{r.Notes}";
    }
    async Task ChooseRootAsync()
    {
        var current = rootPath.Text?.Trim() ?? "";
        var start = current.Length > 0 && await Probe(() => Directory.Exists(current), 3) ? await StorageProvider.TryGetFolderFromPathAsync(current) : null;
        var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Složka archivu (obsahuje registr XLSX a složku dokumentů)", AllowMultiple = false, SuggestedStartLocation = start });
        if (folders.Count == 0) return;
        rootPath.Text = folders[0].TryGetLocalPath() ?? throw new InvalidOperationException("Vyberte místní složku.");
        await DiscoverAsync();
    }
    async Task DiscoverAsync()
    {
        var root = rootPath.Text?.Trim() ?? "";
        var options = await Task.Run(() => ArchiveService.Discover(root));
        archiveChoices.ItemsSource = options; archiveChoices.SelectedIndex = options.Count > 0 ? 0 : -1;
        if (options.Count == 1 && options[0].Folder != null) { await OpenChosenAsync(); return; }
        status.Text = options.Count == 0 ? "Ve složce není žádný registr XLSX. Vyberte jinou složku nebo založte nový archiv." : "Vyberte archiv a otevřete jej.";
    }
    async Task OpenChosenAsync()
    {
        var root = rootPath.Text?.Trim() ?? "";
        if (archiveChoices.SelectedItem is not ArchiveChoice choice) { await DiscoverAsync(); return; }
        await OpenArchiveAsync(root, choice);
    }
    Task CreateArchiveAsync()
    {
        var name = archiveName.Text?.Trim() ?? "";
        if (name.Length == 0) throw new InvalidOperationException("Zadejte název nového archivu.");
        return OpenArchiveAsync(rootPath.Text?.Trim() ?? "", new(name, null, null));
    }
    async Task OpenArchiveAsync(string root, ArchiveChoice choice)
    {
        var p = await Task.Run(() => service.Open(root, choice, !settings.ReadOnly));
        if (settings.Archive?.Key == p.Key) p = p with { DriveRootId = settings.Archive.DriveRootId, AccountId = settings.Archive.AccountId, AccountEmail = settings.Archive.AccountEmail };
        else driveOnline = false;
        SetArchive(p); await LoadCatalogAsync(); if (catalog != null) ShowDocuments();
        driveUrl.Text = p.DriveRootId is { Length: > 0 } id ? "https://drive.google.com/drive/folders/" + id : "";
        status.Text = $"Archiv {p.Name} otevřen ({catalog?.Records.Count ?? 0} dokumentů).";
    }
    async Task LinkDriveAsync()
    {
        var p = Profile(); var rootId = ArchivePaths.ParseRoot(driveUrl.Text ?? "");
        status.Text = "Přihlaste se v prohlížeči…"; await auth.SignInAsync(Token); driveOnline = true;
        var name = await ArchiveService.VerifyDriveRootAsync(p, new DriveClient(auth), rootId, Token);
        settings.Archive = p with { DriveRootId = rootId, AccountId = auth.AccountId, AccountEmail = auth.Email }; Save(); UpdateArchive();
        status.Text = $"Propojeno se složkou Google Drive „{name}“."; await LinkIdsAsync(true);
    }
    async Task LinkIdsAsync(bool report)
    {
        var p = Profile();
        if (!p.DriveLinked) throw new InvalidOperationException("Nejprve propojte archiv s Google Drive.");
        if (!driveOnline) throw new InvalidOperationException("Přihlášení ke Google vypršelo. Použijte Přihlásit Google a propojit.");
        RequireWrite();
        status.Text = "Doplňuji Google ID…";
        var count = await service.LinkDriveIdsAsync(p, new DriveClient(auth), Token);
        if (count > 0) await LoadCatalogAsync();
        if (report) status.Text = count > 0 ? $"Doplněno {count} Google ID." : "Nic nového k doplnění. Nově přidané složky se objeví, až je synchronizační klient nahraje.";
    }
    Task UnlinkDriveAsync()
    {
        var p = Profile(); settings.Archive = p with { DriveRootId = null, AccountId = "", AccountEmail = "" }; driveOnline = false; Save(); UpdateArchive();
        status.Text = "Propojení s Google Drive zrušeno. Archiv dál funguje přes synchronizovanou složku."; return Task.CompletedTask;
    }
    // The system dialog picks either files or folders, so there is one button for each; Explorer drag and drop takes both at once.
    async Task<List<string>> PickAsync(string title, bool folders)
    {
        var picked = folders ? (await StorageProvider.OpenFolderPickerAsync(new() { Title = title, AllowMultiple = true })).Cast<IStorageItem>()
            : await StorageProvider.OpenFilePickerAsync(new() { Title = title, AllowMultiple = true });
        return picked.Select(x => x.TryGetLocalPath() ?? throw new InvalidOperationException("Vyberte místní soubor nebo složku.")).ToList();
    }
    async Task PickAttachmentsAsync(bool folders) { foreach (var path in await PickAsync("Přílohy dokumentu", folders)) if (!attachmentPaths.Contains(path)) attachmentPaths.Add(path); UpdateFiles(); }
    void UpdateFiles() => filesList.ItemsSource = attachmentPaths.Select(x => Path.GetFileName(x) + (Directory.Exists(x) ? "  (složka)" : "")).ToList();
    Progress<string> Progress() => new(text => status.Text = text);
    // A text field for typing the date and a calendar button; the stock date picker showed today's day as a bare number.
    static Control DateField(TextBox box)
    {
        var calendar = new Avalonia.Controls.Calendar { FirstDayOfWeek = DayOfWeek.Monday, SelectionMode = CalendarSelectionMode.SingleDate };
        var flyout = new Flyout { Content = calendar, Placement = PlacementMode.BottomEdgeAlignedLeft };
        var icon = new PathIcon { Width = 16, Height = 16, Data = StreamGeometry.Parse("M7 2h2v2h6V2h2v2h2a2 2 0 0 1 2 2v13a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h2V2zM5 9v10h14V9H5zm2 2h3v3H7v-3z") };
        var button = new Button { Content = icon, Flyout = flyout, Margin = new(6, 0, 0, 0) }; ToolTip.SetTip(button, "Vybrat datum v kalendáři");
        var syncing = false;
        flyout.Opening += (_, _) =>
        {
            var date = WorkbookCatalog.ParseDate(box.Text ?? "");
            syncing = true; calendar.SelectedDate = date; calendar.DisplayDate = date ?? DateTime.Today; syncing = false;
        };
        calendar.SelectedDatesChanged += (_, _) =>
        {
            if (syncing || calendar.SelectedDate is not { } date) return;
            box.Text = date.ToString("d.M.yyyy", CultureInfo.InvariantCulture); flyout.Hide();
        };
        return Row(box, button);
    }
    string Validity()
    {
        var text = newValidity.Text?.Trim() ?? "";
        if (text.Length == 0) return "";
        return WorkbookCatalog.ParseDate(text)?.ToString("d.M.yyyy", CultureInfo.InvariantCulture) ?? throw new InvalidOperationException("Platnost zadejte jako datum d.M.rrrr (např. 31.12.2030), nebo ji nechte prázdnou.");
    }
    async Task SaveDocumentAsync()
    {
        RequireWrite();
        var code = (newCategory.SelectedItem as CategoryOption)?.Code ?? throw new InvalidOperationException("Vyberte kategorii.");
        var draft = new DocumentDraft(code, newTitle.Text?.Trim() ?? "", newReference.Text?.Trim() ?? "", newAuthor.Text?.Trim() ?? "",
            Validity(), newNotes.Text?.Trim() ?? "", newPending.IsChecked == true);
        status.Text = "Ukládání dokumentu…";
        var record = await service.AddAsync(Profile(), draft, attachmentPaths, null, Progress(), Token);
        Queue(record); attachmentPaths.Clear(); UpdateFiles(); newTitle.Text = ""; newReference.Text = ""; newNotes.Text = ""; newValidity.Text = ""; newPending.IsChecked = false;
        await LoadCatalogAsync(); ShowDocuments(); query.Text = ""; records.SelectedItem = (records.ItemsSource as IEnumerable<DocumentRecord>)?.FirstOrDefault(x => x.Code == record.Code);
        status.Text = $"Dokument {record.Code} uložen. Štítek je ve frontě." + (record.Electronic ? " Přílohy na Google Drive nahraje synchronizační klient." : "");
    }
    async Task TogglePendingAsync() { RequireWrite(); var record = Selected(); await service.SetPendingAsync(Profile(), record.Code, !record.Pending, Token); await LoadCatalogAsync(); }
    async Task AttachAsync(bool folders)
    {
        RequireWrite(); var r = Selected();
        var picked = await PickAsync("Doplnit přílohy k " + r.Code, folders); if (picked.Count == 0) return;
        await service.AddAsync(Profile(), new(r.Category, r.Title), picked, r.Code, Progress(), Token);
        await LoadCatalogAsync(); status.Text = $"Přílohy doplněny k dokumentu {r.Code}.";
    }
    async Task OpenSelectedAsync()
    {
        var r = Selected(); var p = Profile(); var local = await Task.Run(() => service.LocalPath(p, r), Token);
        if (local != null) { GoogleAuth.OpenBrowser(local); if (r.Problem.Length > 0) status.Text = "Pozor, příloha byla otevřena podle sloupce L, který ukazuje na jiný dokument. " + r.Problem; return; }
        throw new InvalidOperationException(!r.Electronic ? "Dokument existuje pouze v papírovém archivu." : cached ? "Složka archivu není dostupná." : $"Přílohy nebyly ve složce {Profile().DocumentsPath} nalezeny. Zkontrolujte synchronizaci, případně je otevřete na Google Drive.");
    }
    Task OpenDriveAsync()
    {
        var r = Selected();
        if (r.DriveUrl.Length == 0) throw new InvalidOperationException("Dokument zatím nemá Google ID. Doplní se po propojení s Google Drive (karta Archiv), až synchronizační klient nahraje přílohy.");
        GoogleAuth.OpenBrowser(r.DriveUrl); return Task.CompletedTask;
    }
    void Queue(DocumentRecord record) { settings.LabelQueue.Add(new(record.Code, record.Title, record.DriveUrl, record.DriveId)); Save(); UpdateLabels(); }
    Task QueueSelectedAsync() { Queue(Selected()); status.Text = "Štítek přidán do fronty."; return Task.CompletedTask; }
    // Labels are matched by Key: a label refreshed with its Google link is a new record instance.
    Task RemoveLabelAsync()
    {
        var keys = queueList.SelectedItems?.OfType<LabelItem>().Select(x => x.Key).ToHashSet() ?? [];
        if (keys.Count == 0) throw new InvalidOperationException("Vyberte ve frontě štítky k odebrání (více štítků s klávesou Ctrl).");
        settings.LabelQueue.RemoveAll(x => keys.Contains(x.Key)); Save(); UpdateLabels(); status.Text = $"Z fronty odebráno: {Count(keys.Count, "štítek", "štítky", "štítků")}."; return Task.CompletedTask;
    }
    Task ClearQueueAsync() { var count = settings.LabelQueue.Count; settings.LabelQueue.Clear(); Save(); UpdateLabels(); status.Text = $"Fronta vyprázdněna ({Count(count, "štítek", "štítky", "štítků")})."; return Task.CompletedTask; }
    async Task LoadPrintersAsync()
    {
        var printers = await Task.Run(LabelPrinter.List); var fallback = await Task.Run(LabelPrinter.Default);
        printerChoice.ItemsSource = printers;
        printerChoice.SelectedItem = printers.Contains(settings.Printer) ? settings.Printer : printers.Contains(fallback ?? "") ? fallback : printers.FirstOrDefault();
        if (printers.Count == 0) printerChoice.PlaceholderText = "Žádná tiskárna nenalezena (použije se výchozí)";
    }
    void FillProfile() { var p = settings.Labels; profileName.Text = p.Name; includeQr.IsChecked = p.Qr; foreach (var x in dimensions) x.Value.Text = typeof(LabelProfile).GetProperty(x.Key)!.GetValue(p)!.ToString(); filling = true; profileChoice.ItemsSource = settings.LabelProfiles.Keys.ToList(); profileChoice.SelectedItem = p.Name; filling = false; }
    Task SaveProfileAsync()
    {
        var p = new LabelProfile { Name = profileName.Text ?? "Arch", Qr = includeQr.IsChecked == true }; foreach (var x in dimensions) { var prop = typeof(LabelProfile).GetProperty(x.Key)!; var value = float.Parse((x.Value.Text ?? "").Replace(',', '.'), CultureInfo.InvariantCulture); prop.SetValue(p, prop.PropertyType == typeof(int) ? (object)checked((int)value) : value); }
        p.Validate(); settings.LabelProfiles[p.Name] = p; settings.Labels = p; settings.Sheet = settings.LabelSheets.Values.LastOrDefault(x => x.ProfileKey == p.Key) ?? new() { ProfileKey = p.Key }; Save(); FillProfile(); UpdateLabels(); status.Text = "Typ archu uložen. Rozměry ověřte zkušebním tiskem v měřítku 100 %."; return Task.CompletedTask;
    }
    Task NewSheetAsync() { settings.Sheet = new() { ProfileKey = settings.Labels.Key }; Save(); UpdateLabels(); return Task.CompletedTask; }
    void UpdateLabels(bool updateSheets = true)
    {
        queueList.ItemsSource = settings.LabelQueue.ToList(); var p = settings.Labels; var s = settings.Sheet; labelGrid.Children.Clear(); labelGrid.RowDefinitions.Clear(); labelGrid.ColumnDefinitions.Clear();
        for (int i = 0; i < p.Rows; i++) labelGrid.RowDefinitions.Add(new(GridLength.Auto)); for (int i = 0; i < p.Columns; i++) labelGrid.ColumnDefinitions.Add(new(GridLength.Star));
        PrintPlan? plan = null;
        if (settings.LabelQueue.Count > 0) { try { plan = LabelPlanner.Plan(p, s, settings.LabelQueue); } catch (InvalidOperationException) { } }
        var first = plan?.Pages[0].Placements.ToDictionary(x => x.Position, x => x.Label.Code) ?? [];
        for (int i = 0; i < p.Capacity; i++)
        {
            int index = i; var used = new CheckBox { Content = "Použito", IsChecked = s.Used.Contains(i) };
            used.IsCheckedChanged += (_, _) => { if (used.IsChecked == true) s.Used.Add(index); else s.Used.Remove(index); Save(); UpdateLabels(); };
            var button = new Button { Content = $"{i / p.Columns + 1}:{i % p.Columns + 1}" + (first.TryGetValue(i, out var code) ? "  " + code : ""), HorizontalAlignment = HorizontalAlignment.Stretch };
            button.Click += (_, _) => { s.Start = index; Save(); UpdateLabels(); }; var cell = Stack(button, used); cell.Margin = new(2); Grid.SetRow(cell, i / p.Columns); Grid.SetColumn(cell, i % p.Columns); labelGrid.Children.Add(cell);
        }
        var next = s.Next(p); sheetStatus.Text = $"List {SheetNumber(s)} · použito {s.Used.Count} z {p.Capacity} · " + (next == p.Capacity ? "plný" : $"další pozice {next / p.Columns + 1}:{next % p.Columns + 1}") + $" · fronta {settings.LabelQueue.Count}";
        // The current sheet is listed even before it is first saved.
        if (s.ProfileKey.Length == 0) s.ProfileKey = p.Key;
        settings.LabelSheets.TryAdd(s.Id, s);
        if (updateSheets) { filling = true; var sheets = settings.LabelSheets.Values.Where(x => x.ProfileKey == p.Key).Select((x, i) => new SheetOption(x, i + 1, p.Capacity)).ToList(); sheetChoice.ItemsSource = sheets; sheetChoice.SelectedItem = sheets.FirstOrDefault(x => x.Sheet.Id == s.Id); filling = false; }
    }
    // The queue is the only state: preview and print always show exactly the queue, and labels leave it
    // (and consume sheet positions) only when the result of the print is confirmed right afterwards.
    async Task ExportAsync(bool print)
    {
        // Google IDs may have been filled since the label was queued; use the current link for the QR code.
        var current = catalog?.Records.ToDictionary(r => r.Code) ?? [];
        settings.LabelQueue = settings.LabelQueue.Select(l => l.Url.Length == 0 && current.GetValueOrDefault(l.Code) is { DriveUrl.Length: > 0 } r ? l with { Url = r.DriveUrl, Id = r.DriveId } : l).ToList();
        Save(); UpdateLabels();
        var plan = LabelPlanner.Plan(settings.Labels, settings.Sheet, settings.LabelQueue);
        var withoutLink = settings.Labels.Qr ? plan.Placements.Count(x => x.Label.Url.Length == 0) : 0;
        var note = withoutLink > 0 ? $" Bez Google ID ({Count(withoutLink, "štítek", "štítky", "štítků")}) nese QR evidenční číslo." : "";
        if (print)
        {
            status.Text = "Tisk…";
            status.Text = await LabelPrinter.PrintAsync(settings.Labels, plan, printerChoice.SelectedItem as string, Path.Combine(store.Root, "labels"), Token) + note;
            await ConfirmPrintAsync(plan); return;
        }
        var folder = Path.Combine(store.Root, "labels"); Directory.CreateDirectory(folder); var path = Path.Combine(folder, plan.Id + ".pdf");
        LabelPdf.Export(path, settings.Labels, plan);
        GoogleAuth.OpenBrowser(path); status.Text = "Náhled otevřen. Tiskněte tlačítkem Tisknout; náhled arch ani frontu nemění." + note;
    }
    enum PrintOutcome { Nothing, All, Selection }
    // Opens right after printing with every label preselected, so a successful print takes one click (or Enter).
    // It needs an answer: without it the queue and the sheet would no longer match the paper.
    async Task ConfirmPrintAsync(PrintPlan plan)
    {
        var columns = settings.Labels.Columns;
        var picks = plan.Placements.ToDictionary(x => x, x => new CheckBox
        {
            IsChecked = true,
            Content = (plan.Pages.Count > 1 ? $"List {x.Page + 1}, " : "") + $"pozice {x.Position / columns + 1}:{x.Position % columns + 1} — {x.Label.Code} {x.Label.Title}"
        });
        var outcome = PrintOutcome.Nothing; var answered = false;
        var dialog = new Window { Title = "Výsledek tisku", Width = 640, Height = 480, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Closing += (_, e) => { if (!answered && !e.IsProgrammatic && e.CloseReason == WindowCloseReason.WindowClosing) e.Cancel = true; };
        Button Choice(string text, PrintOutcome value) { var b = new Button { Content = text }; b.Click += (_, _) => { outcome = value; answered = true; dialog.Close(); }; return b; }
        var all = Choice($"Ano, vytisklo se vše ({picks.Count})", PrintOutcome.All); all.Classes.Add("accent"); all.IsDefault = true;
        var selection = Choice("Uložit jen zaškrtnuté", PrintOutcome.Selection);
        // Unticking a label means a partial result, so Enter then saves the selection instead of "all".
        foreach (var box in picks.Values) box.IsCheckedChanged += (_, _) => { var complete = picks.Values.All(x => x.IsChecked == true); all.IsDefault = complete; selection.IsDefault = !complete; };
        var header = Stack(Heading("Vytiskly se všechny štítky správně?"), Text("Pokud se některé nepovedly, zrušte u nich zaškrtnutí a zvolte Uložit jen zaškrtnuté. Nevytištěné štítky zůstanou ve frontě a jejich pozice na archu volné."));
        var buttons = new WrapPanel(); foreach (var b in new[] { all, selection, Choice("Nic se nevytisklo", PrintOutcome.Nothing) }) { b.Margin = new(0, 8, 8, 0); buttons.Children.Add(b); }
        var layout = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header); DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
        layout.Children.Add(new ScrollViewer { Content = Stack([.. picks.Values]), Margin = new(0, 8) });
        dialog.Content = layout;
        await dialog.ShowDialog(this);
        if (outcome == PrintOutcome.Nothing) { status.Text = "Nic nebylo vytištěno. Štítky zůstávají ve frontě, pozice archu jsou volné."; return; }
        var confirmed = outcome == PrintOutcome.All ? plan.Placements.ToList() : picks.Where(x => x.Value.IsChecked == true).Select(x => x.Key).ToList();
        var result = LabelPlanner.Confirm(settings.Labels, settings.Sheet, plan, confirmed, settings.LabelQueue);
        foreach (var page in plan.Pages.Select((p, i) => (Page: p, Index: i)))
        { var sheet = settings.LabelSheets.GetValueOrDefault(page.Page.SheetId) ?? new() { Id = page.Page.SheetId, ProfileKey = plan.ProfileKey }; foreach (var p in confirmed.Where(x => x.Page == page.Index)) sheet.Used.Add(p.Position); settings.LabelSheets[sheet.Id] = sheet; }
        settings.Sheet = result.Sheet; settings.LabelQueue = result.Queue; Save(); UpdateLabels();
        status.Text = $"Potvrzeno {confirmed.Count} z {Count(plan.Placements.Count, "štítku", "štítků", "štítků")}." + (result.Queue.Count > 0 ? $" Ve frontě zůstává {result.Queue.Count}." : "");
    }
    void ImportOAuth(string json) { var root = JsonDocument.Parse(json).RootElement; var item = root.TryGetProperty("installed", out var installed) ? installed : root; settings.ClientId = item.GetProperty("client_id").GetString() ?? ""; settings.ClientSecret = item.TryGetProperty("client_secret", out var s) ? s.GetString() ?? "" : ""; }
    async Task ImportOAuthAsync() { var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "Google OAuth desktop client JSON", AllowMultiple = false }); if (files.Count == 0) return; var path = files[0].TryGetLocalPath() ?? throw new InvalidOperationException("Vyberte místní JSON."); ImportOAuth(await File.ReadAllTextAsync(path, Token)); clientId.Text = settings.ClientId; clientSecret.Text = settings.ClientSecret; Save(); InitializeServices(); driveOnline = false; UpdateArchive(); status.Text = "OAuth klient nastaven. Propojení s Google Drive obnovíte na kartě Archiv."; }
    Task SaveOAuthAsync() { settings.ClientId = clientId.Text?.Trim() ?? ""; settings.ClientSecret = clientSecret.Text?.Trim() ?? ""; Save(); InitializeServices(); driveOnline = false; UpdateArchive(); status.Text = "Google nastavení uloženo. Propojení s Google Drive obnovíte na kartě Archiv."; return Task.CompletedTask; }
}
