using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Silverbud.Controls;

public partial class CaseGauge : UserControl
{
    public static readonly DependencyProperty LevelProperty = Register(nameof(Level), typeof(int), -1);
    public static readonly DependencyProperty ChargingProperty = Register(nameof(Charging), typeof(bool), false);
    public static readonly DependencyProperty StateProperty = Register(nameof(State), typeof(string), "");

    private bool? _pulsing;

    public CaseGauge()
    {
        InitializeComponent();
        Render();
    }

    public int Level { get => (int)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public bool Charging { get => (bool)GetValue(ChargingProperty); set => SetValue(ChargingProperty, value); }
    public string State { get => (string)GetValue(StateProperty); set => SetValue(StateProperty, value); }

    private static DependencyProperty Register(string name, System.Type type, object fallback) =>
        DependencyProperty.Register(name, type, typeof(CaseGauge),
            new PropertyMetadata(fallback, (d, _) => ((CaseGauge)d).Render()));

    private void Render()
    {
        if (Number is null)
            return;
        bool known = Level >= 0;
        Number.Text = known ? Level.ToString() : "—";
        Percent.Visibility = known ? Visibility.Visible : Visibility.Collapsed;
        StateText.Text = State;

        var color = Gauge.LevelColor(Level);
        Light.Background = new SolidColorBrush(color);
        Glow.Color = color;
        Gauge.Fill(LightScale, ScaleTransform.ScaleXProperty, Level);
        if (_pulsing != Charging)
        {
            _pulsing = Charging;
            Gauge.Pulse(Light, Charging);
        }
    }
}
