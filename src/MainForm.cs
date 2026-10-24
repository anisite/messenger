using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using CommunityToolkit.WinUI.Notifications;
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
    const int DockWidth = 460;
    Rectangle preDockBounds;
    bool preDockTopMost;
    bool docked;
    int lastUnread;
    int scrapeToken;
    string lastSignature = "";
    bool startHidden;
    bool quitting;

    public MainForm(EventWaitHandle activate, bool startHidden, bool startDocked)
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
        var topmost = new ToolStripMenuItem("Toujours au premier plan");
        topmost.Click += (_, _) => TopMost = !TopMost;
        var dock = new ToolStripMenuItem("Épingler sur le côté");
        dock.Click += (_, _) => { Restore(); ToggleDock(); };
        menu.Items.Add(topmost);
        menu.Items.Add(dock);
        menu.Opening += (_, _) =>
        {
            topmost.Checked = TopMost;
            dock.Checked = docked;
        };
        var autostart = new ToolStripMenuItem("Démarrer avec Windows") { Checked = IsAutostart() };
        autostart.Click += (_, _) =>
        {
            SetAutostart(!IsAutostart());
            autostart.Checked = IsAutostart();
        };
        menu.Items.Add(autostart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => Quit());
        tray = new NotifyIcon { Icon = iconIdle, Text = "Messenger", Visible = true, ContextMenuStrip = menu };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Restore(); };
        ToastNotificationManagerCompat.OnActivated += OnToastActivated;
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
            if (startDocked) ToggleDock();
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
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            (Environment.GetEnvironmentVariable("MW_DEBUG") == "1" ? "window.__mwDebug = true;" : "") + OpenImageScript + CloseConversationScript);
        core.WebMessageReceived += OnWebMessageReceived;
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

        Log($"nouvelle fenêtre: {uri.Host}{uri.AbsolutePath}");
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

    // --- Images -----------------------------------------------------------

    // Un clic sur une image de message l'ouvre hors de l'appli (Maj+clic : visionneuse intégrée).
    // La miniature du fil est petite (480 px) : on laisse la page ouvrir sa visionneuse (rendue invisible), on lit
    // l'image en grande taille qu'elle charge, on l'envoie à l'appli, puis on referme la visionneuse.
    // Dans les conversations chiffrées (/e2ee/) l'image est un blob: valide seulement dans la page : on la renvoie
    // en data: URL pour que l'appli l'enregistre dans un fichier temporaire.
    const string OpenImageScript = """
        (() => {
          const wait = ms => new Promise(r => setTimeout(r, ms));
          const dialogImage = () => [...document.querySelectorAll('[role="dialog"] img')]
            .filter(i => i.complete && i.naturalWidth > 0 && i.getBoundingClientRect().width >= 200)
            .sort((a, b) => b.naturalWidth * b.naturalHeight - a.naturalWidth * a.naturalHeight)[0];

          async function toDataUrl(img) {
            const src = img.currentSrc || img.src;
            if (/^data:/.test(src)) return src;
            try {
              const blob = await (await fetch(src)).blob();
              return await new Promise(ok => { const f = new FileReader(); f.onload = () => ok(f.result); f.readAsDataURL(blob); });
            } catch {
              // fetch peut être bloqué par la CSP de la page : on redessine l'image dans un canvas (blob: = même origine).
              const c = document.createElement('canvas'); c.width = img.naturalWidth; c.height = img.naturalHeight;
              c.getContext('2d').drawImage(img, 0, 0);
              return c.toDataURL('image/jpeg', 0.95);
            }
          }

          async function closeViewer() {
            const viewerOpen = () => !!document.querySelector('[role="dialog"]');
            const button = [...document.querySelectorAll('[role="dialog"] [aria-label]')]
              .find(b => /fermer|close/i.test(b.getAttribute('aria-label')));
            if (button) { button.click(); await wait(300); }
            // Échap une seule fois, et seulement si la visionneuse est encore ouverte : une fois fermée,
            // Échap ferme la conversation.
            if (viewerOpen()) {
              document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', code: 'Escape', keyCode: 27, which: 27, bubbles: true, cancelable: true }));
            }
          }

          async function openFromViewer() {
            const hide = document.createElement('style');
            hide.textContent = '[role="dialog"] { opacity: 0 !important; }';
            document.head.appendChild(hide);
            try {
              let big;
              for (let i = 0; i < 50 && !(big = dialogImage()); i++) await wait(100);
              if (!big) return;
              const src = big.currentSrc || big.src;
              if (/^https:/.test(src)) chrome.webview.postMessage({ url: src });
              else chrome.webview.postMessage({ data: await toDataUrl(big) });
              await closeViewer();
            } catch (err) {
              chrome.webview.postMessage({ error: String(err) });
            } finally {
              setTimeout(() => hide.remove(), 500);
            }
          }

          let reopening = false;
          document.addEventListener('click', e => {
            if (e.shiftKey || e.button !== 0 || reopening) return;
            // L'image peut être recouverte par un calque transparent : on regarde tout ce qui est sous le pointeur.
            const thumb = document.elementsFromPoint(e.clientX, e.clientY)
              .find(el => el instanceof HTMLImageElement && el.getBoundingClientRect().width >= 120 && el.getBoundingClientRect().height >= 120);
            if (!thumb || !thumb.closest('[role="main"]')) return;
            // Un lien autre que le média du message (aperçu de lien...) garde son comportement normal.
            const link = thumb.closest('a[href]');
            if (link && !link.getAttribute('href').startsWith('/messenger_media/')) return;

            if (e.ctrlKey || e.metaKey) {
              // Ctrl+clic : navigateur par défaut. Le clic réel est annulé et remplacé par un clic simple, que la
              // page traite en ouvrant sa visionneuse (rendue invisible par openFromViewer).
              e.preventDefault(); e.stopImmediatePropagation();
              reopening = true;
              thumb.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, button: 0, clientX: e.clientX, clientY: e.clientY }));
              reopening = false;
              openFromViewer();
            } else if (link) {
              // Clic normal : page du média dans une fenêtre à part (comportement du Ctrl+clic d'origine).
              e.preventDefault(); e.stopImmediatePropagation();
              window.open(link.href, '_blank');
            }
          }, true);
        })();
        """;

    // Échap ferme la conversation ouverte (Messenger n'a pas ce raccourci) : navigation interne vers /t/, sans recharger.
    // Rien ne se passe si Échap sert déjà à autre chose : visionneuse ou menu ouvert (vérifié avant que la page
    // traite la touche, sinon on fermerait aussi la conversation en refermant la visionneuse), ou brouillon en cours.
    const string CloseConversationScript = """
        (() => {
          let armed = false;
          const close = () => {
            history.pushState({}, '', '/t/');
            window.dispatchEvent(new PopStateEvent('popstate', { state: {} }));
          };
          // Phase de capture : on décide avant que la page traite la touche.
          window.addEventListener('keydown', e => {
            armed = false;
            if (e.key !== 'Escape' || e.repeat || !/^\/(e2ee\/)?t\/\d+/.test(location.pathname)) return;
            if (document.querySelector('[role="dialog"], [role="menu"], [role="listbox"]')) return;
            const a = document.activeElement;
            if (a?.matches?.('input, textarea, [contenteditable="true"]')) {
              // Zone de saisie : la page y consomme Échap (elle enlève juste le focus). Brouillon en cours : on ne touche à rien.
              if (!(a.value ?? a.textContent ?? '').trim()) close();
              return;
            }
            armed = true;
          }, true);
          // Ailleurs : on ferme seulement si la page n'a pas déjà utilisé la touche.
          window.addEventListener('keydown', e => {
            if (armed && !e.defaultPrevented) close();
            armed = false;
          });
        })();
        """;

    static readonly string ImageTempDir =Path.Combine(Path.GetTempPath(), "MessengerWrapper");

    void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var origin) || !IsInternal(origin)) return;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            if (doc.RootElement.TryGetProperty("url", out var url)
                && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var image)
                && image.Scheme == "https" && (image.Host.Contains("fbcdn") || image.Host.Contains("fbsbx")))
            {
                Log($"image ouverte: {image.Host}{image.AbsolutePath}");
                OpenExternal(image);
            }
            else if (doc.RootElement.TryGetProperty("data", out var data))
            {
                OpenDataImage(data.GetString());
            }
            else if (doc.RootElement.TryGetProperty("dbg", out var dbg))
            {
                Log("clic: " + string.Join(" | ", dbg.EnumerateArray().Select(x => x.GetString())));
            }
            else if (doc.RootElement.TryGetProperty("error", out var error))
            {
                Log($"image (page): {error.GetString()}");
            }
        }
        catch (Exception ex)
        {
            Log($"image: {ex.Message}");
        }
    }

    static void OpenDataImage(string? dataUrl)
    {
        var m = Regex.Match(dataUrl ?? "", @"^data:image/(jpeg|png|gif|webp);base64,(.+)$", RegexOptions.Singleline);
        if (!m.Success || m.Groups[2].Length > 35_000_000) return;

        Directory.CreateDirectory(ImageTempDir);
        foreach (var old in Directory.EnumerateFiles(ImageTempDir, "image-*").Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1)))
            File.Delete(old);

        var ext = m.Groups[1].Value == "jpeg" ? "jpg" : m.Groups[1].Value;
        var path = Path.Combine(ImageTempDir, $"image-{Guid.NewGuid():N}.{ext}");
        File.WriteAllBytes(path, Convert.FromBase64String(m.Groups[2].Value));
        Log($"image ouverte: {path}");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
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
    async Task NotifyFromPageAsync(bool force = false)
    {
        int token = ++scrapeToken;
        await Task.Delay(700);
        if (token != scrapeToken || (ActiveForm == this && !force) || web.CoreWebView2 == null) return;

        var raw = await web.CoreWebView2.ExecuteScriptAsync(
            "JSON.stringify([...document.querySelectorAll('a[href*=\"/t/\"]')].slice(0, 15).map(a => ({ h: a.getAttribute('href'), t: a.innerText, i: (() => { const im = (a.closest('[role=\"row\"], [role=\"gridcell\"], li') ?? a.parentElement?.parentElement ?? a).querySelector('img, image'); return im?.src ?? im?.getAttribute('xlink:href') ?? im?.getAttribute('href'); })() })))");
        Log($"scrape: {raw}");
        if (JsonSerializer.Deserialize<string>(raw) is not { } json) return;
        var rows = JsonSerializer.Deserialize<ThreadRow[]>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        // Les premiers liens /t/ sont des raccourcis ("Discussions · 1 non lus") : une vraie ligne de
        // conversation a plusieurs lignes de texte, et on prend la première qui est non lue.
        var row = rows?.FirstOrDefault(r => r.T.Contains('\n') && (force || UnreadMarker().IsMatch(r.T)));
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
        if (signature == lastSignature && !force) return;
        lastSignature = signature;

        pendingNotification = null;
        ShowToast(sender, preview, row.H, await DownloadAvatarAsync(row.I));
    }

    // Toast Windows natif : avatar de l'expéditeur, un clic ouvre la conversation, et un nouveau message
    // de la même conversation remplace le toast précédent (même Tag).
    static void ShowToast(string title, string? body, string? href, string? avatarPath)
    {
        var builder = new ToastContentBuilder()
            .AddArgument("href", href ?? "")
            .AddText(title);
        if (!string.IsNullOrWhiteSpace(body)) builder.AddText(body);
        if (avatarPath != null) builder.AddAppLogoOverride(new Uri(avatarPath), ToastGenericAppLogoCrop.Circle);
        builder.Show(toast =>
        {
            toast.Group = "messages";
            if (!string.IsNullOrEmpty(href)) toast.Tag = href.Length > 60 ? href[..60] : href;
        });
    }

    void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var args = ToastArguments.Parse(e.Argument);
        args.TryGetValue("href", out var href);
        BeginInvoke(async () =>
        {
            Restore();
            pendingNotification?.ReportClicked();
            pendingNotification = null;
            if (!string.IsNullOrEmpty(href) && web.CoreWebView2 != null)
            {
                // Clic sur le lien de la liste : navigation interne de l'appli, sans recharger la page.
                await web.CoreWebView2.ExecuteScriptAsync(
                    $"document.querySelector('a[href=' + JSON.stringify({JsonSerializer.Serialize(href)}) + ']')?.click()");
            }
        });
    }

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    // L'avatar est téléchargé dans un petit cache : un toast Windows ne peut afficher qu'un fichier local.
    static async Task<string?> DownloadAvatarAsync(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.Host.Contains("fbcdn")) return null;
        try
        {
            var dir = Path.Combine(DataDir, "avatars");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(url))) + ".jpg");
            if (!File.Exists(path)) await File.WriteAllBytesAsync(path, await Http.GetByteArrayAsync(uri));
            return path;
        }
        catch (Exception ex)
        {
            Log($"avatar: {ex.Message}");
            return null;
        }
    }

    record ThreadRow(string H, string T, string? I);

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
        ShowToast(string.IsNullOrWhiteSpace(n.Title) ? "Messenger" : n.Title, n.Body, href: null, avatarPath: null);
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

    // Mode épinglé : colonne étroite collée au bord droit de l'écran, toujours au premier plan.
    // Un second appel restaure la taille, la position et le comportement d'avant.
    void ToggleDock()
    {
        if (!docked)
        {
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            preDockBounds = Bounds;
            preDockTopMost = TopMost;
            var area = Screen.FromControl(this).WorkingArea;
            int width = LogicalToDeviceUnits(DockWidth);
            Bounds = new Rectangle(area.Right - width, area.Top, width, area.Height);
            TopMost = true;
            docked = true;
            MaximizeBox = false;
        }
        else
        {
            docked = false;
            MaximizeBox = true;
            Bounds = preDockBounds;
            TopMost = preDockTopMost;
        }
    }

    // Épinglée, la fenêtre est verrouillée : déplacement, redimensionnement et agrandissement sont refusés
    // (glisser la barre de titre, bords, double-clic sur le titre, Alt+Espace...). Désépingler pour les rétablir.
    const int WmSysCommand = 0x0112, ScSize = 0xF000, ScMove = 0xF010, ScMaximize = 0xF030;

    protected override void WndProc(ref Message m)
    {
        if (docked && m.Msg == WmSysCommand && ((int)m.WParam & 0xFFF0) is ScSize or ScMove or ScMaximize) return;
        base.WndProc(ref m);
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
        ToastNotificationManagerCompat.History.Clear();
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
            // Épinglée, on garde la taille d'avant l'épinglage plutôt que la colonne du bord de l'écran.
            var r = docked ? preDockBounds : WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(BoundsFile, JsonSerializer.Serialize(new SavedBounds(r.X, r.Y, r.Width, r.Height, !docked && WindowState == FormWindowState.Maximized)));
        }
        catch { }
    }

    // --- Barre de titre sombre --------------------------------------------

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Les attributs non supportés par la version de Windows sont simplement ignorés.
        SetDwm(20, 1);                                      // mode sombre (Windows 10 2004+)
        SetDwm(35, ColorTranslator.ToWin32(BackColor));     // couleur de la barre de titre (Windows 11)
        SetDwm(36, ColorTranslator.ToWin32(Color.White));   // couleur du texte du titre (Windows 11)
    }

    void SetDwm(int attribute, int value) => DwmSetWindowAttribute(Handle, attribute, ref value, sizeof(int));

    // --- Démarrage avec Windows -------------------------------------------

    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "MessengerWrapper";

    static bool IsAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) != null;
    }

    // Lancé au démarrage de Windows avec --hidden : l'app démarre réduite dans la zone de notification.
    static void SetAutostart(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key == null) return;
        if (enable) key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\" --hidden");
        else key.DeleteValue(RunValueName, throwOnMissingValue: false);
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
