import datetime
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("profile", Path(__file__).with_name("mac-passkey-profile.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class PasskeyProfileTests(unittest.TestCase):
    def profile(self):
        return {"UUID": "299f88a8-af0d-4ed1-8c60-2bac4261437a", "ProvisionsAllDevices": True,
                "ExpirationDate": datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(days=30),
                "Entitlements": {"com.apple.application-identifier": "9ARDA688G5.com.spindletrack.mac",
                                 "com.apple.developer.associated-domains": ["*"]}}

    def test_accepts_distribution_profile_with_associated_domains(self):
        self.assertEqual(module.profile_id(self.profile()), self.profile()["UUID"])

    def test_accepts_a_profile_that_names_each_of_the_apps_domains(self):
        profile = self.profile()
        profile["Entitlements"]["com.apple.developer.associated-domains"] = list(module.app_domains())
        self.assertEqual(module.profile_id(profile), profile["UUID"])
        profile["Entitlements"]["com.apple.developer.associated-domains"] = ["webcredentials:other.example.com"]
        with self.assertRaises(ValueError): module.profile_id(profile)

    def test_rejects_wrong_app_expired_profile_missing_capability_and_development_profile(self):
        for condition in ["app", "expiry", "domain", "development", "uuid"]:
            with self.subTest(condition=condition):
                profile = self.profile()
                if condition == "app": profile["Entitlements"]["com.apple.application-identifier"] = "other.app"
                if condition == "expiry": profile["ExpirationDate"] -= datetime.timedelta(days=60)
                if condition == "domain": profile["Entitlements"].pop("com.apple.developer.associated-domains")
                if condition == "development": profile["ProvisionsAllDevices"] = False
                if condition == "uuid": profile["UUID"] = "invalid"
                with self.assertRaises(ValueError): module.profile_id(profile)
