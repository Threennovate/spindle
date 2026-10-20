#!/usr/bin/env python3
"""Sums up a trace profile-mac.sh recorded: Spindle's signposts (apple/SpindleKit/.../Performance.swift), the hangs,
what was being measured while each hang lasted, and where the main thread spent its time.

    apple/scripts/profile-summary.py path/to/recording.trace
"""
import statistics
import subprocess
import sys
import xml.etree.ElementTree as ElementTree
from collections import Counter, defaultdict

SUBSYSTEM = "com.spindletrack.performance"
APP_BINARIES = {"Spindle"}


def table(trace, schema):
    """The rows of one table, each a list of its columns with references followed."""
    xpath = f'/trace-toc/run[@number="1"]/data/table[@schema="{schema}"]'
    output = subprocess.run(["xcrun", "xctrace", "export", "--input", trace, "--xpath", xpath],
                            check=True, capture_output=True).stdout
    root = ElementTree.fromstring(output)
    ids = {}
    for element in root.iter():
        if "id" in element.attrib:
            ids[element.attrib["id"]] = element

    def follow(element):
        return ids.get(element.attrib["ref"], element) if "ref" in element.attrib else element

    # Several instruments can record the same table; their copies hold the same rows, so one is enough.
    copies = [node.findall("row") for node in root.iter("node")]
    rows = max(copies, key=len, default=[])
    return [[follow(column) for column in row] for row in rows], follow


def number(element):
    return int(element.text) if element is not None and element.text else 0


def text(element):
    return element.attrib.get("fmt", element.text or "") if element is not None else ""


def signposts(trace):
    rows, _ = table(trace, "os-signpost")
    intervals, events, details, open_ = defaultdict(list), Counter(), defaultdict(Counter), {}
    first = last = None
    for row in rows:
        time, thread, _, kind, _, identifier, name, _, _, subsystem, _, message = row[:12]
        if text(subsystem) != SUBSYSTEM:
            continue
        at, name = number(time), text(name)
        first = at if first is None else min(first, at)
        last = at if last is None else max(last, at)
        main = text(thread).startswith("Main Thread")
        key = (text(identifier), name)
        if text(kind) == "Begin":
            open_[key] = (at, main)
        elif text(kind) == "End" and key in open_:
            start, on_main = open_.pop(key)
            intervals[name].append((start, at, on_main))
        elif text(kind) == "Event":
            events[name] += 1
            if text(message):
                details[name][text(message)] += 1
    return intervals, events, details, (first, last)


def hangs(trace):
    rows, _ = table(trace, "potential-hangs")
    return [(number(row[0]), number(row[0]) + number(row[1]), text(row[2])) for row in rows]


def main_thread_samples(trace):
    """(time, weight, the innermost frame of Spindle's own code) for each sample of the main thread."""
    rows, follow = table(trace, "time-profile")
    samples = []
    for row in rows:
        time, thread, weight, stack = row[0], row[1], row[5], row[6]
        if not text(thread).startswith("Main Thread"):
            continue
        frame_name = None
        for frame in stack.iter("frame"):
            frame = follow(frame)
            binary = frame.find("binary")
            if binary is not None and follow(binary).attrib.get("name") in APP_BINARIES:
                frame_name = frame.attrib.get("name")
                break
        samples.append((number(time), number(weight), frame_name))
    return samples


def ms(nanoseconds):
    return nanoseconds / 1_000_000


def recorded_app(trace):
    """The path of the app recorded, so a recording of another build of Spindle shows."""
    output = subprocess.run(["xcrun", "xctrace", "export", "--input", trace, "--toc"], check=True, capture_output=True).stdout
    paths = [process.attrib.get("path", "") for process in ElementTree.fromstring(output).iter("process")]
    return next((path for path in paths if path and "kernel" not in path), "?")


def report(trace):
    intervals, events, details, (first, last) = signposts(trace)
    span = max(1, (last or 0) - (first or 0))
    minutes = span / 60e9

    print(f"App: {recorded_app(trace)}")
    if not intervals and not events:
        print("No signposts from Spindle: this isn't a build with them, or it wasn't used while recording.")
    print(f"Recording: {span / 1e9:.0f} s with Spindle's signposts\n")
    print("Intervals (ms)                      count    total     p50     p95     max  on main")
    for name, spans in sorted(intervals.items(), key=lambda item: -sum(end - start for start, end, _ in item[1])):
        durations = sorted(ms(end - start) for start, end, _ in spans)
        p95 = durations[min(len(durations) - 1, int(len(durations) * 0.95))]
        on_main = sum(1 for *_, main in spans if main) / len(spans)
        print(f"  {name:32} {len(durations):6} {sum(durations):8.0f} {statistics.median(durations):7.1f} "
              f"{p95:7.1f} {durations[-1]:7.1f} {on_main:7.0%}")

    print("\nEvents                              count  per minute")
    for name, count in events.most_common():
        print(f"  {name:32} {count:6} {count / minutes:11.1f}")
        for detail, times in details[name].most_common(8):
            print(f"      {detail:28} {times:6}")

    found = hangs(trace)
    print(f"\nHangs: {len(found)}, {sum(ms(end - start) for start, end, _ in found):.0f} ms in all")
    for start, end, kind in sorted(found, key=lambda hang: hang[0] - hang[1])[:15]:
        during = Counter()
        for name, spans in intervals.items():
            for s, e, main in spans:
                overlap = min(end, e) - max(start, s)
                if main and overlap > 0:
                    during[name] += overlap
        measured = ", ".join(f"{name} {ms(overlap):.0f}" for name, overlap in during.most_common(4)) or "nothing measured"
        print(f"  {ms(start - (first or 0)) / 1000:7.1f} s  {ms(end - start):6.0f} ms  {kind:12} {measured}")

    samples = main_thread_samples(trace)
    busy = sum(weight for _, weight, _ in samples)
    print(f"\nMain thread busy: {ms(busy):.0f} ms; Spindle's own code by innermost function:")
    own = Counter()
    in_hangs = Counter()
    for time, weight, frame in samples:
        if frame:
            own[frame] += weight
            if any(start <= time <= end for start, end, _ in found):
                in_hangs[frame] += weight
    for frame, weight in own.most_common(20):
        print(f"  {ms(weight):7.0f} ms  {frame[:110]}")
    if in_hangs:
        print("\nDuring hangs:")
        for frame, weight in in_hangs.most_common(12):
            print(f"  {ms(weight):7.0f} ms  {frame[:110]}")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    report(sys.argv[1])
