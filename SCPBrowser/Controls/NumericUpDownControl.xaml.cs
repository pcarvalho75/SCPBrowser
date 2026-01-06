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

        public string Label
        {
            get => LabelText.Text;
            set => LabelText.Text = value;
        }

        private void UpdateDisplay()
        {
            ValueTextBox.Text = _value.ToString();
        }

        private void UpButton_Click(object sender, RoutedEventArgs e)
        {
            Value += _increment;
        }

        private void DownButton_Click(object sender, RoutedEventArgs e)
        {
            Value -= _increment;
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
                Value += _increment;
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                Value -= _increment;
                e.Handled = true;
            }
        }

        private void ValueTextBox_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta > 0)
                Value += _increment;
            else
                Value -= _increment;

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