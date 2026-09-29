using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;
using Microsoft.Win32;

namespace AstueMpsReplacement
{
    public sealed class MainForm : Form
    {
        private int _treeNameWidth = 42;
        private int _treeValueWidth = 17;
        private int _treeQualityWidth = 22;
        private int _treeTimeWidth = 20;
        private int _treeErrorsWidth = 13;
        private int _treeLoadWidth = 18;

        private readonly LogService _log = new LogService();
        private readonly TreeView _tree = new TreeView();
        private readonly DataGridView _props = new DataGridView();
        private readonly TextBox _logBox = new TextBox();
        private readonly ValueCache _cache = new ValueCache();
        private readonly PollingStatistics _statistics = new PollingStatistics();
        private readonly Dictionary<Guid, TreeNode> _treeNodes = new Dictionary<Guid, TreeNode>();
        private PollingEngine _polling;
        private ConfigNode _root;
        private bool _internalCheck;
        private bool _projectDirty;
        private ToolStripButton _saveButton;
        private ToolStripButton _startButton;
        private ToolStripButton _stopButton;
        private ToolStripButton _comPortsButton;
        private ToolStripButton _editModeButton;
        private ToolStripLabel _modeStatusLabel;
        private ToolStripLabel _pollStatusLabel;
        private ToolStripLabel _opcStatusLabel;
        private ToolStripLabel _clientsStatusLabel;
        private readonly List<ToolStripItem> _editOnlyMenuItems = new List<ToolStripItem>();
        private ResizableTreeHeader _treeHeader;
        private bool _editMode;
        private string _currentProjectPath;
        private int _uiLogLineCount;
        private const int UiLogMaxLines = 5000;
        private const int UiLogTrimAt = 5500;
        private const int LogBufferMaxEntries = 100000;
        private readonly List<LogEntry> _uiLogEntries = new List<LogEntry>();
        private readonly Timer _statisticsUiTimer = new Timer();
        private readonly AppUserSettings _userSettings = new AppUserSettings();
        private readonly StartupOptions _startupOptions;
        private OpcSnapshotPublisher _opcPublisher;
        private ToolStripDropDownButton _opcButton;
        private bool _pollingStartedByOpcDemand;
        private bool _manualKeepAlive;
        private DateTime _opcDemandLostSince = DateTime.MinValue;
        private DateTime _autoLaunchIdleSince = DateTime.MinValue;
        private readonly Timer _runtimeControlTimer = new Timer();

        internal MainForm(StartupOptions startupOptions)
        {
            _startupOptions = startupOptions ?? new StartupOptions();
            Text = "ASTUE MPS Replacement — prototype 0.3.2.4 Mercury + SET4 + OPC DA";
            Width = 1540;
            Height = 840;
            StartPosition = FormStartPosition.CenterScreen;

            var rootLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            var tool = BuildToolStrip();
            rootLayout.Controls.Add(tool, 0, 0);
            rootLayout.Controls.Add(BuildRunStatusStrip(), 0, 1);

            // Правая панель свойств занимает всю высоту рабочей области.
            // Слева дерево и журнал разделены горизонтальным splitter'ом.
            var vertical = new SplitContainer
            {
                Dock = DockStyle.Fill
            };
            rootLayout.Controls.Add(vertical, 0, 2);
            Controls.Add(rootLayout);

            var mainAndLog = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal
            };
            vertical.Panel1.Controls.Add(mainAndLog);

            var left = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            left.Controls.Add(BuildTreeHeader(), 0, 0);
            left.Controls.Add(_tree, 0, 1);
            mainAndLog.Panel1.Controls.Add(left);

