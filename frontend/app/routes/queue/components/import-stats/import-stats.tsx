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
    // the history item's failure message; shown here in full since the status
    // badge no longer opens its own tooltip on expandable rows
    failMessage?: string | null,
};

/// Expanded detail row under a history item: how long the download (blue) and
/// verify (green) phases took, and which providers served how much for each.
export function ImportStatsRow({ nzoId, colSpan, failMessage }: ImportStatsRowProps) {
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
                <div className={styles.panel}>
                    {failMessage && <div className={styles.failMessage}>{failMessage}</div>}
                    {data === "loading" && <div className={styles.muted}>Loading import stats…</div>}
                    {data === "error" && <div className={styles.muted}>Could not load import stats.</div>}
                    {data !== "loading" && data !== "error" && !data.found && (
                        <div className={styles.muted}>
                            No stats recorded for this import — it completed before import stats existed.
                        </div>
                    )}
                    {data !== "loading" && data !== "error" && data.found && <StatsContent data={data} />}
                </div>
            </td>
        </tr>
    );
}

function StatsContent({ data }: { data: ImportStatsResponse }) {
    const showVerify = data.verifyMs !== null || data.providers.some(p => p.verifyBytes > 0);
    return (
        <>
            <div className={styles.title}>
                Import stats
                <span className={styles.titleNote}>
                    · covers the import process only
                </span>
            </div>
            <div className={styles.phases}>
                <span className={`${styles.pill} ${styles.pillDownload}`}>
                    <span className={styles.dot} />
                    Download {formatDuration(data.downloadMs)}
                </span>
                {data.verifyMs !== null && (
                    <span className={`${styles.pill} ${styles.pillVerify}`}>
                        <span className={styles.dot} />
                        Verify {formatDuration(data.verifyMs)}
                    </span>
                )}
                <span className={`${styles.pill} ${styles.pillTotal}`}>
                    Total {formatDuration(data.totalMs)}
                </span>
                {data.failed && <span className={`${styles.pill} ${styles.pillFailed}`}>failed</span>}
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
        </>
    );
}

function formatDuration(ms: number): string {
    if (ms < 1000) return `${ms} ms`;
    const seconds = ms / 1000;
    // one decimal below a minute so short phases (e.g. 2.3s) stay meaningful
    if (seconds < 60) return `${seconds.toFixed(1)}s`;
    const totalSeconds = Math.round(seconds);
    const minutes = Math.floor(totalSeconds / 60);
    if (minutes < 60) return `${minutes}m ${totalSeconds % 60}s`;
    return `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
}
