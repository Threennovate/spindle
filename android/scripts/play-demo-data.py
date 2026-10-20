#!/usr/bin/env python3
"""Creates a made-up account for the Play store screenshots on a local API and prints its email and password.

A small studio: eight projects for four clients, full working days (Monday to Friday, 6 to 8 hours, a split entry in
each) for the last two weeks, a full day today whatever the weekday (built backwards from the running timer, so the
calendar is filled up to it), and a timer running on Acme API. Takes the local API and optionally the time to treat as now. Used by play-screenshots.sh and
apple/scripts/appstore-screenshots.sh; never point it at a real server.
"""
import json
import secrets
import sys
import urllib.request
from datetime import datetime, timedelta, timezone
from zoneinfo import ZoneInfo

api = sys.argv[1] if len(sys.argv) > 1 else "http://localhost:5262"
if not api.startswith(("http://localhost", "http://127.0.0.1")):
    sys.exit("play-demo-data.py only fills a local API.")
zone = ZoneInfo("Europe/Zagreb")
token = None


def call(method, path, body=None):
    request = urllib.request.Request(api + path, method=method, data=json.dumps(body).encode() if body is not None else None,
                                     headers={"Content-Type": "application/json", **({"Authorization": f"Bearer {token}"} if token else {})})
    with urllib.request.urlopen(request) as response:
        text = response.read()
        return json.loads(text) if text else None


email = f"emma.{secrets.token_hex(4)}@example.com"
password = secrets.token_urlsafe(18)
token = call("POST", "/api/auth/register", {"email": email, "password": password, "fullName": "Emma K.", "timeZone": "Europe/Zagreb"})["accessToken"]
ws = call("GET", "/api/me")["defaultWorkspaceId"]
call("PUT", f"/api/workspaces/{ws}/settings", {
    "name": "Studio", "currency": "EUR", "defaultHourlyRate": None, "onlyAdminsCreateProjects": False, "onlyAdminsCreateTags": False,
    "lockedBefore": None, "requireDescription": False, "requireProject": False, "requireTask": False, "requireTag": False})

clients = {name: call("POST", f"/api/workspaces/{ws}/clients", {"name": name})["id"] for name in ["Acme", "Northwind", "Birch & Co", "Lumen Health"]}


def project(name, color, client=None, **body):
    if client:
        body.update(clientId=clients[client], isBillable=True, hourlyRate=body.pop("rate", 90))
    return call("POST", f"/api/workspaces/{ws}/projects", {"name": name, "color": color, **body})["id"]


web = project("Acme Web", "#D0477F", "Acme")
api_project = project("Acme API", "#2D6BE0", "Acme")
mobile = project("Acme Mobile", "#7B4FD6", "Acme")
portal = project("Northwind Portal", "#E36A00", "Northwind", rate=95)
reports = project("Northwind Reports", "#C99A00", "Northwind", rate=95)
brand = project("Birch Brand Site", "#2F9E44", "Birch & Co", rate=80)
patient = project("Lumen Patient App", "#0E95B5", "Lumen Health", rate=100)
internal = project("Internal", "#13987F")
review = call("POST", f"/api/workspaces/{ws}/tags", {"name": "review"})["id"]


def entry(start, stop, description, project_id=None, shares=None, tags=None):
    body = {"description": description, "start": start.astimezone(timezone.utc).isoformat(), "stop": stop.astimezone(timezone.utc).isoformat(), "tagIds": tags or []}
    if shares:
        body["shares"] = [{"projectId": p, "percent": percent} for p, percent in shares]
    else:
        body["projectId"] = project_id
    call("POST", f"/api/workspaces/{ws}/time-entries", body)


# One working day as blocks of (minutes, description, project, shares, tags); a block with shares is a split entry.
days = [
    [(105, "Checkout form", web, None, None), (90, "Webhook retries", api_project, None, None), (105, "Pricing page and API docs", None, [(web, 60), (api_project, 40)], None),
     (90, "Order export", portal, None, None), (75, "Weekly planning", internal, None, None)],
    [(120, "Booking flow screens", patient, None, None), (75, "Design review", web, None, [review]), (60, "Newsletter template", brand, None, None),
     (135, "Rate limiter and pagination", None, [(api_project, 70), (portal, 30)], None), (45, "Stand-up and email", internal, None, None)],
    [(150, "Navigation and footer", brand, None, None), (90, "Invoice sync", portal, None, None),
     (150, "Dependency updates across clients", None, [(api_project, 50), (web, 30), (portal, 20)], None), (45, "Release notes", mobile, None, None)],
    [(45, "Stand-up and email", internal, None, None), (150, "Cursor pagination", api_project, None, None), (90, "Push notifications", mobile, None, None),
     (120, "Monthly report layouts", reports, None, None), (60, "Lab results screen", patient, None, [review])],
    [(120, "Mobile layout fixes", web, None, None), (105, "Export to CSV", reports, None, None), (120, "Offline mode and sync tests", None, [(mobile, 60), (patient, 40)], None),
     (75, "Homepage hero and case studies", brand, None, None)],
]
# "Now" is the real time, or the ISO time of an optional second argument (play-screenshots.sh sets its emulator's clock to it).
zone_now = datetime.fromisoformat(sys.argv[2]).astimezone(zone) if len(sys.argv) > 2 else datetime.now(zone)
today = zone_now.date()
for n in range(14, 0, -1):
    day = today - timedelta(days=n)
    if day.weekday() >= 5:
        continue
    cursor = datetime.combine(day, datetime.min.time(), zone).replace(hour=8, minute=45)
    lunch = False
    for minutes, description, project_id, shares, tags in days[day.toordinal() % len(days)]:
        if not lunch and cursor.hour >= 12:
            cursor += timedelta(minutes=45)
            lunch = True
        entry(cursor, cursor + timedelta(minutes=minutes), description, project_id, shares, tags)
        cursor += timedelta(minutes=minutes)

# Today, built backwards from the running timer (47 minutes in) so the calendar is full up to it, with a split entry
# straight before it, and a first block that starts the morning; blocks that would start before 7:00 are left out.
timer_start = zone_now - timedelta(minutes=47)
cursor = timer_start
for minutes, gap, description, project_id, shares, tags in [
    (80, 0, "Retry-After handling and checkout tests", None, [(api_project, 55), (web, 45)], None),
    (45, 0, "Design review", web, None, [review]), (90, 40, "Order export", portal, None, None),
    (75, 0, "Cursor pagination", api_project, None, None), (30, 0, "Stand-up and email", internal, None, None),
    (90, 0, "Homepage hero and case studies", brand, None, None),
]:
    stop = cursor - timedelta(minutes=gap)
    start = stop - timedelta(minutes=minutes)
    if start.date() != today or start.hour < 7:
        break
    entry(start, stop, description, project_id, shares, tags)
    cursor = start

call("POST", f"/api/workspaces/{ws}/time-entries", {"description": "Webhook signatures", "projectId": api_project, "start": timer_start.astimezone(timezone.utc).isoformat()})
print(json.dumps({"email": email, "password": password}))
