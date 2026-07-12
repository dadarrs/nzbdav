import styles from "./health-stats.module.css";
import type { HealthCheckStats, RepairWindowStat } from "~/clients/backend-client.server";

export type RepairBrowseTarget = {
    // RepairAction.Repaired or RepairAction.Deleted
    action: number;
    windowDays: number;
}

export type HealthStatsProps = {
    stats: HealthCheckStats[];
    repairWindows: RepairWindowStat[];
    // which repair-action browse section is open, if any
    activeTarget: RepairBrowseTarget | null;
    onTargetToggle: (target: RepairBrowseTarget) => void;
}

enum HealthResult {
    Healthy = 0,
    Unhealthy = 1,
}

enum RepairAction {
    None = 0,
    Repaired = 1,
    Deleted = 2,
    ActionNeeded = 3,
}

export function HealthStats({ stats, repairWindows, activeTarget, onTargetToggle }: HealthStatsProps) {
    // Calculate totals from HealthCheckStats array
    const totalChecked = stats
        .reduce((sum, stat) => sum + stat.count, 0);
    const healthy = stats
        .filter(stat => stat.result === HealthResult.Healthy)
        .reduce((sum, stat) => sum + stat.count, 0);

    const getPercentage = (count: number) => {
        return totalChecked > 0 ? Math.round((count / totalChecked) * 100) : 0;
    };

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <h3 className={styles.title}>Overview</h3>
                <div className={styles.statusIndicator}>
                    <span className={styles.statusLabel}>Last 30 Days</span>
                </div>
            </div>

            <div className={styles.statsGrid}>
                <div className={styles.statCard}>
                    <div className={styles.statNumber}>{totalChecked}</div>
                    <div className={styles.statLabel}>Total Checked</div>
                </div>

                <div className={styles.statCard}>
                    <div className={styles.statNumber} style={{ color: 'var(--success)' }}>{healthy}</div>
                    <div className={styles.statLabel}>Healthy ({getPercentage(healthy)}%)</div>
                </div>

                <WindowedStatCard
                    label="Repaired"
                    action={RepairAction.Repaired}
                    color="var(--accent)"
                    repairWindows={repairWindows}
                    activeTarget={activeTarget}
                    onTargetToggle={onTargetToggle}
                />

                <WindowedStatCard
                    label="Deleted"
                    action={RepairAction.Deleted}
                    color="var(--danger)"
                    repairWindows={repairWindows}
                    activeTarget={activeTarget}
                    onTargetToggle={onTargetToggle}
                />
            </div>
        </div>
    );
}

type WindowedStatCardProps = {
    label: string;
    action: number;
    color: string;
    repairWindows: RepairWindowStat[];
    activeTarget: RepairBrowseTarget | null;
    onTargetToggle: (target: RepairBrowseTarget) => void;
}

function WindowedStatCard({ label, action, color, repairWindows, activeTarget, onTargetToggle }: WindowedStatCardProps) {
    return (
        <div className={styles.statCard}>
            <div className={styles.windowRow}>
                {repairWindows.map(window => {
                    const count = action === RepairAction.Repaired ? window.repaired : window.deleted;
                    const isActive = activeTarget?.action === action
                        && activeTarget?.windowDays === window.windowDays;
                    return (
                        <button
                            key={window.windowDays}
                            type="button"
                            className={`${styles.windowChip} ${isActive ? styles.activeChip : ""}`}
                            onClick={() => onTargetToggle({ action, windowDays: window.windowDays })}
                            title={`Show files ${label.toLowerCase()} in the last ${formatWindow(window.windowDays)}`}
                        >
                            <span className={styles.chipCount} style={{ color }}>{count}</span>
                            <span className={styles.chipWindow}>{chipLabel(window.windowDays)}</span>
                        </button>
                    );
                })}
            </div>
            <div className={styles.statLabel}>{label}</div>
        </div>
    );
}

function chipLabel(windowDays: number): string {
    return windowDays >= 365 ? "1y" : `${windowDays}d`;
}

function formatWindow(windowDays: number): string {
    return windowDays >= 365 ? "year" : `${windowDays} days`;
}
