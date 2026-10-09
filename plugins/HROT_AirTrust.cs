// HROT AirTrust — native Mission Planner plugin.
// Deploy as plugins/HROT_AirTrust.cs beside MissionPlanner.exe.
// Native Flight Data tab and GMap overlay; no replacement browser UI.
// Requires an explicitly launched local HROT Core API; NEVER fabricates live tracks.
using System;
using System.Drawing;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using GMap.NET;
using GMap.NET.WindowsForms;
using Newtonsoft.Json.Linq;
using MissionPlanner.Utilities;

namespace HROTAirTrust
{
    public sealed class AirTrustPlugin : MissionPlanner.Plugin.Plugin
    {
        private TabPage page;
        private ToolStripButton launchButton;
        private System.Windows.Forms.Timer timer;
        private GMapOverlay overlay;
        private DataGridView tracks, missions, identities, incidents, evidence, sensors;
        private Label statusLabel, countsLabel, detailLabel, integrityLabel;
        private CheckBox showMap;
        private bool polling, disposed;
        private readonly string baseUrl = "http://127.0.0.1:8765";
        private const string Guidance = "KEY POSSESSION is not AIRFRAME BINDING. " +
            "Aircraft source data and physical association require independent verification.";
        public override string Name { get { return "HROT AirTrust"; } }
        public override string Version { get { return "0.2.0-native"; } }
        public override string Author { get { return "HROT Research"; } }
        public override bool Init() { return true; }

        public override bool Loaded()
        {
            if (Host.MainForm == null || Host.MainForm.FlightData == null)
                return false;

            page = new TabPage("HROT AirTrust");
            page.Name = "tabHROTAirTrust";
            page.Padding = new Padding(7);
            BuildNativePage();
            // Exactly the extension mechanism already used by upstream OpenDroneID.
            Host.MainForm.FlightData.TabListOriginal.Add(page);
            Host.MainForm.FlightData.tabControlactions.TabPages.Add(page);
            ThemeManager.ApplyThemeTo(page);

            launchButton = new ToolStripButton("HROT TRUST");
            launchButton.Name = "MenuHROTAirTrust";
            launchButton.ToolTipText = "HROT AirTrust — observer identity and trust console";
            launchButton.TextAlign = ContentAlignment.BottomCenter;
            launchButton.Click += LaunchClick;
            var menu = Host.MainForm.MainMenu.Items;
            var flight = menu.IndexOfKey("MenuFlightData");
            if (flight >= 0) menu.Insert(flight + 1, launchButton);
            else menu.Add(launchButton);

            try
            {
                overlay = new GMapOverlay("HROT_Observed_External_Aircraft");
                Host.FDGMapControl.Overlays.Add(overlay);
            }
            catch (Exception ex)
            {
                SetStatus("Map overlay unavailable: " + ex.Message);
            }

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 5000;
            timer.Tick += (s, e) => { if (page != null && page.Visible) RefreshSnapshot(); };
            timer.Start();
            RefreshSnapshot();
            return true;
        }

