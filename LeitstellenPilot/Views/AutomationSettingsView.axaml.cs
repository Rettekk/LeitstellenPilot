using Avalonia.Controls;

namespace LeitstellenPilot.Views;

public partial class AutomationSettingsView : UserControl
{
    private bool? _pauseCardsStacked;
    private bool? _dispatchCardsStacked;
    private bool? _transportCardsStacked;

    public AutomationSettingsView()
    {
        InitializeComponent();
        PauseSettingsLayout.SizeChanged += (_, _) => ArrangeCards(PauseSettingsLayout, RestSettingsCard, ref _pauseCardsStacked);
        DispatchSettingsLayout.SizeChanged += (_, _) => ArrangeCards(DispatchSettingsLayout, PlannedSettingsCard, ref _dispatchCardsStacked);
        TransportSettingsLayout.SizeChanged += (_, _) => ArrangeCards(TransportSettingsLayout, RewardSettingsCard, ref _transportCardsStacked);
    }

    private static void ArrangeCards(Grid layout, Control secondCard, ref bool? previouslyStacked)
    {
        var stacked = layout.Bounds.Width < 700;
        if (previouslyStacked == stacked) return;
        previouslyStacked = stacked;
        layout.ColumnDefinitions = new ColumnDefinitions(stacked ? "*" : "*,*");
        layout.RowDefinitions = new RowDefinitions(stacked ? "Auto,Auto" : "Auto");
        Grid.SetColumn(secondCard, stacked ? 0 : 1);
        Grid.SetRow(secondCard, stacked ? 1 : 0);
    }
}
