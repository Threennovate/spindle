import { describe, expect, it } from "vitest";
import { appsFor } from "./app-downloads";

const WINDOWS = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36";
const MAC = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15";
const LINUX = "Mozilla/5.0 (X11; Linux x86_64; rv:140.0) Gecko/20100101 Firefox/140.0";

const platforms = (agent: string, architecture?: string) => appsFor(agent, architecture).map((app) => app.platform);

describe("appsFor", () => {
  it("offers Windows on Arm its own installer only when the browser says it is Arm", () => {
    expect(platforms(WINDOWS, "arm")).toEqual(["windows-arm64"]);
    expect(platforms(WINDOWS, "x86")).toEqual(["windows"]);
    // Firefox and others say nothing, and the x64 installer runs on Arm too.
    expect(platforms(WINDOWS)).toEqual(["windows"]);
  });

  it("offers a Mac the Mac app, and anything else both desktops", () => {
    expect(platforms(MAC, "arm")).toEqual(["mac"]);
    expect(platforms(LINUX)).toEqual(["mac", "windows"]);
  });
});
