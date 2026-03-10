using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SCPBrowser.Controls
{
    public partial class NumericUpDownControl : UserControl
    {
        private int _value = 800;
        private int _minimum = 0;
        private int _maximum = 5000;
        private int _increment = 50;
        private bool _showInfinityAtMax = false;
        private int? _snapDownValue = null;

        public event EventHandler<int> ValueChanged;

        public NumericUpDownControl()
        {
            InitializeComponent();
            UpdateDisplay();
        }

        public int Value
        {
            get => _value;
            set
            {
                int clamped = Math.Max(_minimum, Math.Min(_maximum, value));
                if (_value != clamped)
                {
                    _value = clamped;
                    UpdateDisplay();
                    ValueChanged?.Invoke(this, _value);
                }
            }
        }

        public int Minimum
        {
            get => _minimum;
            set
            {
                _minimum = value;
                if (_value < _minimum)
                    Value = _minimum;
            }
        }

        public int Maximum
        {
            get => _maximum;
            set
            {
                _maximum = value;
                if (_value > _maximum)
                    Value = _maximum;
            }
        }

        public int Increment
        {
            get => _increment;
            set => _increment = value;
        }

        public bool ShowInfinityAtMax
        {
            get => _showInfinityAtMax;
            set
            {
                _showInfinityAtMax = value;
                UpdateDisplay();
            }
        }

        /// <summary>
        /// When set, the first down-click from the maximum value snaps to this value
        /// instead of decrementing by the normal increment.
        /// </summary>
        public int? SnapDownValue
        {
            get => _snapDownValue;
            set => _snapDownValue = value;
        }

        public void SetColorScheme(string background, string borderBrush, string foreground)
        {
            OuterBorder.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(background));
            OuterBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(borderBrush));
            ValueTextBox.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(foreground));
        }

        public string Label
        {
            get => LabelText.Text;
            set => LabelText.Text = value;
        }

        private void UpdateDisplay()
        {
            if (_showInfinityAtMax && _value >= _maximum)
                ValueTextBox.Text = "\u221E";
            else
                ValueTextBox.Text = _value.ToString();
        }

        private void UpButton_Click(object sender, RoutedEventArgs e)
        {
            IncrementValue();
        }

        private void DownButton_Click(object sender, RoutedEventArgs e)
        {
            DecrementValue();
        }

        private void IncrementValue()
        {
            if (_snapDownValue.HasValue && _value + _increment > _snapDownValue.Value && _value < _maximum)
            {
                // Jump to max (infinity) from near the snap-down value
                Value = _maximum;
            }
            else
            {
                Value += _increment;
            }
        }

        private void DecrementValue()
        {
            if (_snapDownValue.HasValue && _value >= _maximum)
            {
                // First down-click from infinity: snap to data max
                Value = _snapDownValue.Value;
            }
            else
            {
                Value -= _increment;
            }
        }

        private void ValueTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            // Allow only digits
            foreach (char c in e.Text)
            {
                if (!char.IsDigit(c))
                {
                    e.Handled = true;
                    return;
                }
            }
        }

        private void ValueTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyTextBoxValue();
        }

        private void ValueTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ApplyTextBoxValue();
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                IncrementValue();
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                DecrementValue();
                e.Handled = true;
            }
        }

        private void ValueTextBox_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta > 0)
                IncrementValue();
            else
                DecrementValue();

            e.Handled = true;
        }

        private void ApplyTextBoxValue()
        {
            if (int.TryParse(ValueTextBox.Text, out int parsed))
            {
                Value = parsed;
            }
            else
            {
                UpdateDisplay(); // Reset to current value
            }
        }
    }
}