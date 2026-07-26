import styles from "./indexer-scoreboard.module.css";
import type { IndexerRow, OverviewWindow } from "~/clients/backend-client.server";
import { formatBytes, formatNumber, formatPercent } from "../../utils/format";
import { shortenIndexer } from "~/utils/indexer";

export type IndexerScoreboardProps = {
    indexers: IndexerRow[],
    window: OverviewWindow,
}

export function IndexerScoreboard({ indexers, window }: IndexerScoreboardProps) {
    // no origin data yet (feature just enabled, or nothing imported in window) -> hide
    if (indexers.length === 0) return null;

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <h3 className={styles.title}>Indexers</h3>
                <div className={styles.sub}>Per-indexer imports, {window === "all" ? "all time" : `last ${window}`}</div>
            </div>

            <div className={styles.tableWrap}>
                <table className={styles.table}>
                    <thead>
                        <tr>
                            <th>Indexer</th>
                            <th className={styles.numCol}>Completed</th>
                            <th className={styles.numCol}>Failed</th>
                            <th className={styles.numCol}>Success</th>
                            <th className={styles.numCol}>Downloaded</th>
                            <th className={styles.numCol}>Avg</th>
                        </tr>
                    </thead>
                    <tbody>
                        {indexers.map(ix => {
                            const rate = ix.successRate * 100;
                            const low = (ix.completed + ix.failed) >= 5 && ix.successRate < 0.9;
                            return (
                                <tr key={ix.name}>
                                    <td>
                                        <div className={styles.nameCell} title={ix.name}>
                                            <span className={styles.dot} />
                                            <span className={styles.name}>{shortenIndexer(ix.name)}</span>
                                        </div>
                                    </td>
                                    <td className={styles.numCol}>{formatNumber(ix.completed)}</td>
                                    <td className={`${styles.numCol} ${ix.failed > 0 ? styles.warn : ""}`}>
                                        {formatNumber(ix.failed)}
                                    </td>
                                    <td className={styles.numCol}>
                                        <div className={styles.rateBar}>
                                            <div
                                                className={low ? styles.rateFillLow : styles.rateFill}
                                                style={{ width: `${rate.toFixed(0)}%` }}
                                            />
                                            <span className={styles.rateText}>{formatPercent(rate, 0)}</span>
                                        </div>
                                    </td>
                                    <td className={styles.numCol}>{formatBytes(ix.bytesCompleted)}</td>
                                    <td className={styles.numCol}>{formatDuration(ix.avgSeconds)}</td>
                                </tr>
                            );
                        })}
                    </tbody>
                </table>
            </div>
        </div>
    );
}

function formatDuration(seconds: number): string {
    if (seconds <= 0) return "—";
    if (seconds < 60) return `${seconds}s`;
    const m = Math.floor(seconds / 60);
    const s = seconds % 60;
    return s === 0 ? `${m}m` : `${m}m ${s}s`;
}
