import { useQueries, useQuery } from "@tanstack/react-query";

export type App = {
    platform: "mac" | "windows" | "windows-arm64";
    label: string;
    url: string;
    type: string;
};

const EXE = "application/vnd.microsoft.portable-executable";

const apps: App[] = [
    {
        platform: "mac",
        label: "Download for Mac",
        url: "/downloads/Spindle.dmg",
        type: "application/x-apple-diskimage",
    },
    {
        platform: "windows",
        label: "Download for Windows",
        url: "/downloads/Spindle-Setup.exe",
        type: EXE,
    },
    {
        platform: "windows-arm64",
        label: "Download for Windows on Arm",
        url: "/downloads/Spindle-Setup-arm64.exe",
        type: EXE,
    },
];

/**
 * The apps for the visitor's desktop: a Mac gets the Mac app, a Windows PC the Windows one, and anything else both.
 * Windows on Arm gets its native installer only when the browser says it's Arm (Chromium does); browsers report x64
 * there otherwise, and the x64 installer runs on Arm too.
 */
export function appsFor(agent: string, architecture: string | undefined): App[] {
    const platforms: App["platform"][] = /Windows/i.test(agent)
        ? [architecture === "arm" ? "windows-arm64" : "windows"]
        : /Macintosh/i.test(agent)
          ? ["mac"]
          : ["mac", "windows"];
    return apps.filter((app) => platforms.includes(app.platform));
}

type UserAgentData = {
    getHighEntropyValues(hints: string[]): Promise<{ architecture?: string }>;
};

/** "arm" or "x86" where the browser tells (Chromium, when asked), null elsewhere. */
async function architecture(): Promise<string | null> {
    const data = (navigator as Navigator & { userAgentData?: UserAgentData })
        .userAgentData;
    if (!data) return null;
    try {
        return (await data.getHighEntropyValues(["architecture"])).architecture ?? null;
    } catch {
        return null;
    }
}

/** The desktop apps for this computer that the server has a file for: none until a version is published. */
export function useAppDownloads(): App[] {
    const windows = /Windows/i.test(navigator.userAgent);
    // Only Windows needs the architecture; until the browser answers, nothing is offered rather than the wrong file.
    const arch = useQuery({
        queryKey: ["desktop-architecture"],
        queryFn: architecture,
        enabled: windows,
        staleTime: Infinity,
    });
    const shown =
        windows && arch.isPending
            ? []
            : appsFor(navigator.userAgent, arch.data ?? undefined);
    // A link only once the server has the file: until a version is published, there is nothing to download.
    const available = useQueries({
        queries: shown.map((app) => ({
            queryKey: ["app-download", app.platform],
            queryFn: async () => {
                const response = await fetch(app.url, { method: "HEAD" });
                return (
                    response.ok &&
                    response.headers.get("content-type")?.includes(app.type) ===
                        true
                );
            },
            staleTime: 5 * 60_000,
            retry: false,
        })),
    });
    return shown.filter((_, index) => available[index]?.data === true);
}
