import { Download } from "lucide-react";
import { useAppDownloads } from "@/lib/app-downloads";

export function AppDownload() {
    const links = useAppDownloads();

    if (links.length === 0) return null;

    return (
        <div className="flex items-center gap-4">
            {links.map((app) => (
                <a
                    key={app.platform}
                    href={app.url}
                    className="text-muted-foreground hover:text-foreground inline-flex items-center gap-2 text-sm"
                >
                    <Download className="size-4" aria-hidden="true" />
                    {app.label}
                </a>
            ))}
        </div>
    );
}
