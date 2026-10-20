import SwiftUI
import SpindleKit

extension View {
    /// Dates and times as the account shows them: in its time zone, and with a 12- or 24-hour clock as chosen in
    /// its preferences, whatever the phone is set to.
    func accountTime(_ time: UserTime) -> some View {
        environment(\.timeZone, time.timeZone)
            .environment(\.calendar, time.calendar)
            .environment(\.locale, Locale(identifier: time.hour12 ? "en_US" : "en_GB"))
    }
}
