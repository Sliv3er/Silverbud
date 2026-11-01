using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Silverbud.Controls;

public partial class BudGauge : UserControl
{
    public static readonly DependencyProperty LevelProperty = Register(nameof(Level), typeof(int), -1);
    public static readonly DependencyProperty ChargingProperty = Register(nameof(Charging), typeof(bool), false);
    public static readonly DependencyProperty CaptionProperty = Register(nameof(Caption), typeof(string), "");
    public static readonly DependencyProperty StateProperty = Register(nameof(State), typeof(string), "");
    public static readonly DependencyProperty MirroredProperty = Register(nameof(Mirrored), typeof(bool), false);

    private bool? _pulsing;

    public BudGauge()
    {
        InitializeComponent();
        Render();
    }

    public int Level { get => (int)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public bool Charging { get => (bool)GetValue(ChargingProperty); set => SetValue(ChargingProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
    public string State { get => (string)GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public bool Mirrored { get => (bool)GetValue(MirroredProperty); set => SetValue(MirroredProperty, value); }

    private static DependencyProperty Register(string name, System.Type type, object fallback) =>
        DependencyProperty.Register(name, type, typeof(BudGauge),
            new PropertyMetadata(fallback, (d, _) => ((BudGauge)d).Render()));

    private void Render()
    {
        if (Number is null)
            return;
        bool known = Level >= 0;
        Number.Text = known ? Level.ToString() : "—";
        Percent.Visibility = known ? Visibility.Visible : Visibility.Collapsed;
        CaptionText.Text = Caption;
        StateText.Text = State;
        Readout.Visibility = string.IsNullOrEmpty(Caption) ? Visibility.Collapsed : Visibility.Visible;
        Mirror.ScaleX = Mirrored ? -1 : 1;

        var color = Gauge.LevelColor(Level);
        Light.Background = new SolidColorBrush(color);
        Glow.Color = color;
        Gauge.Fill(LightScale, ScaleTransform.ScaleYProperty, Level);
        if (_pulsing != Charging)
        {
            _pulsing = Charging;
            Gauge.Pulse(Light, Charging);
        }
    }
}
