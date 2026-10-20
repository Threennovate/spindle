import ActivityKit
import AppIntents
import SwiftUI
import WidgetKit

@main
struct SpindleWidgets: WidgetBundle {
    var body: some Widget {
        TimerLiveActivity()
    }
}

/// The running timer on the Lock Screen and in the Dynamic Island, with a button to stop it.
struct TimerLiveActivity: Widget {
    var body: some WidgetConfiguration {
        ActivityConfiguration(for: TimerActivityAttributes.self) { context in
            LockScreenTimer(state: context.state)
                .activityBackgroundTint(Color.black.opacity(0.35))
                .activitySystemActionForegroundColor(.white)
        } dynamicIsland: { context in
            DynamicIsland {
                DynamicIslandExpandedRegion(.leading) {
                    Label {
                        Text("Spindle").font(.caption.weight(.semibold))
                    } icon: {
                        Image("LogoMark").resizable().frame(width: 18, height: 18)
                    }
                    .padding(.leading, 4)
                }
                DynamicIslandExpandedRegion(.trailing) {
                    Elapsed(start: context.state.start)
                        .font(.title3.weight(.semibold))
                        .foregroundStyle(Color.brandText)
                        .padding(.trailing, 4)
                }
                DynamicIslandExpandedRegion(.bottom) {
                    HStack(alignment: .center, spacing: 12) {
                        EntrySummary(state: context.state)
                        Spacer(minLength: 8)
                        StopButton()
                    }
                    .padding(.horizontal, 4)
                }
            } compactLeading: {
                if let color = context.state.projectColor {
                    Circle().fill(Color(hex: color)).frame(width: 10, height: 10)
                } else {
                    Image("LogoMark").resizable().frame(width: 18, height: 18)
                }
            } compactTrailing: {
                Elapsed(start: context.state.start)
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(Color.brandText)
                    .frame(maxWidth: 58)
            } minimal: {
                Image("LogoMark").resizable().frame(width: 18, height: 18)
            }
            .keylineTint(Color.brand)
        }
    }
}

/// On the Lock Screen: what's running and for how long, and the stop button.
private struct LockScreenTimer: View {
    let state: TimerActivityAttributes.ContentState

    var body: some View {
        HStack(alignment: .center, spacing: 14) {
            Image("LogoMark")
                .resizable()
                .frame(width: 36, height: 36)
            VStack(alignment: .leading, spacing: 3) {
                EntrySummary(state: state)
                Elapsed(start: state.start, alignment: .leading)
                    .font(.title2.weight(.semibold))
                    .foregroundStyle(Color.brandText)
            }
            Spacer(minLength: 8)
            StopButton()
        }
        .padding(16)
        .foregroundStyle(.white)
    }
}

/// The description and the project, in the project's color.
private struct EntrySummary: View {
    let state: TimerActivityAttributes.ContentState

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(state.description.isEmpty ? "No description" : state.description)
                .font(.subheadline.weight(.semibold))
                .lineLimit(1)
            if let name = state.projectName {
                HStack(spacing: 5) {
                    Circle().fill(Color(hex: state.projectColor ?? "#888888")).frame(width: 7, height: 7)
                    Text(name).lineLimit(1)
                }
                .font(.caption)
                .foregroundStyle(.secondary)
            }
        }
    }
}

/// The time since the timer started, counting on its own.
private struct Elapsed: View {
    let start: Date
    var alignment: TextAlignment = .trailing

    var body: some View {
        Text(timerInterval: start...Date.distantFuture, countsDown: false)
            .monospacedDigit()
            .multilineTextAlignment(alignment)
    }
}

private struct StopButton: View {
    var body: some View {
        Button(intent: StopTimerIntent()) {
            Image(systemName: "stop.fill")
                .font(.system(size: 16, weight: .bold))
                .foregroundStyle(.white)
                .frame(width: 44, height: 44)
                .background(Color.red, in: Circle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel("Stop timer")
    }
}
