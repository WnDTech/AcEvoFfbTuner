using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

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

        public static readonly DependencyProperty LabelAboveProperty =
            DependencyProperty.Register(nameof(LabelAbove), typeof(bool), typeof(LabeledSlider),
                new PropertyMetadata(false, OnLabelAboveChanged));

        public bool LabelAbove { get => (bool)GetValue(LabelAboveProperty); set => SetValue(LabelAboveProperty, value); }

        // ── Feel hints: what each end of the slider does + live "vs default" readout ──
        public static readonly DependencyProperty LowLabelProperty =
            DependencyProperty.Register(nameof(LowLabel), typeof(string), typeof(LabeledSlider),
                new PropertyMetadata(string.Empty, OnEndsChanged));
        public static readonly DependencyProperty HighLabelProperty =
            DependencyProperty.Register(nameof(HighLabel), typeof(string), typeof(LabeledSlider),
                new PropertyMetadata(string.Empty, OnEndsChanged));
        public static readonly DependencyProperty ShowMeaningProperty =
            DependencyProperty.Register(nameof(ShowMeaning), typeof(bool), typeof(LabeledSlider),
                new PropertyMetadata(true, OnEndsChanged));

        /// <summary>What the car feels like with the slider at the LEFT end (e.g. "Lighter, less detail").</summary>
        public string LowLabel { get => (string)GetValue(LowLabelProperty); set => SetValue(LowLabelProperty, value); }
        /// <summary>What the car feels like with the slider at the RIGHT end (e.g. "Heavier, more detail").</summary>
        public string HighLabel { get => (string)GetValue(HighLabelProperty); set => SetValue(HighLabelProperty, value); }
        /// <summary>Show the "+25% vs default" line under the slider.</summary>
        public bool ShowMeaning { get => (bool)GetValue(ShowMeaningProperty); set => SetValue(ShowMeaningProperty, value); }

        private static void OnEndsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (LabeledSlider)d;
            c.ApplyEnds();
            c.UpdateMeaning();
        }

        public static readonly DependencyProperty RecentlyChangedProperty =
            DependencyProperty.Register(nameof(RecentlyChanged), typeof(bool), typeof(LabeledSlider),
                new PropertyMetadata(false, OnRecentlyChanged));

        public bool RecentlyChanged { get => (bool)GetValue(RecentlyChangedProperty); set => SetValue(RecentlyChangedProperty, value); }

        private DispatcherTimer? _glowTimer;
        private static readonly Duration GlowDuration = TimeSpan.FromSeconds(5);

        private static void OnRecentlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (LabeledSlider)d;
            if (c.GlowBorder == null) return;

            if ((bool)e.NewValue)
            {
                c.StartGlowAnimation();
            }
        }

        private void StartGlowAnimation()
        {
            _glowTimer?.Stop();

            var sectionBrush = SectionColor as SolidColorBrush;
            var glowColor = sectionBrush?.Color ?? Color.FromRgb(0xF0, 0x88, 0x3E);

            var borderAnim = new ColorAnimation
            {
                From = glowColor,
                To = Colors.Transparent,
                Duration = GlowDuration,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            var thicknessAnim = new ThicknessAnimation
            {
                From = new Thickness(2, 1, 2, 1),
                To = new Thickness(0),
                Duration = GlowDuration,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            Storyboard.SetTarget(borderAnim, GlowBorder);
            Storyboard.SetTargetProperty(borderAnim, new PropertyPath("(Border.BorderBrush).(SolidColorBrush.Color)"));
            Storyboard.SetTarget(thicknessAnim, GlowBorder);
            Storyboard.SetTargetProperty(thicknessAnim, new PropertyPath("(Border.BorderThickness)"));

            var sb = new Storyboard();
            sb.Children.Add(borderAnim);
            sb.Children.Add(thicknessAnim);

            GlowBorder.BorderBrush = new SolidColorBrush(glowColor);
            GlowBorder.BorderThickness = new Thickness(2, 1, 2, 1);

            sb.Completed += (s, e) =>
            {
                RecentlyChanged = false;
                GlowBorder.BorderThickness = new Thickness(0);
            };

            sb.Begin(GlowBorder);

            _glowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5.1) };
            _glowTimer.Tick += (s, e) =>
            {
                _glowTimer.Stop();
                RecentlyChanged = false;
            };
            _glowTimer.Start();
        }

        private void TriggerRecentlyChanged()
        {
            RecentlyChanged = false;
            RecentlyChanged = true;
        }

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
            ApplyEnds();
            UpdateMeaning();
            UpdateDefaultMarker();
            BuildContextMenu();
            PART_Slider.SizeChanged += (_, _) => UpdateDefaultMarker();

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
            c.UpdateDefaultMarker();
            c.UpdateMeaning();
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
            ((LabeledSlider)d).UpdateDefaultMarker();
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

        private static void OnLabelAboveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((LabeledSlider)d).ApplyLabel();
            ((LabeledSlider)d).ApplyDescription();
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
            TriggerRecentlyChanged();
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
            TriggerRecentlyChanged();
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
            // Plain number while editing, so unit/percent formats ("50%", "42 Hz") stay editable.
            PART_ValueTextBox.Text = Value.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
            PART_ValueTextBox.SelectAll();
        }

        private void OnTextBoxLostFocus(object sender, RoutedEventArgs e)
        {
            CommitTextBox();
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
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        }

        private void CommitTextBox()
        {
            if (!_isEditing) return;
            _isEditing = false;

            string text = PART_ValueTextBox.Text.Trim().TrimEnd('%', '°', 'x', '×').Trim();
            int sp = text.IndexOf(' ');
            if (sp > 0) text = text.Substring(0, sp);
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
                TriggerRecentlyChanged();
            }
            SyncTextBoxFromValue();
        }

        #endregion

        #region Apply Methods

        private void ApplyLabel()
        {
            if (PART_Label == null) return;
            string text = Label ?? string.Empty;
            PART_Label.Text = text;
            if (PART_LabelAbove != null)
            {
                PART_LabelAbove.Text = text;
                PART_LabelAbove.Visibility = LabelAbove ? Visibility.Visible : Visibility.Collapsed;
            }
            PART_Label.Visibility = LabelAbove ? Visibility.Collapsed : Visibility.Visible;
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
            double left = LabelAbove
                ? (ShowCheckBox ? 32 : 0)
                : LabelWidth + (ShowCheckBox ? 30 : 0) + 12;
            PART_Description.Margin = new Thickness(left, 3, 30, 2);
        }

        private void ApplyLabelWidth()
        {
            if (PART_Label == null) return;
            PART_Label.Width = LabelWidth;
        }

        private void ApplyValueWidth()
        {
            if (PART_ValueTextBox == null) return;
            PART_ValueTextBox.MinWidth = ValueWidth;
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

        #region Feel hints

        private void ApplyEnds()
        {
            if (PART_EndsRow == null) return;
            bool any = !string.IsNullOrWhiteSpace(LowLabel) || !string.IsNullOrWhiteSpace(HighLabel);
            PART_EndsRow.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            PART_LowLabel.Text = string.IsNullOrWhiteSpace(LowLabel) ? string.Empty : "◀ " + LowLabel;
            PART_HighLabel.Text = string.IsNullOrWhiteSpace(HighLabel) ? string.Empty : HighLabel + " ▶";
        }

        private void UpdateDefaultMarker()
        {
            if (PART_DefaultMarker == null || PART_Slider == null) return;
            if (!DefaultValue.HasValue || Maximum <= Minimum || PART_Slider.ActualWidth <= 40)
            {
                PART_DefaultMarker.Visibility = Visibility.Collapsed;
                return;
            }
            // Thumb centre travels from (padding 4 + half thumb 15) to (width - 19).
            double frac = ValueToPosition(DefaultValue.Value) / Res;
            double usable = PART_Slider.ActualWidth - 38;
            Canvas.SetLeft(PART_DefaultMarker, 19 + frac * usable - 1);
            PART_DefaultMarker.Visibility = Visibility.Visible;
            PART_DefaultMarker.ToolTip = $"Default: {FormatValue(DefaultValue.Value)}";
        }

        /// <summary>
        /// Turns the raw number into something readable: how far it is from the
        /// built-in default, and which way (using the Low/High feel words).
        /// </summary>
        private void UpdateMeaning()
        {
            if (PART_Meaning == null) return;
            if (!ShowMeaning || !DefaultValue.HasValue)
            {
                PART_Meaning.Visibility = Visibility.Collapsed;
                return;
            }

            double d = DefaultValue.Value;
            double v = Value;
            double tol = Math.Max(TickFrequency * 0.5, 1e-9);
            string def = FormatValue(d);

            string text;
            if (Math.Abs(v - d) <= tol)
            {
                text = $"● At default ({def})";
            }
            else
            {
                string toward = v > d ? HighLabel : LowLabel;
                string dir = string.IsNullOrWhiteSpace(toward) ? string.Empty : $" → {toward.ToLowerInvariant()}";

                if (Math.Abs(d) < 1e-9)
                    text = Math.Abs(v) < 1e-9
                        ? $"Default is {def} (off)"
                        : $"Default is {def} (off) — now active{dir}";
                else if (Math.Abs(v) < 1e-9)
                    text = $"Off (default {def})";
                else if (Math.Sign(v) != Math.Sign(d))
                    text = $"Direction flipped vs default ({def})";
                else
                {
                    double ratio = v / d;
                    string rel = ratio >= 2.0 || ratio <= 0.5
                        ? $"{ratio:0.0#}× default"
                        : $"{(ratio - 1.0) * 100.0:+0;-0}% vs default";
                    text = $"{rel} ({def}){dir}";
                }
            }

            PART_Meaning.Text = text;
            PART_Meaning.ToolTip = text;
            PART_Meaning.Visibility = Visibility.Visible;
            double left = LabelAbove ? (ShowCheckBox ? 32 : 0) : LabelWidth + (ShowCheckBox ? 30 : 0) + 12;
            PART_Meaning.Margin = new Thickness(left, 6, 30, 0);
        }

        #endregion

        #region Reset Button

        private void UpdateResetButton()
        {
            UpdateMeaning();
            if (PART_ResetButton == null) return;
            if (DefaultValue.HasValue && Math.Abs(Value - DefaultValue.Value) > TickFrequency * 0.01)
            {
                PART_ResetButton.Visibility = Visibility.Visible;
                PART_ResetButton.ToolTip = $"Reset to default ({FormatValue(DefaultValue.Value)})";
            }
            else
            {
                // Hidden (not Collapsed) keeps its 26px slot, so the track never
                // resizes under the mouse when you drag across the default.
                PART_ResetButton.Visibility = Visibility.Hidden;
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
            var menuStyle = new Style(typeof(ContextMenu));
            menuStyle.Setters.Add(new Setter(BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x3A))));
            menuStyle.Setters.Add(new Setter(BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55))));
            menuStyle.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(1)));
            menuStyle.Setters.Add(new Setter(ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0))));
            menuStyle.Setters.Add(new Setter(FontSizeProperty, 14.0));

            var itemStyle = new Style(typeof(MenuItem));
            itemStyle.Setters.Add(new Setter(ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0))));
            itemStyle.Setters.Add(new Setter(FontSizeProperty, 14.0));
            itemStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(24, 6, 24, 6)));
            var itemHoverTrigger = new Trigger { Property = IsMouseOverProperty, Value = true };
            itemHoverTrigger.Setters.Add(new Setter(BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x55))));
            itemHoverTrigger.Setters.Add(new Setter(ForegroundProperty, Brushes.White));
            itemStyle.Triggers.Add(itemHoverTrigger);
            var itemDisabledTrigger = new Trigger { Property = IsEnabledProperty, Value = false };
            itemDisabledTrigger.Setters.Add(new Setter(ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88))));
            itemStyle.Triggers.Add(itemDisabledTrigger);

            var menu = new ContextMenu { Style = menuStyle };

            MenuItem MakeItem(string header, RoutedEventHandler handler)
            {
                var item = new MenuItem { Header = header, Style = itemStyle };
                item.Click += handler;
                return item;
            }

            var undoItem = MakeItem("Undo last change", (s, e) => UndoLastChange());
            undoItem.InputGestureText = "Ctrl+Z";
            menu.Items.Add(undoItem);

            menu.Items.Add(new Separator());

            var resetItem = MakeItem("Reset to Default", (s, e) => { if (DefaultValue.HasValue) ResetToValue(DefaultValue.Value); });
            menu.Items.Add(resetItem);

            menu.Items.Add(new Separator());

            menu.Items.Add(MakeItem("Copy Value", (s, e) => Clipboard.SetText(Value.ToString("G", System.Globalization.CultureInfo.InvariantCulture))));

            menu.Items.Add(MakeItem("Paste Value", (s, e) =>
            {
                if (double.TryParse(Clipboard.GetText(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double val))
                {
                    val = Math.Clamp(val, Minimum, Maximum);
                    val = SnapToTick(val);
                    ResetToValue(val);
                }
            }));

            menu.Items.Add(new Separator());

            menu.Items.Add(MakeItem("Set current as Default", (s, e) => DefaultValue = Value));

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
