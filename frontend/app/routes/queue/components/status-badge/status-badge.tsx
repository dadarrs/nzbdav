import { OverlayTrigger, Tooltip } from "react-bootstrap";
import styles from "./status-badge.module.css";
import { classNames } from "~/utils/styling";

export type StatusBadgeProps = {
    className?: string,
    status: string,
    percentage?: string,
    error?: string,
    // set false on rows whose click already opens a detail view (e.g. history
    // rows expanding import stats) so the badge's click-tooltip doesn't compete
    errorTooltip?: boolean,
}


export function StatusBadge({ className, status, percentage, error, errorTooltip = true }: StatusBadgeProps) {
    const statusLower = status?.toLowerCase();

    if (statusLower === "completed") {
        return (
            <div className={styles.container}>
                <div className={styles.badge} style={{ backgroundColor: "rgba(var(--bs-success-rgb)" }}>
                    <div className={styles.badgeText}>{statusLower}</div>
                </div>
            </div>
        );
    }

    if (statusLower === "failed" || statusLower == "upload failed") {
        const badgeTextClass = statusLower == "upload failed"
            ? classNames([styles.badgeText, styles.uploadIcon])
            : styles.badgeText;

        if (error?.startsWith("Article with message-id"))
            error = "Missing articles";

        const badge = (
            <div className={classNames([styles.container, errorTooltip && styles.failureBadge])}>
                <div className={styles.badge} style={{ backgroundColor: "rgba(var(--bs-danger-rgb)" }}>
                    <div className={badgeTextClass}>{'failed'}</div>
                </div>
            </div>
        );

        if (!errorTooltip) return badge;
        return (
            <OverlayTrigger placement="top" overlay={<Tooltip>{error}</Tooltip>} trigger="click">
                {badge}
            </OverlayTrigger >
        );
    }

    if (statusLower === "downloading") {
        const percentNum = Number(percentage);
        const badgeText = `${percentNum > 100 ? percentNum - 100 : percentNum}%`;
        const isHealthChecking = percentNum > 100;

        // download progress-bar
        const downloadProgressClass = isHealthChecking
            ? `${styles.progress} ${styles.gray}`
            : styles.progress;
        const downloadProgressStyle = (percentNum >= 0)
            ? { width: `${Math.min(percentNum, 100)}%` }
            : undefined;

        // health-check progress-bar
        const healthCheckProgressClass = `${styles.progress} ${styles.healthcheckProgress}`;
        const healthCheckProgressStyle = isHealthChecking
            ? { width: `${Math.min(percentNum - 100, 100)}%` }
            : undefined;

        return (
            <div className={styles.container}>
                <div className={styles.badge} style={{ backgroundColor: "#333" }}>
                    <div className={downloadProgressClass} style={downloadProgressStyle} />
                    <div className={healthCheckProgressClass} style={healthCheckProgressStyle} />
                    <div className={styles.badgeText}>{badgeText}</div>
                </div>
            </div>
        );
    }

    if (statusLower === "uploading") {
        const percentNum = Number(percentage);
        const badgeText = `${percentNum}%`;
        const uploadProgressClass = `${styles.progress} ${styles.uploadProgress}`;
        const uploadProgressStyle = { width: `${Math.min(percentNum, 100)}%` };

        return (
            <div className={styles.container}>
                <div className={styles.badge} style={{ backgroundColor: "#333" }}>
                    <div className={uploadProgressClass} style={uploadProgressStyle} />
                    <div className={classNames([styles.badgeText, styles.uploadIcon])}>{badgeText}</div>
                </div>
            </div>
        );
    }

    if (statusLower === "pending") {
        return (
            <div className={styles.container}>
                <div className={styles.badge} style={{ backgroundColor: "#333" }}>
                    <div className={classNames([styles.badgeText, styles.uploadIcon])}>pending</div>
                </div>
            </div>
        );
    }

    if (statusLower === "health-checking") {
        const percentNum = Number(percentage);
        const badgeText = `${percentNum}%`;
        const healthCheckProgressClass = `${styles.progress} ${styles.healthcheckProgress}`;
        const healthCheckProgressStyle = { width: `${Math.min(percentNum, 100)}%` };

        return (
            <div className={classNames([styles.badge, className])} style={{ backgroundColor: "#333" }}>
                <div className={healthCheckProgressClass} style={healthCheckProgressStyle} />
                <div className={styles.badgeText}>{badgeText}</div>
            </div>
        );
    }

    return (
        <div className={styles.container}>
            <div className={styles.badge} style={{ backgroundColor: "grey" }}>
                <div className={styles.badgeText}>{statusLower}</div>
            </div>
        </div>
    );
}