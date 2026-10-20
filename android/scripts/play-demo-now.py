#!/usr/bin/env python3
"""Prints the made-up "now" of the store screenshots as an ISO 8601 time: 16:30 today in Zagreb, or the real time if that
is earlier. Used by play-screenshots.sh and apple/scripts/appstore-screenshots.sh, so both stores show the same day;
with --hhmm it prints just the clock time ("1630")."""
import sys
from datetime import datetime
from zoneinfo import ZoneInfo

n = datetime.now(ZoneInfo("Europe/Zagreb"))
now = min(n, n.replace(hour=16, minute=30, second=0, microsecond=0))
print(now.strftime("%H%M") if "--hhmm" in sys.argv else now.isoformat())