            _tree.Dock = DockStyle.Fill;
            _tree.CheckBoxes = true;
            _tree.HideSelection = false;
            _tree.LabelEdit = false;
            _tree.ShowNodeToolTips = true;
            _tree.Font = new Font("Consolas", 9f);
            _tree.ItemHeight = Math.Max(20, _tree.ItemHeight);
            _tree.AfterSelect += (_, __) =>
            {
                ShowProperties(SelectedConfigNode());
                UpdateToolbarStatus();
                RefreshFilteredLog();
            };
            _tree.BeforeCheck += TreeBeforeCheck;
            _tree.AfterCheck += TreeAfterCheck;
            _tree.BeforeExpand += TreeBeforeExpand;
            _tree.KeyDown += TreeKeyDown;
            _tree.NodeMouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Right && e.Node != null)
                    _tree.SelectedNode = e.Node;
            };
            _tree.NodeMouseDoubleClick += (_, e) =>
            {
                if (_editMode && e.Button == MouseButtons.Left && e.Node != null && (ModifierKeys & Keys.Control) == Keys.Control)
                {
                    _tree.SelectedNode = e.Node;
                    RenameSelected();
                }
            };

            _props.Dock = DockStyle.Fill;
            _props.AllowUserToAddRows = false;
            _props.AllowUserToDeleteRows = false;
            _props.RowHeadersVisible = false;
            _props.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _props.Columns.Add("Property", "Свойство");
            _props.Columns.Add("Value", "Значение");
            _props.Columns[0].ReadOnly = true;
            _props.CellEndEdit += PropsCellEndEdit;
            _props.EditMode = DataGridViewEditMode.EditOnEnter;
            _props.CellClick += PropsCellClick;
            _props.EditingControlShowing += PropsEditingControlShowing;
            _props.ReadOnly = true;
            vertical.Panel2.Controls.Add(_props);

            _logBox.Dock = DockStyle.Fill;
            _logBox.Multiline = true;
            _logBox.ScrollBars = ScrollBars.Both;
            _logBox.ReadOnly = true;
            _logBox.WordWrap = false;
            _logBox.Font = new Font(FontFamily.GenericMonospace, 9f);
            mainAndLog.Panel2.Controls.Add(_logBox);

            _log.Entry += AppendLog;
            _log.ReadError += _statistics.RecordReadError;
            _cache.ValueChanged += CacheValueChanged;
            _opcPublisher = new OpcSnapshotPublisher(() => _root, _cache, _log);

            _statisticsUiTimer.Interval = 1000;
            _statisticsUiTimer.Tick += (_, __) => RefreshStatisticsUi();
            _statisticsUiTimer.Start();

            _runtimeControlTimer.Interval = 1000;
            _runtimeControlTimer.Tick += (_, __) => RuntimeControlTick();
            _runtimeControlTimer.Start();

            FormClosing += (_, __) =>
            {
                _statisticsUiTimer.Stop();
                _runtimeControlTimer.Stop();
                StopPolling();
                if (_opcPublisher != null) _opcPublisher.Dispose();
                RuntimeControl.SetOpcManualKeepAlive(false);
                // Demand-started OPC LocalServer belongs to COM/DCOM, not to the GUI.
                // If an OPC client is still connected, leave it alive: it will relaunch
                // this main application on active demand. A deliberate STOP has already
                // set OpcDemandStartEnabled=false and may stop the server explicitly.
                if (!RuntimeControl.OpcDemandStartEnabled) StopOpcServerInternal(false);
                RuntimeControl.SetConfigurationMode(false);
                RuntimeControl.ClearOwnedRuntimeFiles();
            };

            BuildContextMenu();
            SetEditMode(_startupOptions.ConfigurationMode);
            UpdateToolbarStatus();
            UpdateWindowTitle();

            Shown += (_, __) =>
            {
                // SplitterDistance нельзя надёжно задавать в конструкторе:
                // до первого layout WinForms контейнеры имеют служебные размеры.
                ApplyInitialSplitterLayout(mainAndLog, vertical);
                TryOpenLastProject();
                RuntimeControlTick();
                if (_startupOptions.StartMinimized)
                    WindowState = FormWindowState.Minimized;
            };

            _log.Info(_startupOptions.StartedByOpc
                ? "v0.3.2.4 запущена по запросу OPC-клиента. Рабочий режим; опрос запускается по OPC demand."
                : (_startupOptions.ConfigurationMode
                    ? "v0.3.2.4 запущена в режиме КОНФИГУРИРОВАНИЯ: опрос и OPC DA заблокированы."
                    : "v0.3.2.4 запущена в РАБОЧЕМ режиме. Опросом и OPC DA можно управлять вручную; OPC-клиент может запустить их по demand."));
        }

        private static void ApplyInitialSplitterLayout(SplitContainer mainAndLog, SplitContainer vertical)
        {
            if (mainAndLog != null && mainAndLog.Height > 260)
            {
                mainAndLog.Panel1MinSize = 180;
                mainAndLog.Panel2MinSize = 100;
                var desired = mainAndLog.Height - 190;
                var max = mainAndLog.Height - mainAndLog.Panel2MinSize - mainAndLog.SplitterWidth;
                mainAndLog.SplitterDistance = Math.Max(mainAndLog.Panel1MinSize, Math.Min(desired, max));
            }

            if (vertical != null && vertical.Width > 700)
            {
                vertical.Panel1MinSize = 360;
                vertical.Panel2MinSize = 280;
                var desired = vertical.Width - 390;
                var max = vertical.Width - vertical.Panel2MinSize - vertical.SplitterWidth;
                vertical.SplitterDistance = Math.Max(vertical.Panel1MinSize, Math.Min(desired, max));
            }
        }

        private Control BuildTreeHeader()
        {
            _treeHeader = new ResizableTreeHeader();
            _treeHeader.WidthsChanged += (namePx, valuePx, qualityPx, timePx, errorsPx, loadPx) =>
            {
                // TreeView использует моноширинный шрифт; переводим пиксели заголовка в символы.
                const double charPx = 7.2;
                _treeNameWidth = Math.Max(18, (int)Math.Round((namePx - 20) / charPx));
                _treeValueWidth = Math.Max(8, (int)Math.Round(valuePx / charPx));
                _treeQualityWidth = Math.Max(10, (int)Math.Round(qualityPx / charPx));
                _treeTimeWidth = Math.Max(12, (int)Math.Round(timePx / charPx));
                _treeErrorsWidth = Math.Max(10, (int)Math.Round(errorsPx / charPx));
                _treeLoadWidth = Math.Max(12, (int)Math.Round(loadPx / charPx));
                RefreshAllTreeDisplay();
            };
            return _treeHeader;
        }

        private void AppendLog(LogEntry entry)
        {
            if (entry == null || IsDisposed) return;
            if (!IsHandleCreated || !InvokeRequired)
            {
                AppendLogUi(entry);
                return;
            }
            BeginInvoke((Action)(() => AppendLogUi(entry)));
        }

        private void AppendLogUi(LogEntry entry)
        {
            _uiLogEntries.Add(entry);
            if (_uiLogEntries.Count > LogBufferMaxEntries)
            {
                var remove = Math.Max(1000, _uiLogEntries.Count - LogBufferMaxEntries);
                _uiLogEntries.RemoveRange(0, remove);
            }

            if (!LogMatchesSelection(entry)) return;

            _logBox.AppendText(entry.Formatted + Environment.NewLine);
            _uiLogLineCount++;

            // Экранный журнал дополнительно ограничиваем по видимым строкам.
            if (_uiLogLineCount < UiLogTrimAt) return;

            var lines = _logBox.Lines;
            var keep = Math.Min(UiLogMaxLines, lines.Length);
            var trimmed = new string[keep];
            Array.Copy(lines, lines.Length - keep, trimmed, 0, keep);
            _logBox.Lines = trimmed;
            _uiLogLineCount = keep;
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.ScrollToCaret();
        }

        private bool LogMatchesSelection(LogEntry entry)
        {
            var selected = SelectedConfigNode();
            if (selected == null || selected.Kind == ConfigNodeKind.Main) return true;

            var device = ConfigTree.AncestorOrSelf(selected, ConfigNodeKind.Device);
            if (device != null)
                return entry.DeviceId.HasValue && entry.DeviceId.Value == device.Id;

            var bus = ConfigTree.AncestorOrSelf(selected, ConfigNodeKind.Bus);
            if (bus != null)
                return entry.BusId.HasValue && entry.BusId.Value == bus.Id;

            return true;
        }

        private void RefreshFilteredLog()
        {
            if (IsDisposed || !IsHandleCreated) return;

            _logBox.SuspendLayout();
            try
            {
                _logBox.Clear();
                _uiLogLineCount = 0;

                var startIndex = Math.Max(0, _uiLogEntries.Count - UiLogMaxLines);
                for (var i = startIndex; i < _uiLogEntries.Count; i++)
                {
                    var entry = _uiLogEntries[i];
                    if (!LogMatchesSelection(entry)) continue;
                    _logBox.AppendText(entry.Formatted + Environment.NewLine);
                    _uiLogLineCount++;
                }

                _logBox.SelectionStart = _logBox.TextLength;
                _logBox.ScrollToCaret();
            }
            finally
            {
                _logBox.ResumeLayout();
            }
        }

        private ToolStrip BuildRunStatusStrip()
        {
            var ts = new ToolStrip
            {
                Dock = DockStyle.Fill,
                GripStyle = ToolStripGripStyle.Hidden,
                RenderMode = ToolStripRenderMode.System,
                BackColor = Color.WhiteSmoke
            };
            ts.Items.Add(new ToolStripLabel("СОСТОЯНИЕ:") { Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) });
            _modeStatusLabel = StatusLabel(" РАБОТА ", Color.PaleGreen, Color.DarkGreen);
            _pollStatusLabel = StatusLabel(" ОПРОС: СТОП ", Color.Gainsboro, Color.DimGray);
            _opcStatusLabel = StatusLabel(" OPC DA: СТОП ", Color.Gainsboro, Color.DimGray);
            _clientsStatusLabel = StatusLabel(" КЛИЕНТОВ: 0 ", Color.WhiteSmoke, Color.Black);
            ts.Items.Add(_modeStatusLabel);
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(_pollStatusLabel);
            ts.Items.Add(_opcStatusLabel);
            ts.Items.Add(_clientsStatusLabel);
            return ts;
        }

        private static ToolStripLabel StatusLabel(string text, Color back, Color fore)
        {
            return new ToolStripLabel(text)
            {
                BackColor = back,
                ForeColor = fore,
                Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
                Margin = new Padding(4, 2, 4, 2),
                Padding = new Padding(6, 3, 6, 3)
            };
        }

        private void UpdateRunStatusStrip()
        {
            if (_modeStatusLabel == null) return;
            var polling = _polling != null;
            var opc = IsOpcServerRunning();
            var opcReady = !_editMode && !opc && RuntimeControl.OpcDemandStartEnabled && IsOpcServerRegistered();
            var state = RuntimeControl.ReadOpcState();
            string clients;
            if (!state.TryGetValue("clients", out clients)) clients = "0";

            if (_editMode)
            {
                _modeStatusLabel.Text = " КОНФИГУРИРОВАНИЕ ";
                _modeStatusLabel.BackColor = Color.Khaki;
                _modeStatusLabel.ForeColor = Color.DarkGoldenrod;
            }
            else
            {
                _modeStatusLabel.Text = _startupOptions.StartedByOpc ? " РАБОТА — OPC DEMAND " : " РАБОТА ";
                _modeStatusLabel.BackColor = Color.PaleGreen;
                _modeStatusLabel.ForeColor = Color.DarkGreen;
            }

            _pollStatusLabel.Text = polling ? " ОПРОС: РАБОТАЕТ " : " ОПРОС: СТОП ";
            _pollStatusLabel.BackColor = polling ? Color.LightGreen : Color.Gainsboro;
            _pollStatusLabel.ForeColor = polling ? Color.DarkGreen : Color.DarkRed;
            _opcStatusLabel.Text = opc ? " OPC DA: РАБОТАЕТ " : (opcReady ? " OPC DA: ГОТОВ " : " OPC DA: СТОП ");
            _opcStatusLabel.BackColor = opc ? Color.LightGreen : (opcReady ? Color.LightCyan : Color.Gainsboro);
            _opcStatusLabel.ForeColor = (opc || opcReady) ? Color.DarkGreen : Color.DarkRed;
            _clientsStatusLabel.Text = " КЛИЕНТОВ OPC: " + clients + " ";
            _clientsStatusLabel.BackColor = (clients != "0") ? Color.LightCyan : Color.WhiteSmoke;
        }

        private ToolStrip BuildToolStrip()
        {
            var ts = new ToolStrip
            {
                Dock = DockStyle.Fill,
                GripStyle = ToolStripGripStyle.Hidden,
                RenderMode = ToolStripRenderMode.System
            };

            ts.Items.Add(Button("Импорт MPP", (_, __) => ImportMpp()));
            ts.Items.Add(Button("Открыть ASTUE", (_, __) => OpenProject()));
            _saveButton = Button("Сохранить ASTUE", (_, __) => SaveProject());
            ts.Items.Add(_saveButton);

            _editModeButton = new ToolStripButton("КОНФИГУРИРОВАНИЕ")
            {
                CheckOnClick = true,
                ToolTipText = "В рабочем режиме чекбоксы и свойства защищены от случайного изменения"
            };
            _editModeButton.CheckedChanged += (_, __) => SetEditMode(_editModeButton.Checked);
            ts.Items.Add(_editModeButton);
            ts.Items.Add(new ToolStripSeparator());

            _startButton = Button("▶ СТАРТ", (_, __) => StartSystem());
            _stopButton = Button("■ СТОП", (_, __) => StopSystemManual());
            ts.Items.Add(_startButton);
            ts.Items.Add(_stopButton);
            ts.Items.Add(new ToolStripSeparator());

            _comPortsButton = Button("COM-порты", (_, __) => ShowComPorts());
            ts.Items.Add(_comPortsButton);
            ts.Items.Add(Button("Развернуть", (_, __) => ExpandTree()));
            ts.Items.Add(Button("Свернуть", (_, __) => CollapseTree()));
            ts.Items.Add(new ToolStripSeparator());

            _opcButton = new ToolStripDropDownButton("OPC DA / диагностика");
            _opcButton.DropDownItems.Add("Состояние...", null, (_, __) => ShowOpcStatus());
            _opcButton.DropDownItems.Add(new ToolStripSeparator());
            _opcButton.DropDownItems.Add("Зарегистрировать текущую версию...", null, (_, __) => RegisterOpcServer(false));
            _opcButton.DropDownItems.Add("Отменить регистрацию...", null, (_, __) => RegisterOpcServer(true));
            _opcButton.DropDownItems.Add(new ToolStripSeparator());
            _opcButton.DropDownItems.Add("Открыть папку обмена", null, (_, __) => OpenOpcDataFolder());
            ts.Items.Add(_opcButton);
            return ts;
        }

        private void UpdateToolbarStatus()
        {
            var polling = _polling != null;

            if (_editModeButton != null)
            {
                _editModeButton.Text = _editMode ? "КОНФИГУРИРОВАНИЕ" : "РАБОТА";
                _editModeButton.BackColor = _editMode ? Color.Khaki : Color.PaleGreen;
                _editModeButton.ForeColor = _editMode ? Color.DarkGoldenrod : Color.DarkGreen;
                _editModeButton.ToolTipText = _editMode
                    ? "КОНФИГУРИРОВАНИЕ: опрос остановлен, OPC DA остановлен и автозапуск OPC заблокирован"
                    : "РАБОТА: конфигурация защищена; разрешены ручной и demand-запуск опроса/OPC";
            }

            if (_startButton != null)
            {
                var opcRunning = IsOpcServerRunning();
                var allRunning = polling && opcRunning;
                _startButton.BackColor = allRunning ? Color.LightGreen : Color.PaleGreen;
                _startButton.ForeColor = Color.DarkGreen;
                _startButton.Enabled = !_editMode && _root != null && !allRunning;
                _startButton.Font = new Font(_startButton.Font, FontStyle.Bold);
                _startButton.ToolTipText = _editMode ? "В конфигурировании запуск заблокирован" : "Запустить опрос и OPC DA одновременно";
            }

            if (_stopButton != null)
            {
                var anythingRunning = polling || IsOpcServerRunning();
                _stopButton.BackColor = Color.LightCoral;
                _stopButton.ForeColor = Color.DarkRed;
                _stopButton.Enabled = anythingRunning && !_editMode;
                _stopButton.Font = new Font(_stopButton.Font, FontStyle.Bold);
                _stopButton.ToolTipText = "Остановить опрос и OPC DA одновременно";
            }

            if (_saveButton != null)
            {
                _saveButton.Enabled = _root != null;
                _saveButton.BackColor = _projectDirty ? Color.Khaki : SystemColors.Control;
                _saveButton.ForeColor = _projectDirty ? Color.DarkGoldenrod : SystemColors.ControlText;
                _saveButton.ToolTipText = _projectDirty
                    ? "Есть несохранённые изменения"
                    : "Нет несохранённых изменений";
            }

            if (_comPortsButton != null)
            {
                _comPortsButton.BackColor = SystemColors.Control;
                _comPortsButton.ForeColor = SystemColors.ControlText;
                _comPortsButton.ToolTipText = "Показать локальные COM-порты";

                var selected = SelectedConfigNode();
                var bus = selected == null ? null : ConfigTree.AncestorOrSelf(selected, ConfigNodeKind.Bus);
                if (bus != null)
                {
                    var configured = NormalizeComDisplay(MppSettings.GetString(bus, "COMPort", string.Empty));
                    var exists = SerialPort.GetPortNames()
                        .Any(p => p.Equals(configured, StringComparison.OrdinalIgnoreCase));

                    _comPortsButton.BackColor = exists ? Color.PaleGreen : Color.LightCoral;
                    _comPortsButton.ForeColor = exists ? Color.DarkGreen : Color.DarkRed;
                    _comPortsButton.ToolTipText = exists
                        ? configured + " присутствует в системе"
                        : configured + " отсутствует в системе";
                }
            }

            UpdateOpcButtonStatus();
            UpdateRunStatusStrip();
        }

        private void MarkProjectDirty()
        {
            _projectDirty = true;
            UpdateToolbarStatus();
            UpdateWindowTitle();
        }

        private void SetEditMode(bool enabled)
        {
            if (_editMode == enabled && (_editModeButton == null || _editModeButton.Checked == enabled))
            {
                RuntimeControl.SetConfigurationMode(enabled);
                if (enabled) _opcPublisher?.Stop();
                else if (_root != null) _opcPublisher?.Start();
                UpdateToolbarStatus();
                return;
            }

            if (enabled)
            {
                _manualKeepAlive = true;
                StopPolling();
                _pollingStartedByOpcDemand = false;
                _opcPublisher?.Stop();
                RuntimeControl.SetConfigurationMode(true);
                RuntimeControl.SetOpcManualKeepAlive(false);
                StopOpcServerInternal(false);
                _log.Info("КОНФИГУРИРОВАНИЕ: опрос остановлен; OPC DA остановлен и заблокирован на время конфигурирования.");
            }
            else
            {
                RuntimeControl.SetConfigurationMode(false);
                if (_root != null) _opcPublisher?.Start();
                _log.Info("РАБОЧИЙ режим: конфигурация защищена; разрешены ручной и OPC-demand запуск.");
            }

            _editMode = enabled;
            if (_editModeButton != null && _editModeButton.Checked != enabled)
                _editModeButton.Checked = enabled;

            _props.ReadOnly = !enabled;
            foreach (var item in _editOnlyMenuItems)
                item.Enabled = enabled;

            ShowProperties(SelectedConfigNode());
            RuntimeControl.TouchMainState(_editMode, _polling != null);
            UpdateToolbarStatus();
        }

        private void UpdateWindowTitle()
        {
            var project = string.IsNullOrWhiteSpace(_currentProjectPath)
                ? "проект не открыт"
                : _currentProjectPath;
            var dirty = _projectDirty ? " *" : string.Empty;
            Text = "ASTUE MPS Replacement — v0.3.2.4 Mercury + SET4 + OPC DA — " + project + dirty;
        }

        private static ToolStripButton Button(string text, EventHandler handler)
        {
            var b = new ToolStripButton(text);
            b.Click += handler;
            return b;
        }

        private void BuildContextMenu()
        {
            var menu = new ContextMenuStrip();
            _editOnlyMenuItems.Clear();

            _editOnlyMenuItems.Add(menu.Items.Add("Мастер добавления...", null, (_, __) => ShowAddWizard()));
            menu.Items.Add(new ToolStripSeparator());
            _editOnlyMenuItems.Add(menu.Items.Add("Переименовать", null, (_, __) => RenameSelected()));
            _editOnlyMenuItems.Add(menu.Items.Add("Удалить", null, (_, __) => DeleteSelected()));

            menu.Items.Add(new ToolStripSeparator());
            var exportMenu = new ToolStripMenuItem("Экспорт журнала линии");
            exportMenu.DropDownItems.Add("Последний час (CSV)...", null, (_, __) => ExportSelectedLineLog(TimeSpan.FromHours(1)));
            exportMenu.DropDownItems.Add("Буфер текущего сеанса (CSV)...", null, (_, __) => ExportSelectedLineLog(null));
            menu.Items.Add(exportMenu);
            var diagnosticItem = menu.Items.Add("Диагностический пакет линии (ZIP)...", null, (_, __) => ExportSelectedLineDiagnosticPackage());
            var resetStatisticsItem = menu.Items.Add("Сбросить статистику линии", null, (_, __) => ResetSelectedLineStatistics());

            menu.Opening += (_, __) =>
            {
                var bus = SelectedConfigNode();
                var isBus = bus != null && bus.Kind == ConfigNodeKind.Bus;
                exportMenu.Enabled = isBus;
                diagnosticItem.Enabled = isBus;
                resetStatisticsItem.Enabled = isBus;
            };

            foreach (var item in _editOnlyMenuItems) item.Enabled = false;
            _tree.ContextMenuStrip = menu;
        }

        private ConfigNode SelectedBusForLineAction()
        {
            var selected = SelectedConfigNode();
            return selected != null && selected.Kind == ConfigNodeKind.Bus ? selected : null;
        }

        private void ExportSelectedLineLog(TimeSpan? period)
        {
            var bus = SelectedBusForLineAction();
            if (bus == null) return;

            var cutoff = period.HasValue ? DateTime.Now.Subtract(period.Value) : DateTime.MinValue;
            var entries = _uiLogEntries
                .Where(x => x.BusId.HasValue && x.BusId.Value == bus.Id && x.Timestamp >= cutoff)
                .ToArray();

            using (var sfd = new SaveFileDialog
            {
                Filter = "CSV (*.csv)|*.csv|Все файлы|*.*",
                FileName = SafeFileName(bus.Name) + "_log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv"
            })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(sfd.FileName, BuildLineLogCsv(bus, entries), new UTF8Encoding(true));
                    _log.Info($"Экспорт журнала {bus.Name}: {entries.Length} записей -> {sfd.FileName}");
                }
                catch (Exception ex)
                {
                    _log.Error("Экспорт журнала линии: " + ex.Message);
                    MessageBox.Show(this, ex.Message, "Экспорт журнала", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private string BuildLineLogCsv(ConfigNode bus, IEnumerable<LogEntry> source)
        {
            var names = _root == null
                ? new Dictionary<Guid, string>()
                : ConfigTree.DescendantsAndSelf(_root).ToDictionary(x => x.Id, x => x.Name);
            var sb = new StringBuilder();
            sb.AppendLine("Timestamp;Level;Line;Device;Message");
            foreach (var entry in source)
            {
                string deviceName = string.Empty;
                if (entry.DeviceId.HasValue && !names.TryGetValue(entry.DeviceId.Value, out deviceName))
                    deviceName = entry.DeviceId.Value.ToString();

                sb.Append(CsvField(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))).Append(';')
                  .Append(CsvField(entry.Level)).Append(';')
                  .Append(CsvField(bus == null ? string.Empty : bus.Name)).Append(';')
                  .Append(CsvField(deviceName)).Append(';')
                  .Append(CsvField(entry.Text)).AppendLine();
            }
            return sb.ToString();
        }

        private void ExportSelectedLineDiagnosticPackage()
        {
            var bus = SelectedBusForLineAction();
            if (bus == null) return;

            using (var sfd = new SaveFileDialog
            {
                Filter = "ZIP (*.zip)|*.zip|Все файлы|*.*",
                FileName = SafeFileName(bus.Name) + "_diagnostic_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".zip"
            })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var generated = DateTime.Now;
                    var cutoff = generated.AddHours(-1);
                    var lastHour = _uiLogEntries
                        .Where(x => x.BusId.HasValue && x.BusId.Value == bus.Id && x.Timestamp >= cutoff)
                        .ToArray();
                    var allBuffered = _uiLogEntries
                        .Where(x => x.BusId.HasValue && x.BusId.Value == bus.Id)
                        .ToArray();
                    var errorsOnly = lastHour
                        .Where(x => x.Level.Equals("WARN", StringComparison.OrdinalIgnoreCase) ||
                                    x.Level.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                    var zipEntries = new List<SimpleZip.Entry>
                    {
                        ZipText("summary.json", BuildLineSummaryJson(bus, generated, cutoff, allBuffered), new UTF8Encoding(false)),
                        ZipText("events_last_hour.csv", BuildLineLogCsv(bus, lastHour), new UTF8Encoding(true)),
                        ZipText("events_session_buffer.csv", BuildLineLogCsv(bus, allBuffered), new UTF8Encoding(true)),
                        ZipText("errors_only.csv", BuildLineErrorsCsv(bus, errorsOnly), new UTF8Encoding(true)),
                        ZipText("messages_last_hour.log", string.Join(Environment.NewLine, lastHour.Select(x => x.Formatted).ToArray()) + Environment.NewLine, new UTF8Encoding(true)),
                        ZipText("statistics.txt", BuildLineStatisticsText(bus, generated, cutoff, allBuffered), new UTF8Encoding(true)),
                        ZipText("line_config_compact.json", BuildCompactLineConfigJson(bus), new UTF8Encoding(false)),
                        ZipText("line_config.json", JsonConvert.SerializeObject(bus, Formatting.Indented), new UTF8Encoding(false))
                    };
                    SimpleZip.WriteArchive(sfd.FileName, zipEntries);

                    _log.Info($"Диагностический пакет {bus.Name}: {sfd.FileName}");
                }
                catch (Exception ex)
                {
                    _log.Error("Диагностический пакет линии: " + ex.Message);
                    MessageBox.Show(this, ex.Message, "Диагностический пакет", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private string BuildLineErrorsCsv(ConfigNode bus, IEnumerable<LogEntry> source)
        {
            var names = _root == null
                ? new Dictionary<Guid, string>()
                : ConfigTree.DescendantsAndSelf(_root).ToDictionary(x => x.Id, x => x.Name);
            var sb = new StringBuilder();
            sb.AppendLine("Timestamp;Level;Line;Device;Category;Message");
            foreach (var entry in source)
            {
                string deviceName = string.Empty;
                if (entry.DeviceId.HasValue && !names.TryGetValue(entry.DeviceId.Value, out deviceName))
                    deviceName = entry.DeviceId.Value.ToString();
                sb.Append(CsvField(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))).Append(';')
                  .Append(CsvField(entry.Level)).Append(';')
                  .Append(CsvField(bus == null ? string.Empty : bus.Name)).Append(';')
                  .Append(CsvField(deviceName)).Append(';')
                  .Append(CsvField(ClassifyLogMessage(entry.Text))).Append(';')
                  .Append(CsvField(entry.Text)).AppendLine();
            }
            return sb.ToString();
        }

        private static string ClassifyLogMessage(string message)
        {
            var text = message ?? string.Empty;
            if (text.IndexOf("CRC", StringComparison.OrdinalIgnoreCase) >= 0) return "CRC";
            if (text.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("таймаут", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("без ответа", StringComparison.OrdinalIgnoreCase) >= 0) return "Timeout";
            if (text.IndexOf("неверный адрес", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("wrong address", StringComparison.OrdinalIgnoreCase) >= 0) return "Address";
            if (text.IndexOf("COM", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("порт", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("системных ресурсов", StringComparison.OrdinalIgnoreCase) >= 0) return "Transport";
            if (text.IndexOf("ответ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("response", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("status", StringComparison.OrdinalIgnoreCase) >= 0) return "Protocol";
            return "Other";
        }

        private string BuildLineSummaryJson(ConfigNode bus, DateTime generated, DateTime cutoff, LogEntry[] allBuffered)
        {
            var line = _statistics.GetLine(bus.Id);
            var errors = _statistics.GetLineErrors(bus.Id);
            var sessionStart = allBuffered.Length == 0 ? (DateTime?)null : allBuffered.Min(x => x.Timestamp);
            var devices = bus.Children.Where(x => x.Kind == ConfigNodeKind.Device).Select(device =>
            {
                var d = _statistics.GetDeviceErrors(device.Id);
                return new
                {
                    Name = device.Name,
                    Errors1Hour = d.Total,
                    StartupSystemErrors1Hour = d.Startup,
                    Timeout = d.Timeout,
                    CRC = d.Crc,
                    WrongAddress = d.Address,
                    Transport = d.Transport,
                    Protocol = d.Protocol,
                    Other = d.Other
                };
            }).ToArray();

            var summary = new
            {
                Version = "0.3.2.4",
                Generated = generated,
                LastHourWindowStart = cutoff,
                SessionBufferStart = sessionStart,
                Line = bus.Name,
                COM = MppSettings.GetComName(bus),
                Serial = new
                {
                    BaudRate = MppSettings.GetInt(bus, "COMSpeed", 9600),
                    DataBits = MppSettings.GetInt(bus, "COMData", 8),
                    Parity = MppSettings.GetParity(bus).ToString(),
                    StopBits = MppSettings.GetStopBits(bus).ToString()
                },
                Utilization = new { Last5Minutes = line.Utilization5Min, LastHour = line.Utilization1Hour },
                NoResponseOccupancy = new { Last5Minutes = line.TimeoutOccupancy5Min, LastHour = line.TimeoutOccupancy1Hour },
                Transactions1Hour = line.Transactions1Hour,
                NoResponseTransactions1Hour = line.NoResponse1Hour,
                ReadErrors1Hour = errors.Total,
                StartupSystemErrors1Hour = errors.Startup,
                AverageTransactionMs1Hour = line.AverageTransactionMs1Hour,
                MaximumTransactionMs1Hour = line.MaxTransactionMs1Hour,
                TxBytes1Hour = line.TxBytes1Hour,
                RxBytes1Hour = line.RxBytes1Hour,
                Devices = devices
            };
            return JsonConvert.SerializeObject(summary, Formatting.Indented);
        }

        private string BuildCompactLineConfigJson(ConfigNode bus)
        {
            var plugin = MppSettings.GetString(bus, "TypePlugin", string.Empty);
            var devices = bus.Children.Where(x => x.Kind == ConfigNodeKind.Device).Select(device => new
            {
                Name = device.Name,
                Enabled = device.ConfiguredEnabled,
                Address = IsSet4Device(device) ? MppSettings.GetSet4Address(device) : MppSettings.GetMercuryAddress(device),
                AnswerTimeoutMs = MppSettings.GetInt(device, "AnswerTimeOut", 2000),
                RetryCount = MppSettings.GetInt(device, "RepeatCount", 2),
                ReadingInterval = MppSettings.GetInt(device, "ReadingInterval", 60),
                ReadingIntervalUnit = MppSettings.GetString(device, "TReadingInterval", "с"),
                Tags = ConfigTree.Tags(device).Count(),
                Comment = MppSettings.GetString(device, "Comment", string.Empty)
            }).ToArray();

            var compact = new
            {
                Name = bus.Name,
                Enabled = bus.ConfiguredEnabled,
                Driver = plugin,
                COM = MppSettings.GetComName(bus),
                BaudRate = MppSettings.GetInt(bus, "COMSpeed", 9600),
                DataBits = MppSettings.GetInt(bus, "COMData", 8),
                Parity = MppSettings.GetParity(bus).ToString(),
                StopBits = MppSettings.GetStopBits(bus).ToString(),
                Devices = devices
            };
            return JsonConvert.SerializeObject(compact, Formatting.Indented);
        }

        private string BuildLineStatisticsText(ConfigNode bus, DateTime generated, DateTime cutoff, LogEntry[] allBuffered)
        {
            var line = _statistics.GetLine(bus.Id);
            var errors = _statistics.GetLineErrors(bus.Id);
            var sb = new StringBuilder();
            sb.AppendLine("ASTUE MPS Replacement v0.3.2.4");
            sb.AppendLine("Generated: " + generated.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            sb.AppendLine("Last-hour window: " + cutoff.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " -> " + generated.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            if (allBuffered != null && allBuffered.Length > 0)
                sb.AppendLine("Session buffer: " + allBuffered.Min(x => x.Timestamp).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " -> " + generated.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            else
                sb.AppendLine("Session buffer: empty");
            sb.AppendLine("Line: " + bus.Name);
            sb.AppendLine("COM: " + MppSettings.GetComName(bus));
            sb.AppendLine("Serial: " + MppSettings.GetInt(bus, "COMSpeed", 9600) + ", " +
                          MppSettings.GetInt(bus, "COMData", 8) + ", " + MppSettings.GetParity(bus) + ", " + MppSettings.GetStopBits(bus));
            sb.AppendLine();
            sb.AppendLine("Utilization 5 min: " + line.Utilization5Min.ToString("0.0", CultureInfo.InvariantCulture) + " %");
            sb.AppendLine("Utilization 1 hour: " + line.Utilization1Hour.ToString("0.0", CultureInfo.InvariantCulture) + " %");
            sb.AppendLine("No-response occupancy 5 min: " + line.TimeoutOccupancy5Min.ToString("0.0", CultureInfo.InvariantCulture) + " %");
            sb.AppendLine("No-response occupancy 1 hour: " + line.TimeoutOccupancy1Hour.ToString("0.0", CultureInfo.InvariantCulture) + " %");
            sb.AppendLine("Transactions 1 hour: " + line.Transactions1Hour);
            sb.AppendLine("No-response transactions 1 hour: " + line.NoResponse1Hour);
            sb.AppendLine("Read errors 1 hour: " + errors.Total);
            sb.AppendLine("Startup/system errors 1 hour: " + errors.Startup);
            sb.AppendLine("Average transaction: " + line.AverageTransactionMs1Hour.ToString("0.0", CultureInfo.InvariantCulture) + " ms");
            sb.AppendLine("Maximum transaction: " + line.MaxTransactionMs1Hour.ToString("0.0", CultureInfo.InvariantCulture) + " ms");
            sb.AppendLine("TX/RX 1 hour: " + line.TxBytes1Hour + " / " + line.RxBytes1Hour + " bytes");
            sb.AppendLine();
            sb.AppendLine("Operational error categories 1 hour:");
            sb.AppendLine("  Timeout: " + errors.Timeout);
            sb.AppendLine("  CRC: " + errors.Crc);
            sb.AppendLine("  Wrong address: " + errors.Address);
            sb.AppendLine("  Transport: " + errors.Transport);
            sb.AppendLine("  Protocol: " + errors.Protocol);
            sb.AppendLine("  Other: " + errors.Other);
            sb.AppendLine();
            sb.AppendLine("Devices:");
            foreach (var device in bus.Children.Where(x => x.Kind == ConfigNodeKind.Device))
            {
                var d = _statistics.GetDeviceErrors(device.Id);
                sb.AppendLine("  " + device.Name + ": errors=" + d.Total +
                              ", startup/system=" + d.Startup +
                              ", timeout=" + d.Timeout + ", crc=" + d.Crc +
                              ", address=" + d.Address + ", transport=" + d.Transport +
                              ", protocol=" + d.Protocol + ", other=" + d.Other);
            }
            return sb.ToString();
        }

        private static SimpleZip.Entry ZipText(string name, string text, Encoding encoding)
        {
            return new SimpleZip.Entry(name, encoding.GetBytes(text ?? string.Empty));
        }

        private static string CsvField(string value)
        {
            value = value ?? string.Empty;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static string SafeFileName(string value)
        {
            var text = string.IsNullOrWhiteSpace(value) ? "COM_line" : value.Trim();
            foreach (var c in Path.GetInvalidFileNameChars()) text = text.Replace(c, '_');
            return text;
        }

        private void ResetSelectedLineStatistics()
        {
            var bus = SelectedBusForLineAction();
            if (bus == null) return;
            var deviceIds = bus.Children.Where(x => x.Kind == ConfigNodeKind.Device).Select(x => x.Id).ToArray();
            _statistics.ResetLine(bus.Id, deviceIds);
            RefreshTreeNodeFor(bus);
            foreach (var d in bus.Children.Where(x => x.Kind == ConfigNodeKind.Device)) RefreshTreeNodeFor(d);
            ShowProperties(bus);
            _log.Info("Статистика линии сброшена: " + bus.Name);
        }

        private void ImportMpp()
        {
            using (var ofd = new OpenFileDialog { Filter = "MPS project (*.mpp)|*.mpp|Все файлы|*.*" })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    Cursor = Cursors.WaitCursor;
                    StopPolling();
                    _cache.Clear();
                    _root = MppImporter.Import(ofd.FileName, _log.Info);
                    _currentProjectPath = "MPP: " + ofd.FileName;
                    BuildTree();
                    _projectDirty = true;
                    SetEditMode(true);
                    UpdateToolbarStatus();
                    UpdateWindowTitle();
                }
                catch (Exception ex)
                {
                    _log.Error(ex.ToString());
                    MessageBox.Show(this, ex.Message, "Импорт MPP", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally { Cursor = Cursors.Default; }
            }
        }

        private void OpenProject()
        {
            using (var ofd = new OpenFileDialog
            {
                Filter = "ASTUE project (*.astue;*.astue.json)|*.astue;*.astue.json|ASTUE package (*.astue)|*.astue|Legacy ASTUE JSON (*.astue.json)|*.astue.json|Все файлы|*.*"
            })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                LoadProject(ofd.FileName, true);
            }
        }

        private bool LoadProject(string path, bool showErrors)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    _log.Warn("Файл проекта не найден: " + path);
                    return false;
                }

                var preserveConfigurationMode = _editMode;
                StopPolling();
                _cache.Clear();
                Cursor = Cursors.WaitCursor;

                var fi = new FileInfo(path);
                var format = ProjectSerializer.DescribeFormat(path);
                _log.Info(string.Format("Открытие проекта: {0} ({1:0.0} MiB, {2})...", path, fi.Length / 1024.0 / 1024.0, format));
                if (format == "ASTUE JSON package" || format == "legacy JSON")
                    _log.Warn("Открывается старый JSON-формат. После загрузки сохраните проект как новый .astue (ASTUE binary v1) — последующие открытия будут значительно быстрее.");

                var totalSw = Stopwatch.StartNew();
                var modelSw = Stopwatch.StartNew();
                var loaded = ProjectSerializer.Load(path);
                modelSw.Stop();

                var treeSw = Stopwatch.StartNew();
                _root = loaded;
                BuildTree();
                treeSw.Stop();

                totalSw.Stop();
                _currentProjectPath = path;
                _projectDirty = false;
                RuntimeControl.SetRuntimeProjectPath(path);
                SetEditMode(preserveConfigurationMode);
                UpdateToolbarStatus();
                UpdateWindowTitle();
                _userSettings.LastProjectPath = path;
                _userSettings.Save(_log.Warn);
                _log.Info(string.Format("Проект открыт: модель={0:0.0} с, дерево={1:0.0} с, всего={2:0.0} с: {3}", modelSw.Elapsed.TotalSeconds, treeSw.Elapsed.TotalSeconds, totalSw.Elapsed.TotalSeconds, path));
                return true;
            }
            catch (Exception ex)
            {
                _log.Error(ex.ToString());
                if (showErrors)
                    MessageBox.Show(this, ex.Message, "Открыть ASTUE", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void TryOpenLastProject()
        {
            _userSettings.Load(_log.Warn);

            string path = null;
            if (_startupOptions.RuntimeMode)
                path = RuntimeControl.GetRuntimeProjectPath();
            if (string.IsNullOrWhiteSpace(path))
                path = _userSettings.LastProjectPath;

            if (string.IsNullOrWhiteSpace(path))
            {
                _log.Info("Последний рабочий проект не задан.");
                return;
            }

            if (!File.Exists(path))
            {
                _log.Warn("Рабочий проект не найден, автозагрузка пропущена: " + path);
                return;
            }

            _log.Info((_startupOptions.StartedByOpc ? "OPC demand: загрузка рабочего проекта: " : "Автозагрузка последнего проекта: ") + path);
            var loaded = LoadProject(path, false);
            if (!loaded) return;

            if (_startupOptions.StartedByOpc)
            {
                _log.Info("OPC demand получен. При разрешённом автозапуске опрос будет запущен автоматически.");
                RuntimeControlTick();
            }
            else
            {
                _log.Info(_editMode
                    ? "Проект загружен в режиме конфигурирования: опрос и OPC DA не запускаются."
                    : "Проект загружен в рабочем режиме. Опрос можно запустить вручную или по запросу OPC-клиента.");
            }
        }

        private void SaveProject()
        {
            if (_root == null) return;
            using (var sfd = new SaveFileDialog
            {
                Filter = "ASTUE project (*.astue)|*.astue|Legacy JSON (*.astue.json)|*.astue.json",
                FileName = "astue_project.astue",
                AddExtension = false
            })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                var path = NormalizeAstuePath(sfd.FileName);
                try
                {
                    Cursor = Cursors.WaitCursor;
                    _log.Info("Сохранение проекта: " + path);
                    var sw = Stopwatch.StartNew();

                    ProjectSerializer.Save(path, _root);

                    sw.Stop();
                    _currentProjectPath = path;
                    RuntimeControl.SetRuntimeProjectPath(path);
                    _userSettings.LastProjectPath = path;
                    _userSettings.Save(_log.Warn);
                    _projectDirty = false;
                    UpdateToolbarStatus();
                    UpdateWindowTitle();
                    var fi = new FileInfo(path);
                    _log.Info(string.Format("Проект сохранён за {0:0.0} с: {1} ({2:0.0} MiB, {3})", sw.Elapsed.TotalSeconds, path, fi.Length / 1024.0 / 1024.0, ProjectSerializer.DescribeFormat(path)));
                }
                catch (Exception ex)
                {
                    _log.Error(ex.ToString());
                    MessageBox.Show(this, ex.Message, "Сохранить ASTUE", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    Cursor = Cursors.Default;
                }
            }
        }

        private static string NormalizeAstuePath(string path)
        {
            if (path.EndsWith(".astue", StringComparison.OrdinalIgnoreCase)) return path;
            if (path.EndsWith(".astue.json", StringComparison.OrdinalIgnoreCase)) return path;
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                return path.Substring(0, path.Length - 5) + ".astue.json";
            return path + ".astue";
        }

        private void BuildTree()
        {
            _internalCheck = true;
            try
            {
                _tree.BeginUpdate();
                _tree.Nodes.Clear();
                _treeNodes.Clear();
                if (_root != null)
                {
                    // Ленивое дерево: при открытии создаём только Server и его линии.
                    // Тысячи групп/тегов материализуются только при раскрытии ветки.
                    var rootNode = BuildTreeNodeShallow(_root);
                    _tree.Nodes.Add(rootNode);
                    PopulateTreeNode(rootNode);
                    RefreshAllTreeDisplay();
                    rootNode.Expand();
                    rootNode.EnsureVisible();
                }
            }
            finally
            {
                _tree.EndUpdate();
                _internalCheck = false;
            }

            // Качество отключённых тегов инициализируем без тысяч UI-событий.
            InitializeDisabledQuality();
            RefreshEffectiveColors();
        }

        private TreeNode BuildTreeNodeShallow(ConfigNode node)
        {
            var tn = new TreeNode
            {
                Tag = node,
                Checked = node.ConfiguredEnabled,
                ToolTipText = node.OpcItemId,
                Text = node.Name
            };
            _treeNodes[node.Id] = tn;
            if (node.Children != null && node.Children.Count > 0)
                tn.Nodes.Add(new TreeNode("…") { Tag = null, Name = "__LAZY__" });
            return tn;
        }

        private void TreeBeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            PopulateTreeNode(e.Node);
        }

        private void PopulateTreeNode(TreeNode tn)
        {
            if (tn == null) return;
            var node = tn.Tag as ConfigNode;
            if (node == null) return;
            if (tn.Nodes.Count != 1 || tn.Nodes[0].Tag != null) return;

            var oldInternal = _internalCheck;
            _internalCheck = true;
            try
            {
                tn.Nodes.Clear();
                foreach (var child in node.Children)
                {
                    var childTree = BuildTreeNodeShallow(child);
                    tn.Nodes.Add(childTree);
                    RefreshTreeNodeDisplay(childTree, false);
                    RefreshNodeColorSingle(childTree);
                }
            }
            finally
            {
                _internalCheck = oldInternal;
            }
        }

        private void RefreshAllTreeDisplay()
        {
            foreach (TreeNode n in _tree.Nodes)
                RefreshTreeNodeDisplay(n, true);
        }

        private void RefreshTreeNodeDisplay(TreeNode tn, bool recursive)
        {
            if (tn == null) return;
            var node = tn.Tag as ConfigNode;
            if (node != null)
            {
                var errors = string.Empty;
                var lineLoad = string.Empty;
                var tooltip = node.OpcItemId;

                if (node.Kind == ConfigNodeKind.Bus)
                {
                    var errorStats = _statistics.GetLineErrors(node.Id);
                    var lineStats = _statistics.GetLine(node.Id);
                    errors = errorStats.Total.ToString(CultureInfo.InvariantCulture);
                    lineLoad = lineStats.Utilization5Min.ToString("0.0", CultureInfo.CurrentCulture) + "% / " +
                               lineStats.Utilization1Hour.ToString("0.0", CultureInfo.CurrentCulture) + "%";
                    tooltip += Environment.NewLine +
                               "Ошибки чтения за 1 ч: " + errorStats.Total + Environment.NewLine +
                               "Ошибки запуска/системы за 1 ч: " + errorStats.Startup + Environment.NewLine +
                               "Загрузка 5 мин / 1 ч: " + lineLoad + Environment.NewLine +
                               "Ожидание без ответа 1 ч: " + lineStats.TimeoutOccupancy1Hour.ToString("0.0", CultureInfo.CurrentCulture) + "%" + Environment.NewLine +
                               "Транзакций за 1 ч: " + lineStats.Transactions1Hour;
                }
                else if (node.Kind == ConfigNodeKind.Device)
                {
                    var errorStats = _statistics.GetDeviceErrors(node.Id);
                    errors = errorStats.Total.ToString(CultureInfo.InvariantCulture);
                    tooltip += Environment.NewLine +
                               "Ошибки чтения за 1 ч: " + errorStats.Total +
                               ", запуск/система=" + errorStats.Startup +
                               " (timeout=" + errorStats.Timeout +
                               ", CRC=" + errorStats.Crc +
                               ", address=" + errorStats.Address +
                               ", transport=" + errorStats.Transport +
                               ", protocol=" + errorStats.Protocol + ")";
                    if (errorStats.LastErrorLocal != DateTime.MinValue)
                        tooltip += Environment.NewLine + "Последняя ошибка: " +
                                   errorStats.LastErrorLocal.ToString("dd.MM HH:mm:ss", CultureInfo.CurrentCulture) +
                                   "  " + errorStats.LastMessage;
                }

                if (node.Kind == ConfigNodeKind.Tag)
                {
                    var live = _cache.Get(node);
                    var value = live == null ? string.Empty : FormatValue(node, live.Value);
                    var quality = live == null ? string.Empty : FormatQuality(live.Quality);
                    var timestamp = live == null || live.Timestamp == DateTime.MinValue
                        ? string.Empty
                        : live.Timestamp.ToString("dd.MM HH:mm:ss.fff", CultureInfo.CurrentCulture);
                    tn.Text = FormatTreeLine(node.Name, tn.Level, value, quality, timestamp, errors, lineLoad);
                }
                else
                {
                    tn.Text = FormatTreeLine(node.Name, tn.Level, string.Empty, string.Empty, string.Empty, errors, lineLoad);
                }

                tn.ToolTipText = tooltip;
            }

            if (recursive)
                foreach (TreeNode child in tn.Nodes)
                    RefreshTreeNodeDisplay(child, true);
        }

        private string FormatTreeLine(string name, int level, string value, string quality, string timestamp, string errors, string lineLoad)
        {
            var nameWidth = Math.Max(18, _treeNameWidth - (level * 3));
            return Fit(name, nameWidth) + " " +
                   Fit(value, _treeValueWidth) + " " +
                   Fit(quality, _treeQualityWidth) + " " +
                   Fit(timestamp, _treeTimeWidth) + " " +
                   Fit(errors, _treeErrorsWidth) + " " +
                   Fit(lineLoad, _treeLoadWidth);
        }

        private void RefreshStatisticsUi()
        {
            if (IsDisposed || !IsHandleCreated || _tree.Nodes.Count == 0) return;
            _tree.BeginUpdate();
            try
            {
                foreach (TreeNode root in _tree.Nodes)
                    RefreshStatisticsTreeNode(root);
            }
            finally
            {
                _tree.EndUpdate();
            }

            if (!_editMode)
            {
                var selected = SelectedConfigNode();
                if (selected != null && (selected.Kind == ConfigNodeKind.Bus || selected.Kind == ConfigNodeKind.Device))
                    ShowProperties(selected);
            }
            UpdateOpcButtonStatus();
        }

        private void RefreshStatisticsTreeNode(TreeNode tn)
        {
            if (tn == null) return;
            var node = tn.Tag as ConfigNode;
            if (node != null && (node.Kind == ConfigNodeKind.Bus || node.Kind == ConfigNodeKind.Device))
                RefreshTreeNodeDisplay(tn, false);

            foreach (TreeNode child in tn.Nodes)
                if (child.Tag != null) RefreshStatisticsTreeNode(child);
        }

        private static string Fit(string text, int width)
        {
            text = text ?? string.Empty;
            if (text.Length > width)
                return width <= 1 ? text.Substring(0, width) : text.Substring(0, width - 1) + "…";
            return text.PadRight(width);
        }

        private static string FormatValue(ConfigNode tag, object value)
        {
            if (value == null) return string.Empty;

            string text;
            if (value is float || value is double || value is decimal)
                text = Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("0.###", CultureInfo.CurrentCulture);
            else if (value is bool)
                text = ((bool)value) ? "true" : "false";
            else
                text = Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;

            var unit = GetUnit(tag);
            return string.IsNullOrEmpty(unit) ? text : text + " " + unit;
        }

        private static string GetUnit(ConfigNode tag)
        {
            if (tag == null) return string.Empty;
            var id = tag.OpcItemId ?? string.Empty;

            if (id.IndexOf(".SP.P.", StringComparison.OrdinalIgnoreCase) >= 0) return "W";
            if (id.IndexOf(".SP.Q.", StringComparison.OrdinalIgnoreCase) >= 0) return "var";
            if (id.IndexOf(".SP.S.", StringComparison.OrdinalIgnoreCase) >= 0) return "VA";
            if (id.IndexOf(".SP.U.", StringComparison.OrdinalIgnoreCase) >= 0) return "V";
            if (id.IndexOf(".SP.I.", StringComparison.OrdinalIgnoreCase) >= 0) return "A";
            if (id.IndexOf(".SP.ANGLE.", StringComparison.OrdinalIgnoreCase) >= 0) return "°";
            if (id.EndsWith(".SP.Frequency", StringComparison.OrdinalIgnoreCase)) return "Hz";
            if (id.IndexOf(".Energy.", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (id.IndexOf(".A+.", StringComparison.OrdinalIgnoreCase) >= 0) return "kWh";
                if (id.IndexOf(".R+.", StringComparison.OrdinalIgnoreCase) >= 0) return "kvarh";
            }
            if (id.EndsWith(".PowerProfile.A+", StringComparison.OrdinalIgnoreCase) ||
                id.EndsWith(".PowerProfile.A-", StringComparison.OrdinalIgnoreCase)) return "kW";
            if (id.EndsWith(".PowerProfile.R+", StringComparison.OrdinalIgnoreCase) ||
                id.EndsWith(".PowerProfile.R-", StringComparison.OrdinalIgnoreCase)) return "kvar";
            if (id.EndsWith(".PowerProfile.Period", StringComparison.OrdinalIgnoreCase)) return "min";
            return string.Empty;
        }

        private static string FormatQuality(ValueQuality quality)
        {
            switch (quality)
            {
                case ValueQuality.Good: return "Хорошее";
                case ValueQuality.BadCommunication: return "Ошибка связи";
                case ValueQuality.BadOutOfService: return "Отключено";
                case ValueQuality.Uncertain: return "Неопределённое";
                default: return quality.ToString();
            }
        }

        private void TreeBeforeCheck(object sender, TreeViewCancelEventArgs e)
        {
            if (_internalCheck) return;
            if (_editMode) return;

            // В рабочем режиме чекбоксы являются только индикаторами состояния.
            // Это исключает случайное отключение тега простым кликом по дереву.
            e.Cancel = true;
            if (e.Node != null) _tree.SelectedNode = e.Node;
        }

        private void TreeAfterCheck(object sender, TreeViewEventArgs e)
        {
            if (_internalCheck || !_editMode) return;
            var n = e.Node.Tag as ConfigNode;
            if (n == null) return;

            n.ConfiguredEnabled = e.Node.Checked;
            n.Properties["Enabled"] = e.Node.Checked ? "true" : "false";
            MarkProjectDirty();
            _opcPublisher?.PublishNow();
            _log.Info($"Опрос {(e.Node.Checked ? "ВКЛ" : "ВЫКЛ")}: {n.OpcItemId}");

            if (!n.EffectiveEnabled)
                MarkOutOfService(n);

            RefreshEffectiveColors();
            ShowProperties(n);
        }

        private void InitializeDisabledQuality()
        {
            if (_root == null) return;
            foreach (var tag in ConfigTree.Tags(_root))
                if (!tag.EffectiveEnabled)
                    _cache.MarkQuality(tag, ValueQuality.BadOutOfService, true, false);
        }

        private void MarkOutOfService(ConfigNode node)
        {
            foreach (var tag in ConfigTree.Tags(node))
                _cache.MarkQuality(tag, ValueQuality.BadOutOfService, true, false);
        }

        private void RefreshEffectiveColors()
        {
            foreach (TreeNode n in _tree.Nodes) RefreshNodeColor(n);
        }

        private void RefreshNodeColor(TreeNode tn)
        {
            RefreshNodeColorSingle(tn);
            foreach (TreeNode c in tn.Nodes)
                if (c.Tag != null) RefreshNodeColor(c);
        }

        private void RefreshNodeColorSingle(TreeNode tn)
        {
            var n = tn == null ? null : tn.Tag as ConfigNode;
            if (n == null) return;
            tn.ForeColor = n.EffectiveEnabled
                ? SystemColors.WindowText
                : (n.ConfiguredEnabled ? Color.Gray : Color.Firebrick);
            RefreshTreeNodeDisplay(tn, false);
        }

        private ConfigNode SelectedConfigNode()
        {
            return _tree.SelectedNode == null ? null : _tree.SelectedNode.Tag as ConfigNode;
        }

        private void ShowProperties(ConfigNode n)
        {
            _props.Rows.Clear();
            if (n == null) return;

            // В обычной панели показываем только параметры, которые действительно нужны
            // оператору/наладчику. Служебные поля импортированного MPS остаются в модели,
            // но не засоряют интерфейс.
            if (n.Kind == ConfigNodeKind.Main)
            {
                AddPropertyRow("NameInTree", "Имя в дереве", n.Name);
                AddPropertyRow("Comment", "Комментарий", MppSettings.GetString(n, "Comment", string.Empty));
                return;
            }

            if (n.Kind == ConfigNodeKind.Bus)
            {
                AddPropertyRow("NameInTree", "Имя линии", n.Name);
                AddPropertyRow("Enabled", "Включено", FormatBoolRu(n.ConfiguredEnabled));
                if (_editMode) AddComPortProp(MppSettings.GetString(n, "COMPort", "1"));
                else AddPropertyRow("COMPort", "COM-порт", MppSettings.GetComName(n));
                AddPropertyRow("COMSpeed", "Скорость, бод", MppSettings.GetInt(n, "COMSpeed", 9600).ToString(CultureInfo.InvariantCulture));
                AddPropertyRow("COMData", "Биты данных", MppSettings.GetInt(n, "COMData", 8).ToString(CultureInfo.InvariantCulture));
                AddPropertyRow("COMParitet", "Чётность", FormatParityRu(MppSettings.GetParity(n)));
                AddPropertyRow("COMStop", "Стоп-биты", MppSettings.GetInt(n, "COMStop", 1).ToString(CultureInfo.InvariantCulture));
                AddPropertyRow("Comment", "Комментарий", MppSettings.GetString(n, "Comment", string.Empty));

                var line = _statistics.GetLine(n.Id);
                var errors = _statistics.GetLineErrors(n.Id);
                AddPropertyRow("Live.ErrorsLastHour", "Ошибки чтения за 1 час", errors.Total.ToString(CultureInfo.InvariantCulture), true);
                AddPropertyRow("Live.StartupErrorsLastHour", "Ошибки запуска/системы за 1 час", errors.Startup.ToString(CultureInfo.InvariantCulture), true);
                AddPropertyRow("Live.LineLoad", "Загрузка (5 мин / 1 час)",
                    line.Utilization5Min.ToString("0.0", CultureInfo.CurrentCulture) + " % / " +
                    line.Utilization1Hour.ToString("0.0", CultureInfo.CurrentCulture) + " %", true);
                AddPropertyRow("Live.Transactions1h", "Транзакций за 1 час", line.Transactions1Hour.ToString(CultureInfo.InvariantCulture), true);
                AddPropertyRow("Live.NoResponse1h", "Без ответа за 1 час", line.NoResponse1Hour.ToString(CultureInfo.InvariantCulture), true);
                return;
            }

            if (n.Kind == ConfigNodeKind.Device)
            {
                AddPropertyRow("NameInTree", "Имя устройства", n.Name);
                AddPropertyRow("Enabled", "Включено", FormatBoolRu(n.ConfiguredEnabled));
                if (IsMercuryDevice(n))
                    AddPropertyRow("DeviceAddress", "Адрес устройства", MppSettings.GetMercuryAddress(n).ToString(CultureInfo.InvariantCulture));
                else if (IsSet4Device(n))
                    AddPropertyRow("DeviceAddress", "Адрес устройства", MppSettings.GetSet4Address(n).ToString(CultureInfo.InvariantCulture));

                AddPropertyRow("AnswerTimeOut", "Таймаут ответа, мс", MppSettings.GetInt(n, "AnswerTimeOut", 2000).ToString(CultureInfo.InvariantCulture));
                AddPropertyRow("RepeatCount", "Повторов при ошибке", MppSettings.GetInt(n, "RepeatCount", 2).ToString(CultureInfo.InvariantCulture));
                var intervalUnit = MppSettings.GetString(n, "TReadingInterval", "с").Trim();
                AddPropertyRow("ReadingInterval", "Интервал чтения, " + (string.IsNullOrWhiteSpace(intervalUnit) ? "с" : intervalUnit),
                    MppSettings.GetInt(n, "ReadingInterval", 60).ToString(CultureInfo.InvariantCulture));
                AddPropertyRow("Comment", "Комментарий", MppSettings.GetString(n, "Comment", string.Empty));

                var errors = _statistics.GetDeviceErrors(n.Id);
                AddPropertyRow("Live.ErrorsLastHour", "Ошибки чтения за 1 час", errors.Total.ToString(CultureInfo.InvariantCulture), true);
                AddPropertyRow("Live.StartupErrorsLastHour", "Ошибки запуска/системы за 1 час", errors.Startup.ToString(CultureInfo.InvariantCulture), true);
                if (errors.LastErrorLocal != DateTime.MinValue)
                    AddPropertyRow("Live.LastReadError", "Последняя ошибка чтения",
                        errors.LastErrorLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) + "  " + errors.LastMessage, true);
                return;
            }

            if (n.Kind == ConfigNodeKind.Tag)
            {
                AddPropertyRow("NameInTree", "Имя тега", n.Name);
                AddPropertyRow("Enabled", "Включено", FormatBoolRu(n.ConfiguredEnabled));
                AddPropertyRow("OPC ItemID", "OPC ItemID", n.OpcItemId, true);
                AddExistingPropertyRow(n, "Type", "Тип данных", true);
                string access;
                if (n.Properties.TryGetValue("Access", out access))
                    AddPropertyRow("Access", "Доступ", FormatAccessRu(access), true);
                AddPropertyRow("Comment", "Комментарий", MppSettings.GetString(n, "Comment", string.Empty));

                var live = _cache.Get(n);
                AddPropertyRow("Live.Value", "Текущее значение", live == null || live.Value == null
                    ? string.Empty
                    : Convert.ToString(live.Value, CultureInfo.CurrentCulture), true);
                AddPropertyRow("Live.Quality", "Качество", live == null ? string.Empty : FormatQuality(live.Quality), true);
                AddPropertyRow("Live.Timestamp", "Обновлено", live == null || live.Timestamp == DateTime.MinValue
                    ? string.Empty
                    : live.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.CurrentCulture), true);
                return;
            }

            // Группы и подустройства: имя/включение/комментарий и вычисляемый OPC-путь.
            AddPropertyRow("NameInTree", "Имя в дереве", n.Name);
            AddPropertyRow("Enabled", "Включено", FormatBoolRu(n.ConfiguredEnabled));
            AddPropertyRow("OPC ItemID", "OPC путь", n.OpcItemId, true);
            AddPropertyRow("Comment", "Комментарий", MppSettings.GetString(n, "Comment", string.Empty));
        }

        private static string FormatBoolRu(bool value) => value ? "Да" : "Нет";

        private static string FormatParityRu(Parity parity)
        {
            switch (parity)
            {
                case Parity.Odd: return "Нечётная";
                case Parity.Even: return "Чётная";
                case Parity.Mark: return "Mark";
                case Parity.Space: return "Space";
                default: return "Нет";
            }
        }

        private static string FormatAccessRu(string value)
        {
            var text = (value ?? string.Empty).Trim();
            if (text.Equals("ReadOnly", StringComparison.OrdinalIgnoreCase)) return "Только чтение";
            if (text.Equals("ReadWrite", StringComparison.OrdinalIgnoreCase)) return "Чтение / запись";
            if (text.Equals("WriteOnly", StringComparison.OrdinalIgnoreCase)) return "Только запись";
            return text;
        }

        private static bool ParseBoolUi(string value, bool defaultValue)
        {
            var text = (value ?? string.Empty).Trim();
            if (text.Equals("да", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("вкл", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("включено", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Equals("нет", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("выкл", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("выключено", StringComparison.OrdinalIgnoreCase)) return false;
            return ConfigNode.ParseBool(text, defaultValue);
        }

        private void AddExistingPropertyRow(ConfigNode node, string key, string caption, bool readOnly = false)
        {
            string value;
            if (node != null && node.Properties.TryGetValue(key, out value))
                AddPropertyRow(key, caption, value, readOnly);
        }

        private int AddPropertyRow(string key, string caption, string value, bool readOnly = false)
        {
            var rowIndex = _props.Rows.Add(caption, value ?? string.Empty);
            _props.Rows[rowIndex].Tag = key;
            _props.Rows[rowIndex].Cells[1].ReadOnly = readOnly;
            return rowIndex;
        }

        private static bool IsMercuryDevice(ConfigNode node)
        {
            var bus = ConfigTree.AncestorOrSelf(node, ConfigNodeKind.Bus);
            return bus != null &&
                   MppSettings.GetString(bus, "TypePlugin", string.Empty)
                       .Equals("MERCURY", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSet4Device(ConfigNode node)
        {
            var bus = ConfigTree.AncestorOrSelf(node, ConfigNodeKind.Bus);
            if (bus == null) return false;
            var plugin = MppSettings.GetString(bus, "TypePlugin", string.Empty);
            return plugin.Equals("SET4", StringComparison.OrdinalIgnoreCase) ||
                   plugin.IndexOf("SET4", StringComparison.OrdinalIgnoreCase) >= 0;
        }


        private void AddComPortProp(string rawValue)
        {
            var current = NormalizeComDisplay(rawValue);
            var rowIndex = _props.Rows.Add("COM-порт", null);
            _props.Rows[rowIndex].Tag = "COMPort";
            var cell = new DataGridViewComboBoxCell
            {
                FlatStyle = FlatStyle.Flat,
                DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton
            };

            var ports = new List<string>();
            try { ports.AddRange(SerialPort.GetPortNames()); }
            catch (Exception ex) { _log.Warn("Не удалось получить список COM-портов: " + ex.Message); }

            // Помимо реально существующих портов разрешаем выбрать стандартный номер вручную
            // через список. Это удобно до/после переназначения Moxa RealCOM.
            for (var i = 1; i <= 255; i++) ports.Add("COM" + i.ToString(CultureInfo.InvariantCulture));
            ports.Add(current);
            ports = ports.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(PortNumber).ToList();

            foreach (var port in ports)
                cell.Items.Add(port);

            cell.Value = current;
            _props.Rows[rowIndex].Cells[1] = cell;
        }

        private void PropsCellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (!_editMode || e.RowIndex < 0 || e.ColumnIndex != 1) return;
            var cell = _props.Rows[e.RowIndex].Cells[e.ColumnIndex] as DataGridViewComboBoxCell;
            if (cell == null) return;

            _props.CurrentCell = cell;
            _props.BeginEdit(true);
            var combo = _props.EditingControl as DataGridViewComboBoxEditingControl;
            if (combo != null) combo.DroppedDown = true;
        }

        private void PropsEditingControlShowing(object sender, DataGridViewEditingControlShowingEventArgs e)
        {
            var combo = e.Control as DataGridViewComboBoxEditingControl;
            if (combo == null) return;

            combo.SelectionChangeCommitted -= ComPortSelectionCommitted;
            combo.SelectionChangeCommitted += ComPortSelectionCommitted;
        }

        private void ComPortSelectionCommitted(object sender, EventArgs e)
        {
            if (!_editMode) return;
            BeginInvoke((Action)(() => _props.EndEdit()));
        }

        private static string NormalizeComDisplay(string rawValue)
        {
            var s = (rawValue ?? string.Empty).Trim();
            if (s.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) return s.ToUpperInvariant();
            return "COM" + s;
        }

        private void PropsCellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            if (!_editMode) return;
            var n = SelectedConfigNode();
            if (n == null || e.RowIndex < 0) return;

            var row = _props.Rows[e.RowIndex];
            var key = Convert.ToString(row.Tag) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key)) return;
            var value = Convert.ToString(row.Cells[1].Value) ?? string.Empty;

            if (key.Equals("EffectiveEnabled", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("OPC ItemID", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Category", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("MercuryModel", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("MercuryCapabilities", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("SET4Mode", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("SET4Capabilities", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("Live.", StringComparison.OrdinalIgnoreCase))
                return;

            if (key.Equals("NameInTree", StringComparison.OrdinalIgnoreCase))
            {
                var newName = value.Trim();
                if (string.IsNullOrWhiteSpace(newName))
                {
                    MessageBox.Show(this, "Имя не может быть пустым.", "Переименование", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    BeginInvoke((Action)(() => ShowProperties(n)));
                    return;
                }

                if (!string.Equals(n.Name, newName, StringComparison.Ordinal))
                {
                    var affectedTags = ConfigTree.Tags(n).Count();
                    if (affectedTags > 0 && n.Kind != ConfigNodeKind.Main)
                    {
                        var oldPath = n.OpcItemId;
                        var oldName = n.Name;
                        n.Name = newName;
                        var newPath = n.OpcItemId;
                        n.Name = oldName;
                        var answer = MessageBox.Show(this,
                            "Переименование изменит OPC ItemID у " + affectedTags + " тег(ов).\r\n\r\n" +
                            "Старый путь: " + oldPath + "\r\n" +
                            "Новый путь:  " + newPath + "\r\n\r\n" +
                            "Это может потребовать изменения привязок в MasterSCADA. Продолжить?",
                            "Изменение OPC namespace", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                        if (answer != DialogResult.Yes)
                        {
                            BeginInvoke((Action)(() => ShowProperties(n)));
                            return;
                        }
                    }
                }

                n.Name = newName;
                n.Properties["NameInTree"] = n.Name;
                foreach (var descendant in ConfigTree.DescendantsAndSelf(n))
                {
                    if (descendant.Properties.ContainsKey("NodeId"))
                        descendant.Properties["NodeId"] = descendant.OpcItemId;
                    RefreshTreeNodeFor(descendant);
                }
            }
            else if (key.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
            {
                n.ConfiguredEnabled = ParseBoolUi(value, n.ConfiguredEnabled);
                n.Properties["Enabled"] = n.ConfiguredEnabled ? "true" : "false";
                _internalCheck = true;
                try
                {
                    if (_tree.SelectedNode != null)
                        _tree.SelectedNode.Checked = n.ConfiguredEnabled;
                }
                finally { _internalCheck = false; }

                if (!n.EffectiveEnabled)
                    MarkOutOfService(n);
                RefreshEffectiveColors();
            }
            else if (key.Equals("COMPort", StringComparison.OrdinalIgnoreCase) && n.Kind == ConfigNodeKind.Bus)
            {
                var port = value.Trim();
                if (port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                    port = port.Substring(3);

                int portNumber;
                if (!int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out portNumber) || portNumber < 1 || portNumber > 4096)
                {
                    MessageBox.Show(this, "Некорректный COM-порт: " + value, "COMPort", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    BeginInvoke((Action)(() => ShowProperties(n)));
                    return;
                }

                n.Properties["COMPort"] = portNumber.ToString(CultureInfo.InvariantCulture);
                _log.Info($"{n.OpcItemId}: COMPort=COM{portNumber}");
            }
            else if (key.Equals("DeviceAddress", StringComparison.OrdinalIgnoreCase) && n.Kind == ConfigNodeKind.Device)
            {
                int address;
                var maxAddress = IsSet4Device(n) ? 255 : 240;
                if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out address) || address < 0 || address > maxAddress)
                {
                    MessageBox.Show(this, "Адрес устройства должен быть в диапазоне 0.." + maxAddress + ".", "Адрес устройства", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    BeginInvoke((Action)(() => ShowProperties(n)));
                    return;
                }

                if (!MppSettings.SetDeviceAddress(n, address))
                {
                    MessageBox.Show(this, "Не удалось сохранить адрес устройства.", "Адрес устройства", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    BeginInvoke((Action)(() => ShowProperties(n)));
                    return;
                }

                _log.Info($"{n.OpcItemId}: DeviceAddress={address}");
            }
            else if (n.Kind == ConfigNodeKind.Bus && key.Equals("COMSpeed", StringComparison.OrdinalIgnoreCase))
            {
                int number;
                if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) || number < 300 || number > 2000000)
                {
                    MessageBox.Show(this, "Скорость должна быть целым числом 300..2000000 бод.", "Скорость COM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    BeginInvoke((Action)(() => ShowProperties(n)));
                    return;
                }
                n.Properties["COMSpeed"] = number.ToString(CultureInfo.InvariantCulture);
            }
            else if (n.Kind == ConfigNodeKind.Bus && key.Equals("COMData", StringComparison.OrdinalIgnoreCase))
            {
                int number;
                if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) || number < 5 || number > 8)
                {
                    MessageBox.Show(this, "Биты данных: 5..8.", "COM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    BeginInvoke((Action)(() => ShowProperties(n)));
                    return;
                }
                n.Properties["COMData"] = number.ToString(CultureInfo.InvariantCulture);
            }
            else if (n.Kind == ConfigNodeKind.Bus && key.Equals("COMStop", StringComparison.OrdinalIgnoreCase))
            {
                int number;
                if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) || (number != 1 && number != 2))
                {
                    MessageBox.Show(this, "Стоп-биты: 1 или 2.", "COM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    BeginInvoke((Action)(() => ShowProperties(n)));
                    return;
                }
                n.Properties["COMStop"] = number.ToString(CultureInfo.InvariantCulture);
            }
            else if (n.Kind == ConfigNodeKind.Device && key.Equals("AnswerTimeOut", StringComparison.OrdinalIgnoreCase))
            {
                int number;
                if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) || number < 50 || number > 120000)
                {
                    MessageBox.Show(this, "Таймаут ответа должен быть 50..120000 мс.", "Таймаут", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    BeginInvoke((Action)(() => ShowProperties(n)));
                    return;
                }
                n.Properties["AnswerTimeOut"] = number.ToString(CultureInfo.InvariantCulture);
            }
            else if (n.Kind == ConfigNodeKind.Device && key.Equals("RepeatCount", StringComparison.OrdinalIgnoreCase))
            {
                int number;
                if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) || number < 0 || number > 20)
                {
                    MessageBox.Show(this, "Количество повторов должно быть 0..20.", "Повторы", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    BeginInvoke((Action)(() => ShowProperties(n)));
                    return;
                }
                n.Properties["RepeatCount"] = number.ToString(CultureInfo.InvariantCulture);
            }
            else if (n.Kind == ConfigNodeKind.Device && key.Equals("ReadingInterval", StringComparison.OrdinalIgnoreCase))
            {
                int number;
                if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) || number < 1 || number > 86400)
                {
                    MessageBox.Show(this, "Интервал чтения должен быть 1..86400.", "Интервал чтения", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    BeginInvoke((Action)(() => ShowProperties(n)));
                    return;
                }
                n.Properties["ReadingInterval"] = number.ToString(CultureInfo.InvariantCulture);
            }
            else if (n.Kind == ConfigNodeKind.Device && IsSet4Device(n) && key.Equals("SET4Password", StringComparison.OrdinalIgnoreCase))
            {
                var password = Set4Protocol.NormalizePassword(value);
                if (password.Length != 6 || password.Any(c => c > 0x7F))
                {
                    MessageBox.Show(this, "SET4Password должен содержать 6 ASCII-символов.", "SET4", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    BeginInvoke((Action)(() => ShowProperties(n)));
                    return;
                }
                n.Properties["SET4Password"] = password;
            }
            else if (n.Kind == ConfigNodeKind.Device && IsMercuryDevice(n) &&
                     (key.Equals("PasswordLevel1", StringComparison.OrdinalIgnoreCase) ||
                      key.Equals("PasswordLevel2", StringComparison.OrdinalIgnoreCase)))
            {
                var pluginKey = key.Equals("PasswordLevel2", StringComparison.OrdinalIgnoreCase) ? "Psw2" : "Psw1";
                var passwordValue = NormalizeMercuryPasswordInput(value);
                if (!MppSettings.SetDevicePluginInitial(n, pluginKey, passwordValue))
                {
                    MessageBox.Show(this, "Не удалось записать " + pluginKey + " в PluginProperties.", "Mercury", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    BeginInvoke((Action)(() => ShowProperties(n)));
                    return;
                }
            }
            else if (n.Kind == ConfigNodeKind.Device && IsMercuryDevice(n) &&
                     (key.Equals("TimeCorrection", StringComparison.OrdinalIgnoreCase) ||
                      key.Equals("UseTimeLocal", StringComparison.OrdinalIgnoreCase) ||
                      key.Equals("UseTimeOffset", StringComparison.OrdinalIgnoreCase)))
            {
                var b = ConfigNode.ParseBool(value, false);
                if (!MppSettings.SetDevicePluginInitial(n, key, b ? "true" : "false"))
                {
                    MessageBox.Show(this, "Не удалось записать " + key + " в PluginProperties.", "Mercury", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    BeginInvoke((Action)(() => ShowProperties(n)));
                    return;
                }
            }
            else if (n.Kind == ConfigNodeKind.Device && IsMercuryDevice(n) && key.Equals("TimeOffset", StringComparison.OrdinalIgnoreCase))
            {
                int offset;
                if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out offset) || offset < -12 || offset > 12)
                {
                    MessageBox.Show(this, "TimeOffset должен быть в диапазоне -12..12 часов.", "Mercury", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    BeginInvoke((Action)(() => ShowProperties(n)));
                    return;
                }
                if (!MppSettings.SetDevicePluginInitial(n, "TimeOffset", offset.ToString(CultureInfo.InvariantCulture)))
                {
                    MessageBox.Show(this, "Не удалось записать TimeOffset в PluginProperties.", "Mercury", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    BeginInvoke((Action)(() => ShowProperties(n)));
                    return;
                }
            }
            else if (n.Kind == ConfigNodeKind.Device && IsMercuryDevice(n) && key.Equals("PollingAccessLevel", StringComparison.OrdinalIgnoreCase))
            {
                int level;
                if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out level) || (level != 1 && level != 2))
                {
                    MessageBox.Show(this, "PollingAccessLevel должен быть 1 или 2.", "Mercury", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    BeginInvoke((Action)(() => ShowProperties(n)));
                    return;
                }
                n.Properties["PollingAccessLevel"] = level.ToString(CultureInfo.InvariantCulture);
                n.Properties["Level"] = level.ToString(CultureInfo.InvariantCulture);
                MppSettings.SetDevicePluginInitial(n, "Level", level.ToString(CultureInfo.InvariantCulture));
            }
            else if (n.Kind == ConfigNodeKind.Device && IsMercuryDevice(n) && key.Equals("TimeSyncWriteEnabled", StringComparison.OrdinalIgnoreCase))
            {
                n.Properties["TimeSyncWriteEnabled"] = ConfigNode.ParseBool(value, false) ? "true" : "false";
            }
            else if (n.Kind == ConfigNodeKind.Device && IsMercuryDevice(n) && key.Equals("TimeSyncThresholdSec", StringComparison.OrdinalIgnoreCase))
            {
                int seconds;
                if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds) || seconds < 1 || seconds > 240)
                {
                    MessageBox.Show(this, "TimeSyncThresholdSec должен быть 1..240 секунд.", "Mercury", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    BeginInvoke((Action)(() => ShowProperties(n)));
                    return;
                }
                n.Properties["TimeSyncThresholdSec"] = seconds.ToString(CultureInfo.InvariantCulture);
            }
            else if (n.Kind == ConfigNodeKind.Device && IsMercuryDevice(n) && key.Equals("EventLogNumber", StringComparison.OrdinalIgnoreCase))
            {
                var t = value.Trim();
                int journal;
                var ok = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? int.TryParse(t.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out journal)
                    : int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out journal);
                if (!ok || journal < 0 || journal > 255)
                {
                    MessageBox.Show(this, "EventLogNumber: 0..255 или 0x00..0xFF.", "Mercury", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    BeginInvoke((Action)(() => ShowProperties(n)));
                    return;
                }
                n.Properties["EventLogNumber"] = journal.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                n.Properties[key] = value;
            }

            MarkProjectDirty();
            _opcPublisher?.PublishNow();
            _log.Info($"Изменено {n.OpcItemId}: {key}={value}");
        }

        private static string NormalizeMercuryPasswordInput(string value)
        {
            var text = (value ?? string.Empty).Trim();
            if (text.StartsWith("{X}", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("{A}", StringComparison.OrdinalIgnoreCase))
                return text;

            // MPS stores the usual Mercury numeric/hex password as {X}111111 / {X}222222.
            // Accept the human-friendly six hex digits and normalize them to the MPS form.
            if (text.Length == 6 && text.All(c => Uri.IsHexDigit(c)))
                return "{X}" + text.ToUpperInvariant();

            // Explicit ASCII password remains possible for non-default meters.
            return "{A}" + text;
        }

        private void RenameSelected()
        {
            if (!_editMode) return;
            var tn = _tree.SelectedNode;
            var n = tn == null ? null : tn.Tag as ConfigNode;
            if (n == null) return;

            var value = Prompt.Show("Переименовать", n.Name);
            if (string.IsNullOrWhiteSpace(value) || value == n.Name) return;

            var newName = value.Trim();
            var affectedTags = ConfigTree.Tags(n).Count();
            if (affectedTags > 0 && n.Kind != ConfigNodeKind.Main)
            {
                var oldPath = n.OpcItemId;
                var oldName = n.Name;
                n.Name = newName;
                var newPath = n.OpcItemId;
                n.Name = oldName;
                if (MessageBox.Show(this,
                    "Переименование изменит OPC ItemID у " + affectedTags + " тег(ов).\r\n\r\n" +
                    "Старый путь: " + oldPath + "\r\n" +
                    "Новый путь:  " + newPath + "\r\n\r\n" +
                    "Это может потребовать изменения привязок в MasterSCADA. Продолжить?",
                    "Изменение OPC namespace", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
            }

            n.Name = newName;
            n.Properties["NameInTree"] = n.Name;
            foreach (var descendant in ConfigTree.DescendantsAndSelf(n))
            {
                if (descendant.Properties.ContainsKey("NodeId"))
                    descendant.Properties["NodeId"] = descendant.OpcItemId;
                RefreshTreeNodeFor(descendant);
            }
            ShowProperties(n);
            MarkProjectDirty();
            _opcPublisher?.PublishNow();
            _log.Info("Переименовано: " + n.OpcItemId);
        }

        private void ShowAddWizard()
        {
            if (!_editMode || _root == null) return;

            var selected = SelectedConfigNode() ?? _root;
            if (selected.Kind == ConfigNodeKind.Tag && selected.Parent != null)
                selected = selected.Parent;

            using (var wizard = new TreeAddWizardForm(_root, selected))
            {
                if (wizard.ShowDialog(this) != DialogResult.OK || wizard.Result == null) return;
                try
                {
                    CreateNodeFromWizard(selected, wizard.Result);
                }
                catch (Exception ex)
                {
                    _log.Error("Мастер добавления: " + ex);
                    MessageBox.Show(this, ex.Message, "Мастер добавления", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void CreateNodeFromWizard(ConfigNode parent, TreeAddWizardResult r)
        {
            if (parent == null || r == null) return;
            if (parent.Children.Any(x => x.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("У выбранного родителя уже есть узел с именем '" + r.Name + "'.");

            ConfigNode node;
            switch (r.Kind)
            {
                case TreeAddKind.Line:
                    if (parent.Kind != ConfigNodeKind.Main)
                        throw new InvalidOperationException("Линию можно добавить только в корень проекта.");
                    node = new ConfigNode { Kind = ConfigNodeKind.Bus, Name = r.Name, ConfiguredEnabled = r.Enabled };
                    node.Properties["Category"] = "Bus";
                    node.Properties["NameInTree"] = r.Name;
                    node.Properties["Enabled"] = r.Enabled ? "true" : "false";
                    node.Properties["TypeNode"] = "COM";
                    node.Properties["TypePlugin"] = string.IsNullOrWhiteSpace(r.LineProtocol) ? "MERCURY" : r.LineProtocol;
                    node.Properties["COMPort"] = r.ComPort.ToString(CultureInfo.InvariantCulture);
                    node.Properties["COMSpeed"] = r.BaudRate.ToString(CultureInfo.InvariantCulture);
                    node.Properties["COMData"] = r.DataBits.ToString(CultureInfo.InvariantCulture);
                    node.Properties["COMParitet"] = r.Parity ?? "Нет";
                    node.Properties["COMStop"] = r.StopBits.ToString(CultureInfo.InvariantCulture);
                    node.Properties["Comment"] = r.Comment ?? string.Empty;

                    if (r.DeviceTemplate == null)
                        throw new InvalidOperationException("Для новой линии обязательно выберите модель счётчика.");
                    if (string.IsNullOrWhiteSpace(r.InitialDeviceName))
                        throw new InvalidOperationException("Введите имя счётчика.");
                    var initialDevice = ConfigNodeTools.CreateDeviceFromTemplate(r.DeviceTemplate, r.InitialDeviceName, r.Address,
                        r.TimeoutMs, r.Retries, r.ReadingInterval);
                    initialDevice.Parent = node;
                    node.Children.Add(initialDevice);
                    break;

                case TreeAddKind.Device:
                    if (parent.Kind != ConfigNodeKind.Bus)
                        throw new InvalidOperationException("Устройство можно добавить только в линию COM.");
                    node = ConfigNodeTools.CreateDeviceFromTemplate(r.DeviceTemplate, r.Name, r.Address,
                        r.TimeoutMs, r.Retries, r.ReadingInterval);
                    node.ConfiguredEnabled = r.Enabled;
                    node.Properties["Enabled"] = r.Enabled ? "true" : "false";
                    if (!string.IsNullOrWhiteSpace(r.Comment)) node.Properties["Comment"] = r.Comment;
                    break;

                case TreeAddKind.SubDevice:
                    if (parent.Kind != ConfigNodeKind.Device)
                        throw new InvalidOperationException("Раздел устройства можно добавить непосредственно в устройство.");
                    node = CreateSimpleNode(ConfigNodeKind.SubDevice, r.Name, r.Enabled, r.Comment);
                    break;

                case TreeAddKind.Group:
                    if (parent.Kind != ConfigNodeKind.Device && parent.Kind != ConfigNodeKind.SubDevice && parent.Kind != ConfigNodeKind.Group)
                        throw new InvalidOperationException("Группу можно добавить в устройство, раздел или другую группу.");
                    node = CreateSimpleNode(ConfigNodeKind.Group, r.Name, r.Enabled, r.Comment);
                    break;

                case TreeAddKind.Tag:
                    if (parent.Kind != ConfigNodeKind.Device && parent.Kind != ConfigNodeKind.SubDevice && parent.Kind != ConfigNodeKind.Group)
                        throw new InvalidOperationException("Тег можно добавить в устройство, раздел или группу.");
                    node = CreateSimpleNode(ConfigNodeKind.Tag, r.Name, r.Enabled, r.Comment);
                    node.Properties["Category"] = "Teg";
                    node.Properties["Type"] = r.DataType ?? "float";
                    node.Properties["Access"] = r.Access ?? "ReadOnly";
                    node.Properties["IsHDA"] = "false";
                    break;

                default:
                    throw new InvalidOperationException("Неизвестный тип создаваемого узла.");
            }

            node.Parent = parent;
            parent.Children.Add(node);
            ConfigNodeTools.RefreshNodeIds(node);

            TreeNode parentTree;
            if (!_treeNodes.TryGetValue(parent.Id, out parentTree))
            {
                BuildTree();
                _treeNodes.TryGetValue(parent.Id, out parentTree);
            }
            if (parentTree != null)
            {
                TreeNode tn;
                var wasLazy = parentTree.Nodes.Count == 1 && parentTree.Nodes[0].Tag == null;
                if (wasLazy)
                {
                    PopulateTreeNode(parentTree);
                    _treeNodes.TryGetValue(node.Id, out tn);
                }
                else
                {
                    tn = BuildTreeNodeShallow(node);
                    parentTree.Nodes.Add(tn);
                    RefreshTreeNodeDisplay(tn, false);
                    RefreshNodeColorSingle(tn);
                }

                parentTree.Expand();
                if (tn != null)
                {
                    _tree.SelectedNode = tn;
                    tn.EnsureVisible();
                }
            }
            else BuildTree();

            MarkProjectDirty();
            _opcPublisher?.PublishNow();

            var tags = ConfigTree.Tags(node).Count();
            if (r.Kind == TreeAddKind.Device)
            {
                _log.Info("Добавлен счётчик " + node.OpcItemId + ": модель=" +
                          (r.DeviceTemplate == null ? "?" : r.DeviceTemplate.ModelId) +
                          ", адрес=" + r.Address + ", тегов=" + tags + ".");
            }
            else if (r.Kind == TreeAddKind.Line)
            {
                var child = node.Children.FirstOrDefault(x => x.Kind == ConfigNodeKind.Device);
                _log.Info("Создана ветка/линия " + node.OpcItemId + " со счётчиком " +
                          (child == null ? "?" : child.Name) + ", модель=" +
                          (r.DeviceTemplate == null ? "?" : r.DeviceTemplate.ModelId) +
                          ", OPC-тегов=" + tags + ".");
            }
            else
            {
                _log.Info("Создан узел: " + node.OpcItemId + (tags > 0 ? ", тегов=" + tags : string.Empty));
            }
        }

        private static ConfigNode CreateSimpleNode(ConfigNodeKind kind, string name, bool enabled, string comment)
        {
            var n = new ConfigNode
            {
                Kind = kind,
                Name = name,
                ConfiguredEnabled = enabled
            };
            n.Properties["Category"] = kind == ConfigNodeKind.Tag ? "Teg" : kind.ToString();
            n.Properties["NameInTree"] = name;
            n.Properties["Enabled"] = enabled ? "true" : "false";
            n.Properties["Comment"] = comment ?? string.Empty;
            return n;
        }

        private void DeleteSelected()
        {
            if (!_editMode) return;
            var tn = _tree.SelectedNode;
            var n = tn == null ? null : tn.Tag as ConfigNode;
            if (tn == null || n == null || n.Parent == null) return;

            var oldPath = n.OpcItemId;
            var tagCount = ConfigTree.Tags(n).Count();
            var deviceCount = ConfigNodeTools.CountKind(n, ConfigNodeKind.Device);
            var branchCount = ConfigTree.DescendantsAndSelf(n)
                .Count(x => x.Kind == ConfigNodeKind.SubDevice || x.Kind == ConfigNodeKind.Group);

            var text = new StringBuilder();
            text.AppendLine("Удалить выбранный узел и всё его содержимое?");
            text.AppendLine();
            text.AppendLine(oldPath);
            if (deviceCount > 0) text.AppendLine("Устройств: " + deviceCount);
            if (branchCount > 0) text.AppendLine("Веток/групп: " + branchCount);
            if (tagCount > 0)
            {
                text.AppendLine("OPC-тегов: " + tagCount);
                text.AppendLine();
                text.AppendLine("Эти OPC ItemID исчезнут из namespace. Привязки MasterSCADA к ним перестанут работать.");
            }

            if (MessageBox.Show(this, text.ToString(), "Удаление из проекта",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            var parent = n.Parent;
            _cache.Remove(n);
            RemoveTreeNodeIndex(tn);
            parent.Children.Remove(n);
            tn.Remove();
            TreeNode parentTn;
            if (_treeNodes.TryGetValue(parent.Id, out parentTn)) _tree.SelectedNode = parentTn;
            MarkProjectDirty();
            _opcPublisher?.PublishNow();
            _log.Warn("Удалён: " + oldPath + "; устройств=" + deviceCount + ", веток=" + branchCount + ", тегов=" + tagCount);
        }

        private void RemoveTreeNodeIndex(TreeNode tn)
        {
            var n = tn.Tag as ConfigNode;
            if (n != null) _treeNodes.Remove(n.Id);
            foreach (TreeNode child in tn.Nodes)
                RemoveTreeNodeIndex(child);
        }

        private void TreeKeyDown(object sender, KeyEventArgs e)
        {
            if (!_editMode) return;
            if (e.KeyCode == Keys.Delete)
            {
                DeleteSelected();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.F2)
            {
                RenameSelected();
                e.Handled = true;
            }
        }

        private void CacheValueChanged(ConfigNode node, TagValue value)
        {
            if (IsDisposed || !IsHandleCreated) return;

            Action refresh = () =>
            {
                RefreshTreeNodeFor(node);

                var selected = SelectedConfigNode();
                if (selected != null && selected.Id == node.Id)
                    ShowProperties(selected);
            };

            if (InvokeRequired) BeginInvoke(refresh);
            else refresh();
        }

        private void RefreshTreeNodeFor(ConfigNode node)
        {
            if (node == null) return;
            TreeNode tn;
            if (_treeNodes.TryGetValue(node.Id, out tn))
            {
                RefreshTreeNodeDisplay(tn, false);
                tn.ForeColor = node.EffectiveEnabled
                    ? SystemColors.WindowText
                    : (node.ConfiguredEnabled ? Color.Gray : Color.Firebrick);
                _tree.Invalidate(tn.Bounds);
            }
        }

        private void ExpandTree()
        {
            if (_tree.Nodes.Count == 0) return;
            Cursor = Cursors.WaitCursor;
            _tree.BeginUpdate();
            try
            {
                foreach (TreeNode root in _tree.Nodes)
                    MaterializeAndExpand(root);
                if (_tree.SelectedNode != null)
                    _tree.SelectedNode.EnsureVisible();
                else
                    _tree.Nodes[0].EnsureVisible();
            }
            finally
            {
                _tree.EndUpdate();
                Cursor = Cursors.Default;
            }
        }

        private void MaterializeAndExpand(TreeNode tn)
        {
            PopulateTreeNode(tn);
            var children = new List<TreeNode>();
            foreach (TreeNode child in tn.Nodes)
                if (child.Tag != null) children.Add(child);
            foreach (var child in children)
                MaterializeAndExpand(child);
            tn.Expand();
        }

        private void CollapseTree()
        {
            if (_tree.Nodes.Count == 0) return;
            _tree.BeginUpdate();
            try
            {
                _tree.CollapseAll();
                var root = _tree.Nodes[0];
                root.Expand();
                root.EnsureVisible();
            }
            finally { _tree.EndUpdate(); }
        }

        private void RuntimeControlTick()
        {
            RuntimeControl.TouchMainState(_editMode, _polling != null);

            if (_editMode)
            {
                RuntimeControl.SetConfigurationMode(true);
                _opcDemandLostSince = DateTime.MinValue;
                UpdateOpcButtonStatus();
                return;
            }

            var demand = RuntimeControl.IsOpcDemandFresh(TimeSpan.FromSeconds(5));
            if (demand)
            {
                _opcDemandLostSince = DateTime.MinValue;
                _autoLaunchIdleSince = DateTime.MinValue;
                if (_root != null && _polling == null && RuntimeControl.PollOnOpcDemandEnabled && !RuntimeControl.PollManualHold)
                {
                    _log.Info("OPC demand: обнаружен активный OPC-клиент, запускаю опрос.");
                    _pollingStartedByOpcDemand = true;
                    StartPollingScope(_root, false);
                    if (_polling == null) _pollingStartedByOpcDemand = false;
                }
            }
            else if (_pollingStartedByOpcDemand && _polling != null)
            {
                if (_opcDemandLostSince == DateTime.MinValue)
                    _opcDemandLostSince = DateTime.UtcNow;
                else if (DateTime.UtcNow - _opcDemandLostSince >= TimeSpan.FromSeconds(10))
                {
                    _log.Info("OPC demand завершён: клиентов нет 10 с, автоматически запущенный опрос останавливается.");
                    StopPolling();
                    _pollingStartedByOpcDemand = false;
                    _opcDemandLostSince = DateTime.MinValue;
                }
            }

            if (_startupOptions.StartedByOpc && !_manualKeepAlive && !_editMode && !demand)
            {
                if (_autoLaunchIdleSince == DateTime.MinValue)
                    _autoLaunchIdleSince = DateTime.UtcNow;
                else if (_polling == null && DateTime.UtcNow - _autoLaunchIdleSince >= TimeSpan.FromSeconds(20))
                {
                    _log.Info("OPC demand завершён: автоматически запущенное приложение завершает работу.");
                    BeginInvoke((Action)Close);
                    return;
                }
            }

            UpdateOpcButtonStatus();
        }

        private void StartSystem()
        {
            if (_editMode)
            {
                MessageBox.Show(this, "В режиме КОНФИГУРИРОВАНИЯ запуск запрещён.", "СТАРТ",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (_root == null)
            {
                MessageBox.Show(this, "Сначала откройте ASTUE-проект или импортируйте MPP.", "СТАРТ",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Одна кнопка означает одно состояние системы: опрос + публикация + OPC DA.
            RuntimeControl.SetPollManualHold(false);
            RuntimeControl.SetPollOnOpcDemandEnabled(true);
            RuntimeControl.SetOpcDemandStartEnabled(true);
            _manualKeepAlive = true;
            _pollingStartedByOpcDemand = false;
            if (_opcPublisher != null) { _opcPublisher.Start(); _opcPublisher.PublishNow(); }
            StartPollingScope(_root, true);
            StartOpcServer();
            _log.Info("СТАРТ: опрос запущен; OPC DA разрешён и готов к COM/DCOM-активации по требованию клиента.");
            UpdateToolbarStatus();
        }

        private void StopSystemManual()
        {
            _manualKeepAlive = true;
            _pollingStartedByOpcDemand = false;
            RuntimeControl.SetPollManualHold(true);
            // Явный STOP должен сохраняться: удалённый клиент не должен тут же поднять OPC снова.
            RuntimeControl.SetOpcDemandStartEnabled(false);
            RuntimeControl.SetOpcManualKeepAlive(false);
            StopPolling();
            StopOpcServerInternal(true);
            if (_opcPublisher != null) _opcPublisher.Stop();
            _log.Info("СТОП: опрос и OPC DA остановлены; demand-запуск заблокирован до следующего СТАРТ.");
            UpdateToolbarStatus();
        }

        private void StartPolling()
        {
            if (_editMode)
            {
                MessageBox.Show(this, "В режиме КОНФИГУРИРОВАНИЯ опрос запрещён. Переключитесь в режим РАБОТА.", "Опрос");
                return;
            }
            if (_root == null)
            {
                MessageBox.Show(this, "Сначала импортируйте MPP или откройте ASTUE-проект.", "Опрос");
                return;
            }

            _manualKeepAlive = true;
            RuntimeControl.SetPollManualHold(false);
            _pollingStartedByOpcDemand = false;
            _opcDemandLostSince = DateTime.MinValue;
            StartPollingScope(_root, true);
        }

        private void StartPollingScope(ConfigNode scope, bool interactive)
        {
            if (_root == null || scope == null) return;

            if (_editMode)
            {
                if (interactive)
                    MessageBox.Show(this, "В режиме КОНФИГУРИРОВАНИЯ опрос запрещён.", "Опрос");
                else
                    _log.Warn("Автостарт опроса пропущен: включён режим КОНФИГУРИРОВАНИЯ.");
                return;
            }

            if (_polling != null)
            {
                if (interactive)
                {
                    _pollingStartedByOpcDemand = false;
                    RuntimeControl.SetPollManualHold(false);
                }
                _log.Warn("Опрос уже запущен.");
                return;
            }

            try
            {
                _polling = new PollingEngine(_root, scope, _log, _cache, _statistics);
                _log.Info("Опрос запущен: состав определяется только галочками Enabled; " +
                          "выделение мышкой на состав опроса не влияет.");
                _polling.Start();
                RuntimeControl.TouchMainState(_editMode, true);
                UpdateToolbarStatus();
            }
            catch (Exception ex)
            {
                _log.Error(ex.ToString());
                if (_polling != null)
                {
                    _polling.Dispose();
                    _polling = null;
                }
                RuntimeControl.TouchMainState(_editMode, false);
                UpdateToolbarStatus();

                if (interactive)
                    MessageBox.Show(this, ex.Message, "Старт опроса", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StopPollingManual()
        {
            _manualKeepAlive = true;
            RuntimeControl.SetPollManualHold(true);
            _pollingStartedByOpcDemand = false;
            _opcDemandLostSince = DateTime.MinValue;
            StopPolling();
            _log.Info("Опрос остановлен вручную. Автозапуск опроса по OPC demand удерживается до ручного 'Старт опроса' или повторного включения пункта 'Автозапуск опроса при OPC-клиенте'.");
        }

        private void StopPolling()
        {
            if (_polling == null)
            {
                RuntimeControl.TouchMainState(_editMode, false);
                UpdateToolbarStatus();
                return;
            }

            try { _polling.Dispose(); }
            catch (Exception ex) { _log.Error("Стоп опроса: " + ex.Message); }
            finally
            {
                _polling = null;
                MarkStoppedPollingQuality();
                RuntimeControl.TouchMainState(_editMode, false);
                UpdateToolbarStatus();
            }
        }

        private void MarkStoppedPollingQuality()
        {
            if (_root == null) return;
            foreach (var tag in ConfigTree.Tags(_root))
                _cache.MarkQuality(tag, ValueQuality.BadOutOfService, true, false);
        }

        private string OpcServerExePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AstueMpsOpcDaServer.exe");

        private bool IsOpcServerRunning()
        {
            try { return Process.GetProcessesByName("AstueMpsOpcDaServer").Length > 0; }
            catch { return false; }
        }

        private bool IsOpcServerRegistered()
        {
            try
            {
                using (var key = Registry.ClassesRoot.OpenSubKey(@"Astue.MpsOpcDa.1\CLSID", false))
                    return key != null && !string.IsNullOrWhiteSpace(Convert.ToString(key.GetValue(null), CultureInfo.InvariantCulture));
            }
            catch { return false; }
        }

        private string GetRegisteredOpcServerPath()
        {
            try
            {
                using (var key = Registry.ClassesRoot.OpenSubKey(@"CLSID\{7B0E174E-31F0-4A2A-9D8E-3C3D952BD901}\LocalServer32", false))
                    return key == null ? string.Empty : (Convert.ToString(key.GetValue(null), CultureInfo.InvariantCulture) ?? string.Empty).Trim();
            }
            catch { return string.Empty; }
        }

        private void UpdateOpcButtonStatus()
        {
            if (_opcButton == null) return;
            var exists = File.Exists(OpcServerExePath);
            var running = exists && IsOpcServerRunning();
            var registered = exists && IsOpcServerRegistered();
            var autoStart = RuntimeControl.OpcDemandStartEnabled;
            var ready = !_editMode && exists && registered && autoStart && !running;

            _opcButton.Text = running ? "OPC DA ●" : (_editMode ? "OPC DA ×" : (ready ? "OPC DA ◌" : "OPC DA"));
            _opcButton.BackColor = _editMode ? Color.Khaki : (running ? Color.PaleGreen : (ready ? Color.LightCyan : (exists ? SystemColors.Control : Color.LightCoral)));
            _opcButton.ForeColor = _editMode ? Color.DarkGoldenrod : ((running || ready) ? Color.DarkGreen : (exists ? SystemColors.ControlText : Color.DarkRed));
            _opcButton.ToolTipText = !exists
                ? "AstueMpsOpcDaServer.exe отсутствует в папке программы"
                : (_editMode
                    ? "КОНФИГУРИРОВАНИЕ: OPC DA заблокирован"
                    : (running
                        ? "OPC DA сервер запущен COM/DCOM; " + (registered ? "зарегистрирован" : "не зарегистрирован")
                        : (ready
                            ? "OPC DA готов: зарегистрирован; COM/DCOM запустит LocalServer по запросу клиента"
                            : "OPC DA остановлен; " + (registered ? "зарегистрирован" : "не зарегистрирован") +
                              "; автозапуск по клиенту " + (autoStart ? "разрешён" : "запрещён"))));
            UpdateRunStatusStrip();
        }

        private void ShowOpcStatus()
        {
            if (!_editMode && _root != null) _opcPublisher?.PublishNow();
            var exists = File.Exists(OpcServerExePath);
            var running = exists && IsOpcServerRunning();
            var registered = exists && IsOpcServerRegistered();
            var snapshotExists = File.Exists(OpcSnapshotPublisher.SnapshotPath);
            var snapshotSize = snapshotExists ? new FileInfo(OpcSnapshotPublisher.SnapshotPath).Length : 0L;
            var state = RuntimeControl.ReadOpcState();

            string serverMode;
            if (!state.TryGetValue("mode", out serverMode)) serverMode = "-";
            string clients;
            if (!state.TryGetValue("clients", out clients)) clients = "0";
            string groups;
            if (!state.TryGetValue("groups", out groups)) groups = "0";
            string objects;
            if (!state.TryGetValue("objects", out objects)) objects = "0";
            string pending;
            if (!state.TryGetValue("pending", out pending)) pending = "0";

            var sb = new StringBuilder();
            sb.AppendLine("OPC DA 2.05a (ReadOnly)");
            sb.AppendLine("ProgID: Astue.MpsOpcDa.1");
            sb.AppendLine("Разрядность: x86");
            sb.AppendLine();
            sb.AppendLine("Режим приложения: " + (_editMode ? "КОНФИГУРИРОВАНИЕ" : "РАБОТА"));
            sb.AppendLine("Опрос: " + (_polling != null ? (_pollingStartedByOpcDemand ? "работает (OPC demand)" : "работает (ручной)") : "остановлен"));
            sb.AppendLine("Автозапуск OPC по клиенту: " + (RuntimeControl.OpcDemandStartEnabled ? "разрешён" : "заблокирован вручную"));
            sb.AppendLine("Автозапуск опроса по OPC: " + (RuntimeControl.PollOnOpcDemandEnabled ? "разрешён" : "выключен"));
            sb.AppendLine("Ручная блокировка опроса: " + (RuntimeControl.PollManualHold ? "ДА" : "нет"));
            sb.AppendLine();
            sb.AppendLine("EXE: " + (exists ? "есть" : "НЕ НАЙДЕН"));
            sb.AppendLine("Регистрация: " + (registered ? "есть" : "нет"));
            var registeredPath = GetRegisteredOpcServerPath();
            sb.AppendLine("LocalServer32: " + (string.IsNullOrWhiteSpace(registeredPath) ? "-" : registeredPath));
            sb.AppendLine("Текущий EXE: " + OpcServerExePath);
            if (!string.IsNullOrWhiteSpace(registeredPath) && registeredPath.IndexOf(OpcServerExePath, StringComparison.OrdinalIgnoreCase) < 0)
                sb.AppendLine("ВНИМАНИЕ: зарегистрирована другая папка/версия OPC-сервера.");
            sb.AppendLine("Процесс: " + (running ? "запущен" : "остановлен"));
            sb.AppendLine("Режим OPC-процесса: " + serverMode);
            sb.AppendLine("OPC server clients: " + clients);
            sb.AppendLine("OPC groups: " + groups);
            sb.AppendLine("Активных COM objects: " + objects);
            sb.AppendLine("Ожидающих COM activation: " + pending);
            sb.AppendLine("OPC demand: " + (RuntimeControl.IsOpcDemandFresh(TimeSpan.FromSeconds(5)) ? "активен" : "нет"));
            sb.AppendLine();
            sb.AppendLine("Файл обмена: " + OpcSnapshotPublisher.SnapshotPath);
            sb.AppendLine("Snapshot publisher: " + (_opcPublisher != null && _opcPublisher.IsRunning ? "работает" : "остановлен"));
            sb.AppendLine("Snapshot: " + (snapshotExists ? (snapshotSize.ToString("N0", CultureInfo.CurrentCulture) + " байт") : "нет"));
            if (_opcPublisher != null)
            {
                sb.AppendLine("Тегов в последней публикации: " + _opcPublisher.LastTagCount);
                if (_opcPublisher.LastPublishTime != DateTime.MinValue)
                    sb.AppendLine("Последняя публикация: " + _opcPublisher.LastPublishTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture));
                if (!string.IsNullOrWhiteSpace(_opcPublisher.LastError))
                    sb.AppendLine("Ошибка публикации: " + _opcPublisher.LastError);
            }
            sb.AppendLine();
            sb.AppendLine("В КОНФИГУРИРОВАНИИ OPC и опрос не запускаются. В РАБОТЕ OPC-клиент может автоматически поднять OPC LocalServer и основное приложение/опрос.");

            MessageBox.Show(this, sb.ToString(), "Состояние OPC DA", MessageBoxButtons.OK,
                exists ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            UpdateOpcButtonStatus();
        }

        private void StartOpcServer()
        {
            if (_editMode)
            {
                MessageBox.Show(this, "В режиме КОНФИГУРИРОВАНИЯ OPC DA запрещён. Переключитесь в режим РАБОТА.", "OPC DA", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!File.Exists(OpcServerExePath))
            {
                MessageBox.Show(this, "Не найден " + OpcServerExePath, "OPC DA", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                // v0.3.2.4: do NOT start a persistent --manual LocalServer here.
                // COM/DCOM must own activation (-Embedding). This avoids the global
                // single-instance mutex conflict that caused 0x80080005 remotely.
                _manualKeepAlive = true;
                RuntimeControl.SetOpcDemandStartEnabled(true);
                RuntimeControl.SetOpcManualKeepAlive(false);
                if (_root != null)
                {
                    _opcPublisher?.Start();
                    _opcPublisher?.PublishNow();
                }

                if (IsOpcServerRunning())
                    _log.Info("OPC DA уже активирован COM/DCOM-клиентом.");
                else
                    _log.Info("OPC DA готов: LocalServer зарегистрирован и будет запущен COM/DCOM по требованию клиента.");
            }
            catch (Exception ex)
            {
                _log.Error("Подготовка OPC DA: " + ex.Message);
                MessageBox.Show(this, ex.Message, "OPC DA", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            UpdateOpcButtonStatus();
        }

        private void StopOpcServerManual()
        {
            _manualKeepAlive = true;
            RuntimeControl.SetOpcManualKeepAlive(false);
            RuntimeControl.SetOpcDemandStartEnabled(false);
            StopOpcServerInternal(true);
            _log.Info("OPC DA остановлен вручную. Автозапуск по запросу клиента заблокирован до ручного запуска или включения пункта автозапуска.");
        }

        private void StopOpcServerInternal(bool manualBlock)
        {
            if (manualBlock) RuntimeControl.SetOpcDemandStartEnabled(false);
            if (!File.Exists(OpcServerExePath) || !IsOpcServerRunning()) return;
            try
            {
                using (var p = Process.Start(new ProcessStartInfo
                {
                    FileName = OpcServerExePath,
                    Arguments = "--stop",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                }))
                {
                    if (p != null) p.WaitForExit(5000);
                }
                _log.Info("Команда остановки OPC DA отправлена.");
            }
            catch (Exception ex)
            {
                _log.Error("Остановка OPC DA: " + ex.Message);
            }
            UpdateOpcButtonStatus();
        }

        private void RegisterOpcServer(bool unregister)
        {
            if (!File.Exists(OpcServerExePath))
            {
                MessageBox.Show(this, "Не найден " + OpcServerExePath, "OPC DA", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var action = unregister ? "отменить регистрацию" : "зарегистрировать";
            if (MessageBox.Show(this,
                    (unregister ? "Отменить регистрацию" : "Зарегистрировать") +
                    " OPC DA сервера Astue.MpsOpcDa.1?\r\n\r\nПотребуются права администратора.",
                    "OPC DA", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = OpcServerExePath,
                    Arguments = unregister ? "--unregister" : "--register",
                    Verb = "runas",
                    UseShellExecute = true,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) throw new InvalidOperationException("Не удалось запустить регистрацию OPC DA.");
                    p.WaitForExit();
                    if (p.ExitCode != 0)
                        throw new InvalidOperationException("Команда завершилась с кодом " + p.ExitCode + ".");
                }
                _log.Info("OPC DA: " + (unregister ? "регистрация удалена" : "сервер зарегистрирован") + ".");
                MessageBox.Show(this,
                    unregister ? "Регистрация OPC DA удалена." : "OPC DA сервер зарегистрирован. ProgID: Astue.MpsOpcDa.1",
                    "OPC DA", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                _log.Error("OPC DA — " + action + ": " + ex.Message);
                MessageBox.Show(this, ex.Message, "OPC DA", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            UpdateOpcButtonStatus();
        }

        private void OpenOpcDataFolder()
        {
            try
            {
                Directory.CreateDirectory(OpcSnapshotPublisher.DataDirectory);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + OpcSnapshotPublisher.DataDirectory + "\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "OPC DA", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ShowComPorts()
        {
            var ports = SerialPort.GetPortNames().OrderBy(PortNumber).ToArray();
            MessageBox.Show(this,
                ports.Length == 0 ? "COM-порты не найдены" : string.Join(Environment.NewLine, ports),
                "COM-порты");
        }

        private static int PortNumber(string s)
        {
            if (string.IsNullOrEmpty(s)) return int.MaxValue;
            if (s.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(s.Substring(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                return n;
            return int.MaxValue;
        }
    }

    internal static class Prompt
    {
        public static string Show(string caption, string initial)
        {
            using (var f = new Form
            {
                Width = 450,
                Height = 150,
                Text = caption,
                StartPosition = FormStartPosition.CenterParent
            })
            {
                var tb = new TextBox { Left = 12, Top = 15, Width = 410, Text = initial };
                var ok = new Button { Text = "OK", Left = 266, Width = 75, Top = 52, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Отмена", Left = 347, Width = 75, Top = 52, DialogResult = DialogResult.Cancel };
                f.Controls.AddRange(new Control[] { tb, ok, cancel });
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                return f.ShowDialog() == DialogResult.OK ? tb.Text.Trim() : null;
            }
        }
    }
}
