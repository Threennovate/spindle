using Avalonia.Controls;
using Spindle.Windows.Model;

namespace Spindle.Windows.Views;

/// <summary>
/// "Beacon has been quiet since 11:05": for a split entry the timer just stopped, when its work changed while it ran
/// and nobody answered that, offers to end the split there. It goes away on its own after a while.
/// </summary>
public sealed class DriftOfferCard : UserControl
{
    public DriftOfferCard(MainWindow owner, DriftOffer offer)
    {
        var text = Ui.Stack(2, Ui.Text(owner.Splits.Headline(offer.Drift), "strong"), Ui.Text(owner.Splits.Question(offer), "secondary", "mono"),
            Ui.Text("Also in today's review", "caption", "tertiary").Margin(0, 2, 0, 0));
        var notNow = Ui.Button("Not now", () => owner.Desktop.NotNowAsync(offer), owner.Notify).Tip("Leave the entry as it is; nothing more is offered until its split changes");
        var review = Ui.Button("Review…", () => owner.Desktop.ReviewAsync(offer), owner.Notify).Tip("Open the entry to change its split or times first");
        var take = Ui.Button(owner.Splits.Title(offer.Drift), () => owner.Desktop.TakeAsync(offer), owner.Notify, "primary").Named("TakeDrift");
        var row = Ui.Columns("Auto,*,Auto", 14, Ui.Icon("IconSparkle", "brand", "large").Centered(), text.Centered(), Ui.Row(8, notNow, review, take).Centered());
        Content = new Border { Child = row, MaxWidth = 680 }.Classed("toast").Named("DriftOffer");
    }
}
