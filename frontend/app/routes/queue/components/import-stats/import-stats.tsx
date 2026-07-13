import { useEffect, useState } from "react";
import styles from "./import-stats.module.css";
import { formatFileSize } from "~/utils/file-size";

type ProviderStat = {
    provider: string,
    downloadBytes: number,
    verifyBytes: number,
    articles: number,
};

type ImportStatsResponse = {
    status: boolean,
    found: boolean,
    jobName?: string,
    completedAt: number,
    downloadMs: number,
    verifyMs: number | null,
    totalMs: number,
    failed: boolean,
    providers: ProviderStat[],
};

export type ImportStatsRowProps = {
    nzoId: string,
    colSpan: number,
};

/// Expanded detail row under a history item: how long the download (blue) and
/// verify (green) phases took, and which providers served how much for each.
export function ImportStatsRow({ nzoId, colSpan }: ImportStatsRowProps) {
    const [data, setData] = useState<ImportStatsResponse | "loading" | "error">("loading");

    useEffect(() => {
        let disposed = false;
        (async () => {
            try {
                const response = await fetch(`/api/get-import-stats?id=${encodeURIComponent(nzoId)}`);
                if (!response.ok) throw new Error();
                const body: ImportStatsResponse = await response.json();
                if (!disposed) setData(body.status ? body : "error");
            } catch {
                if (!disposed) setData("error");
            }
        })();
        return () => { disposed = true; };
    }, [nzoId]);

    return (
        <tr className={styles.detailsTr}>
            <td colSpan={colSpan} className={styles.detailsTd}>
                {data === "loading" && <div className={styles.muted}>Loading import stats…</div>}
                {data === "error" && <div className={styles.muted}>Could not load import stats.</div>}
                {data !== "loading" && data !== "error" && !data.found && (
                    <div className={styles.muted}>
                        No stats recorded for this import — it completed before import stats existed.
                    </div>
                )}
                {data !== "loading" && data !== "error" && data.found && <StatsContent data={data} />}
            </td>
        </tr>
    );
}

function StatsContent({ data }: { data: ImportStatsResponse }) {
    const showVerify = data.verifyMs !== null || data.providers.some(p => p.verifyBytes > 0);
    return (
        <div className={styles.content}>
            <div className={styles.phases}>
                <span className={styles.phase}>
                    <span className={`${styles.dot} ${styles.dotDownload}`} />
                    Download {formatDuration(data.downloadMs)}
                </span>
                {data.verifyMs !== null && (
                    <span className={styles.phase}>
                        <span className={`${styles.dot} ${styles.dotVerify}`} />
                        Verify {formatDuration(data.verifyMs)}
                    </span>
                )}
                <span className={styles.phase}>
                    Total {formatDuration(data.totalMs)}
                </span>
                {data.failed && <span className={styles.failed}>failed</span>}
            </div>

            {data.providers.length > 0 && (
                <table className={styles.providerTable}>
                    <thead>
                        <tr>
                            <th>Provider</th>
                            <th>Articles</th>
                            <th>Download</th>
                            {showVerify && <th>Verify</th>}
                        </tr>
                    </thead>
                    <tbody>
                        {data.providers.map(p => (
                            <tr key={p.provider}>
                                <td>{p.provider}</td>
                                <td>{p.articles > 0 ? p.articles : "—"}</td>
                                <td>{p.downloadBytes > 0 ? formatFileSize(p.downloadBytes) : "—"}</td>
                                {showVerify && <td>{p.verifyBytes > 0 ? formatFileSize(p.verifyBytes) : "—"}</td>}
                            </tr>
                        ))}
                    </tbody>
                </table>
            )}
        </div>
    );
}

function formatDuration(ms: number): string {
    if (ms < 1000) return `${ms} ms`;
    const totalSeconds = Math.round(ms / 1000);
    if (totalSeconds < 60) return `${totalSeconds}s`;
    const minutes = Math.floor(totalSeconds / 60);
    const seconds = totalSeconds % 60;
    if (minutes < 60) return `${minutes}m ${seconds}s`;
    return `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
}
