using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Licznik odwiedzin")]
[assembly: AssemblyProduct("Licznik odwiedzin")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace LicznikOdwiedzin
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--selftest")
            {
                return SelfTest.Run(args[1]);
            }
            bool preview = args.Length == 1 && args[0] == "--podglad";

            bool first;
            using (var mutex = new Mutex(true, "Local\\LicznikOdwiedzin", out first))
            {
                if (!first)
                {
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                Config config = preview ? new Config { Host = "mojastrona.pl" } : Config.Load();
                if (config == null)
                {
                    config = SetupForm.Ask(null);
                    if (config == null)
                    {
                        return 0;
                    }
                }
                using (var app = new CounterApp(config, preview))
                {
                    Application.Run(app);
                }
                GC.KeepAlive(mutex);
            }
            return 0;
        }
    }

    internal sealed class Config
    {
        public string AccountId;
        public string Token;
        public string Host;

        private static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LicznikOdwiedzin"); }
        }

        private static string FilePath
        {
            get { return Path.Combine(Dir, "konfiguracja.dat"); }
        }

        public static string Protect(string token)
        {
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(data);
        }

        public static string Unprotect(string stored)
        {
            byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }

        public static Config Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return null;
                }
                var raw = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath, Encoding.UTF8));
                return new Config { AccountId = raw["konto"], Host = raw["domena"], Token = Unprotect(raw["klucz"]) };
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            var raw = new Dictionary<string, string> { { "konto", AccountId }, { "domena", Host }, { "klucz", Protect(Token) } };
            File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(raw), new UTF8Encoding(false));
        }

        public static void Delete()
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
    }

    internal static class Autostart
    {
        private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string Name = "LicznikOdwiedzin";

        public static bool Enabled
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    return k != null && k.GetValue(Name) != null;
                }
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Key))
                {
                    if (value)
                    {
                        k.SetValue(Name, "\"" + Application.ExecutablePath + "\"");
                    }
                    else
                    {
                        k.DeleteValue(Name, false);
                    }
                }
            }
        }
    }

    internal sealed class PageCount
    {
        public string Path;
        public long Views;
    }

    internal sealed class Stats
    {
        public long Visits;
        public long Views;
        public List<PageCount> Pages = new List<PageCount>();
    }

    internal sealed class ApiException : Exception
    {
        public ApiException(string message) : base(message) { }
    }

    internal static class Api
    {
        private const string Url = "https://api.cloudflare.com/client/v4/graphql";
        private const string Query =
            "query($account:String!,$filter:AccountRumPageloadEventsAdaptiveGroupsFilter_InputObject!){" +
            "viewer{accounts(filter:{accountTag:$account}){" +
            "total:rumPageloadEventsAdaptiveGroups(limit:1,filter:$filter){count sum{visits}}" +
            "pages:rumPageloadEventsAdaptiveGroups(limit:5,filter:$filter,orderBy:[count_DESC]){count dimensions{requestPath}}" +
            "}}}";

        public static string Body(string accountId, string host, DateTime utcNow)
        {
            var filter = new Dictionary<string, object>
            {
                { "datetime_geq", utcNow.AddHours(-24).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) },
                { "datetime_leq", utcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) },
                { "requestHost", host },
            };
            var body = new Dictionary<string, object>
            {
                { "query", Query },
                { "variables", new Dictionary<string, object> { { "account", accountId }, { "filter", filter } } },
            };
            return new JavaScriptSerializer().Serialize(body);
        }

        public static Stats Parse(string json)
        {
            var root = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.DeserializeObject(json) as IDictionary<string, object>;
            if (root == null)
            {
                throw new ApiException("Nieznana odpowiedź Cloudflare");
            }
            object errors;
            if (root.TryGetValue("errors", out errors) && errors is object[] && ((object[])errors).Length > 0)
            {
                var first = ((object[])errors)[0] as IDictionary<string, object>;
                object message;
                string text = first != null && first.TryGetValue("message", out message) ? Convert.ToString(message, CultureInfo.InvariantCulture) : "";
                throw new ApiException(text.IndexOf("not authorized", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "Klucz nie ma uprawnień do analityki"
                    : "Błąd Cloudflare: " + text);
            }
            object[] accounts = Path(root, "data", "viewer", "accounts") as object[];
            if (accounts == null || accounts.Length == 0)
            {
                throw new ApiException("Nie znaleziono konta Cloudflare");
            }
            var account = (IDictionary<string, object>)accounts[0];
            var stats = new Stats();
            var total = account["total"] as object[];
            if (total != null && total.Length > 0)
            {
                var row = (IDictionary<string, object>)total[0];
                stats.Views = Convert.ToInt64(row["count"], CultureInfo.InvariantCulture);
                stats.Visits = Convert.ToInt64(((IDictionary<string, object>)row["sum"])["visits"], CultureInfo.InvariantCulture);
            }
            var pages = account["pages"] as object[];
            if (pages != null)
            {
                foreach (IDictionary<string, object> row in pages)
                {
                    var dims = (IDictionary<string, object>)row["dimensions"];
                    stats.Pages.Add(new PageCount
                    {
                        Path = Convert.ToString(dims["requestPath"], CultureInfo.InvariantCulture),
                        Views = Convert.ToInt64(row["count"], CultureInfo.InvariantCulture),
                    });
                }
            }
            return stats;
        }

        private static object Path(IDictionary<string, object> root, params string[] keys)
        {
            object current = root;
            foreach (string key in keys)
            {
                var map = current as IDictionary<string, object>;
                if (map == null || !map.TryGetValue(key, out current))
                {
                    return null;
                }
            }
            return current;
        }

        public static Stats Fetch(Config config)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(Url);
                request.Method = "POST";
                request.Timeout = 20000;
                request.ReadWriteTimeout = 20000;
                request.ContentType = "application/json";
                request.UserAgent = "LicznikOdwiedzin/1.0";
                request.Headers["Authorization"] = "Bearer " + config.Token;
                byte[] body = Encoding.UTF8.GetBytes(Body(config.AccountId, config.Host, DateTime.UtcNow));
                using (Stream stream = request.GetRequestStream())
                {
                    stream.Write(body, 0, body.Length);
                }
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    return Parse(reader.ReadToEnd());
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response == null)
                {
                    throw new ApiException("Brak połączenia z internetem");
                }
                int code = (int)response.StatusCode;
                throw new ApiException(code == 401 || code == 403 ? "Nieprawidłowy klucz Cloudflare" : "Błąd serwera " + code);
            }
        }
    }

    internal static class Text
    {
        private static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");

        public static string Visits(long n)
        {
            long d = n % 10;
            long h = n % 100;
            return n == 1 || (d >= 2 && d <= 4 && (h < 12 || h > 14)) ? "odwiedziny" : "odwiedzin";
        }

        public static string Number(long n)
        {
            return n.ToString("#,0", Pl);
        }

        public static string TrayNumber(long n)
        {
            if (n < 1000)
            {
                return n.ToString(CultureInfo.InvariantCulture);
            }
            if (n < 10000)
            {
                return (n / 1000) + "k+";
            }
            return "9k+";
        }

        public static string CleanHost(string input)
        {
            string host = input.Trim().ToLowerInvariant();
            int scheme = host.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0)
            {
                host = host.Substring(scheme + 3);
            }
            int slash = host.IndexOf('/');
            return slash >= 0 ? host.Substring(0, slash) : host;
        }

        public static string PageName(string path)
        {
            return string.IsNullOrEmpty(path) || path == "/" ? "Strona główna" : path;
        }
    }

    internal sealed class LimitsPlacement
    {
        public string ScreenName = "";
        public int Offset = 12;

        private static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LimityClaude", "ustawienia.json"); }
        }

        public static DateTime Stamp()
        {
            return File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
        }

        public static LimitsPlacement Parse(string json)
        {
            var result = new LimitsPlacement();
            var root = new JavaScriptSerializer().DeserializeObject(json) as IDictionary<string, object>;
            if (root == null)
            {
                return result;
            }
            object value;
            if (root.TryGetValue("ScreenName", out value) && value != null)
            {
                result.ScreenName = Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            if (root.TryGetValue("StripOffset", out value) && value != null)
            {
                result.Offset = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            return result;
        }

        public static LimitsPlacement Load()
        {
            try
            {
                return File.Exists(FilePath) ? Parse(File.ReadAllText(FilePath, Encoding.UTF8)) : new LimitsPlacement();
            }
            catch (Exception)
            {
                return new LimitsPlacement();
            }
        }
    }

    internal sealed class StripColors
    {
        public Color Key, Text, Dim;

        public static StripColors Current()
        {
            bool light = false;
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                if (k != null)
                {
                    object v = k.GetValue("SystemUsesLightTheme");
                    light = v is int && (int)v == 1;
                }
            }
            return light
                ? new StripColors { Key = Color.FromArgb(238, 240, 243), Text = Color.FromArgb(32, 32, 32), Dim = Color.FromArgb(96, 96, 96) }
                : new StripColors { Key = Color.FromArgb(31, 31, 31), Text = Color.FromArgb(232, 232, 232), Dim = Color.FromArgb(155, 155, 155) };
        }
    }

    internal sealed class StripForm : Form
    {
        private const int WsExToolWindow = 0x80;
        private const int WsExNoActivate = 0x08000000;
        private readonly CounterApp app;
        private readonly Font regular = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        private readonly Font bold = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point);

        public StripColors Colors { get; set; }

        public StripForm(CounterApp app)
        {
            this.app = app;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams p = base.CreateParams;
                p.ExStyle |= WsExToolWindow | WsExNoActivate;
                return p;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                app.Toggle();
            }
            else if (e.Button == MouseButtons.Right)
            {
                app.ShowMenu(Control.MousePosition);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float s = g.DpiX / 96f;
            int lineH = (int)(18 * s);
            int top = (Height - 2 * lineH) / 2;
            using (var dim = new SolidBrush(Colors.Dim))
            using (var main = new SolidBrush(app.Stale || app.Stats == null ? Colors.Dim : Colors.Text))
            {
                g.DrawString(app.Host, regular, dim, 0, top);
                string value = app.Stats == null
                    ? (app.Error != null ? "brak danych" : "ładowanie…")
                    : Text_.Number(app.Stats.Visits) + " " + Text_.VisitsWord(app.Stats.Visits);
                g.DrawString(value, bold, main, 0, top + lineH);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                regular.Dispose();
                bold.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly TextBox domain = new TextBox();
        private readonly TextBox account = new TextBox();
        private readonly TextBox token = new TextBox { UseSystemPasswordChar = true };
        private readonly CheckBox start = new CheckBox { Text = "Uruchamiaj razem z Windowsem", AutoSize = true, Checked = true };
        private readonly Label status = new Label { AutoSize = false, ForeColor = Color.Firebrick };
        private readonly Button save = new Button { Text = "Zapisz" };

        private SetupForm(Config current)
        {
            Text = "Licznik odwiedzin – konfiguracja";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(460, 334);

            Controls.Add(new Label { Text = "Adres strony (np. mojastrona.pl):", Location = new Point(18, 16), AutoSize = true });
            domain.SetBounds(18, 40, 424, 26);
            Controls.Add(domain);
            Controls.Add(new Label { Text = "Identyfikator konta Cloudflare (Account ID):", Location = new Point(18, 80), AutoSize = true });
            account.SetBounds(18, 104, 424, 26);
            Controls.Add(account);
            Controls.Add(new Label { Text = "Klucz API z uprawnieniem Account Analytics: Read:", Location = new Point(18, 144), AutoSize = true });
            token.SetBounds(18, 168, 424, 26);
            Controls.Add(token);
            start.Location = new Point(18, 206);
            Controls.Add(start);
            status.SetBounds(18, 234, 424, 42);
            Controls.Add(status);
            save.SetBounds(358, 288, 84, 30);
            save.Click += OnSave;
            Controls.Add(save);
            AcceptButton = save;

            if (current != null)
            {
                domain.Text = current.Host;
                account.Text = current.AccountId;
            }
        }

        private Config Result { get; set; }

        private void OnSave(object sender, EventArgs e)
        {
            string host = Text_.Host(domain.Text);
            string id = account.Text.Trim();
            string key = token.Text.Trim();
            if (host.Length == 0 || id.Length == 0 || key.Length == 0)
            {
                status.Text = "Uzupełnij wszystkie pola.";
                return;
            }
            var candidate = new Config { Host = host, AccountId = id, Token = key };
            save.Enabled = false;
            status.ForeColor = Color.DimGray;
            status.Text = "Sprawdzam połączenie…";
            status.Refresh();
            try
            {
                Api.Fetch(candidate);
                candidate.Save();
                Autostart.Enabled = start.Checked;
                Result = candidate;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (ApiException ex)
            {
                status.ForeColor = Color.Firebrick;
                status.Text = ex.Message;
                save.Enabled = true;
            }
        }

        public static Config Ask(Config current)
        {
            using (var form = new SetupForm(current))
            {
                return form.ShowDialog() == DialogResult.OK ? form.Result : null;
            }
        }
    }

    internal sealed class PopupForm : Form
    {
        private readonly CounterApp app;
        private readonly Font numberFont = new Font("Segoe UI", 30f, FontStyle.Bold);
        private readonly Font titleFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        private readonly Font smallFont = new Font("Segoe UI", 9f);
        private readonly Button refresh = new Button { Text = "Odśwież", FlatStyle = FlatStyle.Flat };
        private readonly Color back = Color.FromArgb(32, 32, 32);
        private readonly Color text = Color.FromArgb(235, 235, 235);
        private readonly Color dim = Color.FromArgb(160, 160, 160);

        public PopupForm(CounterApp app)
        {
            this.app = app;
            Text = "Odwiedziny: " + app.Host;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            BackColor = back;
            ClientSize = new Size(330, 300);
            refresh.ForeColor = text;
            refresh.BackColor = Color.FromArgb(70, 70, 70);
            refresh.FlatAppearance.BorderSize = 0;
            refresh.SetBounds(232, 258, 84, 28);
            refresh.Click += delegate { app.RefreshNow(); };
            Controls.Add(refresh);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var bright = new SolidBrush(app.Stale ? dim : text))
            using (var soft = new SolidBrush(dim))
            using (var accent = new SolidBrush(Color.FromArgb(245, 192, 0)))
            using (var error = new SolidBrush(Color.FromArgb(240, 125, 105)))
            {
                Stats stats = app.Stats;
                g.DrawString(stats == null ? "–" : Text_.Number(stats.Visits), numberFont, accent, 12, 8);
                g.DrawString("odwiedzin w ostatnich 24 godzinach", smallFont, soft, 16, 64);
                if (stats != null)
                {
                    g.DrawString("Wyświetlenia stron: " + Text_.Number(stats.Views), smallFont, bright, 16, 86);
                    g.DrawString("Najczęściej oglądane", titleFont, bright, 16, 116);
                    int y = 140;
                    using (var right = new StringFormat { Alignment = StringAlignment.Far })
                    using (var clip = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                    {
                        foreach (PageCount page in stats.Pages)
                        {
                            g.DrawString(Text_.Page(page.Path), smallFont, soft, new RectangleF(16, y, 240, 20), clip);
                            g.DrawString(Text_.Number(page.Views), smallFont, bright, new RectangleF(250, y, 64, 20), right);
                            y += 21;
                        }
                    }
                }
                g.DrawString(app.StatusLine(), smallFont, app.Error != null ? error : soft, new RectangleF(16, 258, 210, 36));
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                numberFont.Dispose();
                titleFont.Dispose();
                smallFont.Dispose();
                refresh.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal static class Text_
    {
        public static string Number(long n) { return Text.Number(n); }
        public static string Page(string path) { return Text.PageName(path); }
        public static string VisitsWord(long n) { return Text.Visits(n); }
        public static string Host(string input) { return Text.CleanHost(input); }
    }

    internal sealed class CounterApp : ApplicationContext
    {
        private const int RefreshMinutes = 5;
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly PopupForm popup;
        private readonly StripForm strip;
        private readonly System.Windows.Forms.Timer top = new System.Windows.Forms.Timer { Interval = 500 };
        private readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = RefreshMinutes * 60000 };
        private readonly SynchronizationContext ui;
        private Config config;
        private IntPtr iconHandle = IntPtr.Zero;
        private readonly bool preview;
        private bool busy;
        private DateTime lastOk;
        private DateTime limitsStamp;
        private Screen screen = Screen.PrimaryScreen;
        private int ticks;

        public Stats Stats { get; private set; }
        public string Host { get { return config.Host; } }
        public string Error { get; private set; }
        public bool Stale { get; private set; }

        public CounterApp(Config config, bool preview)
        {
            this.config = config;
            this.preview = preview;
            popup = new PopupForm(this);
            strip = new StripForm(this);
            IntPtr handle = popup.Handle;
            IntPtr stripHandle = strip.Handle;
            ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(ui);

            menu.Items.Add("Pokaż", null, delegate { Toggle(); });
            menu.Items.Add("Odśwież teraz", null, delegate { RefreshNow(); });
            var auto = new ToolStripMenuItem("Uruchamiaj z Windowsem") { Checked = Autostart.Enabled };
            auto.Click += delegate { Autostart.Enabled = !Autostart.Enabled; auto.Checked = Autostart.Enabled; };
            menu.Items.Add(auto);
            menu.Items.Add("Zmień klucz…", null, delegate { Reconfigure(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Zamknij", null, delegate { ExitThread(); });
            tray.ContextMenuStrip = menu;
            tray.Text = "Licznik odwiedzin";
            tray.Visible = true;
            tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    Toggle();
                }
            };
            UpdateIcon("…", Color.Gray);
            poll.Tick += delegate { RefreshAsync(); };
            top.Tick += delegate { OnTop(); };
            SystemEvents.PowerModeChanged += OnPower;
            SystemEvents.DisplaySettingsChanged += OnDisplay;
            SystemEvents.UserPreferenceChanged += OnPreference;
            Place();
            top.Start();
            if (preview)
            {
                Stats = new Stats { Visits = 37, Views = 120 };
                strip.Invalidate();
                var close = new System.Windows.Forms.Timer { Interval = 8000 };
                close.Tick += delegate { close.Stop(); ExitThread(); };
                close.Start();
            }
            else
            {
                poll.Start();
                RefreshAsync();
            }
            GC.KeepAlive(handle);
            GC.KeepAlive(stripHandle);
        }

        public void ShowMenu(Point at)
        {
            menu.Show(at);
        }

        private void Place()
        {
            LimitsPlacement limits = LimitsPlacement.Load();
            limitsStamp = LimitsPlacement.Stamp();
            screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == limits.ScreenName) ?? Screen.PrimaryScreen;
            strip.Colors = StripColors.Current();
            strip.BackColor = strip.Colors.Key;
            strip.TransparencyKey = strip.Colors.Key;
            float scale;
            using (Graphics g = strip.CreateGraphics())
            {
                scale = g.DpiX / 96f;
            }
            Rectangle b = screen.Bounds;
            Rectangle w = screen.WorkingArea;
            int bottomBar = b.Bottom - w.Bottom;
            int topBar = w.Top - b.Top;
            bool atTop = topBar > bottomBar;
            int height = atTop ? topBar : bottomBar;
            if (height <= 0)
            {
                height = 48;
            }
            int width = (int)(128 * scale);
            int x = b.Left + limits.Offset + (int)(252 * scale) + (int)(14 * scale);
            x = Math.Max(b.Left, Math.Min(b.Right - width, x));
            strip.SetBounds(x, atTop ? b.Top : b.Bottom - height, width, height);
            if (!strip.Visible)
            {
                strip.Show();
            }
            strip.Invalidate();
        }

        private void OnTop()
        {
            ticks++;
            if (ticks % 10 == 0 && LimitsPlacement.Stamp() != limitsStamp)
            {
                Place();
            }
            bool full = NativeMethods.FullscreenOn(screen.Bounds);
            if (full && strip.Visible)
            {
                strip.Hide();
            }
            else if (!full)
            {
                if (!strip.Visible)
                {
                    strip.Show();
                }
                NativeMethods.KeepTopmost(strip.Handle);
            }
        }

        private void OnDisplay(object sender, EventArgs e)
        {
            Place();
        }

        private void OnPreference(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color)
            {
                Place();
            }
        }

        public string StatusLine()
        {
            string updated = lastOk == default(DateTime) ? "" : "Aktualizacja: " + lastOk.ToString("HH:mm:ss");
            return Error == null ? (updated.Length > 0 ? updated : "Ładowanie…") : Error + (updated.Length > 0 ? "\n" + updated : "");
        }

        public void RefreshNow()
        {
            RefreshAsync();
        }

        public void Toggle()
        {
            if (popup.Visible)
            {
                popup.Hide();
                return;
            }
            Rectangle w = screen.WorkingArea;
            int x = Math.Max(w.Left + 8, Math.Min(w.Right - popup.Width - 8, strip.Left));
            popup.Location = new Point(x, w.Bottom - popup.Height - 8);
            popup.Invalidate();
            popup.Show();
            popup.Activate();
        }

        private void Reconfigure()
        {
            Config updated = SetupForm.Ask(config);
            if (updated != null)
            {
                config = updated;
                RefreshAsync();
            }
        }

        private void RefreshAsync()
        {
            if (busy)
            {
                return;
            }
            busy = true;
            Config current = config;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Stats result = null;
                string error = null;
                try
                {
                    result = Api.Fetch(current);
                }
                catch (ApiException ex)
                {
                    error = ex.Message;
                }
                catch (Exception)
                {
                    error = "Nieoczekiwany błąd odczytu";
                }
                ui.Post(delegate { OnFetched(result, error); }, null);
            });
        }

        private void OnFetched(Stats result, string error)
        {
            busy = false;
            Error = error;
            if (result != null)
            {
                Stats = result;
                Stale = false;
                lastOk = DateTime.Now;
                UpdateIcon(Text.TrayNumber(result.Visits), Color.FromArgb(44, 26, 0));
                string text = Text.Number(result.Visits) + " " + Text.Visits(result.Visits) + " (24 h)";
                tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
            }
            else
            {
                Stale = Stats != null;
                if (Stats == null)
                {
                    UpdateIcon("?", Color.Gray);
                }
                tray.Text = "Licznik: brak aktualnych danych";
            }
            strip.Invalidate();
            popup.Invalidate();
        }

        private void UpdateIcon(string text, Color color)
        {
            using (var bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            using (var brush = new SolidBrush(color))
            using (var font = new Font("Segoe UI", text.Length >= 3 ? 12f : 17f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                g.FillRectangle(brush, 0, 0, 32, 32);
                g.DrawString(text, font, Brushes.White, new RectangleF(0, 0, 32, 32), format);
                IntPtr handle = bmp.GetHicon();
                using (Icon icon = Icon.FromHandle(handle))
                {
                    tray.Icon = (Icon)icon.Clone();
                }
                if (iconHandle != IntPtr.Zero)
                {
                    NativeMethods.DestroyIcon(iconHandle);
                }
                iconHandle = handle;
            }
        }

        private void OnPower(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                RefreshAsync();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.PowerModeChanged -= OnPower;
                SystemEvents.DisplaySettingsChanged -= OnDisplay;
                SystemEvents.UserPreferenceChanged -= OnPreference;
                poll.Dispose();
                top.Dispose();
                strip.Dispose();
                tray.Visible = false;
                tray.Dispose();
                menu.Dispose();
                popup.Dispose();
                if (iconHandle != IntPtr.Zero)
                {
                    NativeMethods.DestroyIcon(iconHandle);
                    iconHandle = IntPtr.Zero;
                }
            }
            base.Dispose(disposing);
        }
    }

    internal static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr handle);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        public static void KeepTopmost(IntPtr hWnd)
        {
            SetWindowPos(hWnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }

        public static bool FullscreenOn(Rectangle screen)
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero)
            {
                return false;
            }
            var name = new StringBuilder(256);
            GetClassName(window, name, 256);
            string cls = name.ToString();
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
            {
                return false;
            }
            RECT r;
            return GetWindowRect(window, out r) && r.Left <= screen.Left && r.Top <= screen.Top && r.Right >= screen.Right && r.Bottom >= screen.Bottom;
        }
    }

    internal static class SelfTest
    {
        public static int Run(string outFile)
        {
            var log = new StringBuilder();
            int failed = 0;
            Action<string, bool> check = (name, ok) =>
            {
                log.AppendLine((ok ? "OK    " : "BLAD  ") + name);
                if (!ok)
                {
                    failed++;
                }
            };

            const string good = "{\"data\":{\"viewer\":{\"accounts\":[{\"total\":[{\"count\":120,\"sum\":{\"visits\":37}}],\"pages\":[{\"count\":80,\"dimensions\":{\"requestPath\":\"/\"}},{\"count\":40,\"dimensions\":{\"requestPath\":\"/produkty/\"}}]}]}},\"errors\":null}";
            Stats s = Api.Parse(good);
            check("odwiedziny", s.Visits == 37);
            check("wyswietlenia", s.Views == 120);
            check("dwie podstrony", s.Pages.Count == 2 && s.Pages[1].Path == "/produkty/");

            const string empty = "{\"data\":{\"viewer\":{\"accounts\":[{\"total\":[],\"pages\":[]}]}},\"errors\":null}";
            Stats z = Api.Parse(empty);
            check("brak ruchu = 0", z.Visits == 0 && z.Views == 0 && z.Pages.Count == 0);

            bool denied = false;
            try { Api.Parse("{\"data\":null,\"errors\":[{\"message\":\"not authorized for that account\"}]}"); }
            catch (ApiException ex) { denied = ex.Message.Contains("uprawnień"); }
            check("brak uprawnien", denied);

            bool noAccount = false;
            try { Api.Parse("{\"data\":{\"viewer\":{\"accounts\":[]}},\"errors\":null}"); }
            catch (ApiException) { noAccount = true; }
            check("brak konta", noAccount);

            string body = Api.Body("abc", "mojastrona.pl", new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc));
            check("okres 24 h", body.Contains("2026-11-04T12:00:00Z") && body.Contains("2026-11-05T12:00:00Z"));
            check("filtr domeny", body.Contains("mojastrona.pl"));
            check("konto w zapytaniu", body.Contains("\"account\":\"abc\""));

            string stored = Config.Protect("tajny-klucz");
            check("klucz zaszyfrowany", !stored.Contains("tajny") && !Encoding.UTF8.GetString(Convert.FromBase64String(stored)).Contains("tajny"));
            check("klucz odczytany", Config.Unprotect(stored) == "tajny-klucz");

            check("liczba na ikonie", Text.TrayNumber(999) == "999" && Text.TrayNumber(1500) == "1k+" && Text.TrayNumber(12000) == "9k+");
            check("oczyszczanie adresu", Text.CleanHost(" https://MojaStrona.pl/produkty/ ") == "mojastrona.pl");
            check("nazwa strony glownej", Text.PageName("/") == "Strona główna");
            check("odmiana", Text.Visits(1) == "odwiedziny" && Text.Visits(3) == "odwiedziny" && Text.Visits(5) == "odwiedzin" && Text.Visits(12) == "odwiedzin" && Text.Visits(22) == "odwiedziny");
            LimitsPlacement lp = LimitsPlacement.Parse("{\"ScreenName\":\"\\\\\\\\.\\\\DISPLAY1\",\"StripOffset\":300}");
            check("polozenie limitow", lp.Offset == 300 && lp.ScreenName == "\\\\.\\DISPLAY1");
            check("domyslne polozenie", LimitsPlacement.Parse("{}").Offset == 12);

            log.AppendLine(failed == 0 ? "WYNIK: wszystko OK" : "WYNIK: bledow " + failed);
            File.WriteAllText(outFile, log.ToString(), new UTF8Encoding(false));
            return failed == 0 ? 0 : 1;
        }
    }
}
