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
    GoogleAuth auth = null!;
    ArchiveService service = null!;
    WorkbookCatalog? catalog;
    bool online, busy;
    CancellationTokenSource? operation;
    readonly TextBlock archiveTitle = new() { Text = "SimpleDMS", FontSize = 24, FontWeight = FontWeight.SemiBold };
    readonly TextBlock mode = new() { Text = "Připojte archiv", FontSize = 13 };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBlock details = new() { Text = "Vyberte záznam.", TextWrapping = TextWrapping.Wrap };
    readonly TextBlock counter = new();
    readonly TextBox rootUrl = new() { PlaceholderText = "https://drive.google.com/drive/folders/…" };
    readonly TextBox archiveName = new() { PlaceholderText = "Název nového archivu" };
    readonly ComboBox archiveChoices = new() { MinWidth = 280 };
    readonly TextBox query = new() { PlaceholderText = "Číslo, název, autor, poznámky…" };
    readonly ComboBox category = new() { ItemsSource = new[] { "Všechny kategorie" }, SelectedIndex = 0, MinWidth = 150 };
    readonly ComboBox state = new() { ItemsSource = new[] { "Všechny stavy", "Rozpracované", "Dokončené" }, SelectedIndex = 0, MinWidth = 140 };
    readonly ComboBox electronic = new() { ItemsSource = new[] { "Všechny dokumenty", "S elektronickou přílohou", "Pouze papír" }, SelectedIndex = 0, MinWidth = 180 };
    readonly ListBox records = new() { MinHeight = 120 };
    readonly TextBox newCategory = new() { PlaceholderText = "Kategorie (2 číslice)", Width = 160 };
    readonly TextBox newTitle = new() { PlaceholderText = "Název dokumentu" };
    readonly TextBox newAuthor = new() { PlaceholderText = "Autor / účastníci", Width = 300 };
    readonly TextBox newReference = new() { PlaceholderText = "Reference", Width = 250 };
    readonly TextBox newValidity = new() { PlaceholderText = "Platnost", Width = 200 };
    readonly TextBox newNotes = new() { PlaceholderText = "Poznámky a fyzické umístění (skříň / šanon)", AcceptsReturn = true, MinHeight = 80 };
    readonly CheckBox newPending = new() { Content = "Rozpracovaný dokument" };
    readonly ListBox filesList = new() { MinHeight = 100 };
    readonly List<string> attachmentPaths = [];
    readonly ListBox queueList = new() { MinHeight = 100 };
    readonly Grid labelGrid = new();
    readonly TextBlock sheetStatus = new();
    readonly Dictionary<string, TextBox> dimensions = [];
    readonly TextBox profileName = new() { PlaceholderText = "Jméno profilu" };
    readonly ComboBox profileChoice = new() { MinWidth = 180 };
    readonly ComboBox sheetChoice = new() { MinWidth = 220 };
    readonly CheckBox includeQr = new() { Content = "QR kód" };
    readonly TextBox printer = new() { PlaceholderText = "Linux: tiskárna (prázdné = výchozí)" };
    readonly TextBox clientId = new() { PlaceholderText = "Google OAuth Client ID" };
    readonly TextBox clientSecret = new() { PlaceholderText = "Desktop client secret (z JSON klienta)", PasswordChar = '●' };
    readonly CheckBox readOnly = new() { Content = "Čtenář: žádat pouze oprávnění číst Drive" };
    readonly TextBlock localRootText = new() { TextWrapping = TextWrapping.Wrap };
    readonly TabControl tabs = new();
    readonly Button saveDocument;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMinutes(5) };
    public MainWindow() : this(new LocalStore()) { }
    public MainWindow(LocalStore local)
    {
        store = local; settings = store.Read<AppSettings>("settings.json") ?? new();
        var shipped = Path.Combine(AppContext.BaseDirectory, "oauth-client.json");
        if (settings.ClientId.Length == 0 && File.Exists(shipped)) ImportOAuth(File.ReadAllText(shipped));
        InitializeServices();
        Title = "SimpleDMS"; Width = 1180; Height = 840; MinWidth = 800; MinHeight = 600;
        var body = new DockPanel { Margin = new Thickness(20) };
        var header = Stack(archiveTitle, mode); header.Margin = new(0, 0, 0, 14); DockPanel.SetDock(header, Dock.Top); body.Children.Add(header);
        var bottom = Stack(status, Row(Action("Zrušit probíhající operaci", () => { operation?.Cancel(); return Task.CompletedTask; }))); bottom.Margin = new(0, 12, 0, 0); DockPanel.SetDock(bottom, Dock.Bottom); body.Children.Add(bottom);
        body.Children.Add(tabs); Content = body;
        var archivePanel = Stack(Heading("Otevřít archiv Google Drive"), Text("Vložte odkaz na root složku. Přihlaste se vlastním účtem Google; nalezená evidence se otevře automaticky."), rootUrl,
            Row(Action("Přihlásit Google a otevřít archiv", ConnectAsync), Action("Aktualizovat", RefreshAsync)),
            archiveChoices, Action("Otevřít vybraný archiv", OpenChosenAsync), archiveName, Action("Založit nový archiv", () => { archiveChoices.SelectedIndex = -1; return OpenChosenAsync(); }), Heading("Offline kopie"), localRootText,
            Row(Action("Připojit synchronizovanou složku", () => ChooseLocalAsync(false)), Action("Vytvořit offline kopii", () => ChooseLocalAsync(true))),
            Action("Aktualizovat offline kopii", SyncAsync), Text("Připojte místní root s XLSX a složkou dokumentů. Na Linuxu lze použít Insync nebo jednosměrné rclone. Vlastní kopie SimpleDMS se ukládá do samostatné složky."));
        AddTab("Archiv", archivePanel);
        var searchPanel = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), ColumnDefinitions = new("2*,*") };
        var filters = Stack(query, Row(category, state, electronic)); Grid.SetColumnSpan(filters, 2); searchPanel.Children.Add(filters);
        Grid.SetRow(counter, 1); Grid.SetColumnSpan(counter, 2); searchPanel.Children.Add(counter);
        Grid.SetRow(records, 2); searchPanel.Children.Add(records);
        var detailPanel = Stack(Heading("Detail dokumentu"), details, Action("Otevřít přílohy", OpenSelectedAsync), Action("Otevřít na Drive", OpenDriveAsync),
            Action("Přepnout Rozpracovaný / Dokončený", TogglePendingAsync), Action("Doplnit přílohy ke stejnému číslu", AttachAsync), Action("Přidat štítek do fronty", QueueSelectedAsync));
        detailPanel.Margin = new(18, 0, 0, 0); Grid.SetRow(detailPanel, 2); Grid.SetColumn(detailPanel, 1); searchPanel.Children.Add(new ScrollViewer { Content = detailPanel, [Grid.RowProperty] = 2, [Grid.ColumnProperty] = 1 });
        AddTab("Dokumenty", searchPanel, false);
        saveDocument = Action("Uložit dokument a připravit štítek", SaveDocumentAsync);
        AddTab("Přidat dokument", Stack(Heading("Nový dokument"), Text("Číslo a Google ID doplní aplikace. Přílohy můžete vybrat hromadně; papírový dokument může být bez příloh."),
            newCategory, newTitle, Row(newAuthor, newReference, newValidity), newNotes, newPending,
            Row(Action("Vybrat přílohy", PickAttachmentsAsync), Action("Přidat složku", PickFolderAsync), Action("Vyprázdnit přílohy", () => { attachmentPaths.Clear(); UpdateFiles(); return Task.CompletedTask; })), Text("Soubory nebo složky lze přetáhnout do seznamu příloh. Vnořené složky se zachovají."), filesList, saveDocument,
            Action("Obnovit přerušené přidávání", ResumeAsync)));
        DragDrop.SetAllowDrop(filesList, true);
        DragDrop.AddDragOverHandler(filesList, (_, e) => { e.DragEffects = e.DataTransfer.TryGetFiles() != null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; });
        DragDrop.AddDropHandler(filesList, (_, e) => { foreach (var file in e.DataTransfer.TryGetFiles() ?? []) { var path = file.TryGetLocalPath(); if (path != null && !attachmentPaths.Contains(path)) attachmentPaths.Add(path); } UpdateFiles(); e.Handled = true; });
        var labelPanel = Stack(Heading("Štítky na arch nálepek"), Text("Pozice se spotřebují až po potvrzení výsledku tisku. Náhled a PDF export samy arch neposouvají."),
            Row(profileChoice, sheetChoice), profileName);
        var inputs = new Grid { ColumnDefinitions = new("*,*,*,*"), RowDefinitions = new("Auto,Auto,Auto,Auto") };
        var fields = new[] { ("Rows", "Řádky"), ("Columns", "Sloupce"), ("PaperWidth", "Papír šířka mm"), ("PaperHeight", "Papír výška mm"), ("Width", "Nálepka šířka mm"), ("Height", "Nálepka výška mm"), ("Left", "Levý okraj mm"), ("Top", "Horní okraj mm"), ("GapX", "Mezera X mm"), ("GapY", "Mezera Y mm"), ("OffsetX", "Posun X mm"), ("OffsetY", "Posun Y mm") };
        for (var i = 0; i < fields.Length; i++) { var (key, title) = fields[i]; var input = new TextBox(); dimensions[key] = input; var group = Stack(Text(title), input); group.Margin = new(0, 0, 8, 8); Grid.SetRow(group, i / 4); Grid.SetColumn(group, i % 4); inputs.Children.Add(group); }
        labelPanel.Children.Add(inputs); labelPanel.Children.Add(Row(includeQr, Action("Uložit profil", SaveProfileAsync), Action("Nový arch", NewSheetAsync)));
        labelPanel.Children.Add(sheetStatus); labelPanel.Children.Add(Text("Kliknutím určíte začátek; zaškrtávátko označuje již použitou nebo chybějící nálepku.")); labelPanel.Children.Add(labelGrid);
        labelPanel.Children.Add(queueList); labelPanel.Children.Add(Row(Action("Odebrat vybraný štítek", RemoveLabelAsync), Action("Náhled / export PDF", () => ExportAsync(false)), Action("Tisknout", () => ExportAsync(true))));
        labelPanel.Children.Add(printer); labelPanel.Children.Add(Row(Action("Potvrdit výsledek tisku", ConfirmPrintAsync), Action("Zrušit tiskovou úlohu", CancelPrintAsync))); AddTab("Štítky", labelPanel);
        clientId.Text = settings.ClientId; clientSecret.Text = settings.ClientSecret; readOnly.IsChecked = settings.ReadOnly;
        AddTab("Nastavení", Stack(Heading("Google připojení"), Text("Běžný uživatel používá OAuth klienta dodaného vydavatelem. Toto nastavení slouží pro vlastní sestavení nebo první konfiguraci správce."), clientId, clientSecret, readOnly,
            Row(Action("Importovat Google client JSON", ImportOAuthAsync), Action("Uložit nastavení Google", SaveOAuthAsync), Action("Návod pro správce", () => { GoogleAuth.OpenBrowser("https://github.com/KoudelkaB/SimpleDMS/blob/main/docs/google-setup.md"); return Task.CompletedTask; })),
            Heading("Údržba evidence"), Action("Automaticky doplnit chybějící Google ID v Q", RepairIdsAsync), Action("Otevřít místní zálohy a deník", () => { if (settings.Archive != null) GoogleAuth.OpenBrowser(store.ArchiveDirectory(settings.Archive)); return Task.CompletedTask; }),
            Text("Jeden aktivní správce upravuje archiv online. Ostatní uživatelé nahlížejí. Při otevřeném Excelu neupravujte stejný registr současně.")));
        query.TextChanged += (_, _) => Filter(); category.SelectionChanged += (_, _) => Filter(); state.SelectionChanged += (_, _) => Filter(); electronic.SelectionChanged += (_, _) => Filter(); records.SelectionChanged += (_, _) => UpdateDetail();
        records.ItemTemplate = new FuncDataTemplate<DocumentRecord>((r, _) => { if (r == null) return new TextBlock(); var row = new Grid { ColumnDefinitions = new("80,*,140"), Margin = new Thickness(8, 5) }; row.Children.Add(new TextBlock { Text = r.Code, FontWeight = FontWeight.SemiBold }); row.Children.Add(new TextBlock { Text = r.Title, TextTrimming = TextTrimming.CharacterEllipsis, [Grid.ColumnProperty] = 1 }); row.Children.Add(new TextBlock { Text = r.State, [Grid.ColumnProperty] = 2, Foreground = r.Pending ? Brushes.DarkOrange : null }); return row; });
        profileChoice.SelectionChanged += (_, _) => { if (profileChoice.SelectedItem is string name && settings.LabelProfiles.TryGetValue(name, out var p) && settings.PendingPrint == null) { settings.Labels = p; settings.Sheet = settings.LabelSheets.Values.LastOrDefault(s => s.ProfileKey == p.Key) ?? new() { ProfileKey = p.Key }; FillProfile(); UpdateLabels(); Save(); } };
        sheetChoice.SelectionChanged += (_, _) => { if (sheetChoice.SelectedItem is SheetOption s && settings.PendingPrint == null) { settings.Sheet = s.Sheet; UpdateLabels(false); Save(); } };
        if (settings.LabelProfiles.Count == 0) settings.LabelProfiles[settings.Labels.Name] = settings.Labels;
        FillProfile(); UpdateLabels();
        if (settings.Archive != null)
        {
            rootUrl.Text = "https://drive.google.com/drive/folders/" + settings.Archive.RootId;
            catalog = service.LoadOffline(settings.Archive); UpdateArchive(); if (catalog != null) tabs.SelectedIndex = 1;
        }
        Opened += async (_, _) => { if (settings.Archive != null && settings.ClientId.Length > 0) await Run(async () => { try { await auth.RestoreAsync(settings.Archive.AccountId, settings.Archive.AccountEmail, Token); online = true; await RefreshAsync(); } catch (Exception) { online = false; UpdateArchive(); status.Text = "Offline režim. Připravená místní evidence je dostupná; přihlášení obnovíte na kartě Archiv."; } }); };
        timer.Tick += async (_, _) => { if (busy || settings.Archive == null) return; await Run(async () => { if (online) await RefreshAsync(); else { catalog = service.LoadOffline(settings.Archive); Filter(); } if (online && settings.Archive.ManagedCopy) await SyncAsync(); }); }; timer.Start();
        Closed += (_, _) => { timer.Stop(); operation?.Cancel(); };
    }
    sealed record SheetOption(LabelSheet Sheet, int Capacity) { public override string ToString() => "Arch " + Sheet.Id[..6] + $" — {Sheet.Used.Count}/{Capacity}"; }
    CancellationToken Token => operation?.Token ?? CancellationToken.None;
    static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    static TextBlock Heading(string text) => new() { Text = text, FontSize = 18, FontWeight = FontWeight.SemiBold, Margin = new(0, 12, 0, 4) };
    static StackPanel Stack(params Control[] controls) { var p = new StackPanel { Spacing = 8 }; foreach (var c in controls) p.Children.Add(c); return p; }
    static StackPanel Row(params Control[] controls) { var p = Stack(controls); p.Orientation = Orientation.Horizontal; return p; }
    Button Action(string title, Func<Task> work) { var b = new Button { Content = title }; b.Click += async (_, _) => await Run(work); return b; }
    void AddTab(string title, Control content, bool scroll = true) => tabs.Items.Add(new TabItem { Header = title, Content = scroll ? new ScrollViewer { Content = content, Margin = new Thickness(10) } : content });
    async Task Run(Func<Task> work)
    {
        if (busy) return; busy = true; operation = new(); saveDocument.IsEnabled = false;
        try { await work(); }
        catch (OperationCanceledException) { status.Text = "Operace přerušena. Rozpracované přidávání lze obnovit."; }
        catch (Exception e) { status.Text = e.Message; }
        finally { busy = false; operation.Dispose(); operation = null; saveDocument.IsEnabled = online && settings.Archive?.CanWrite == true && !settings.ReadOnly; }
    }
    void InitializeServices() { auth = new(settings, new OsSecretStore(store)); service = new(new DriveClient(auth), store); }
    void Save()
    {
        if (settings.Archive != null) { settings.ArchiveLabelQueues[settings.Archive.Key] = settings.LabelQueue; settings.ArchivePrintPlans[settings.Archive.Key] = settings.PendingPrint; }
        settings.Sheet.ProfileKey = settings.Labels.Key; settings.LabelSheets[settings.Sheet.Id] = settings.Sheet; store.Write("settings.json", settings);
    }
    void SetArchive(ArchiveProfile p)
    {
        Save(); settings.Archive = p; settings.LabelQueue = settings.ArchiveLabelQueues.GetValueOrDefault(p.Key) ?? []; settings.PendingPrint = settings.ArchivePrintPlans.GetValueOrDefault(p.Key); Save(); UpdateLabels();
    }
    void UpdateArchive()
    {
        var p = settings.Archive; archiveTitle.Text = p?.Name ?? "SimpleDMS"; Title = p == null ? "SimpleDMS" : p.Name + " — SimpleDMS";
        mode.Text = p == null ? "Připojte archiv" : $"{p.AccountEmail} · {(online ? (!settings.ReadOnly && p.CanWrite ? "Správce online" : "Čtení online") : "Offline čtení")}";
        localRootText.Text = p?.LocalRoot == null ? "Místní kopie zatím není vybrána." : p.LocalRoot + (p.ManagedCopy ? " · spravuje SimpleDMS" : " · externí synchronizační klient");
        var selectedCategory = category.SelectedItem as string;
        var categories = new[] { "Všechny kategorie" }.Concat((catalog?.Categories.Keys ?? Enumerable.Empty<string>()).Order()).ToList();
        category.ItemsSource = categories; category.SelectedIndex = Math.Max(0, categories.IndexOf(selectedCategory ?? ""));
        if (newCategory.Text is null or "") newCategory.Text = catalog?.Categories.Keys.Order().FirstOrDefault() ?? "10";
        Filter();
    }
    void Filter()
    {
        var list = catalog?.Records.Where(r => r.Matches(query.Text ?? "") && (category.SelectedIndex <= 0 || r.Category == (string?)category.SelectedItem) && (state.SelectedIndex <= 0 || r.Pending == (state.SelectedIndex == 1)) && (electronic.SelectedIndex <= 0 || r.Electronic == (electronic.SelectedIndex == 1))).ToList() ?? [];
        var selectedCode = (records.SelectedItem as DocumentRecord)?.Code;
        records.ItemsSource = list; records.SelectedItem = list.FirstOrDefault(x => x.Code == selectedCode); counter.Text = $"{list.Count} z {catalog?.Records.Count ?? 0} dokumentů" + (catalog?.Warnings.Count > 0 ? $" · {catalog.Warnings.Count} upozornění v registru" : "");
        counter.ToolTipSet(catalog == null ? "" : string.Join('\n', catalog.Warnings));
    }
    DocumentRecord Selected() => records.SelectedItem as DocumentRecord ?? throw new InvalidOperationException("Vyberte dokument v seznamu.");
    ArchiveProfile Profile() => settings.Archive ?? throw new InvalidOperationException("Nejprve připojte archiv.");
    void RequireOnline() { if (!online) throw new InvalidOperationException("Tato operace potřebuje připojení a přihlášení Google."); }
    void UpdateDetail() { if (records.SelectedItem is DocumentRecord r) details.Text = $"{r.Code}\n{r.Title}\n\n{r.State}\nAutor: {r.Author}\nReference: {r.Reference}\nPlatnost: {r.Validity}\nElektronická forma: {(r.Electronic ? "ano" : "ne")}\n\n{r.Notes}"; else details.Text = "Vyberte záznam."; }
    async Task ConnectAsync()
    {
        var root = ArchivePaths.ParseRoot(rootUrl.Text ?? ""); status.Text = "Přihlaste se v prohlížeči…"; await auth.SignInAsync(Token); online = true;
        var options = await service.DiscoverAsync(root, Token); archiveChoices.ItemsSource = options; archiveChoices.SelectedIndex = options.Count > 0 ? 0 : -1;
        if (options.Count == 1) { await OpenChosenAsync(); return; }
        archiveName.Text = (await new DriveClient(auth).GetAsync(root, Token)).Name;
        status.Text = options.Count == 0 ? "Zadejte název a založte nový archiv." : "Vyberte registr a jeho složku.";
    }
    async Task OpenChosenAsync()
    {
        RequireOnline(); var root = ArchivePaths.ParseRoot(rootUrl.Text ?? "");
        var choice = archiveChoices.SelectedItem as ArchiveChoice ?? new(archiveName.Text ?? "", null, null);
        var p = await service.OpenAsync(root, choice, auth.AccountId, auth.Email, settings.ReadOnly, Token);
        if(settings.Archive?.Key==p.Key)p=p with{LocalRoot=settings.Archive.LocalRoot,ManagedCopy=settings.Archive.ManagedCopy};
        SetArchive(p); catalog = service.LoadOffline(p); UpdateArchive(); tabs.SelectedIndex = 1;
        if (p.CanWrite && service.PendingOperation(p) == null) { await service.RepairIdsAsync(p, Token); catalog = service.LoadOffline(p); UpdateArchive(); }
        status.Text = "Archiv otevřen." + (service.PendingOperation(p) != null ? " Je dostupná obnova přerušeného přidávání." : "");
    }
    async Task RefreshAsync() { RequireOnline(); var result = await service.RefreshAsync(Profile(), Token); settings.Archive = result.Profile with { CanWrite = result.Profile.CanWrite && !settings.ReadOnly }; catalog = result.Catalog; Save(); UpdateArchive(); status.Text = "Evidence aktualizována."; }
    async Task ChooseLocalAsync(bool managed)
    {
        var p = Profile(); var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = managed ? "Umístění samostatné offline kopie" : "Root synchronizované kopie (XLSX a složka dokumentů)", AllowMultiple = false });
        if (folders.Count == 0) return; var path = folders[0].TryGetLocalPath() ?? throw new InvalidOperationException("Vyberte místní adresář.");
        if (managed) path = Path.Combine(path, "SimpleDMS-" + p.Key);
        else if (!File.Exists(Path.Combine(path, p.Name + ".xlsx"))) throw new InvalidOperationException("Vybraná složka neobsahuje " + p.Name + ".xlsx. Vyberte její nadřazený root a nastavte dostupnost offline v synchronizačním klientovi.");
        settings.Archive = p with { LocalRoot = path, ManagedCopy = managed }; Save(); UpdateArchive();
        if (managed) await SyncAsync(); else { if (online) await service.IndexLocalAsync(Profile(), Token); catalog = service.LoadOffline(Profile()); Filter(); status.Text = "Místní synchronizovaná kopie připojena. Dostupnost offline nastavte také v synchronizačním klientovi."; }
    }
    async Task SyncAsync() { RequireOnline(); await service.SynchronizeAsync(Profile(), new Progress<string>(text => status.Text = text), Token); Save(); }
    async Task PickAttachmentsAsync() { var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "Přílohy dokumentu", AllowMultiple = true }); foreach (var file in files) { var path = file.TryGetLocalPath(); if (path != null && !attachmentPaths.Contains(path)) attachmentPaths.Add(path); } UpdateFiles(); }
    async Task PickFolderAsync() { var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Složka s přílohami", AllowMultiple = true }); foreach (var folder in folders) { var path = folder.TryGetLocalPath(); if (path != null && !attachmentPaths.Contains(path)) attachmentPaths.Add(path); } UpdateFiles(); }
    void UpdateFiles() => filesList.ItemsSource = attachmentPaths.Select(Path.GetFileName).ToList();
    async Task SaveDocumentAsync()
    {
        RequireOnline(); if (settings.ReadOnly) throw new InvalidOperationException("Čtenář nemůže přidávat dokumenty.");
        if (service.PendingOperation(Profile()) != null) throw new InvalidOperationException("Nejprve použijte Obnovit přerušené přidávání.");
        status.Text = "Ukládání dokumentu…";
        var record = await service.AddAsync(Profile(), new(newCategory.Text ?? "", newTitle.Text ?? "", newReference.Text ?? "", newAuthor.Text ?? "", newValidity.Text ?? "", newNotes.Text ?? "", newPending.IsChecked == true), attachmentPaths, ct: Token);
        Queue(record); attachmentPaths.Clear(); UpdateFiles(); newTitle.Text = ""; newNotes.Text = ""; await RefreshAsync(); status.Text = $"Dokument {record.Code} uložen. Štítek je ve frontě."; tabs.SelectedIndex = 1;
    }
    async Task ResumeAsync() { RequireOnline(); var op = service.PendingOperation(Profile()) ?? throw new InvalidOperationException("Žádné přidávání nečeká na obnovu."); var record = await service.AddAsync(Profile(), op.Draft, op.Files, op.Attach ? op.Code : null, Token); Queue(record); await RefreshAsync(); status.Text = "Rozpracované přidávání dokončeno."; }
    async Task TogglePendingAsync() { RequireOnline(); var record = Selected(); await service.SetPendingAsync(Profile(), record.Code, !record.Pending, Token); await RefreshAsync(); }
    async Task AttachAsync() { RequireOnline(); var r = Selected(); var picked = await StorageProvider.OpenFilePickerAsync(new() { Title = "Doplnit přílohy k " + r.Code, AllowMultiple = true }); if (picked.Count == 0) return; await service.AddAsync(Profile(), new(r.Category, r.Title, r.Reference, r.Author, r.Validity, r.Notes, r.Pending), picked.Select(x => x.TryGetLocalPath() ?? throw new InvalidOperationException("Příloha není místní.")), r.Code, Token); await RefreshAsync(); }
    async Task OpenSelectedAsync()
    {
        var r = Selected(); var local = await service.LocalPathAsync(Profile(), r, online, Token); if (local != null) { GoogleAuth.OpenBrowser(local); return; }
        if (online) { await OpenDriveAsync(); return; }
        throw new InvalidOperationException(r.Electronic ? "Příloha zatím není připravena offline. Aktualizujte místní kopii." : "Dokument existuje pouze v papírovém archivu.");
    }
    Task OpenDriveAsync() { RequireOnline(); var r = Selected(); if (r.DriveId.Length > 0) GoogleAuth.OpenBrowser("https://drive.google.com/open?id=" + Uri.EscapeDataString(r.DriveId)); else if (Uri.TryCreate(r.DriveUrl, UriKind.Absolute, out var url) && url.Host == "drive.google.com" && url.Scheme == "https") GoogleAuth.OpenBrowser(url.AbsoluteUri); else throw new InvalidOperationException("Dokument nemá odkaz na Drive."); return Task.CompletedTask; }
    void Queue(DocumentRecord record) { settings.LabelQueue.Add(new(record.Code, record.Title, record.DriveUrl, record.DriveId)); Save(); UpdateLabels(); }
    Task QueueSelectedAsync() { Queue(Selected()); status.Text = "Štítek přidán do fronty."; return Task.CompletedTask; }
    Task RemoveLabelAsync() { if (settings.PendingPrint != null) throw new InvalidOperationException("Nejprve potvrďte nebo zrušte tiskovou úlohu."); if (queueList.SelectedItem is LabelItem item) settings.LabelQueue.Remove(item); Save(); UpdateLabels(); return Task.CompletedTask; }
    void FillProfile() { var p = settings.Labels; profileName.Text = p.Name; includeQr.IsChecked = p.Qr; foreach (var x in dimensions) x.Value.Text = typeof(LabelProfile).GetProperty(x.Key)!.GetValue(p)!.ToString(); profileChoice.ItemsSource = settings.LabelProfiles.Keys.ToList(); }
    Task SaveProfileAsync()
    {
        if (settings.PendingPrint != null) throw new InvalidOperationException("Nejprve potvrďte nebo zrušte tiskovou úlohu.");
        var p = new LabelProfile { Name = profileName.Text ?? "Arch", Qr = includeQr.IsChecked == true }; foreach (var x in dimensions) { var prop = typeof(LabelProfile).GetProperty(x.Key)!; var value = float.Parse((x.Value.Text ?? "").Replace(',', '.'), CultureInfo.InvariantCulture); prop.SetValue(p, prop.PropertyType == typeof(int) ? (object)checked((int)value) : value); }
        p.Validate(); settings.LabelProfiles[p.Name] = p; settings.Labels = p; settings.Sheet = settings.LabelSheets.Values.LastOrDefault(x => x.ProfileKey == p.Key) ?? new() { ProfileKey = p.Key }; Save(); UpdateLabels(); status.Text = "Profil archu uložen. Rozměry ověřte zkušebním tiskem v měřítku 100 %."; return Task.CompletedTask;
    }
    Task NewSheetAsync() { if (settings.PendingPrint != null) throw new InvalidOperationException("Nejprve potvrďte nebo zrušte tiskovou úlohu."); settings.Sheet = new() { ProfileKey = settings.Labels.Key }; Save(); UpdateLabels(); return Task.CompletedTask; }
    void UpdateLabels(bool updateSheets = true)
    {
        queueList.ItemsSource = settings.LabelQueue.ToList(); var p = settings.Labels; var s = settings.Sheet; labelGrid.Children.Clear(); labelGrid.RowDefinitions.Clear(); labelGrid.ColumnDefinitions.Clear();
        for (int i = 0; i < p.Rows; i++) labelGrid.RowDefinitions.Add(new(GridLength.Auto)); for (int i = 0; i < p.Columns; i++) labelGrid.ColumnDefinitions.Add(new(GridLength.Star));
        PrintPlan? plan = settings.PendingPrint;
        if (plan == null && settings.LabelQueue.Count > 0) { try { plan = LabelPlanner.Plan(p, s, settings.LabelQueue); } catch (InvalidOperationException) { } }
        var first = plan?.Pages[0].Placements.ToDictionary(x => x.Position, x => x.Label.Code) ?? [];
        for (int i = 0; i < p.Capacity; i++)
        {
            int index = i; var used = new CheckBox { Content = "Použito", IsChecked = s.Used.Contains(i), IsEnabled = settings.PendingPrint == null };
            used.IsCheckedChanged += (_, _) => { if (used.IsChecked == true) s.Used.Add(index); else s.Used.Remove(index); Save(); UpdateLabels(); };
            var button = new Button { Content = $"{i / p.Columns + 1}:{i % p.Columns + 1}" + (first.TryGetValue(i, out var code) ? "  " + code : ""), HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = settings.PendingPrint == null };
            button.Click += (_, _) => { s.Start = index; Save(); UpdateLabels(); }; var cell = Stack(button, used); cell.Margin = new(2); Grid.SetRow(cell, i / p.Columns); Grid.SetColumn(cell, i % p.Columns); labelGrid.Children.Add(cell);
        }
        var next = s.Next(p); sheetStatus.Text = $"Arch {s.Id[..6]} · použito {s.Used.Count}/{p.Capacity} · " + (next == p.Capacity ? "plný" : $"další pozice {next / p.Columns + 1}:{next % p.Columns + 1}") + $" · fronta {settings.LabelQueue.Count}" + (settings.PendingPrint != null ? " · čeká potvrzení tisku" : "");
        if (updateSheets) sheetChoice.ItemsSource = settings.LabelSheets.Values.Where(x => x.ProfileKey == p.Key).Select(x => new SheetOption(x, p.Capacity)).ToList();
    }
    async Task ExportAsync(bool print)
    {
        settings.PendingPrint ??= LabelPlanner.Plan(settings.Labels, settings.Sheet, settings.LabelQueue); Save(); UpdateLabels();
        var folder = Path.Combine(store.Root, "labels"); Directory.CreateDirectory(folder); var path = Path.Combine(folder, settings.PendingPrint.Id + ".pdf");
        LabelPdf.Export(path, settings.Labels, settings.PendingPrint);
        if (print) { await LabelPdf.SubmitAsync(path, printer.Text, Token); status.Text = Environment.GetEnvironmentVariable("FLATPAK_ID") != null ? "PDF otevřeno pro tisk. Zvolte 100 % a po tisku potvrďte využité nálepky." : "Úloha předána tiskárně. Po tisku potvrďte skutečně využité nálepky."; }
        else { GoogleAuth.OpenBrowser(path); status.Text = "PDF otevřeno. Tiskněte v měřítku 100 %, bez přizpůsobení na stránku. Potom potvrďte výsledek."; }
    }
    async Task ConfirmPrintAsync()
    {
        var plan = settings.PendingPrint ?? throw new InvalidOperationException("Není připravena tisková úloha."); var panel = Stack(Text("Zaškrtněte pouze skutečně vytištěné nálepky. Při chybě tisku ponechte ostatní ve frontě."));
        var picks = new Dictionary<LabelPlacement, CheckBox>(); foreach (var placement in plan.Placements) { var c = new CheckBox { Content = $"Arch {placement.Page + 1}, pozice {placement.Position / settings.Labels.Columns + 1}:{placement.Position % settings.Labels.Columns + 1} — {placement.Label.Code}" }; picks[placement] = c; panel.Children.Add(c); }
        var dialog = new Window { Title = "Potvrdit výsledek tisku", Width = 550, Height = 600 }; var all = new Button { Content = "Označit všechny vytištěné" }; all.Click += (_, _) => { foreach (var c in picks.Values) c.IsChecked = true; }; panel.Children.Add(all);
        var accept = new Button { Content = "Uložit výsledek" }; accept.Click += (_, _) => dialog.Close(true); panel.Children.Add(accept); dialog.Content = new ScrollViewer { Content = panel, Margin = new Thickness(20) };
        if (await dialog.ShowDialog<bool>(this) != true) return; var confirmed = picks.Where(x => x.Value.IsChecked == true).Select(x => x.Key).ToList();
        var result = LabelPlanner.Confirm(settings.Labels, settings.Sheet, plan, confirmed, settings.LabelQueue);
        foreach (var page in plan.Pages.Select((p, i) => (Page: p, Index: i)))
        { var sheet = settings.LabelSheets.GetValueOrDefault(page.Page.SheetId) ?? new() { Id = page.Page.SheetId, ProfileKey = plan.ProfileKey }; foreach (var p in confirmed.Where(x => x.Page == page.Index)) sheet.Used.Add(p.Position); settings.LabelSheets[sheet.Id] = sheet; }
        settings.Sheet = result.Sheet; settings.LabelQueue = result.Queue; settings.PendingPrint = null; Save(); UpdateLabels(); status.Text = $"Potvrzeno {confirmed.Count} nálepek. Zbytek zůstává ve frontě.";
    }
    Task CancelPrintAsync() { settings.PendingPrint = null; Save(); UpdateLabels(); status.Text = "Tisková úloha zrušena; pozice archu zůstaly zachované."; return Task.CompletedTask; }
    void ImportOAuth(string json) { var root = JsonDocument.Parse(json).RootElement; var item = root.TryGetProperty("installed", out var installed) ? installed : root; settings.ClientId = item.GetProperty("client_id").GetString() ?? ""; settings.ClientSecret = item.TryGetProperty("client_secret", out var s) ? s.GetString() ?? "" : ""; }
    async Task ImportOAuthAsync() { var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "Google OAuth desktop client JSON", AllowMultiple = false }); if (files.Count == 0) return; var path = files[0].TryGetLocalPath() ?? throw new InvalidOperationException("Vyberte místní JSON."); ImportOAuth(await File.ReadAllTextAsync(path, Token)); clientId.Text = settings.ClientId; clientSecret.Text = settings.ClientSecret; Save(); InitializeServices(); online = false; UpdateArchive(); status.Text = "OAuth klient nastaven. Na kartě Archiv se přihlaste Google účtem."; }
    Task SaveOAuthAsync() { settings.ClientId = clientId.Text?.Trim() ?? ""; settings.ClientSecret = clientSecret.Text?.Trim() ?? ""; settings.ReadOnly = readOnly.IsChecked == true; Save(); InitializeServices(); online = false; UpdateArchive(); status.Text = "Google nastavení uloženo. Znovu se přihlaste na kartě Archiv."; return Task.CompletedTask; }
    async Task RepairIdsAsync() { RequireOnline(); var count = await service.RepairIdsAsync(Profile(), Token); await RefreshAsync(); status.Text = $"Doplněno {count} jednoznačných Google ID v Q."; }
}
static class UiExtensions { public static void ToolTipSet(this Control c, string text) => ToolTip.SetTip(c, text); }
