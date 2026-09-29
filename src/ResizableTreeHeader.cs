using System;
using System.Drawing;
using System.Windows.Forms;

namespace AstueMpsReplacement
{
    internal sealed class ResizableTreeHeader : Control
    {
        private readonly string[] _titles = { "Объект / тег", "Значение", "Качество", "Обновлено", "Ошибки/1ч", "Загрузка (5мин/1час)" };
        private readonly int[] _widths = { 300, 115, 145, 165, 100, 175 };
        private int _dragIndex = -1;
        private int _dragStartX;
        private int _leftStart;
        private int _rightStart;

        public event Action<int, int, int, int, int, int> WidthsChanged;

        public ResizableTreeHeader()
        {
            Height = 24;
            Dock = DockStyle.Fill;
            BackColor = SystemColors.Control;
            Font = new Font("Consolas", 9f, FontStyle.Bold);
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint, true);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            NormalizeToClientWidth();
            RaiseWidthsChanged();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.Clear(BackColor);

            using (var border = new Pen(SystemColors.ControlDark))
            using (var sep = new Pen(SystemColors.ControlDark))
            using (var brush = new SolidBrush(SystemColors.ControlText))
            {
                g.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));

                var x = 0;
                for (var i = 0; i < _titles.Length; i++)
                {
                    var rect = new Rectangle(x + (i == 0 ? 24 : 6), 0, Math.Max(0, _widths[i] - (i == 0 ? 28 : 10)), Height);
                    using (var sf = new StringFormat
                    {
                        Alignment = StringAlignment.Near,
                        LineAlignment = StringAlignment.Center,
                        Trimming = StringTrimming.EllipsisCharacter,
                        FormatFlags = StringFormatFlags.NoWrap
                    })
                    {
                        g.DrawString(_titles[i], Font, brush, rect, sf);
                    }
                    x += _widths[i];
                    if (i < _titles.Length - 1)
                        g.DrawLine(sep, x, 0, x, Height);
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            var hit = HitSeparator(e.X);
            if (hit < 0) return;

            _dragIndex = hit;
            _dragStartX = e.X;
            _leftStart = _widths[hit];
            _rightStart = _widths[hit + 1];
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragIndex >= 0)
            {
                var delta = e.X - _dragStartX;
                const int minWidth = 70;
                var left = _leftStart + delta;
                var right = _rightStart - delta;
                if (left < minWidth)
                {
                    right -= (minWidth - left);
                    left = minWidth;
                }
                if (right < minWidth)
                {
                    left -= (minWidth - right);
                    right = minWidth;
                }

                _widths[_dragIndex] = Math.Max(minWidth, left);
                _widths[_dragIndex + 1] = Math.Max(minWidth, right);
                Invalidate();
                RaiseWidthsChanged();
                return;
            }

            Cursor = HitSeparator(e.X) >= 0 ? Cursors.VSplit : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragIndex >= 0)
            {
                _dragIndex = -1;
                Capture = false;
                Cursor = Cursors.Default;
                RaiseWidthsChanged();
            }
        }

        private int HitSeparator(int x)
        {
            var pos = 0;
            for (var i = 0; i < _widths.Length - 1; i++)
            {
                pos += _widths[i];
                if (Math.Abs(x - pos) <= 5) return i;
            }
            return -1;
        }

        private void NormalizeToClientWidth()
        {
            if (Width <= 0) return;
            var total = 0;
            for (var i = 0; i < _widths.Length; i++) total += _widths[i];
            var diff = Width - total;
            _widths[_widths.Length - 1] = Math.Max(70, _widths[_widths.Length - 1] + diff);
        }

        private void RaiseWidthsChanged()
        {
            WidthsChanged?.Invoke(_widths[0], _widths[1], _widths[2], _widths[3], _widths[4], _widths[5]);
        }
    }
}
