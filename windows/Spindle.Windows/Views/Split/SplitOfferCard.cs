using Avalonia;
using Avalonia.Controls;
using Spindle.Windows.Desktop;

namespace Spindle.Windows.Views;

/// <summary>
/// "“Booking flow” went to more than one project": offers a split for the entry the timer just stopped, to take with
/// one click, look at first, or leave. It goes away on its own after a while; the entry keeps its chip, and the day's
/// review has it.
/// </summary>
public sealed class SplitOfferCard : UserControl
{
    public SplitOfferCard(MainWindow owner, SplitOffer offer)
    {
        var entry = offer.Entry;
        var title = (entry.Description.Length == 0 ? "This time" : $"“{entry.Description}”") + " went to more than one project";
        var strip = new EvidenceStrip(owner, offer.Suggestion) { Width = 260, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 0) };
        // When it goes away unanswered, nothing is lost.
        var text = Ui.Stack(2, Ui.Text(title, "strong"), Ui.Text(owner.Splits.Summary(offer.Suggestion, entry), "secondary", "mono"), strip,
            Ui.Text("Also in today's review", "caption", "tertiary").Margin(0, 2, 0, 0));
        var keep = Ui.Button("Keep as is", () => owner.Desktop.KeepAsync(offer), owner.Notify).Tip("Leave the entry on one project and stop suggesting a split for it");
        var review = Ui.Button("Review…", () => owner.Desktop.ReviewAsync(offer), owner.Notify).Tip("Open the entry with the split, to change it first");
        var take = Ui.Button(owner.Splits.Title(offer.Suggestion), () => owner.Desktop.TakeAsync(offer), owner.Notify, "primary").Named("TakeOffer");
        var row = Ui.Columns("Auto,*,Auto", 14, Ui.Icon("IconSparkle", "brand", "large").Centered(), text.Centered(), Ui.Row(8, keep, review, take).Centered());
        Content = new Border { Child = row, MaxWidth = 680 }.Classed("toast").Named("SplitOffer");
    }
}
