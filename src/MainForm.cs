using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MessengerWrapper;

sealed partial class MainForm : Form
{
    // messenger.com redirige vers facebook.com/messages depuis avril 2026.
    const string HomeUrl = "https://www.messenger.com/t/";

    static readonly string[] InternalHosts = ["messenger.com", "facebook.com", "fb.com", "meta.com"];
    static readonly string[] Redirectors = ["l.facebook.com", "lm.facebook.com"];
    static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MessengerWrapper");
    static readonly string BoundsFile = Path.Combine(DataDir, "window.json");

    record SavedBounds(int X, int Y, int W, int H, bool Max);

    readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(24, 25, 26) };
    readonly NotifyIcon tray;
    readonly Icon iconIdle = MakeIcon(unread: false);
    readonly Icon iconUnread = MakeIcon(unread: true);
    readonly EventWaitHandle activate;
    CoreWebView2Notification? pendingNotification;
    int lastUnread;
    int scrapeToken;
    string lastSignature = "";
    bool startHidden;
    bool quitting;

    public MainForm(EventWaitHandle activate, bool startHidden)
    {
        this.activate = activate;
        this.startHidden = startHidden;

        Text = "Messenger";
        Icon = iconIdle;
        BackColor = Color.FromArgb(24, 25, 26);
        Size = new Size(1100, 750);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(web);
        LoadBounds();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Ouvrir", null, (_, _) => Restore());
        menu.Items.Add("Accueil", null, (_, _) => { Restore(); web.CoreWebView2?.Navigate(HomeUrl); });
        menu.Items.Add("Tester les notifications", null, async (_, _) => await SendTestNotification());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => Quit());
        tray = new NotifyIcon { Icon = iconIdle, Text = "Messenger", Visible = true, ContextMenuStrip = menu };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Restore(); };
        tray.BalloonTipClicked += (_, _) =>
        {
            Restore();
            pendingNotification?.ReportClicked();
            pendingNotification = null;
        };
        Activated += (_, _) => Flash(false);

        ThreadPool.RegisterWaitForSingleObject(activate, (_, _) =>
        {
            if (IsHandleCreated && !IsDisposed) BeginInvoke(Restore);
        }, null, Timeout.Infinite, executeOnlyOnce: false);

        if (startHidden)
        {
            Opacity = 0;
            ShowInTaskbar = false;
        }
        Shown += (_, _) =>
        {
            if (!this.startHidden) return;
            Hide();
            Opacity = 1;
            ShowInTaskbar = true;
            this.startHidden = false;
        };
        Load += async (_, _) => await InitWebAsync();
    }

    async Task InitWebAsync()
    {
        try
        {
            // Profil persistant : cookies et session conservés d'un lancement à l'autre.
            // --disable-gpu supprime le processus GPU (~100 Mo) ; le rendu logiciel suffit pour un chat.
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataDir, "WebView2"),
                new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--disable-gpu --enable-low-end-device-mode --disable-features=msSmartScreenProtection" });
            await web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Impossible de démarrer WebView2.\nInstalle le runtime : winget install Microsoft.EdgeWebView2Runtime\n\n{ex.Message}", "Messenger", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Quit();
            return;
        }

        var core = web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = true;

        core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.PermissionRequested += OnPermissionRequested;
        core.NotificationReceived += OnNotificationReceived;
        core.NavigationCompleted += async (_, _) => await EnsureNotificationPermission();
        core.DocumentTitleChanged += (_, _) => OnTitleChanged(core.DocumentTitle);
        core.Navigate(HomeUrl);
    }

    // --- Navigation -------------------------------------------------------

    static bool IsInternal(Uri u) => InternalHosts.Any(h => u.Host == h || u.Host.EndsWith("." + h));

    static bool IsHttp(Uri u) => u.Scheme is "http" or "https";

    static Uri? UnwrapRedirect(Uri u)
    {
        if (!Redirectors.Contains(u.Host)) return null;
        var target = HttpUtility.ParseQueryString(u.Query)["u"];
        return Uri.TryCreate(target, UriKind.Absolute, out var t) ? t : null;
    }

    static void OpenExternal(Uri u)
    {
        if (IsHttp(u)) Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
    }

    void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || !IsHttp(uri)) return;

        var target = UnwrapRedirect(uri);
        if (target != null)
        {
            e.Cancel = true;
            OpenExternal(target);
        }
        else if (!IsInternal(uri))
        {
            e.Cancel = true;
            OpenExternal(uri);
        }
    }

    void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || !IsHttp(uri)) return;

        // Liens externes -> navigateur par défaut ; popups internes (appels, etc.) -> fenêtre WebView2 par défaut.
        var target = UnwrapRedirect(uri) ?? uri;
        if (target != uri || !IsInternal(uri))
        {
            e.Handled = true;
            OpenExternal(target);
        }
    }

    // Facebook n'envoie de notifications que si Notification.permission == "granted" ;
    // on le demande nous-mêmes (autorisé par OnPermissionRequested) au lieu d'attendre un clic dans l'interface.
    async Task EnsureNotificationPermission()
    {
        var core = web.CoreWebView2;
        if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) || !IsInternal(uri)) return;

        var state = await core.ExecuteScriptAsync("Notification.permission");
        Log($"permission avant: {state} ({uri})");
        if (state != "\"granted\"")
        {
            state = await core.ExecuteScriptAsync("Notification.requestPermission()");
            Log($"requestPermission -> {state}");
        }
    }

    async Task SendTestNotification()
    {
        var r = await web.CoreWebView2.ExecuteScriptAsync(
            "Notification.requestPermission().then(p => { if (p === 'granted') new Notification('Test Messenger', { body: 'Les notifications fonctionnent.' }); return p; })");
        Log($"test notification -> {r}");
    }

    static void Log(string message)
    {
        if (Environment.GetEnvironmentVariable("MW_DEBUG") != "1") return;
        try { File.AppendAllText(Path.Combine(DataDir, "log.txt"), $"{DateTime.Now:HH:mm:ss} {message}\n"); } catch { }
    }

    void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        Log($"PermissionRequested {e.PermissionKind} {e.Uri}");
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || !IsInternal(uri)) return;

        if (e.PermissionKind is CoreWebView2PermissionKind.Notifications
            or CoreWebView2PermissionKind.Microphone
            or CoreWebView2PermissionKind.Camera
            or CoreWebView2PermissionKind.ClipboardRead)
        {
            e.State = CoreWebView2PermissionState.Allow;
        }
    }

    // --- Fenêtre / zone de notification ----------------------------------

    void OnTitleChanged(string title)
    {
        // Facebook préfixe le titre avec le nombre de non-lus : "(3) Messenger".
        var m = UnreadPrefix().Match(title);
        int count = m.Success ? int.Parse(m.Groups[1].Value) : 0;
        Text = title;
        tray.Text = title.Length > 63 ? title[..63] : title;
        tray.Icon = count > 0 ? iconUnread : iconIdle;
        bool newActivity = count > lastUnread || !BaseTitle().IsMatch(title);
        lastUnread = count;
        if (newActivity)
        {
            Alert();
            _ = NotifyFromPageAsync();
        }
    }

    // L'appli web ne déclenche pas l'API Notification : on lit la conversation du haut de la liste
    // (celle qui vient de recevoir un message) et on en fait un toast avec l'expéditeur et le texte.
    async Task NotifyFromPageAsync()
    {
        int token = ++scrapeToken;
        await Task.Delay(700);
        if (token != scrapeToken || ActiveForm == this || web.CoreWebView2 == null) return;

        var raw = await web.CoreWebView2.ExecuteScriptAsync(
            "JSON.stringify([...document.querySelectorAll('a[href*=\"/t/\"]')].slice(0, 15).map(a => ({ h: a.getAttribute('href'), t: a.innerText })))");
        Log($"scrape: {raw}");
        if (JsonSerializer.Deserialize<string>(raw) is not { } json) return;
        var rows = JsonSerializer.Deserialize<ThreadRow[]>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        // Les premiers liens /t/ sont des raccourcis ("Discussions · 1 non lus") : une vraie ligne de
        // conversation a plusieurs lignes de texte, et on prend la première qui est non lue.
        var row = rows?.FirstOrDefault(r => r.T.Contains('\n') && UnreadMarker().IsMatch(r.T));
        if (row == null) return;

        var content = row.T.Split('\n').Select(l => l.Replace(' ', ' ').Trim())
            .Where(l => l.Length > 0 && l != "·"
                && !UnreadMarker().IsMatch(l)
                && !Regex.IsMatch(l, @"^\d+\s?(s|m|min|h|j|d|w|sem)$")
                && !Regex.IsMatch(l, @"^(En ligne|Actif|Active)", RegexOptions.IgnoreCase))
            .ToList();
        if (content.Count == 0) return;

        var sender = content[0];
        var preview = content.Count > 1 ? content[1] : "";
        var signature = row.H + "|" + preview;
        if (signature == lastSignature) return;
        lastSignature = signature;

        pendingNotification = null;
        tray.ShowBalloonTip(8000, sender, preview.Length > 0 ? preview : " ", ToolTipIcon.None);
    }

    record ThreadRow(string H, string T);

    [GeneratedRegex(@"^(\(\d+\+?\)\s*)?Messenger(\s*\|.*)?$")]
    private static partial Regex BaseTitle();

    [GeneratedRegex(@"non lu|unread", RegexOptions.IgnoreCase)]
    private static partial Regex UnreadMarker();

    void OnNotificationReceived(object? sender, CoreWebView2NotificationReceivedEventArgs e)
    {
        // On gère nous-mêmes : toast + clignotement + retour à la fenêtre au clic.
        e.Handled = true;
        var n = e.Notification;
        Log($"NotificationReceived '{n.Title}' / '{n.Body}' (actif: {ActiveForm == this})");
        n.ReportShown();
        if (ActiveForm == this) return;

        pendingNotification = n;
        tray.ShowBalloonTip(8000, string.IsNullOrWhiteSpace(n.Title) ? "Messenger" : n.Title,
            string.IsNullOrWhiteSpace(n.Body) ? " " : n.Body, ToolTipIcon.None);
        Alert();
    }

    // Fait clignoter le bouton de la barre des tâches (le fait réapparaître, réduit, si la fenêtre était cachée).
    void Alert()
    {
        if (ActiveForm == this || !IsHandleCreated) return;
        if (!Visible)
        {
            WindowState = FormWindowState.Minimized;
            Show();
        }
        Flash(true);
    }

    void Flash(bool on)
    {
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = Handle,
            dwFlags = on ? FlashTray | FlashUntilForeground : FlashStop,
            uCount = uint.MaxValue,
        };
        FlashWindowEx(ref info);
    }

    [GeneratedRegex(@"^\((\d+)\+?\)")]
    private static partial Regex UnreadPrefix();

    [StructLayout(LayoutKind.Sequential)]
    struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    static extern bool FlashWindowEx(ref FLASHWINFO info);

    const uint FlashStop = 0, FlashTray = 2, FlashUntilForeground = 12;

    void Restore()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    void Quit()
    {
        quitting = true;
        tray.Visible = false;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveBounds();
        if (!quitting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        tray.Dispose();
        web.Dispose();
        base.OnFormClosed(e);
    }

    void LoadBounds()
    {
        try
        {
            var b = JsonSerializer.Deserialize<SavedBounds>(File.ReadAllText(BoundsFile));
            if (b == null) return;
            var rect = new Rectangle(b.X, b.Y, b.W, b.H);
            if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect))) return;
            StartPosition = FormStartPosition.Manual;
            Bounds = rect;
            if (b.Max) WindowState = FormWindowState.Maximized;
        }
        catch { /* première exécution ou fichier invalide */ }
    }

    void SaveBounds()
    {
        try
        {
            var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(BoundsFile, JsonSerializer.Serialize(new SavedBounds(r.X, r.Y, r.Width, r.Height, WindowState == FormWindowState.Maximized)));
        }
        catch { }
    }

    // --- Icône ------------------------------------------------------------

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    static Icon MakeIcon(bool unread)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var fill = new LinearGradientBrush(new Rectangle(0, 0, 32, 32), Color.FromArgb(0, 198, 255), Color.FromArgb(160, 51, 255), 45f);
            g.FillEllipse(fill, 1, 1, 30, 30);
            g.FillPolygon(Brushes.White, [new(7, 19), new(16, 9), new(18, 16), new(25, 13), new(16, 24), new(14, 17)]);
            if (unread)
            {
                g.FillEllipse(Brushes.White, 19, 0, 13, 13);
                g.FillEllipse(Brushes.Red, 21, 2, 9, 9);
            }
        }
        var handle = bmp.GetHicon();
        try { using var tmp = Icon.FromHandle(handle); return (Icon)tmp.Clone(); }
        finally { DestroyIcon(handle); }
    }
}
