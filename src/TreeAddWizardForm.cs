using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace AstueMpsReplacement
{
    internal enum TreeAddKind { Line, Device, SubDevice, Group, Tag }

    internal sealed class TreeAddWizardResult
    {
        public TreeAddKind Kind { get; set; }
        public string Name { get; set; }
        public string InitialDeviceName { get; set; }
        public bool Enabled { get; set; }
        public int ComPort { get; set; }
        public int BaudRate { get; set; }
        public int DataBits { get; set; }
        public string Parity { get; set; }
        public int StopBits { get; set; }
        public string LineProtocol { get; set; }
        public DeviceTemplateInfo DeviceTemplate { get; set; }
        public int Address { get; set; }
        public int TimeoutMs { get; set; }
        public int Retries { get; set; }
        public int ReadingInterval { get; set; }
        public string DataType { get; set; }
        public string Access { get; set; }
        public string Comment { get; set; }
    }

    internal sealed class TreeAddWizardForm : Form
    {
        private readonly ComboBox _kind = new ComboBox();
        private readonly TextBox _name = new TextBox();
        private readonly TextBox _deviceName = new TextBox();
        private readonly CheckBox _enabled = new CheckBox();
        private readonly ComboBox _model = new ComboBox();
        private readonly NumericUpDown _address = new NumericUpDown();
        private readonly NumericUpDown _timeout = new NumericUpDown();
        private readonly NumericUpDown _retries = new NumericUpDown();
        private readonly NumericUpDown _interval = new NumericUpDown();
        private readonly NumericUpDown _com = new NumericUpDown();
        private readonly ComboBox _baud = new ComboBox();
        private readonly ComboBox _dataBits = new ComboBox();
        private readonly ComboBox _parity = new ComboBox();
        private readonly ComboBox _stopBits = new ComboBox();
        private readonly ComboBox _lineProtocol = new ComboBox();
        private readonly ComboBox _dataType = new ComboBox();
        private readonly ComboBox _access = new ComboBox();
        private readonly TextBox _comment = new TextBox();
        private readonly Label _modelInfo = new Label();
        private readonly TableLayoutPanel _grid = new TableLayoutPanel();
        private readonly Dictionary<Control, Label> _labels = new Dictionary<Control, Label>();
        private readonly ConfigNode _parent;
        private string _fixedProtocol;
        private bool _syncingModelProtocol;

        public TreeAddWizardResult Result { get; private set; }

        public TreeAddWizardForm(ConfigNode root, ConfigNode parent)
        {
            _parent = NormalizeParent(parent);
            if (_parent != null && _parent.Kind == ConfigNodeKind.Bus)
            {
                var plugin = MppSettings.GetString(_parent, "TypePlugin", string.Empty);
                _fixedProtocol = plugin.IndexOf("SET4", StringComparison.OrdinalIgnoreCase) >= 0 ? "SET4" : "MERCURY";
            }

            Text = "Мастер конфигурации дерева";
            Width = 640;
            Height = 670;
            MinimumSize = new Size(600, 540);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;

            _grid.Dock = DockStyle.Fill;
            _grid.Padding = new Padding(12);
            _grid.ColumnCount = 2;
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(_grid);

            AddRow("Что добавить", _kind);
            AddRow("Имя ветки / линии", _name);
            AddRow("Имя счётчика", _deviceName);
            AddRow("Включено", _enabled);
            AddRow("Модель счётчика", _model);
            AddRow("Адрес счётчика", _address);
            AddRow("Таймаут ответа, мс", _timeout);
            AddRow("Повторов при ошибке", _retries);
            AddRow("Интервал чтения, с", _interval);
            AddRow("Протокол линии", _lineProtocol);
            AddRow("COM-порт", _com);
            AddRow("Скорость, бод", _baud);
            AddRow("Биты данных", _dataBits);
            AddRow("Чётность", _parity);
            AddRow("Стоп-биты", _stopBits);
            AddRow("Тип данных", _dataType);
            AddRow("Доступ", _access);
            AddRow("Комментарий", _comment);

            _modelInfo.AutoSize = true;
            _modelInfo.MaximumSize = new Size(390, 0);
            _modelInfo.ForeColor = Color.DimGray;
            AddRow("Эталонный шаблон", _modelInfo);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
            var ok = new Button { Text = "Создать", Width = 110, Height = 32 };
            var cancel = new Button { Text = "Отмена", Width = 100, Height = 32, DialogResult = DialogResult.Cancel };
            ok.Click += (_, __) => AcceptWizard();
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
            _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _grid.Controls.Add(buttons, 0, _grid.RowCount); _grid.SetColumnSpan(buttons, 2); _grid.RowCount++;
            AcceptButton = ok; CancelButton = cancel;

            ConfigureControls();
            PopulateKinds();
            _kind.SelectedIndexChanged += (_, __) => UpdateVisibleFields();
            _model.SelectedIndexChanged += (_, __) => ModelChanged();
            _lineProtocol.SelectedIndexChanged += (_, __) => ProtocolChanged();
            _address.ValueChanged += (_, __) =>
            {
                var def = BuildDeviceDefaultName(CurrentModelId(), (int)_address.Value);
                if (CurrentKind == TreeAddKind.Line && IsAutoDeviceName()) _deviceName.Text = def;
                else if (CurrentKind == TreeAddKind.Device && (string.IsNullOrWhiteSpace(_name.Text) || _name.Text.StartsWith("M230", StringComparison.OrdinalIgnoreCase) || _name.Text.StartsWith("set4_", StringComparison.OrdinalIgnoreCase) || _name.Text.StartsWith("SET4_", StringComparison.OrdinalIgnoreCase))) _name.Text = def;
            };
            UpdateVisibleFields();
        }

        private static ConfigNode NormalizeParent(ConfigNode selected)
        {
            if (selected == null) return null;
            if (selected.Kind == ConfigNodeKind.Tag) return selected.Parent;
            return selected;
        }

        private void ConfigureControls()
        {
            _kind.DropDownStyle = ComboBoxStyle.DropDownList;
            _model.DropDownStyle = ComboBoxStyle.DropDownList;
            _enabled.Checked = true;
            _address.Minimum = 0; _address.Maximum = 255; _address.Value = 1;
            _timeout.Minimum = 100; _timeout.Maximum = 60000; _timeout.Increment = 100; _timeout.Value = 2000;
            _retries.Minimum = 0; _retries.Maximum = 20; _retries.Value = 2;
            _interval.Minimum = 1; _interval.Maximum = 86400; _interval.Value = 60;
            _com.Minimum = 1; _com.Maximum = 255; _com.Value = 1;

            _lineProtocol.DropDownStyle = ComboBoxStyle.DropDownList;
            _lineProtocol.Items.AddRange(new object[] { "Mercury 230", "СЭТ-4ТМ" }); _lineProtocol.SelectedIndex = 0;
            _baud.DropDownStyle = ComboBoxStyle.DropDownList;
            _baud.Items.AddRange(new object[] { "600", "1200", "2400", "4800", "9600", "19200", "38400", "57600", "115200" }); _baud.SelectedItem = "9600";
            _dataBits.DropDownStyle = ComboBoxStyle.DropDownList; _dataBits.Items.AddRange(new object[] { "7", "8" }); _dataBits.SelectedItem = "8";
            _parity.DropDownStyle = ComboBoxStyle.DropDownList; _parity.Items.AddRange(new object[] { "Нет", "Нечётная", "Чётная", "Mark", "Space" }); _parity.SelectedItem = "Нет";
            _stopBits.DropDownStyle = ComboBoxStyle.DropDownList; _stopBits.Items.AddRange(new object[] { "1", "2" }); _stopBits.SelectedItem = "1";
            _dataType.DropDownStyle = ComboBoxStyle.DropDownList; _dataType.Items.AddRange(new object[] { "bool", "int32", "uint32", "float", "double", "string" }); _dataType.SelectedItem = "float";
            _access.DropDownStyle = ComboBoxStyle.DropDownList; _access.Items.AddRange(new object[] { "ReadOnly", "ReadWrite" }); _access.SelectedItem = "ReadOnly";
        }

        private void PopulateKinds()
        {
            _kind.Items.Clear();
            if (_parent == null || _parent.Kind == ConfigNodeKind.Main) _kind.Items.Add(new KindItem(TreeAddKind.Line, "Ветка / COM-линия со счётчиком"));
            else if (_parent.Kind == ConfigNodeKind.Bus) _kind.Items.Add(new KindItem(TreeAddKind.Device, "Счётчик в существующую линию"));
            else if (_parent.Kind == ConfigNodeKind.Device) { _kind.Items.Add(new KindItem(TreeAddKind.SubDevice, "Раздел устройства (ветка)")); _kind.Items.Add(new KindItem(TreeAddKind.Group, "Группа")); _kind.Items.Add(new KindItem(TreeAddKind.Tag, "Тег")); }
            else if (_parent.Kind == ConfigNodeKind.SubDevice || _parent.Kind == ConfigNodeKind.Group) { _kind.Items.Add(new KindItem(TreeAddKind.Group, "Группа")); _kind.Items.Add(new KindItem(TreeAddKind.Tag, "Тег")); }
            else _kind.Items.Add(new KindItem(TreeAddKind.Tag, "Тег"));
            if (_kind.Items.Count > 0) _kind.SelectedIndex = 0;
        }

        private void AddRow(string caption, Control control)
        {
            var label = new Label { Text = caption, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoSize = true };
            control.Dock = DockStyle.Fill; control.Margin = new Padding(3, 4, 3, 4); _labels[control] = label;
            var row = _grid.RowCount++; _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); _grid.Controls.Add(label, 0, row); _grid.Controls.Add(control, 1, row);
        }

        private TreeAddKind CurrentKind => ((_kind.SelectedItem as KindItem)?.Kind).GetValueOrDefault(TreeAddKind.Tag);
        private void SetVisible(Control c, bool visible) { c.Visible = visible; Label l; if (_labels.TryGetValue(c, out l)) l.Visible = visible; }

        private void UpdateVisibleFields()
        {
            var k = CurrentKind; var isLine = k == TreeAddKind.Line; var isDevice = k == TreeAddKind.Device; var meter = isLine || isDevice; var isTag = k == TreeAddKind.Tag;
            SetVisible(_deviceName, meter); SetVisible(_model, meter); SetVisible(_modelInfo, meter); SetVisible(_address, meter); SetVisible(_timeout, meter); SetVisible(_retries, meter); SetVisible(_interval, meter);
            SetVisible(_lineProtocol, isLine); SetVisible(_com, isLine); SetVisible(_baud, isLine); SetVisible(_dataBits, isLine); SetVisible(_parity, isLine); SetVisible(_stopBits, isLine);
            SetVisible(_dataType, isTag); SetVisible(_access, isTag);
            _labels[_name].Text = isDevice ? "Имя счётчика" : (isLine ? "Имя ветки / линии" : "Имя в дереве");
            SetVisible(_deviceName, isLine); // при добавлении устройства имя вводится в основном поле
            if (isLine && string.IsNullOrWhiteSpace(_name.Text)) _name.Text = "MERCURY_New";
            if ((k == TreeAddKind.SubDevice || k == TreeAddKind.Group) && string.IsNullOrWhiteSpace(_name.Text)) _name.Text = "Новая_ветка";
            if (isTag && string.IsNullOrWhiteSpace(_name.Text)) _name.Text = "Новый_тег";
            ReloadModels();
        }

        private string SelectedProtocol => CurrentKind == TreeAddKind.Line ? (_lineProtocol.SelectedIndex == 1 ? "SET4" : "MERCURY") : (_fixedProtocol ?? "MERCURY");
        private void ProtocolChanged()
        {
            if (_syncingModelProtocol) return;
            if (CurrentKind == TreeAddKind.Line)
            {
                if (SelectedProtocol == "SET4") _parity.SelectedItem = "Нечётная"; else _parity.SelectedItem = "Нет";
                _baud.SelectedItem = "9600"; _dataBits.SelectedItem = "8"; _stopBits.SelectedItem = "1";
                // Для новой линии список моделей общий: Mercury + SET4.
                // Протокол синхронизируется по выбранной модели.
            }
        }

        private void ReloadModels()
        {
            if (CurrentKind != TreeAddKind.Line && CurrentKind != TreeAddKind.Device) return;
            var old = (_model.SelectedItem as DeviceTemplateInfo)?.ModelId;
            _model.Items.Clear();
            var templates = CurrentKind == TreeAddKind.Line
                ? MeterTemplateCatalog.GetTemplates()
                : MeterTemplateCatalog.GetTemplates(SelectedProtocol);
            foreach (var t in templates) _model.Items.Add(t);
            if (_model.Items.Count == 0) { _modelInfo.Text = "Нет встроенных шаблонов для этого протокола."; return; }
            var match = _model.Items.Cast<DeviceTemplateInfo>().FirstOrDefault(x => string.Equals(x.ModelId, old, StringComparison.OrdinalIgnoreCase));
            _model.SelectedItem = match ?? _model.Items[0];
            ModelChanged();
        }

        private string CurrentModelId() => (_model.SelectedItem as DeviceTemplateInfo)?.ModelId ?? "DEVICE";
        private bool IsAutoDeviceName()
        {
            var s = (_deviceName.Text ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(s) || s.StartsWith("M230", StringComparison.OrdinalIgnoreCase) || s.StartsWith("set4_", StringComparison.OrdinalIgnoreCase) || s.StartsWith("SET4_", StringComparison.OrdinalIgnoreCase);
        }

        private void ModelChanged()
        {
            var t = _model.SelectedItem as DeviceTemplateInfo;
            if (t == null) { _modelInfo.Text = "Шаблон не выбран."; return; }

            if (CurrentKind == TreeAddKind.Line)
            {
                // Выбор СЭТ-4ТМ сам переключает протокол линии и типовые параметры.
                _syncingModelProtocol = true;
                try
                {
                    _lineProtocol.SelectedIndex = string.Equals(t.Protocol, "SET4", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                    _baud.SelectedItem = "9600";
                    _dataBits.SelectedItem = "8";
                    _stopBits.SelectedItem = "1";
                    _parity.SelectedItem = string.Equals(t.Protocol, "SET4", StringComparison.OrdinalIgnoreCase) ? "Нечётная" : "Нет";
                    if (string.Equals(t.Protocol, "SET4", StringComparison.OrdinalIgnoreCase) && string.Equals(_name.Text, "MERCURY_New", StringComparison.OrdinalIgnoreCase)) _name.Text = "SET4_New";
                    else if (!string.Equals(t.Protocol, "SET4", StringComparison.OrdinalIgnoreCase) && string.Equals(_name.Text, "SET4_New", StringComparison.OrdinalIgnoreCase)) _name.Text = "MERCURY_New";
                }
                finally { _syncingModelProtocol = false; }
            }

            _modelInfo.Text = string.Format(CultureInfo.CurrentCulture,
                "{0}: {1} веток/групп, {2} тегов. Источник: исходный MPP; шаблон встроен в программу.{3}",
                t.ModelId, t.BranchCount, t.TagCount,
                t.ObservedStructuralVariants > 1 ? " В MPP были варианты структуры; используется полный (максимальный) набор путей." : string.Empty);
            var defaultAddress = t.Protocol == "SET4" ? 4 : 1;
            _address.Value = Clamp(_address, MppSettings.GetInt(t.Template, "DeviceAddress", defaultAddress));
            _timeout.Value = Clamp(_timeout, MppSettings.GetInt(t.Template, "AnswerTimeOut", t.Protocol == "SET4" ? 1000 : 2000));
            _retries.Value = Clamp(_retries, MppSettings.GetInt(t.Template, "RepeatCount", t.Protocol == "SET4" ? 3 : 2));
            _interval.Value = Clamp(_interval, MppSettings.GetInt(t.Template, "ReadingInterval", t.Protocol == "SET4" ? 15 : 60));
            var def = BuildDeviceDefaultName(t.ModelId, (int)_address.Value);
            if (CurrentKind == TreeAddKind.Line) { if (string.IsNullOrWhiteSpace(_deviceName.Text) || IsAutoDeviceName()) _deviceName.Text = def; }
            else if (CurrentKind == TreeAddKind.Device && (string.IsNullOrWhiteSpace(_name.Text) || _name.Text.StartsWith("Новая_", StringComparison.OrdinalIgnoreCase))) _name.Text = def;
        }

        private static decimal Clamp(NumericUpDown n, int value) => Math.Max(n.Minimum, Math.Min(n.Maximum, value));
        private static string BuildDeviceDefaultName(string model, int address) => (string.IsNullOrWhiteSpace(model) ? "DEVICE" : model.Replace("-", string.Empty)) + "_" + address.ToString(CultureInfo.InvariantCulture);

        private void AcceptWizard()
        {
            var name = (_name.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name)) { MessageBox.Show(this, "Введите имя.", "Мастер", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            var meter = CurrentKind == TreeAddKind.Line || CurrentKind == TreeAddKind.Device;
            var template = _model.SelectedItem as DeviceTemplateInfo;
            if (meter && template == null) { MessageBox.Show(this, "Выберите модель счётчика.", "Мастер", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            var deviceName = CurrentKind == TreeAddKind.Line ? (_deviceName.Text ?? string.Empty).Trim() : name;
            if (meter && string.IsNullOrWhiteSpace(deviceName)) { MessageBox.Show(this, "Введите имя счётчика.", "Мастер", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            Result = new TreeAddWizardResult
            {
                Kind = CurrentKind, Name = name, InitialDeviceName = deviceName, Enabled = _enabled.Checked, DeviceTemplate = template,
                Address = (int)_address.Value, TimeoutMs = (int)_timeout.Value, Retries = (int)_retries.Value, ReadingInterval = (int)_interval.Value,
                ComPort = (int)_com.Value, BaudRate = int.Parse(Convert.ToString(_baud.SelectedItem, CultureInfo.InvariantCulture) ?? "9600", CultureInfo.InvariantCulture),
                DataBits = int.Parse(Convert.ToString(_dataBits.SelectedItem, CultureInfo.InvariantCulture) ?? "8", CultureInfo.InvariantCulture),
                Parity = Convert.ToString(_parity.SelectedItem, CultureInfo.InvariantCulture) ?? "Нет",
                StopBits = int.Parse(Convert.ToString(_stopBits.SelectedItem, CultureInfo.InvariantCulture) ?? "1", CultureInfo.InvariantCulture),
                LineProtocol = SelectedProtocol, DataType = Convert.ToString(_dataType.SelectedItem, CultureInfo.InvariantCulture) ?? "float",
                Access = Convert.ToString(_access.SelectedItem, CultureInfo.InvariantCulture) ?? "ReadOnly", Comment = (_comment.Text ?? string.Empty).Trim()
            };
            DialogResult = DialogResult.OK; Close();
        }

        private sealed class KindItem
        {
            public TreeAddKind Kind { get; private set; }
            private readonly string _text;
            public KindItem(TreeAddKind kind, string text) { Kind = kind; _text = text; }
            public override string ToString() => _text;
        }
    }
}
