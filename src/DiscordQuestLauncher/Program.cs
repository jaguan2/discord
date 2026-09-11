using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DiscordQuestLauncher
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length > 0 && args[0] == "--stub")
            {
                Application.Run(new StubForm(args.Length > 1 ? args[1] : "Game"));
                return;
            }
            Application.Run(new MainForm());
        }
    }

    internal sealed class StubForm : Form
    {
        public StubForm(string title)
        {
            Text = title;
            Width = 420;
            Height = 240;
            ShowInTaskbar = true;
            WindowState = FormWindowState.Minimized;
        }
    }

    internal sealed class GameEntry
    {
        public string Name;
        public string Id;
        public readonly List<ExecutableEntry> Executables = new List<ExecutableEntry>();
        public override string ToString() { return Name; }
    }

    internal sealed class ExecutableEntry
    {
        public string Name;
        public bool IsLauncher;
        public override string ToString() { return Name + (IsLauncher ? "  [launcher]" : ""); }
    }

    internal sealed class QueueItem
    {
        public GameEntry Game;
        public ExecutableEntry Executable;
        public override string ToString() { return Game.Name + "  ->  " + Executable.Name.Replace('/', '\\'); }
    }

    internal sealed class WarmButton : Button
    {
        private Color normalColor;
        private Color hoverColor;

        public WarmButton(Color normal, Color hover)
        {
            normalColor = normal;
            hoverColor = hover;
            BackColor = normalColor;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); BackColor = hoverColor; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); BackColor = normalColor; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            using (var path = RoundedRectangle(new Rectangle(0, 0, Width, Height), 9)) Region = new Region(path);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = Enabled ? BackColor : Color.FromArgb(226, 217, 204);
            using (var brush = new SolidBrush(fill)) e.Graphics.FillRectangle(brush, ClientRectangle);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : Color.FromArgb(145, 134, 122), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues)
            {
                var focus = Rectangle.Inflate(ClientRectangle, -4, -4);
                ControlPaint.DrawFocusRectangle(e.Graphics, focus, ForeColor, fill);
            }
        }
        private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class WarmListBox : ListBox
    {
        public WarmListBox()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            ItemHeight = 28;
            IntegralHeight = false;
            BorderStyle = BorderStyle.None;
        }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= Items.Count) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            Color bg = selected ? Color.FromArgb(241, 231, 214) : BackColor;
            Color fg = Color.FromArgb(46, 39, 33);
            using (var brush = new SolidBrush(bg)) e.Graphics.FillRectangle(brush, e.Bounds);
            var textBounds = new Rectangle(e.Bounds.X + 9, e.Bounds.Y, e.Bounds.Width - 14, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, textBounds, fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if ((e.State & DrawItemState.Focus) != 0) ControlPaint.DrawFocusRectangle(e.Graphics, e.Bounds, fg, bg);
        }
    }

    internal sealed class WarmProgressBar : Control
    {
        private int value;
        public int Minimum { get; set; }
        public int Maximum { get; set; }
        public int Value
        {
            get { return value; }
            set { this.value = Math.Max(Minimum, Math.Min(Maximum, value)); Invalidate(); }
        }
        public WarmProgressBar()
        {
            Minimum = 0;
            Maximum = 1000;
            Height = 12;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var track = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var brush = new SolidBrush(Color.FromArgb(232, 217, 190))) e.Graphics.FillRectangle(brush, track);
            double fraction = Maximum == Minimum ? 0 : (Value - Minimum) / (double)(Maximum - Minimum);
            var fill = new Rectangle(0, 0, (int)(track.Width * fraction), track.Height);
            if (fill.Width > 0) using (var brush = new SolidBrush(Color.FromArgb(111, 138, 108))) e.Graphics.FillRectangle(brush, fill);
        }
    }

    internal sealed class MainForm : Form
    {
        private static readonly Color CreamPage = Color.FromArgb(254, 236, 193);
        private static readonly Color CreamCard = Color.FromArgb(255, 253, 248);
        private static readonly Color CreamHover = Color.FromArgb(245, 221, 168);
        private static readonly Color CreamBorder = Color.FromArgb(229, 201, 143);
        private static readonly Color Clay = Color.FromArgb(186, 143, 104);
        private static readonly Color ClayHover = Color.FromArgb(124, 99, 74);
        private static readonly Color Espresso = Color.FromArgb(46, 39, 33);
        private static readonly Color MutedInk = Color.FromArgb(85, 72, 60);
        private static readonly Color Sage = Color.FromArgb(79, 111, 82);
        private const string DetectableUrl = "https://discord.com/api/v9/applications/detectable";
        private readonly string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscordQuestLauncherUI");
        private readonly string cachePath;
        private readonly string historyPath;
        private readonly string gamesDir;
        private readonly List<GameEntry> allGames = new List<GameEntry>();
        private readonly HashSet<string> history = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<QueueItem> queue = new List<QueueItem>();
        private readonly string[] commonGames = { "Marvel Rivals", "Delta Force", "Where Winds Meet", "ARC Raiders", "RuneScape", "Fallout 4", "HELLDIVERS 2", "Arknights: Endfield", "Resident Evil 2", "Umamusume: Pretty Derby" };

        private TextBox searchBox;
        private ComboBox viewBox;
        private ListBox gameList;
        private ComboBox executableBox;
        private ListBox queueList;
        private NumericUpDown minutesBox;
        private Button addButton;
        private Button removeButton;
        private Button startButton;
        private Button stopButton;
        private Button refreshButton;
        private Label statusLabel;
        private WarmProgressBar progress;
        private TextBox logBox;
        private CancellationTokenSource cancellation;
        private Process activeProcess;
        private bool running;

        public MainForm()
        {
            cachePath = Path.Combine(dataDir, "detectable.json");
            historyPath = Path.Combine(dataDir, "history.txt");
            gamesDir = Path.Combine(dataDir, "games");
            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(gamesDir);
            BuildUi();
            LoadHistory();
            Shown += delegate { LoadGames(false); };
            FormClosing += OnFormClosing;
        }

        private void BuildUi()
        {
            Text = "quest launcher";
            Icon = SystemIcons.Application;
            MinimumSize = new Size(900, 610);
            Size = new Size(1050, 680);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = CreamPage;
            ForeColor = Espresso;
            Font = new Font("Segoe UI", 9F);

            var header = new Label { Text = "quest launcher", Font = new Font("Segoe UI Semibold", 20F), AutoSize = true, Location = new Point(18, 12), ForeColor = Espresso, BackColor = Color.Transparent };
            var reminder = new Label { Text = "accept each quest first  •  keep Discord open  •  games run one at a time", AutoSize = true, ForeColor = MutedInk, Location = new Point(21, 54), BackColor = Color.Transparent };
            Controls.Add(header);
            Controls.Add(reminder);

            var split = new SplitContainer { Location = new Point(18, 82), Size = new Size(998, 345), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, SplitterDistance = 485, SplitterWidth = 14, BackColor = CreamPage, BorderStyle = BorderStyle.None };
            split.Panel1.BackColor = CreamCard;
            split.Panel2.BackColor = CreamCard;
            Controls.Add(split);

            searchBox = new TextBox { Location = new Point(10, 11), Width = 290, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, BackColor = Color.White, ForeColor = Espresso, BorderStyle = BorderStyle.FixedSingle };
            searchBox.TextChanged += delegate { ApplyFilter(); };
            viewBox = new ComboBox { Location = new Point(308, 10), Width = 112, DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Top | AnchorStyles.Right, BackColor = CreamCard, ForeColor = Espresso, FlatStyle = FlatStyle.Flat };
            viewBox.Items.AddRange(new object[] { "All games", "Common", "Previously run" });
            viewBox.SelectedIndex = 0;
            viewBox.SelectedIndexChanged += delegate { ApplyFilter(); };
            refreshButton = NewButton("refresh", new Point(426, 9), 50, false);
            refreshButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            refreshButton.Click += delegate { LoadGames(true); };
            gameList = NewListBox(new Point(10, 45), new Size(464, 226));
            gameList.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            gameList.SelectedIndexChanged += OnGameSelected;
            executableBox = new ComboBox { Location = new Point(10, 280), Width = 354, DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, BackColor = CreamCard, ForeColor = Espresso, FlatStyle = FlatStyle.Flat };
            addButton = NewButton("add to queue", new Point(372, 278), 102, true);
            addButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            addButton.Click += AddSelectedGame;
            var hint = new Label { Text = "choose the exact Discord executable before adding", AutoSize = true, ForeColor = MutedInk, Location = new Point(10, 316), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            split.Panel1.Controls.AddRange(new Control[] { searchBox, viewBox, refreshButton, gameList, executableBox, addButton, hint });

            queueList = NewListBox(new Point(10, 11), new Size(483, 260));
            queueList.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            removeButton = NewButton("remove", new Point(10, 278), 76, false);
            removeButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            removeButton.Click += delegate { RemoveSelectedQueueItem(); };
            var minutesLabel = new Label { Text = "Minutes each", AutoSize = true, Location = new Point(108, 284), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            minutesBox = new NumericUpDown { Minimum = 15, Maximum = 120, Value = 17, Width = 55, Location = new Point(189, 280), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            startButton = new WarmButton(Sage, Color.FromArgb(92, 126, 96)) { Text = "start queue", Location = new Point(306, 278), Width = 96, Height = 28, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 9F) };
            startButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            startButton.Click += StartQueue;
            stopButton = NewButton("stop", new Point(410, 278), 83, false);
            stopButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            stopButton.Enabled = false;
            stopButton.Click += delegate { if (cancellation != null) cancellation.Cancel(); };
            var queueHint = new Label { Text = "your queue always runs sequentially", AutoSize = true, ForeColor = MutedInk, Location = new Point(10, 316), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            split.Panel2.Controls.AddRange(new Control[] { queueList, removeButton, minutesLabel, minutesBox, startButton, stopButton, queueHint });

            statusLabel = new Label { Text = "Loading Discord game list...", AutoSize = false, Location = new Point(18, 442), Size = new Size(998, 24), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            statusLabel.ForeColor = Espresso;
            progress = new WarmProgressBar { Location = new Point(18, 469), Size = new Size(998, 12), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Minimum = 0, Maximum = 1000 };
            logBox = new TextBox { Location = new Point(18, 496), Size = new Size(998, 135), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = CreamCard, ForeColor = MutedInk, BorderStyle = BorderStyle.FixedSingle };
            Controls.AddRange(new Control[] { statusLabel, progress, logBox });
        }

        private Button NewButton(string text, Point location, int width, bool primary)
        {
            Color normal = primary ? Clay : CreamHover;
            Color hover = primary ? ClayHover : CreamBorder;
            return new WarmButton(normal, hover) { Text = text, Location = location, Width = width, Height = 28, ForeColor = primary ? Color.White : Espresso, Font = new Font("Segoe UI Semibold", 9F) };
        }

        private ListBox NewListBox(Point location, Size size)
        {
            return new WarmListBox { Location = location, Size = size, BackColor = CreamCard, ForeColor = Espresso };
        }

        private async void LoadGames(bool forceRefresh)
        {
            SetLoading(true);
            try
            {
                string json = null;
                if (!forceRefresh && File.Exists(cachePath) && DateTime.Now - File.GetLastWriteTime(cachePath) < TimeSpan.FromHours(24))
                    json = File.ReadAllText(cachePath, Encoding.UTF8);
                if (json == null)
                {
                    statusLabel.Text = "Downloading Discord's detectable-games list...";
                    json = await Task.Run(delegate
                    {
                        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                        using (var client = new WebClient())
                        {
                            client.Headers.Add(HttpRequestHeader.UserAgent, "DiscordQuestLauncher/1.0");
                            return client.DownloadString(DetectableUrl);
                        }
                    });
                    File.WriteAllText(cachePath, json, new UTF8Encoding(false));
                }
                var parsed = await Task.Run(() => ParseGames(json));
                allGames.Clear();
                allGames.AddRange(parsed);
                ApplyFilter();
                int runnable = allGames.Count(g => g.Executables.Count > 0);
                statusLabel.Text = string.Format("Loaded {0:N0} games; {1:N0} have a Windows executable.", allGames.Count, runnable);
                Log("Game list ready.");
            }
            catch (Exception ex)
            {
                statusLabel.Text = "Could not load Discord's game list.";
                Log("ERROR: " + ex.Message);
                MessageBox.Show(this, ex.Message, "Game list error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { SetLoading(false); }
        }

        private static List<GameEntry> ParseGames(string json)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 };
            var rows = serializer.DeserializeObject(json) as object[];
            var result = new List<GameEntry>();
            if (rows == null) return result;
            foreach (var raw in rows)
            {
                var row = raw as Dictionary<string, object>;
                if (row == null || !row.ContainsKey("name")) continue;
                var game = new GameEntry { Name = Convert.ToString(row["name"]), Id = row.ContainsKey("id") ? Convert.ToString(row["id"]) : "" };
                object executableObject;
                if (row.TryGetValue("executables", out executableObject))
                {
                    var executables = executableObject as object[];
                    if (executables != null)
                    {
                        foreach (var executableRaw in executables)
                        {
                            var executable = executableRaw as Dictionary<string, object>;
                            if (executable == null || !executable.ContainsKey("name")) continue;
                            string os = executable.ContainsKey("os") ? Convert.ToString(executable["os"]) : "";
                            if (!string.Equals(os, "win32", StringComparison.OrdinalIgnoreCase)) continue;
                            bool launcher = executable.ContainsKey("is_launcher") && Convert.ToBoolean(executable["is_launcher"]);
                            game.Executables.Add(new ExecutableEntry { Name = Convert.ToString(executable["name"]), IsLauncher = launcher });
                        }
                    }
                }
                result.Add(game);
            }
            return result.OrderBy(g => g.Name).ToList();
        }

        private void ApplyFilter()
        {
            if (gameList == null) return;
            string query = searchBox.Text.Trim();
            string mode = Convert.ToString(viewBox.SelectedItem);
            IEnumerable<GameEntry> filtered = allGames;
            if (mode == "Common") filtered = filtered.Where(g => commonGames.Any(c => string.Equals(c, g.Name, StringComparison.OrdinalIgnoreCase)));
            if (mode == "Previously run") filtered = filtered.Where(g => history.Contains(g.Id));
            if (query.Length > 0) filtered = filtered.Where(g => g.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
            gameList.BeginUpdate();
            gameList.Items.Clear();
            foreach (var game in filtered.Take(1000)) gameList.Items.Add(game);
            gameList.EndUpdate();
        }

        private void OnGameSelected(object sender, EventArgs e)
        {
            executableBox.Items.Clear();
            var game = gameList.SelectedItem as GameEntry;
            if (game == null) return;
            foreach (var executable in game.Executables.OrderByDescending(e2 => ScoreExecutable(e2, game.Name)).ThenBy(e2 => e2.Name.Length))
                executableBox.Items.Add(executable);
            if (executableBox.Items.Count > 0) executableBox.SelectedIndex = 0;
            addButton.Enabled = executableBox.Items.Count > 0;
            if (game.Executables.Count == 0) statusLabel.Text = game.Name + " registers no Windows executable and cannot be impersonated.";
        }

        private static string NameKey(string text)
        {
            return new string(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        }

        private static int ScoreExecutable(ExecutableEntry executable, string gameName)
        {
            string n = executable.Name.ToLowerInvariant();
            int score = 0;
            string baseName = NameKey(Path.GetFileNameWithoutExtension(n));
            string game = NameKey(gameName);
            if (baseName.Length > 0 && game.Length > 0)
            {
                if (baseName == game) score += 50;
                else if (game.Contains(baseName) || baseName.Contains(game)) score += 25;
            }
            if (n.Contains("shipping")) score += 30;
            if (n.Contains("/")) score += 10;
            if (n.Contains("launcher") || n.Contains("bootstrap") || n.Contains("updater")) score -= 100;
            if (n.Contains("test") || n.Contains("benchmark") || n.Contains("editor") || n.Contains("server") || n.Contains("crash") || n.Contains("unins")) score -= 50;
            if (executable.IsLauncher) score -= 1000;
            return score;
        }

        private void AddSelectedGame(object sender, EventArgs e)
        {
            var game = gameList.SelectedItem as GameEntry;
            var executable = executableBox.SelectedItem as ExecutableEntry;
            if (game == null || executable == null) return;
            queue.Add(new QueueItem { Game = game, Executable = executable });
            RefreshQueue();
        }

        private void RemoveSelectedQueueItem()
        {
            int index = queueList.SelectedIndex;
            if (index < 0 || index >= queue.Count || running) return;
            queue.RemoveAt(index);
            RefreshQueue();
        }

        private void RefreshQueue()
        {
            queueList.Items.Clear();
            foreach (var item in queue) queueList.Items.Add(item);
        }

        private async void StartQueue(object sender, EventArgs e)
        {
            if (running || queue.Count == 0) return;
            if (!Process.GetProcessesByName("Discord").Any() && !Process.GetProcesses().Any(p => p.ProcessName.StartsWith("Discord", StringComparison.OrdinalIgnoreCase)))
            {
                if (MessageBox.Show(this, "Discord does not appear to be running. Continue anyway?", "Discord not found", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            }
            running = true;
            cancellation = new CancellationTokenSource();
            ToggleRunningUi(true);
            var batch = queue.ToList();
            int minutes = Decimal.ToInt32(minutesBox.Value);
            try
            {
                foreach (var item in batch)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    await RunQuest(item, minutes, cancellation.Token);
                    history.Add(item.Game.Id);
                    SaveHistory();
                }
                statusLabel.Text = "Queue complete.";
                Log("Queue complete.");
                queue.Clear();
                RefreshQueue();
            }
            catch (OperationCanceledException)
            {
                statusLabel.Text = "Queue stopped.";
                Log("Queue stopped by user.");
            }
            catch (Exception ex)
            {
                statusLabel.Text = "Queue stopped because of an error.";
                Log("ERROR: " + ex.Message);
                MessageBox.Show(this, ex.Message, "Quest error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                running = false;
                cancellation.Dispose();
                cancellation = null;
                ToggleRunningUi(false);
                progress.Value = 0;
                CleanupEmptyGameDirectories();
            }
        }

        private async Task RunQuest(QueueItem item, int minutes, CancellationToken token)
        {
            string relative = item.Executable.Name.Replace('/', Path.DirectorySeparatorChar);
            string target = Path.GetFullPath(Path.Combine(gamesDir, relative));
            string root = Path.GetFullPath(gamesDir) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Discord returned an unsafe executable path.");
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(Application.ExecutablePath, target, true);
            Process process = null;
            var started = DateTime.Now;
            try
            {
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = target,
                    Arguments = "--stub \"" + item.Game.Name.Replace("\"", "") + "\"",
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(target)
                });
                if (process == null) throw new InvalidOperationException("The game stub could not be started.");
                activeProcess = process;
                Log("Started " + item.Game.Name + " as " + relative);
                statusLabel.Text = "Starting " + item.Game.Name + "...";
                await DelayWithCancellation(10000, token);
                process.Refresh();
                if (process.HasExited) throw new InvalidOperationException(item.Game.Name + " exited immediately; antivirus may have blocked it.");
                if (process.MainWindowHandle == IntPtr.Zero) throw new InvalidOperationException(item.Game.Name + " has no window, so Discord will ignore it.");
                Log("Verified window handle " + process.MainWindowHandle + " for " + item.Game.Name + ".");
                var duration = TimeSpan.FromMinutes(minutes);
                while (DateTime.Now - started < duration)
                {
                    token.ThrowIfCancellationRequested();
                    if (process.HasExited)
                    {
                        double elapsed = (DateTime.Now - started).TotalMinutes;
                        if (elapsed < 15) throw new InvalidOperationException(item.Game.Name + " exited after only " + elapsed.ToString("N1") + " minutes.");
                        break;
                    }
                    TimeSpan remaining = duration - (DateTime.Now - started);
                    statusLabel.Text = item.Game.Name + " — " + remaining.ToString(@"mm\:ss") + " remaining";
                    progress.Value = Math.Max(0, Math.Min(1000, (int)((DateTime.Now - started).TotalMilliseconds / duration.TotalMilliseconds * 1000)));
                    await DelayWithCancellation(1000, token);
                }
                Log("Completed " + item.Game.Name + " after " + (DateTime.Now - started).TotalMinutes.ToString("N1") + " minutes.");
            }
            finally
            {
                if (process != null)
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    process.Dispose();
                }
                activeProcess = null;
                Thread.Sleep(300);
                try { if (File.Exists(target)) File.Delete(target); } catch (Exception ex) { Log("Cleanup warning: " + ex.Message); }
            }
        }

        private static Task DelayWithCancellation(int milliseconds, CancellationToken token)
        {
            return Task.Delay(milliseconds, token);
        }

        private void LoadHistory()
        {
            if (!File.Exists(historyPath)) return;
            foreach (string id in File.ReadAllLines(historyPath)) if (!string.IsNullOrWhiteSpace(id)) history.Add(id.Trim());
        }

        private void SaveHistory()
        {
            File.WriteAllLines(historyPath, history.OrderBy(x => x).ToArray());
        }

        private void CleanupEmptyGameDirectories()
        {
            if (!Directory.Exists(gamesDir)) return;
            foreach (string dir in Directory.GetDirectories(gamesDir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            {
                try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
            }
        }

        private void SetLoading(bool loading)
        {
            refreshButton.Enabled = !loading && !running;
            searchBox.Enabled = !loading && !running;
            viewBox.Enabled = !loading && !running;
        }

        private void ToggleRunningUi(bool value)
        {
            startButton.Enabled = !value;
            stopButton.Enabled = value;
            addButton.Enabled = !value;
            removeButton.Enabled = !value;
            refreshButton.Enabled = !value;
            minutesBox.Enabled = !value;
        }

        private void Log(string message)
        {
            logBox.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine);
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!running) return;
            if (MessageBox.Show(this, "A quest is running. Stop it and close?", "Queue active", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.No)
            {
                e.Cancel = true;
                return;
            }
            if (cancellation != null) cancellation.Cancel();
            if (activeProcess != null)
            {
                try { if (!activeProcess.HasExited) activeProcess.Kill(); } catch { }
            }
        }
    }
}
