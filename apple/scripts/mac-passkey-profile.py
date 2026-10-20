"""Validate a decoded Developer ID profile and print only its UUID for Xcode signing."""
import datetime
from pathlib import Path
import plistlib
import sys
import uuid


def app_domains():
    """The domains the Mac app is associated with, from its entitlements."""
    entitlements = Path(__file__).resolve().parents[1] / "Mac/Resources/Spindle.entitlements"
    with entitlements.open("rb") as source:
        return plistlib.load(source).get("com.apple.developer.associated-domains", [])


def profile_id(profile):
    entitlements = profile.get("Entitlements", {})
    app = entitlements.get("com.apple.application-identifier", entitlements.get("application-identifier"))
    if app != "9ARDA688G5.com.spindletrack.mac":
        raise ValueError("The profile must belong to Spindle Mac on team 9ARDA688G5.")
    domains = entitlements.get("com.apple.developer.associated-domains", [])
    if "*" not in domains and not (domains and set(app_domains()) <= set(domains)):
        raise ValueError("The profile must include the Associated Domains entitlement for the app's domains.")
    if not profile.get("ProvisionsAllDevices") or entitlements.get("get-task-allow", False) or entitlements.get("com.apple.security.get-task-allow", False):
        raise ValueError("Use a Developer ID distribution profile, not a development profile.")
    expiry = profile.get("ExpirationDate")
    if not isinstance(expiry, datetime.datetime) or expiry.replace(tzinfo=datetime.timezone.utc) <= datetime.datetime.now(datetime.timezone.utc):
        raise ValueError("The provisioning profile is expired or has no expiry date.")
    identifier = profile["UUID"]
    if str(uuid.UUID(identifier)) != identifier.lower():
        raise ValueError("The provisioning profile has an invalid UUID.")
    return identifier


if __name__ == "__main__":
    try:
        with open(sys.argv[1], "rb") as source:
            print(profile_id(plistlib.load(source)))
    except (ValueError, KeyError) as error:
        sys.exit(str(error))
