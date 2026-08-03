using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Services;
using MediaColor = System.Windows.Media.Color;

namespace Kil0bitSystemMonitor
{
    public partial class ColorPickerWindow : Window
    {
        private double _hue;
        private double _sat = 1;
        private double _val = 1;
        private byte _alpha = 255;
        private bool _updating;
        private bool _svDragging;
        private bool _hueDragging;
        private bool _opacityDragging;

        public MediaColor SelectedColor { get; private set; }
        public event Action<MediaColor>? ColorChanged;

        public ColorPickerWindow(MediaColor initial)
        {
            InitializeComponent();
            ApplyLanguage();
            SelectedColor = initial;
            ColorHex.RgbToHsv(initial.R, initial.G, initial.B, out _hue, out _sat, out _val);
            _alpha = initial.A;
            Loaded += (_, _) => SyncUiFromState(pushFields: true, notify: false);
        }

        private void ApplyLanguage()
        {
            var L = LocalizationService.Instance;
            Title = L["ColorPicker.Title"];
            TitleText.Text = L["ColorPicker.Title"];
            OpacityLabel.Text = L["ColorPicker.Opacity"];
            HexLabel.Text = L["ColorPicker.Hex"];
            OkBtn.Content = L["ColorPicker.OK"];
            CancelBtn.Content = L["ColorPicker.Cancel"];
        }

        private MediaColor CurrentOpaqueRgb() => ColorHex.HsvToColor(_hue, _sat, _val, 255);

        private MediaColor CurrentColor() => ColorHex.HsvToColor(_hue, _sat, _val, _alpha);

        private void NotifyColorChanged()
        {
            SelectedColor = CurrentColor();
            ColorChanged?.Invoke(SelectedColor);
        }

        private void SyncUiFromState(bool pushFields, bool notify = true)
        {
            _updating = true;
            try
            {
                var opaque = CurrentOpaqueRgb();
                SvHueFill.Background = new SolidColorBrush(ColorHex.HsvToColor(_hue, 1, 1, 255));
                PreviewFill.Background = new SolidColorBrush(CurrentColor());

                double w = Math.Max(1, SvHost.ActualWidth);
                double h = Math.Max(1, SvHost.ActualHeight);
                SvCursorTransform.X = _sat * w - 7;
                SvCursorTransform.Y = (1 - _val) * h - 7;

                double hueH = Math.Max(1, HueHost.ActualHeight);
                HueCursor.Margin = new Thickness(0, (_hue / 360.0) * hueH - 2, 0, 0);

                int pct = (int)Math.Round(_alpha / 255.0 * 100.0);
                OpacityBox.Text = pct.ToString(CultureInfo.InvariantCulture);
                UpdateOpacityTrack(pct);

                if (pushFields)
                {
                    HexBox.Text = ColorHex.ToArgbHex(CurrentColor());
                    RBox.Text = opaque.R.ToString(CultureInfo.InvariantCulture);
                    GBox.Text = opaque.G.ToString(CultureInfo.InvariantCulture);
                    BBox.Text = opaque.B.ToString(CultureInfo.InvariantCulture);
                }
            }
            finally
            {
                _updating = false;
            }

            if (notify)
                NotifyColorChanged();
        }

        private void UpdateOpacityTrack(int pct)
        {
            double hostW = Math.Max(1, OpacityHost.ActualWidth);
            double thumb = OpacityThumb.Width;
            double travel = Math.Max(0, hostW - thumb);
            double x = Math.Clamp(pct / 100.0, 0, 1) * travel;
            OpacityThumbTransform.X = x;
            OpacityFill.Width = Math.Clamp(x + thumb / 2, 0, hostW);
        }

        private void CommitFromPointer(FrameworkElement host, System.Windows.Point p, bool hue)
        {
            if (hue)
            {
                double t = Math.Clamp(p.Y / Math.Max(1, host.ActualHeight), 0, 1);
                _hue = t * 360.0;
            }
            else
            {
                _sat = Math.Clamp(p.X / Math.Max(1, host.ActualWidth), 0, 1);
                _val = 1 - Math.Clamp(p.Y / Math.Max(1, host.ActualHeight), 0, 1);
            }
            SyncUiFromState(pushFields: true);
        }

