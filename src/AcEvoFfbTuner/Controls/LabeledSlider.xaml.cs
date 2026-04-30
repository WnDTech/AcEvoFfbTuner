using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AcEvoFfbTuner.Controls
{
    public partial class LabeledSlider : UserControl
    {
        private double _previousValue;
        private bool _hasUndoValue;
        private bool _isUpdatingValue;
        private bool _isUpdatingSlider;
        private bool _isEditing;
        private double _valueBeforeEdit;
        private bool _isUndoing;
        private bool _sliderActive;
        private const double Res = 10000.0;

        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(nameof(Value), typeof(double), typeof(LabeledSlider),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

        public static readonly DependencyProperty MinimumProperty =
            DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(LabeledSlider),
                new FrameworkPropertyMetadata(0.0, OnRangeChanged));

        public static readonly DependencyProperty MaximumProperty =
            DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(LabeledSlider),
            new FrameworkPropertyMetadata(100.0, OnRangeChanged));

        public static readonly DependencyProperty TickFrequencyProperty =
            DependencyProperty.Register(nameof(TickFrequency), typeof(double), typeof(LabeledSlider),
                new FrameworkPropertyMetadata(1.0));

        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(nameof(Label), typeof(string), typeof(LabeledSlider),
                new PropertyMetadata(string.Empty, OnLabelChanged));

        public static readonly DependencyProperty DescriptionProperty =
            DependencyProperty.Register(nameof(Description), typeof(string), typeof(LabeledSlider),
                new PropertyMetadata(string.Empty, OnDescriptionChanged));

        public static readonly DependencyProperty DefaultValueProperty =
            DependencyProperty.Register(nameof(DefaultValue), typeof(double?), typeof(LabeledSlider),
                new PropertyMetadata(null, OnDefaultValueChanged));

        public static readonly DependencyProperty RecommendedMinProperty =
            DependencyProperty.Register(nameof(RecommendedMin), typeof(double?), typeof(LabeledSlider),
                new PropertyMetadata(null));

        public static readonly DependencyProperty RecommendedMaxProperty =
            DependencyProperty.Register(nameof(RecommendedMax), typeof(double?), typeof(LabeledSlider),
                new PropertyMetadata(null));

        public static readonly DependencyProperty StringFormatProperty =
            DependencyProperty.Register(nameof(StringFormat), typeof(string), typeof(LabeledSlider),
                new PropertyMetadata("{0:F3}", OnDisplayPropertyChanged));

        public static readonly DependencyProperty IsLogarithmicProperty =
            DependencyProperty.Register(nameof(IsLogarithmic), typeof(bool), typeof(LabeledSlider),
                new FrameworkPropertyMetadata(false));

        public static readonly DependencyProperty SectionColorProperty =
            DependencyProperty.Register(nameof(SectionColor), typeof(Brush), typeof(LabeledSlider),
                new PropertyMetadata(null, OnSectionColorChanged));

        public static readonly DependencyProperty ShowCheckBoxProperty =
            DependencyProperty.Register(nameof(ShowCheckBox), typeof(bool), typeof(LabeledSlider),
                new PropertyMetadata(false, OnShowCheckBoxChanged));

        public static readonly DependencyProperty IsCheckedProperty =
            DependencyProperty.Register(nameof(IsChecked), typeof(bool), typeof(LabeledSlider),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsCheckedChanged));

        public static readonly DependencyProperty SuffixProperty =
            DependencyProperty.Register(nameof(Suffix), typeof(string), typeof(LabeledSlider),
                new PropertyMetadata(string.Empty, OnDisplayPropertyChanged));

        public static readonly DependencyProperty LabelWidthProperty =
            DependencyProperty.Register(nameof(LabelWidth), typeof(double), typeof(LabeledSlider),
                new PropertyMetadata(160.0, OnLabelWidthChanged));

        public static readonly DependencyProperty DisplayTextProperty =
            DependencyProperty.Register(nameof(DisplayText), typeof(string), typeof(LabeledSlider),
                new PropertyMetadata(null, OnDisplayPropertyChanged));

        public static readonly DependencyProperty ValueWidthProperty =
            DependencyProperty.Register(nameof(ValueWidth), typeof(double), typeof(LabeledSlider),
                new PropertyMetadata(72.0, OnValueWidthChanged));

        public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
        public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
        public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
        public double TickFrequency { get => (double)GetValue(TickFrequencyProperty); set => SetValue(TickFrequencyProperty, value); }
        public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
        public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
        public double? DefaultValue { get => (double?)GetValue(DefaultValueProperty); set => SetValue(DefaultValueProperty, value); }
        public double? RecommendedMin { get => (double?)GetValue(RecommendedMinProperty); set => SetValue(RecommendedMinProperty, value); }
        public double? RecommendedMax { get => (double?)GetValue(RecommendedMaxProperty); set => SetValue(RecommendedMaxProperty, value); }
        public string StringFormat { get => (string)GetValue(StringFormatProperty); set => SetValue(StringFormatProperty, value); }
        public bool IsLogarithmic { get => (bool)GetValue(IsLogarithmicProperty); set => SetValue(IsLogarithmicProperty, value); }
        public Brush SectionColor { get => (Brush)GetValue(SectionColorProperty); set => SetValue(SectionColorProperty, value); }
        public bool ShowCheckBox { get => (bool)GetValue(ShowCheckBoxProperty); set => SetValue(ShowCheckBoxProperty, value); }
        public bool IsChecked { get => (bool)GetValue(IsCheckedProperty); set => SetValue(IsCheckedProperty, value); }
        public string Suffix { get => (string)GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }
        public double LabelWidth { get => (double)GetValue(LabelWidthProperty); set => SetValue(LabelWidthProperty, value); }
        public string DisplayText { get => (string)GetValue(DisplayTextProperty); set => SetValue(DisplayTextProperty, value); }
        public double ValueWidth { get => (double)GetValue(ValueWidthProperty); set => SetValue(ValueWidthProperty, value); }

        public LabeledSlider()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplyLabel();
            ApplyDescription();
            ApplyLabelWidth();
            ApplyValueWidth();
            ApplyShowCheckBox();
            ApplyIsChecked();
            ApplySectionColor();
            SyncSliderFromValue();
            SyncTextBoxFromValue();
            UpdateResetButton();
            BuildContextMenu();

            PART_CheckBox.Checked += (s, _) => IsChecked = true;
            PART_CheckBox.Unchecked += (s, _) => IsChecked = false;
        }

        #region DP Callbacks

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (LabeledSlider)d;
            if (c._isUpdatingValue) return;

            if (!c._isUndoing)
            {
                c._previousValue = (double)e.OldValue;
                c._hasUndoValue = true;
            }

            c.SyncSliderFromValue();
            c.SyncTextBoxFromValue();
            c.UpdateResetButton();
        }

        private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (LabeledSlider)d;
            c.SyncSliderFromValue();
        }

        private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((LabeledSlider)d).ApplyLabel();
        }

        private static void OnDescriptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((LabeledSlider)d).ApplyDescription();
        }

        private static void OnDefaultValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((LabeledSlider)d).UpdateResetButton();
        }

        private static void OnDisplayPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((LabeledSlider)d).SyncTextBoxFromValue();
        }

        private static void OnSectionColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((LabeledSlider)d).ApplySectionColor();
        }

        private static void OnShowCheckBoxChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((LabeledSlider)d).ApplyShowCheckBox();
            ((LabeledSlider)d).ApplyDescription();
        }

        private static void OnIsCheckedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((LabeledSlider)d).ApplyIsChecked();
        }

        private static void OnLabelWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((LabeledSlider)d).ApplyLabelWidth();
            ((LabeledSlider)d).ApplyDescription();
        }

        private static void OnValueWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((LabeledSlider)d).ApplyValueWidth();
        }

        #endregion

        #region Position <-> Value Mapping

        private double PositionToValue(double position)
        {
            if (IsLogarithmic && Minimum > 0 && Maximum > Minimum)
            {
                double logMin = Math.Log(Minimum);
                double logMax = Math.Log(Maximum);
                return Math.Exp(logMin + (position / Res) * (logMax - logMin));
            }
            return Minimum + (position / Res) * (Maximum - Minimum);
        }

        private double ValueToPosition(double value)
        {
            if (IsLogarithmic && Minimum > 0 && Maximum > Minimum)
            {
                double logMin = Math.Log(Minimum);
                double logMax = Math.Log(Maximum);
                if (value <= Minimum) return 0;
                if (value >= Maximum) return Res;
                return (Math.Log(value) - logMin) / (logMax - logMin) * Res;
            }
            if (Maximum <= Minimum) return 0;
            return Math.Clamp((value - Minimum) / (Maximum - Minimum) * Res, 0, Res);
        }

        private double SnapToTick(double value)
        {
            if (TickFrequency <= 0) return value;
            return Math.Round(value / TickFrequency) * TickFrequency;
        }

        #endregion

        #region Slider Sync

        private void SyncSliderFromValue()
        {
            if (PART_Slider == null) return;
            _isUpdatingSlider = true;
            PART_Slider.Value = ValueToPosition(Value);
            _isUpdatingSlider = false;
        }

        private void OnSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingSlider) return;
            if (!_sliderActive)
            {
                _previousValue = Value;
                _hasUndoValue = true;
            }
            double val = PositionToValue(PART_Slider.Value);
            val = SnapToTick(val);
            val = Math.Clamp(val, Minimum, Maximum);
            _isUpdatingValue = true;
            Value = val;
            _isUpdatingValue = false;
            SyncTextBoxFromValue();
            UpdateResetButton();
        }

        private void OnSliderPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_sliderActive)
            {
                _previousValue = Value;
                _hasUndoValue = true;
                _sliderActive = true;
            }
        }

        private void OnSliderPreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _sliderActive = false;
        }

        #endregion

        #region Mouse Wheel

        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_isEditing) return;
            if (Keyboard.FocusedElement == PART_ValueTextBox) return;

            double step = TickFrequency;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                step = TickFrequency * 0.1;
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                step = TickFrequency * 10;

            if (step <= 0) step = 0.01;

            double delta = e.Delta > 0 ? step : -step;
            double newVal = SnapToTick(Value + delta);
            newVal = Math.Clamp(newVal, Minimum, Maximum);

            _previousValue = Value;
            _hasUndoValue = true;
            _isUpdatingValue = true;
            Value = newVal;
            _isUpdatingValue = false;
            SyncSliderFromValue();
            SyncTextBoxFromValue();
            UpdateResetButton();
            e.Handled = true;
        }

        #endregion

        #region TextBox Editing

        private void SyncTextBoxFromValue()
        {
            if (_isEditing || PART_ValueTextBox == null) return;
            if (DisplayText != null)
            {
                PART_ValueTextBox.Text = DisplayText;
            }
            else
            {
                try
                {
                    PART_ValueTextBox.Text = string.Format(StringFormat, Value) + Suffix;
                }
                catch
                {
                    PART_ValueTextBox.Text = Value.ToString("G");
                }
            }
        }

        private void OnTextBoxGotFocus(object sender, RoutedEventArgs e)
        {
            _isEditing = true;
            _valueBeforeEdit = Value;
            PART_ValueTextBox.Text = Value.ToString("G");
            PART_ValueTextBox.SelectAll();
            PART_ValueTextBox.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x45));
            PART_ValueTextBox.BorderThickness = new Thickness(1);
            PART_ValueTextBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22));
        }

        private void OnTextBoxLostFocus(object sender, RoutedEventArgs e)
        {
            CommitTextBox();
            PART_ValueTextBox.Background = Brushes.Transparent;
            PART_ValueTextBox.BorderThickness = new Thickness(0);
            PART_ValueTextBox.BorderBrush = Brushes.Transparent;
        }

        private void OnTextBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitTextBox();
                Keyboard.ClearFocus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _isEditing = false;
                _isUpdatingValue = true;
                Value = _valueBeforeEdit;
                _isUpdatingValue = false;
                SyncSliderFromValue();
                SyncTextBoxFromValue();
                PART_ValueTextBox.Background = Brushes.Transparent;
                PART_ValueTextBox.BorderThickness = new Thickness(0);
                PART_ValueTextBox.BorderBrush = Brushes.Transparent;
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        }

        private void CommitTextBox()
        {
            if (!_isEditing) return;
            _isEditing = false;

            string text = PART_ValueTextBox.Text.Trim();
            if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double val))
            {
                val = Math.Clamp(val, Minimum, Maximum);
                val = SnapToTick(val);
                _previousValue = Value;
                _hasUndoValue = true;
                _isUpdatingValue = true;
                Value = val;
                _isUpdatingValue = false;
                SyncSliderFromValue();
                UpdateResetButton();
            }
            SyncTextBoxFromValue();
        }

        #endregion

        #region Apply Methods

        private void ApplyLabel()
        {
            if (PART_Label == null) return;
            PART_Label.Text = Label ?? string.Empty;
        }

        private void ApplyDescription()
        {
            if (PART_Description == null) return;
            if (string.IsNullOrEmpty(Description))
            {
                PART_Description.Visibility = Visibility.Collapsed;
                return;
            }
            PART_Description.Text = Description;
            PART_Description.Visibility = Visibility.Visible;
            double left = LabelWidth + (ShowCheckBox ? 30 : 0) + 12;
            PART_Description.Margin = new Thickness(left, 2, 30, 2);
        }

        private void ApplyLabelWidth()
        {
            if (PART_Label == null) return;
            PART_Label.Width = LabelWidth;
        }

        private void ApplyValueWidth()
        {
            if (PART_ValueTextBox == null) return;
            PART_ValueTextBox.Width = ValueWidth;
        }

        private void ApplyShowCheckBox()
        {
            if (PART_CheckBox == null) return;
            PART_CheckBox.Visibility = ShowCheckBox ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ApplyIsChecked()
        {
            if (PART_CheckBox == null) return;
            PART_CheckBox.IsChecked = IsChecked;
        }

        private void ApplySectionColor()
        {
            var brush = SectionColor ?? new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22));
            if (PART_Slider != null)
                PART_Slider.Foreground = brush;
            if (PART_ValueTextBox != null)
                PART_ValueTextBox.Foreground = brush;
        }

        #endregion

        #region Reset Button

        private void UpdateResetButton()
        {
            if (PART_ResetButton == null) return;
            if (DefaultValue.HasValue && Math.Abs(Value - DefaultValue.Value) > TickFrequency * 0.01)
            {
                PART_ResetButton.Visibility = Visibility.Visible;
                PART_ResetButton.ToolTip = $"Reset to default ({FormatValue(DefaultValue.Value)})";
            }
            else
            {
                PART_ResetButton.Visibility = Visibility.Collapsed;
            }
        }

        private string FormatValue(double val)
        {
            try { return string.Format(StringFormat, val) + Suffix; }
            catch { return val.ToString("G"); }
        }

        private void OnResetClick(object sender, RoutedEventArgs e)
        {
            if (DefaultValue.HasValue)
                ResetToValue(DefaultValue.Value);
        }

        private void ResetToValue(double val)
        {
            _previousValue = Value;
            _hasUndoValue = true;
            _isUpdatingValue = true;
            Value = val;
            _isUpdatingValue = false;
            SyncSliderFromValue();
            SyncTextBoxFromValue();
            UpdateResetButton();
        }

        #endregion

        #region Context Menu

        private void BuildContextMenu()
        {
            var menu = new ContextMenu();

            var undoItem = new MenuItem { Header = "Undo last change", InputGestureText = "Ctrl+Z" };
            undoItem.Click += (s, e) => UndoLastChange();
            menu.Items.Add(undoItem);

            menu.Items.Add(new Separator());

            var resetItem = new MenuItem { Header = "Reset to Default" };
            resetItem.Click += (s, e) => { if (DefaultValue.HasValue) ResetToValue(DefaultValue.Value); };
            menu.Items.Add(resetItem);

            menu.Items.Add(new Separator());

            var copyItem = new MenuItem { Header = "Copy Value" };
            copyItem.Click += (s, e) => Clipboard.SetText(Value.ToString("G", System.Globalization.CultureInfo.InvariantCulture));
            menu.Items.Add(copyItem);

            var pasteItem = new MenuItem { Header = "Paste Value" };
            pasteItem.Click += (s, e) =>
            {
                if (double.TryParse(Clipboard.GetText(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double val))
                {
                    val = Math.Clamp(val, Minimum, Maximum);
                    val = SnapToTick(val);
                    ResetToValue(val);
                }
            };
            menu.Items.Add(pasteItem);

            menu.Items.Add(new Separator());

            var setDefaultItem = new MenuItem { Header = "Set current as Default" };
            setDefaultItem.Click += (s, e) => DefaultValue = Value;
            menu.Items.Add(setDefaultItem);

            menu.Opened += (s, e) =>
            {
                undoItem.IsEnabled = _hasUndoValue;
                resetItem.IsEnabled = DefaultValue.HasValue;
            };

            ContextMenu = menu;
        }

        #endregion

        #region Undo

        private void UndoLastChange()
        {
            if (!_hasUndoValue) return;
            _isUndoing = true;
            double undoVal = _previousValue;
            _hasUndoValue = false;
            _isUpdatingValue = true;
            Value = undoVal;
            _isUpdatingValue = false;
            _isUndoing = false;
            SyncSliderFromValue();
            SyncTextBoxFromValue();
            UpdateResetButton();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
            {
                UndoLastChange();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        #endregion
    }
}