        private static DataGridView Grid(params string[] columnNames)
        {
            var g = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                RowHeadersVisible = false, AutoGenerateColumns = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle
            };
            foreach (var name in columnNames)
                g.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = name, Name = name,
                    SortMode = DataGridViewColumnSortMode.NotSortable
                });
            return g;
        }

        private static void Tab(TabControl tabs, string caption, Control content)
        {
            var child = new TabPage(caption);
            child.Controls.Add(content);
            tabs.TabPages.Add(child);
        }

        private static Button Button(string text, EventHandler handler)
        {
            var b = new Button { Text = text, AutoSize = true, Margin = new Padding(3, 6, 3, 2) };
            b.Click += handler;
            return b;
        }

        private void BuildNativePage()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            page.Controls.Add(layout);

            var headline = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            headline.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
            headline.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            headline.Controls.Add(new Label {
                Dock = DockStyle.Fill, Text = "HROT AIRTRUST  |  OBSERVER SECURITY",
                Font = new Font(SystemFonts.DefaultFont.FontFamily, 12, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);
            countsLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight,
                Text = "NO VERIFIED SENSOR INPUTS" };
            headline.Controls.Add(countsLabel, 1, 0);
            layout.Controls.Add(headline, 0, 0);

            var toolbar = new FlowLayoutPanel {
                Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, Padding = new Padding(0, 4, 0, 0)
            };
            toolbar.Controls.Add(Button("Refresh", (s, e) => RefreshSnapshot()));
            toolbar.Controls.Add(Button("Verify identity...", (s, e) => VerifyIdentity()));
            toolbar.Controls.Add(Button("Enroll key...", (s, e) => EnrollIdentity()));
            toolbar.Controls.Add(Button("Approve mission", (s, e) => ApproveMission()));
            toolbar.Controls.Add(Button("Acknowledge incident", (s, e) => AcknowledgeIncident()));
            toolbar.Controls.Add(Button("Check evidence chain", (s, e) => CheckIntegrity()));
            showMap = new CheckBox { Text = "External tracks on Flight Data map", AutoSize = true,
                Checked = true, Margin = new Padding(15, 12, 3, 0) };
            showMap.CheckedChanged += (s, e) => {
                if (overlay != null) overlay.IsVisibile = showMap.Checked;
                if (Host.FDGMapControl != null) Host.FDGMapControl.Refresh();
            };
            toolbar.Controls.Add(showMap);
            layout.Controls.Add(toolbar, 0, 1);

            var content = new TabControl { Dock = DockStyle.Fill };
            tracks = Grid("Track", "Sensor / adapter", "Reported position", "Identity proof",
                "Physical association", "Authorization", "Source reliability");
            tracks.SelectionChanged += (s, e) => SelectedTrack();
            Tab(content, "Observed aircraft", tracks);
            missions = Grid("Permit ID", "Identity", "Location", "Start UTC", "End UTC", "Approval");
            Tab(content, "Mission permits", missions);
            identities = Grid("Identity", "Label", "Declared issuer", "Key algorithm",
                "Protection (NOT attested)", "Enrollment status");
            Tab(content, "Credential registry", identities);
            incidents = Grid("Incident", "Severity", "Summary", "Track", "Status", "Time UTC");
            Tab(content, "Incident desk", incidents);
            sensors = Grid("Sensor", "Category", "State", "Data type");
            Tab(content, "Sensor inputs", sensors);
            evidence = Grid("UTC", "Event", "Detail", "Origin", "SHA-256");
            Tab(content, "Evidence ledger", evidence);
            layout.Controls.Add(content, 0, 2);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            statusLabel = new Label { Dock = DockStyle.Fill,
                Text = "CORE OFFLINE — start local service at 127.0.0.1:8765",
                Font = new Font(SystemFonts.DefaultFont.FontFamily, 9, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft };
            detailLabel = new Label { Dock = DockStyle.Fill, Text = Guidance,
                AutoEllipsis = true, TextAlign = ContentAlignment.TopLeft };
            integrityLabel = detailLabel;
            footer.Controls.Add(statusLabel, 0, 0);
            footer.Controls.Add(detailLabel, 0, 1);
            layout.Controls.Add(footer, 0, 3);
        }

        private void LaunchClick(object sender, EventArgs e)
        {
            if (page == null || page.IsDisposed) return;
            var flight = Host.MainForm.MainMenu.Items["MenuFlightData"];
            if (flight != null) flight.PerformClick();
            Host.MainForm.FlightData.tabControlactions.SelectedTab = page;
            RefreshSnapshot();
        }

        private void SetStatus(string message)
        {
            if (statusLabel != null && !statusLabel.IsDisposed)
                statusLabel.Text = message;
        }

        private sealed class LocalClient : WebClient
        {
            protected override WebRequest GetWebRequest(Uri address)
            {
                var req = base.GetWebRequest(address);
                if (req != null) req.Timeout = 3500;
                return req;
            }
        }

        private LocalClient Client()
        {
            var web = new LocalClient { Encoding = Encoding.UTF8 };
            var token = Environment.GetEnvironmentVariable("HROT_ADMIN_TOKEN");
            if (!String.IsNullOrWhiteSpace(token))
                web.Headers.Add("X-HROT-Token", token);
            web.Headers[HttpRequestHeader.ContentType] = "application/json";
            return web;
        }

        private JObject Get(string url)
        {
            using (var c = Client())
                return JObject.Parse(c.DownloadString(baseUrl + "/api/" + url));
        }

        private JObject Post(string path, JObject body)
        {
            using (var c = Client())
                return JObject.Parse(c.UploadString(baseUrl + "/api/" + path, "POST", body.ToString()));
        }

        private void RefreshSnapshot()
        {
            if (polling || disposed || page == null || page.IsDisposed) return;
            polling = true;
            Task.Run(() => {
                try { return Tuple.Create(Get("bootstrap"), (string)null); }
                catch (Exception ex) { return Tuple.Create((JObject)null, ex.Message); }
            }).ContinueWith(job => {
                if (disposed || Host.MainForm == null || Host.MainForm.IsDisposed ||
                    !Host.MainForm.IsHandleCreated)
                {
                    polling = false;
                    return;
                }
                try
                {
                    Host.MainForm.BeginInvoke((Action)(() => {
                        polling = false;
                        if (disposed || page == null || page.IsDisposed) return;
                        if (job.IsFaulted || job.Result.Item1 == null)
                        {
                            SetStatus("CORE OFFLINE / API ERROR — " +
                                (job.IsFaulted ? "network request failed" : job.Result.Item2));
                            countsLabel.Text = "NO CURRENT OBSERVATION SOURCE";
                            ClearTables();
                            ClearOverlay();
                            return;
                        }
                        try { Render(job.Result.Item1); }
                        catch (Exception ex) { SetStatus("Invalid Core response: " + ex.Message);
                            ClearTables(); ClearOverlay(); }
                    }));
                }
                catch { polling = false; }
            });
        }

        private static string Value(JToken token, string key)
        {
            if (token == null) return "—";
            var v = token[key];
            return v == null || v.Type == JTokenType.Null ? "—" : v.ToString();
        }

        private static JArray Items(JObject root, string key)
        {
            return root[key] as JArray ?? new JArray();
        }

        private static bool IsDemo(JToken item)
        {
            return Value(item, "is_demo") == "1" ||
                   String.Equals(Value(item, "is_demo"), "true", StringComparison.OrdinalIgnoreCase);
        }

        private static JArray NonDemo(JArray raw)
        {
            var output = new JArray();
            foreach (var row in raw)
                if (!IsDemo(row)) output.Add(row.DeepClone());
            return output;
        }

        private static void Fill(DataGridView grid, JArray data, Func<JToken, object[]> fields)
        {
            grid.Rows.Clear();
            foreach (var row in data) grid.Rows.Add(fields(row));
        }

        private void ClearTables()
        {
            foreach (var grid in new[] { tracks, missions, identities, incidents, sensors, evidence })
                if (grid != null && !grid.IsDisposed) grid.Rows.Clear();
        }

        private void Render(JObject root)
        {
            var demoBackend = Value(root, "mode") == "demo";
            var observations = NonDemo(Items(root, "tracks"));
            var realMissions = NonDemo(Items(root, "missions"));
            var realIncidents = NonDemo(Items(root, "incidents"));
            var realSensors = NonDemo(Items(root, "sensors"));
            var realEvents = new JArray();
            foreach (var entry in Items(root, "evidence"))
                if (Value(entry, "source") != "demo") realEvents.Add(entry.DeepClone());

            int unresolved = 0;
            foreach (var row in observations)
                if (Value(row, "association_state") != "corroborated") unresolved++;

            SetStatus(demoBackend
                ? "CORE CONNECTED — DEMO BACKEND / all synthetic fixtures hidden"
                : "CORE CONNECTED — adapter data displayed; origin not independently attested");
            countsLabel.Text = observations.Count + " ADAPTER TRACKS  /  " + unresolved +
                " UNRESOLVED  /  " + realIncidents.Count + " INCIDENTS";

            Fill(tracks, observations, t => new object[] {
                Value(t, "id"), Value(t, "source"), Value(t, "lat") + ", " + Value(t, "lon"),
                Value(t, "identity_state"), Value(t, "association_state"),
                Value(t, "authorization_state"), "Adapter-submitted (not attested)"
            });
            Fill(missions, realMissions, t => new object[] {
                Value(t, "id"), Value(t, "identity_id"), Value(t, "location"),
                Value(t, "start_time"), Value(t, "end_time"), Value(t, "status")
            });
            Fill(identities, Items(root, "identities"), t => new object[] {
                Value(t, "id"), Value(t, "label"), Value(t, "issuer"),
                Value(t, "key_type"), Value(t, "protection_claim"), Value(t, "status")
            });
            Fill(incidents, realIncidents, t => new object[] {
                Value(t, "id"), Value(t, "severity"), Value(t, "title"),
                Value(t, "track_id"), Value(t, "status"), Value(t, "created_at")
            });
            Fill(sensors, realSensors, t => new object[] {
                Value(t, "id"), Value(t, "type"), Value(t, "status"), "External adapter"
            });
            Fill(evidence, realEvents, t => new object[] {
                Value(t, "timestamp"), Value(t, "kind"), Value(t, "detail"),
                Value(t, "source"), Value(t, "hash")
            });
            DrawExternalTracks(observations);
        }

        private static bool Coordinate(JToken row, out double lat, out double lon)
        {
            return double.TryParse(Value(row, "lat"), NumberStyles.Float,
                       CultureInfo.InvariantCulture, out lat) &&
                   double.TryParse(Value(row, "lon"), NumberStyles.Float,
                       CultureInfo.InvariantCulture, out lon) &&
                   lat >= -90 && lat <= 90 && lon >= -180 && lon <= 180;
        }

        private void ClearOverlay()
        {
            if (overlay == null) return;
            overlay.Markers.Clear();
            if (Host.FDGMapControl != null) Host.FDGMapControl.Refresh();
        }

        private void DrawExternalTracks(JArray observations)
        {
            if (overlay == null || disposed) return;
            overlay.Markers.Clear();
            if (!showMap.Checked) return;
            foreach (var row in observations)
            {
                double lat, lon;
                if (!Coordinate(row, out lat, out lon)) continue;
                var association = Value(row, "association_state");
                var color = association == "corroborated" ? Color.ForestGreen :
                    (association == "contradicted" ? Color.Firebrick : Color.DarkOrange);
                var marker = new ExternalMarker(new PointLatLng(lat, lon),
                    Value(row, "id"), color);
                marker.ToolTipText = "HROT external adapter — " + Value(row,"id") +
                    "\nIdentity: " + Value(row,"identity_state") +
                    "\nPhysical association: " + association +
                    "\nSource: " + Value(row,"source") + " (not attested)";
                overlay.Markers.Add(marker);
            }
            if (Host.FDGMapControl != null) Host.FDGMapControl.Refresh();
        }

        private sealed class ExternalMarker : GMapMarker
        {
            private readonly string label;
            private readonly Color color;
            public ExternalMarker(PointLatLng point, string label, Color color) : base(point)
            {
                this.label = label;
                this.color = color;
                Size = new Size(18, 18);
                Offset = new Point(-9, -9);
            }
            public override void OnRender(System.Drawing.IGraphics graphics)
            {
                // Actual Mission Planner GMap overlay; no synthetic targets.
                var x = LocalPosition.X - Offset.X;
                var y = LocalPosition.Y - Offset.Y;
                using (var brush = new SolidBrush(color))
                using (var pen = new Pen(Color.White, 1.5f))
                {
                    graphics.FillEllipse(brush, x - 7, y - 7, 14, 14);
                    graphics.DrawEllipse(pen, x - 7, y - 7, 14, 14);
                }
            }
        }

        private void SelectedTrack()
        {
            if (tracks == null || detailLabel == null || tracks.CurrentRow == null) return;
            var cells = tracks.CurrentRow.Cells;
            detailLabel.Text = "Track " + Convert.ToString(cells[0].Value) +
                "  |  Identity: " + Convert.ToString(cells[3].Value) +
                "  |  Association: " + Convert.ToString(cells[4].Value) +
                "  |  Authorization: " + Convert.ToString(cells[5].Value) +
                "\r\n" + Guidance;
        }

        private string Selected(DataGridView grid, int index)
        {
            if (grid == null || grid.CurrentRow == null) return null;
            return Convert.ToString(grid.CurrentRow.Cells[index].Value);
        }

        private string Ask(string title, string label, string original = "")
        {
            using (var form = new Form { Text = title, StartPosition = FormStartPosition.CenterParent,
                Size = new Size(550, 205), FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false, MaximizeBox = false })
            {
                var lab = new Label { Text = label, Dock = DockStyle.Top, Height = 37,
                    Padding = new Padding(10, 12, 10, 2) };
                var input = new TextBox { Dock = DockStyle.Top, Text = original,
                    Margin = new Padding(10), Width = 510 };
                var ok = new Button { Text = "Continue", DialogResult = DialogResult.OK,
                    Dock = DockStyle.Bottom, Height = 34 };
                form.Controls.Add(ok); form.Controls.Add(input); form.Controls.Add(lab);
                form.AcceptButton = ok;
                if (form.ShowDialog(Host.MainForm) != DialogResult.OK) return null;
                return input.Text.Trim();
            }
        }

        private void EnrollIdentity()
        {
            var id = Ask("HROT — enroll", "Device identity (letters, digits, underscores and hyphens):");
            if (String.IsNullOrWhiteSpace(id)) return;
            var key = Ask("HROT — enroll", "Ed25519 raw 32-byte public key encoded as Base64:");
            if (String.IsNullOrWhiteSpace(key)) return;
            var label = Ask("HROT — enroll", "Device display name:", id);
            if (String.IsNullOrWhiteSpace(label)) return;
            var issuer = Ask("HROT — enroll", "Issuer (operator-declared; NOT certificate-validated):", "Local laboratory");
            if (String.IsNullOrWhiteSpace(issuer)) return;
            try
            {
                var result = Post("identities", new JObject {
                    ["id"] = id, ["label"] = label, ["public_key"] = key,
                    ["issuer"] = issuer, ["protection_claim"] = "not_attested"
                });
                MessageBox.Show(Host.MainForm,
                    "Public key enrolled. Hardware protection, PUF origin and physical airframe association are NOT verified.",
                    "HROT — enrollment", MessageBoxButtons.OK, MessageBoxIcon.Information);
                RefreshSnapshot();
            }
            catch (Exception ex) { Error(ex); }
        }

        private void VerifyIdentity()
        {
            var id = Selected(identities, 0);
            if (String.IsNullOrWhiteSpace(id))
                id = Ask("HROT verification", "Registered device identity:");
            if (String.IsNullOrWhiteSpace(id)) return;

            try
            {
                var challenge = Post("verify/challenge", new JObject { ["identity_id"] = id });
                var transcript = Value(challenge, "transcript");
                var challengeId = Value(challenge, "challenge_id");

                using (var form = new Form {
                    Text = "HROT — Ed25519 key-possession verification",
                    StartPosition = FormStartPosition.CenterParent,
                    Width = 640, Height = 430, MinimizeBox = false,
                    MaximizeBox = false, FormBorderStyle = FormBorderStyle.FixedDialog })
                {
                    var layout = new TableLayoutPanel {
                        Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5,
                        Padding = new Padding(12) };
                    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
                    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 98));
                    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
                    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 107));
                    layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                    layout.Controls.Add(new Label {
                        Dock = DockStyle.Fill, Text = "Device " + id +
                        "\r\nSign the exact UTF-8 transcript with the enrolled key. Challenge expires after 90 seconds."
                    }, 0, 0);
                    var transcriptBox = new TextBox {
                        Dock = DockStyle.Fill, Multiline = true,
                        ScrollBars = ScrollBars.Vertical, ReadOnly = true, Text = transcript };
                    layout.Controls.Add(transcriptBox, 0, 1);
                    var copy = Button("Copy challenge transcript", (s, e) => {
                        Clipboard.SetText(transcript);
                    });
                    layout.Controls.Add(copy, 0, 2);
                    var signatureBox = new TextBox {
                        Dock = DockStyle.Fill, Multiline = true,
                        ScrollBars = ScrollBars.Vertical,
                        Text = "", WordWrap = false };
                    signatureBox.Enter += (s, e) => {
                        if (String.IsNullOrWhiteSpace(signatureBox.Text))
                            signatureBox.Text = "";
                    };
                    var signaturePanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
                    signaturePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
                    signaturePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                    signaturePanel.Controls.Add(new Label {
                        Text = "Device-generated 64-byte Ed25519 signature (BASE64):",
                        Dock = DockStyle.Fill
                    }, 0, 0);
                    signaturePanel.Controls.Add(signatureBox, 0, 1);
                    layout.Controls.Add(signaturePanel, 0, 3);
                    var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
                    var verify = new Button { Text = "Verify signature", AutoSize = true };
                    var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
                    verify.Click += (s, e) => {
                        if (String.IsNullOrWhiteSpace(signatureBox.Text))
                        {
                            MessageBox.Show(form, "Paste the signer-generated signature first.");
                            return;
                        }
                        try
                        {
                            var result = Post("verify/complete", new JObject {
                                ["challenge_id"] = challengeId,
                                ["signature"] = signatureBox.Text.Trim()
                            });
                            bool accepted = Value(result, "verified").Equals("true",
                                StringComparison.OrdinalIgnoreCase);
                            MessageBox.Show(form,
                                (accepted ? "ENROLLED KEY PROOF VERIFIED" : "PROOF REJECTED") +
                                "\r\nReason: " + Value(result, "reason") +
                                "\r\nPhysical aircraft binding: NOT PROVEN" +
                                "\r\nHardware key protection: NOT ATTESTED",
                                "HROT verification result", MessageBoxButtons.OK,
                                accepted ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                            form.DialogResult = DialogResult.OK;
                            form.Close();
                        }
                        catch (Exception ex) { MessageBox.Show(form, ex.Message, "Verification error"); }
                    };
                    bottom.Controls.Add(cancel);
                    bottom.Controls.Add(verify);
                    layout.Controls.Add(bottom, 0, 4);
                    form.Controls.Add(layout);
                    form.CancelButton = cancel;
                    form.ShowDialog(Host.MainForm);
                }
                RefreshSnapshot();
            }
            catch (Exception ex) { Error(ex); }
        }

        private void ApproveMission()
        {
            var id = Selected(missions, 0);
            if (String.IsNullOrWhiteSpace(id))
            {
                MessageBox.Show(Host.MainForm, "Select a mission permit first.");
                return;
            }
            if (MessageBox.Show(Host.MainForm, "Approve mission " + id +
                "?\nThis records approval; it does NOT cryptographically authorize flight or command the aircraft.",
                "HROT permission", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            try
            {
                Post("missions/" + Uri.EscapeDataString(id) + "/approve", new JObject());
                RefreshSnapshot();
            }
            catch (Exception ex) { Error(ex); }
        }

        private void AcknowledgeIncident()
        {
            var id = Selected(incidents, 0);
            if (String.IsNullOrWhiteSpace(id))
            {
                MessageBox.Show(Host.MainForm, "Select an incident first.");
                return;
            }
            try
            {
                Post("incidents/" + Uri.EscapeDataString(id) + "/ack", new JObject());
                RefreshSnapshot();
            }
            catch (Exception ex) { Error(ex); }
        }

        private void CheckIntegrity()
        {
            try
            {
                var result = Get("evidence/check");
                bool passed = String.Equals(Value(result, "valid"), "true",
                    StringComparison.OrdinalIgnoreCase);
                MessageBox.Show(Host.MainForm,
                    (passed ? "LOCAL HASH CHAIN VALID" : "LOCAL HASH CHAIN INVALID") +
                    "\r\nChecked entries: " + Value(result, "entries_checked") +
                    "\r\nThis is NOT an externally anchored forensic chain.",
                    "HROT evidence integrity", MessageBoxButtons.OK,
                    passed ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex) { Error(ex); }
        }

        private void Error(Exception ex)
        {
            MessageBox.Show(Host.MainForm, ex.Message,
                "HROT Core request failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        public override bool Loop() { return true; }
        public override bool Exit()
        {
            disposed = true;
            if (timer != null) { timer.Stop(); timer.Dispose(); timer = null; }
            if (Host.MainForm != null && launchButton != null)
            {
                launchButton.Click -= LaunchClick;
                Host.MainForm.MainMenu.Items.Remove(launchButton);
                launchButton.Dispose();
            }
            if (Host.MainForm != null && Host.MainForm.FlightData != null && page != null)
            {
                Host.MainForm.FlightData.tabControlactions.TabPages.Remove(page);
                Host.MainForm.FlightData.TabListOriginal.Remove(page);
            }
            if (overlay != null && Host.FDGMapControl != null)
            {
                Host.FDGMapControl.Overlays.Remove(overlay);
                overlay.Markers.Clear();
                overlay = null;
            }
            if (page != null) { page.Dispose(); page = null; }
            return true;
        }
    }
}