        private void CommitOpacityFromPointer(System.Windows.Point p)
        {
            double hostW = Math.Max(1, OpacityHost.ActualWidth);
            double thumb = OpacityThumb.Width;
            double travel = Math.Max(1, hostW - thumb);
            // Центр thumb следует за курсором.
            double t = Math.Clamp((p.X - thumb / 2) / travel, 0, 1);
            int pct = (int)Math.Round(t * 100.0);
            _alpha = (byte)Math.Round(pct / 100.0 * 255.0);
            SyncUiFromState(pushFields: true);
        }

        private void SvHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _svDragging = true;
            SvHost.CaptureMouse();
            CommitFromPointer(SvHost, e.GetPosition(SvHost), hue: false);
        }

        private void SvHost_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_svDragging) return;
            CommitFromPointer(SvHost, e.GetPosition(SvHost), hue: false);
        }

        private void SvHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _svDragging = false;
            SvHost.ReleaseMouseCapture();
        }

        private void HueHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _hueDragging = true;
            HueHost.CaptureMouse();
            CommitFromPointer(HueHost, e.GetPosition(HueHost), hue: true);
        }

        private void HueHost_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_hueDragging) return;
            CommitFromPointer(HueHost, e.GetPosition(HueHost), hue: true);
        }

        private void HueHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _hueDragging = false;
            HueHost.ReleaseMouseCapture();
        }

        private void OpacityHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _opacityDragging = true;
            OpacityHost.CaptureMouse();
            CommitOpacityFromPointer(e.GetPosition(OpacityHost));
        }

        private void OpacityHost_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_opacityDragging) return;
            CommitOpacityFromPointer(e.GetPosition(OpacityHost));
        }

        private void OpacityHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _opacityDragging = false;
            OpacityHost.ReleaseMouseCapture();
        }

        private void OpacityBox_LostFocus(object sender, RoutedEventArgs e) => ApplyOpacityBox();

        private void ApplyOpacityBox()
        {
            if (_updating) return;
            if (!int.TryParse(OpacityBox.Text.Trim().TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pct))
            {
                SyncUiFromState(pushFields: true);
                return;
            }
            pct = Math.Clamp(pct, 0, 100);
            _alpha = (byte)Math.Round(pct / 100.0 * 255.0);
            SyncUiFromState(pushFields: true);
        }

        private void HexBox_LostFocus(object sender, RoutedEventArgs e) => ApplyHexBox();

        private void ApplyHexBox()
        {
            if (_updating) return;
            if (!ColorHex.TryParse(HexBox.Text, out var c))
            {
                SyncUiFromState(pushFields: true);
                return;
            }
            ColorHex.RgbToHsv(c.R, c.G, c.B, out _hue, out _sat, out _val);
            _alpha = c.A;
            SyncUiFromState(pushFields: true);
        }

        private void RgbBox_LostFocus(object sender, RoutedEventArgs e) => ApplyRgbBoxes();

        private void ApplyRgbBoxes()
        {
            if (_updating) return;
            if (!TryParseByte(RBox.Text, out byte r) ||
                !TryParseByte(GBox.Text, out byte g) ||
                !TryParseByte(BBox.Text, out byte b))
            {
                SyncUiFromState(pushFields: true);
                return;
            }
            ColorHex.RgbToHsv(r, g, b, out _hue, out _sat, out _val);
            SyncUiFromState(pushFields: true);
        }

        private static bool TryParseByte(string text, out byte value)
        {
            value = 0;
            return byte.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private void Field_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (sender == OpacityBox) ApplyOpacityBox();
            else if (sender == HexBox) ApplyHexBox();
            else ApplyRgbBoxes();
            e.Handled = true;
        }

        private void OkBtn_Click(object sender, RoutedEventArgs e)
        {
            SelectedColor = CurrentColor();
            DialogResult = true;
            Close();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            if (IsLoaded) SyncUiFromState(pushFields: false, notify: false);
        }
    }
}
