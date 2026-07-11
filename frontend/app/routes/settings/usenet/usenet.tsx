import styles from "./usenet.module.css"
import { type Dispatch, type SetStateAction, useState, useCallback, useEffect, useMemo } from "react";
import { Button } from "react-bootstrap";
import { receiveMessage } from "~/utils/websocket-util";
import type { ProviderUsageItem } from "~/clients/backend-client.server";

const usenetConnectionsTopic = {'cxs': 'state'};
const USAGE_POLL_INTERVAL_MS = 10_000;

type UsenetSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
    initialUsage?: ProviderUsageItem[]
};

enum ProviderType {
    Disabled = 0,
    Pooled = 1,
    BackupAndStats = 2,
    BackupOnly = 3,
}

type ConnectionDetails = {
    Type: ProviderType;
    Host: string;
    Port: number;
    UseSsl: boolean;
    User: string;
    Pass: string;
    MaxConnections: number;
    StatPipeliningEnabled?: boolean;
    // Optional user-set label. Shown in the UI in place of Host when present;
    // Host stays the real NNTP target.
    Nickname?: string;
    // null/0 = no cap. Stored as bytes; the modal takes a GB value and
    // converts on save.
    ByteLimit?: number | null;
    // Counter adjustment in bytes. Zeroed by the "Reset" button on the card.
    BytesUsedOffset?: number;
    // unix-ms cutoff for the usage counter; a reset bumps this to Date.now().
    BytesUsedResetAt?: number;
};

type ConnectionCounts = {
    live: number;
    active: number;
    max: number;
}

type UsenetProviderConfig = {
    Providers: ConnectionDetails[];
};

const PROVIDER_TYPE_LABELS: Record<ProviderType, string> = {
    [ProviderType.Disabled]: "Disabled",
    [ProviderType.Pooled]: "Pooled Providers",
    [ProviderType.BackupAndStats]: "Backup & Health Checks",
    [ProviderType.BackupOnly]: "Backup Only",
};

function parseProviderConfig(jsonString: string): UsenetProviderConfig {
    try {
        if (!jsonString || jsonString.trim() === "") {
            return { Providers: [] };
        }
        return JSON.parse(jsonString);
    } catch {
        return { Providers: [] };
    }
}

function serializeProviderConfig(config: UsenetProviderConfig): string {
    return JSON.stringify(config);
}

// Mirrors UsenetProviderConfig.GetEffectiveNames() on the backend: each
// provider's identity is its Nickname if set, else its Host, deduped
// case-insensitively in list order by suffixing 2, 3, ... onto later
// duplicates. Metrics and usage are keyed by this name server-side.
function computeEffectiveNames(providers: ConnectionDetails[]): string[] {
    const names: string[] = [];
    const used = new Set<string>();
    for (const provider of providers) {
        const name = dedupeEffectiveName(provider.Nickname?.trim() || provider.Host, used);
        used.add(name.toLowerCase());
        names.push(name);
    }
    return names;
}

// Suffixes 2, 3, ... onto baseName until it no longer collides with
// takenLower (a set of already-lowercased names).
function dedupeEffectiveName(baseName: string, takenLower: ReadonlySet<string>): string {
    let name = baseName;
    let suffix = 2;
    while (takenLower.has(name.toLowerCase())) name = `${baseName}${suffix++}`;
    return name;
}

// Data caps use decimal units (1 GB = 10^9 bytes), matching how block
// providers advertise their blocks.
const BYTES_PER_GB = 1_000_000_000;

function bytesToGbString(bytes: number | null | undefined): string {
    if (!bytes || bytes <= 0) return "";
    // Trim trailing zeros so 500 GB doesn't display as "500.000".
    return Number((bytes / BYTES_PER_GB).toFixed(3)).toString();
}

function gbStringToBytes(value: string): number | null {
    const trimmed = value.trim();
    if (trimmed === "") return null;
    const gb = Number(trimmed);
    if (!isFinite(gb) || gb <= 0) return null;
    return Math.round(gb * BYTES_PER_GB);
}

function isValidDataCap(value: string): boolean {
    const trimmed = value.trim();
    if (trimmed === "") return true;
    const gb = Number(trimmed);
    // 0 is allowed and means "no cap", same as leaving the field empty.
    return isFinite(gb) && gb >= 0;
}

function formatBytes(bytes: number): string {
    if (!isFinite(bytes) || bytes <= 0) return "0 B";
    const units = ["B", "KB", "MB", "GB", "TB", "PB"];
    let i = 0;
    let v = bytes;
    while (v >= 1000 && i < units.length - 1) { v /= 1000; i++; }
    return v >= 100 ? `${v.toFixed(0)} ${units[i]}` : `${v.toFixed(1)} ${units[i]}`;
}

function formatDaysRemaining(days: number): string {
    // Friendlier than "0.3 days" or "847 days" — round to the unit that's
    // actually useful at this horizon.
    if (days < 1) {
        const hours = Math.max(1, Math.round(days * 24));
        return `~${hours}h left at this pace`;
    }
    if (days < 60) return `~${Math.round(days)} days left at this pace`;
    const months = days / 30;
    if (months < 24) return `~${Math.round(months)} months left at this pace`;
    return `~${Math.round(months / 12)} years left at this pace`;
}

export function UsenetSettings({ config, setNewConfig, initialUsage }: UsenetSettingsProps) {
    // state
    const [showModal, setShowModal] = useState(false);
    const [editingIndex, setEditingIndex] = useState<number | null>(null);
    const [connections, setConnections] = useState<{[index: number]: ConnectionCounts}>({});
    const [usage, setUsage] = useState<{[index: number]: ProviderUsageItem}>(() => {
        const seeded: {[index: number]: ProviderUsageItem} = {};
        for (const item of initialUsage ?? []) seeded[item.index] = item;
        return seeded;
    });
    const providerConfig = useMemo(() => parseProviderConfig(config["usenet.providers"]), [config]);

    // Effective display name for every provider, by global index.
    const effectiveNames = useMemo(
        () => computeEffectiveNames(providerConfig.Providers),
        [providerConfig]);

    // What the modal validates nicknames against: the effective names of every
    // provider EXCEPT the one being edited (a provider can't collide with itself).
    const otherEffectiveNames = useMemo(
        () => computeEffectiveNames(providerConfig.Providers.filter((_, i) => i !== editingIndex)),
        [providerConfig, editingIndex]);

    // Cards render grouped by provider type, but every handler and data join
    // (edit/delete/reset, usage, websocket connection counts) is keyed by the
    // provider's GLOBAL index in the Providers array — so each entry carries
    // its original index through the grouping instead of using the filtered
    // position. Empty groups (including "Disabled") are not rendered.
    const providerSections = useMemo(() => {
        const indexed = providerConfig.Providers.map((provider, index) => ({ provider, index }));
        const isBackup = (type: ProviderType) =>
            type === ProviderType.BackupAndStats || type === ProviderType.BackupOnly;
        return [
            {
                key: "pooled",
                title: "Pooled Providers",
                // Catch-all for non-backup, non-disabled so a provider with an
                // unexpected Type value still renders somewhere.
                items: indexed.filter(({ provider }) =>
                    provider.Type !== ProviderType.Disabled && !isBackup(provider.Type)),
            },
            {
                key: "backup",
                title: "Backup Providers",
                items: indexed.filter(({ provider }) => isBackup(provider.Type)),
            },
            {
                key: "disabled",
                title: "Disabled",
                items: indexed.filter(({ provider }) => provider.Type === ProviderType.Disabled),
            },
        ].filter(section => section.items.length > 0);
    }, [providerConfig]);

    // handlers
    const handleAddProvider = useCallback(() => {
        setEditingIndex(null);
        setShowModal(true);
    }, []);

    const handleEditProvider = useCallback((index: number) => {
        setEditingIndex(index);
        setShowModal(true);
    }, []);

    const handleDeleteProvider = useCallback((index: number) => {
        const newProviderConfig = { ...providerConfig };
        newProviderConfig.Providers = providerConfig.Providers.filter((_, i) => i !== index);
        setNewConfig({ ...config, "usenet.providers": serializeProviderConfig(newProviderConfig) });
    }, [config, providerConfig, setNewConfig]);

    const handleResetUsage = useCallback((index: number) => {
        const current = providerConfig.Providers[index];
        if (!current) return;
        const label = effectiveNames[index] ?? current.Host;
        if (!confirm(`Reset the bytes-used counter for "${label}" to zero?\n\nUseful after buying a new block. Takes effect when you save settings.`)) return;
        const newProviderConfig = { ...providerConfig };
        newProviderConfig.Providers = providerConfig.Providers.map((p, i) =>
            i === index ? { ...p, BytesUsedOffset: 0, BytesUsedResetAt: Date.now() } : p);
        setNewConfig({ ...config, "usenet.providers": serializeProviderConfig(newProviderConfig) });
    }, [config, providerConfig, effectiveNames, setNewConfig]);

    const handleCloseModal = useCallback(() => {
        setShowModal(false);
        setEditingIndex(null);
    }, []);

    const handleSaveProvider = useCallback((provider: ConnectionDetails) => {
        const newProviderConfig = { ...providerConfig };
        if (editingIndex !== null) {
            newProviderConfig.Providers[editingIndex] = provider;
        } else {
            newProviderConfig.Providers.push(provider);
        }
        setNewConfig({ ...config, "usenet.providers": serializeProviderConfig(newProviderConfig) });
        handleCloseModal();
    }, [config, providerConfig, editingIndex, setNewConfig, handleCloseModal]);

    const handleConnectionsMessage = useCallback((message: string) => {
        const parts = (message || "0|0|0|0|1|0").split("|");
        const [index, live, idle, _0, _1, _2] = parts.map((x: any) => Number(x));
        if (showModal) return;
        if (index >= providerConfig.Providers.length) return;
        setConnections(prev => ({...prev, [index]: {
            active: live - idle,
            live: live,
            max: providerConfig.Providers[index]?.MaxConnections || 1
        }}));
    }, [setConnections]);

    // effects
    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;
        function connect() {
            ws = new WebSocket(window.location.origin.replace(/^http/, 'ws'));
            ws.onmessage = receiveMessage((_, message) => handleConnectionsMessage(message));
            ws.onopen = () => ws.send(JSON.stringify(usenetConnectionsTopic));
            ws.onerror = () => { ws.close() };
            ws.onclose = onClose;
            return () => { disposed = true; ws.close(); }
        }
        function onClose(e: CloseEvent) {
            !disposed && setTimeout(() => connect(), 1000);
            setConnections({});
        }
        return connect();
    }, [setConnections, handleConnectionsMessage]);

    // Poll provider usage so the cards stay fresh while the tab is open. The
    // loader already seeded an initial snapshot; the express proxy attaches
    // the api key for authenticated sessions, same as the connection tests.
    // Skip polling while the edit modal is open so the card behind it doesn't
    // flicker mid-edit.
    useEffect(() => {
        if (showModal) return;
        let disposed = false;
        async function fetchUsage() {
            try {
                const response = await fetch('/api/get-provider-usage');
                if (!response.ok || disposed) return;
                const data: { providers?: ProviderUsageItem[] } = await response.json();
                if (disposed || !data.providers) return;
                const next: {[index: number]: ProviderUsageItem} = {};
                for (const item of data.providers) next[item.index] = item;
                setUsage(next);
            } catch {
                // network blips are fine — the next tick retries.
            }
        }
        fetchUsage();
        const id = setInterval(fetchUsage, USAGE_POLL_INTERVAL_MS);
        return () => { disposed = true; clearInterval(id); };
    }, [showModal, setUsage]);

    // view
    return (
        <div className={styles.container}>
            <div className={styles.section}>
                <div className={styles.sectionHeader}>
                    <div>Usenet Providers</div>
                    <Button variant="primary" size="sm" onClick={handleAddProvider}>
                        Add
                    </Button>
                </div>
                {providerConfig.Providers.length === 0 ? (
                    <p className={styles.alertMessage}>
                        No Usenet providers configured.
                        Click on the "Add" button to get started.
                    </p>
                ) : (
                    providerSections.map((section) => (
                        <div key={section.key} className={styles["provider-section"]}>
                            <div className={styles["provider-section-title"]}>{section.title}</div>
                            <div className={styles["providers-grid"]}>
                                {section.items.map(({ provider, index }) => (
                                    <ProviderCard
                                        key={index}
                                        provider={provider}
                                        effectiveName={effectiveNames[index] ?? provider.Host}
                                        counts={connections[index]}
                                        usage={usage[index]}
                                        onEdit={() => handleEditProvider(index)}
                                        onDelete={() => handleDeleteProvider(index)}
                                        onResetUsage={() => handleResetUsage(index)}
                                    />
                                ))}
                            </div>
                        </div>
                    ))
                )}
            </div>

            <ProviderModal
                show={showModal}
                provider={editingIndex !== null ? providerConfig.Providers[editingIndex] : null}
                pipeliningMasterEnabled={config["usenet.nntp-pipelining.enabled"] === "true"}
                otherEffectiveNames={otherEffectiveNames}
                onClose={handleCloseModal}
                onSave={handleSaveProvider}
            />
        </div>
    );
}

type ProviderCardProps = {
    provider: ConnectionDetails;
    // The provider's unique display identity (nickname/host after dedupe),
    // resolved by the caller from the full provider list.
    effectiveName: string;
    counts: ConnectionCounts | undefined;
    usage: ProviderUsageItem | undefined;
    onEdit: () => void;
    onDelete: () => void;
    onResetUsage: () => void;
};

// One provider card. Everything index-keyed (usage, connection counts,
// handlers) is pre-resolved by the caller against the provider's global index,
// so the card itself never sees a filtered position.
function ProviderCard({ provider, effectiveName, counts, usage, onEdit, onDelete, onResetUsage }: ProviderCardProps) {
    return (
        <div className={styles["provider-card"]}>
            <div className={styles["provider-card-inner"]}>
                <div className={styles["provider-header"]}>
                    <div className={styles["provider-header-content"]}>
                        <div className={styles["provider-host"]}>
                            {effectiveName}
                        </div>
                        {effectiveName !== provider.Host && (
                            <div className={styles["provider-host-secondary"]}>
                                {provider.Host}
                            </div>
                        )}
                        <div className={styles["provider-port"]}>
                            Port {provider.Port}
                        </div>
                    </div>
                    <div className={styles["provider-header-actions"]}>
                        <button
                            className={styles["header-action-button"]}
                            onClick={onEdit}
                            title="Edit Provider"
                        >
                            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" />
                                <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" />
                            </svg>
                        </button>
                        <button
                            className={`${styles["header-action-button"]} ${styles["delete"]}`}
                            onClick={onDelete}
                            title="Delete Provider"
                        >
                            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                <polyline points="3 6 5 6 21 6" />
                                <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" />
                            </svg>
                        </button>
                    </div>
                </div>

                <div className={styles["provider-details"]}>
                    <div className={styles["provider-detail-row"]}>

                        <div className={styles["provider-detail-item"]}>
                            <div className={styles["provider-detail-icon"]}>
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <path d="M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2" />
                                    <circle cx="12" cy="7" r="4" />
                                </svg>
                            </div>
                            <div className={styles["provider-detail-content"]}>
                                <span className={styles["provider-detail-label"]}>Username</span>
                                <span className={styles["provider-detail-value"]}>{provider.User}</span>
                            </div>
                        </div>

                        <div className={styles["provider-detail-item"]}>
                            {counts && (
                                <div className={styles["connection-bar"]}>
                                    <div
                                        className={styles["connection-bar-live"]}
                                        style={{ width: `${100 * (counts.live / counts.max)}%` }}
                                    />
                                    <div
                                        className={styles["connection-bar-active"]}
                                        style={{ width: `${100 * (counts.active / counts.max)}%` }}
                                    />
                                </div>
                            )}
                            <div className={styles["provider-detail-icon"]}>
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z" />
                                </svg>
                            </div>
                            <div className={styles["provider-detail-content"]}>
                                <span className={styles["provider-detail-label"]}>Max Connections</span>
                                <span className={styles["provider-detail-value"]}>{provider.MaxConnections}</span>
                            </div>
                        </div>

                        <div className={styles["provider-detail-item"]}>
                            <div className={styles["provider-detail-icon"]}>
                                {provider.UseSsl ? (
                                    // Closed lock icon
                                    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                        <rect x="5" y="11" width="14" height="11" rx="2" ry="2" />
                                        <path d="M7 11V7a5 5 0 0 1 10 0v4" />
                                        <circle cx="12" cy="16" r="1" fill="currentColor" />
                                    </svg>
                                ) : (
                                    // Open lock icon
                                    <svg width="13" height="13" viewBox="0 -2 24 26" fill="none" stroke="currentColor" strokeWidth="2">
                                        <rect x="5" y="11" width="14" height="11" rx="2" ry="2" />
                                        <path d="M7 11V4a5 5 0 0 1 9.9 1" />
                                        <circle cx="12" cy="16" r="1" fill="currentColor" />
                                    </svg>
                                )}
                            </div>
                            <div className={styles["provider-detail-content"]}>
                                <span className={styles["provider-detail-label"]}>Security</span>
                                <span className={styles["provider-detail-value"]}>
                                    {provider.UseSsl ? "SSL Enabled" : "No SSL"}
                                </span>
                            </div>
                        </div>

                        <div className={styles["provider-detail-item"]}>
                            <div className={styles["provider-detail-icon"]}>
                                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.3">
                                    <text x="12" y="9" fontSize="10" fill="currentColor" textAnchor="middle" fontWeight="600">1</text>
                                    <text x="6" y="21" fontSize="10" fill="currentColor" textAnchor="middle" fontWeight="600">2</text>
                                    <text x="18" y="21" fontSize="10" fill="currentColor" textAnchor="middle" fontWeight="600">3</text>
                                </svg>
                            </div>
                            <div className={styles["provider-detail-content"]}>
                                <span className={styles["provider-detail-label"]}>Behavior</span>
                                <span className={styles["provider-detail-value"]}>{PROVIDER_TYPE_LABELS[provider.Type]}</span>
                            </div>
                        </div>

                    </div>

                    <UsageRow
                        provider={provider}
                        usage={usage}
                        onReset={onResetUsage}
                    />
                </div>
            </div>
        </div>
    );
}

type UsageRowProps = {
    provider: ConnectionDetails;
    usage: ProviderUsageItem | undefined;
    onReset: () => void;
};

function UsageRow({ provider, usage, onReset }: UsageRowProps) {
    // Only providers with a configured cap get a usage bar; null/0 = no cap.
    const limit = provider.ByteLimit ?? 0;
    if (limit <= 0) return null;

    const used = usage?.bytesUsed ?? 0;
    const pct = Math.min(100, (used / limit) * 100);
    // The backend takes an over-limit provider out of rotation at ~95% of the
    // cap, so "danger" starts where the pause actually kicks in.
    const tone = pct >= 95 ? "danger" : pct >= 80 ? "warn" : "ok";

    return (
        <div className={styles["usage-row"]}>
            <div className={styles["usage-header"]}>
                <span className={styles["usage-label"]}>Data Cap</span>
                <span className={styles[`usage-value-${tone}`]}>
                    {formatBytes(used)} / {formatBytes(limit)}&nbsp;&nbsp;·&nbsp;&nbsp;{pct.toFixed(1)}%
                </span>
                <button
                    type="button"
                    className={styles["usage-reset"]}
                    onClick={onReset}
                    title="Reset the counter to zero (e.g. after buying a new block). Applies when you save settings."
                >
                    Reset
                </button>
            </div>
            <div className={styles["usage-bar-track"]}>
                <div
                    className={`${styles["usage-bar-fill"]} ${styles[`usage-bar-${tone}`]}`}
                    style={{ width: `${pct}%` }}
                />
            </div>
            {usage?.overLimit ? (
                <div className={styles["usage-warning"]}>
                    Data cap reached. This provider is paused to keep in-flight fetches from
                    overshooting. Reset the counter or raise the cap to resume.
                </div>
            ) : usage && usage.daysRemaining != null && (
                <div className={styles["usage-hint"]}>
                    {formatDaysRemaining(usage.daysRemaining)}
                </div>
            )}
        </div>
    );
}

type ProviderModalProps = {
    show: boolean;
    provider: ConnectionDetails | null;
    pipeliningMasterEnabled: boolean;
    // Effective names of every OTHER provider (the one being edited excluded).
    // A typed nickname must not collide with these, and an empty nickname
    // auto-fills at save time with the host deduped against them.
    otherEffectiveNames: string[];
    onClose: () => void;
    onSave: (provider: ConnectionDetails) => void;
};

function ProviderModal({ show, provider, pipeliningMasterEnabled, otherEffectiveNames, onClose, onSave }: ProviderModalProps) {
    const [nickname, setNickname] = useState(provider?.Nickname || "");
    const [dataCapGb, setDataCapGb] = useState(bytesToGbString(provider?.ByteLimit));
    const [host, setHost] = useState(provider?.Host || "");
    const [port, setPort] = useState(provider?.Port?.toString() || "");
    const [useSsl, setUseSsl] = useState(provider?.UseSsl ?? true);
    const [user, setUser] = useState(provider?.User || "");
    const [pass, setPass] = useState(provider?.Pass || "");
    const [showPass, setShowPass] = useState(false);
    const [maxConnections, setMaxConnections] = useState(provider?.MaxConnections?.toString() || "");
    const [type, setType] = useState<ProviderType>(provider?.Type ?? ProviderType.Pooled);
    const [statPipeliningEnabled, setStatPipeliningEnabled] = useState(provider?.StatPipeliningEnabled ?? false);
    const [isTestingConnection, setIsTestingConnection] = useState(false);
    const [connectionTested, setConnectionTested] = useState(false);
    const [testError, setTestError] = useState<string | null>(null);
    const [isTestingPipelining, setIsTestingPipelining] = useState(false);
    const [pipeliningTestResult, setPipeliningTestResult] = useState<"supported" | "unsupported" | null>(null);

    // Editing any connection-identity field invalidates both the connection test and the pipelining
    // test/opt-in, since both were validated against the previous host/credentials.
    const invalidateConnectionTests = () => {
        setConnectionTested(false);
        setStatPipeliningEnabled(false);
        setPipeliningTestResult(null);
    };

    // Nickname identity bookkeeping. Note that editing the nickname does NOT
    // invalidate the connection/pipelining tests — it is a display label with
    // no effect on connection identity (host/port/ssl/credentials).
    const takenNamesLower = useMemo(
        () => new Set(otherEffectiveNames.map(name => name.toLowerCase())),
        [otherEffectiveNames]);
    // What an empty nickname is saved as: the host, deduped against the other
    // providers' effective names. Shown live as the field's placeholder.
    const defaultNickname = useMemo(
        () => host.trim() === "" ? "" : dedupeEffectiveName(host.trim(), takenNamesLower),
        [host, takenNamesLower]);
    const nicknameTaken = nickname.trim() !== ""
        && takenNamesLower.has(nickname.trim().toLowerCase());

    // Reset form when modal opens or provider changes
    useEffect(() => {
        if (show) {
            setNickname(provider?.Nickname || "");
            setDataCapGb(bytesToGbString(provider?.ByteLimit));
            setHost(provider?.Host || "");
            setPort(provider?.Port?.toString() || "");
            setUseSsl(provider?.UseSsl ?? true);
            setUser(provider?.User || "");
            setPass(provider?.Pass || "");
            setShowPass(false);
            setMaxConnections(provider?.MaxConnections?.toString() || "");
            setType(provider?.Type ?? ProviderType.Pooled);
            setStatPipeliningEnabled(provider?.StatPipeliningEnabled ?? false);
            setConnectionTested(false);
            setTestError(null);
            setIsTestingPipelining(false);
            setPipeliningTestResult(null);
        }
    }, [show, provider]);

    // Handle Escape key to close modal
    useEffect(() => {
        const handleEscape = (e: KeyboardEvent) => {
            if (e.key === 'Escape' && show) {
                onClose();
            }
        };

        if (show) {
            document.addEventListener('keydown', handleEscape);
            return () => document.removeEventListener('keydown', handleEscape);
        }
    }, [show, onClose]);

    const handleTestConnection = useCallback(async () => {
        setIsTestingConnection(true);
        setTestError(null);

        try {
            const formData = new FormData();
            formData.append('host', host);
            formData.append('port', port);
            formData.append('use-ssl', useSsl.toString());
            formData.append('user', user);
            formData.append('pass', pass);

            const response = await fetch('/api/test-usenet-connection', {
                method: 'POST',
                body: formData,
            });

            if (response.ok) {
                const data = await response.json();
                if (data.connected) {
                    setConnectionTested(true);
                    setTestError(null);
                } else {
                    setTestError("Connection test failed");
                }
            } else {
                setTestError("Failed to test connection");
            }
        } catch (error) {
            setTestError("Network error: " + (error instanceof Error ? error.message : "Unknown error"));
        } finally {
            setIsTestingConnection(false);
        }
    }, [host, port, useSsl, user, pass]);

    const handleTestPipelining = useCallback(async () => {
        setIsTestingPipelining(true);
        setPipeliningTestResult(null);

        try {
            const formData = new FormData();
            formData.append('host', host);
            formData.append('port', port);
            formData.append('use-ssl', useSsl.toString());
            formData.append('user', user);
            formData.append('pass', pass);

            const response = await fetch('/api/test-usenet-pipelining', {
                method: 'POST',
                body: formData,
            });

            if (response.ok) {
                const data = await response.json();
                if (data.supported) {
                    // Auto-enable on a successful probe; the user can still untick it manually.
                    setStatPipeliningEnabled(true);
                    setPipeliningTestResult("supported");
                } else {
                    setStatPipeliningEnabled(false);
                    setPipeliningTestResult("unsupported");
                }
            } else {
                setStatPipeliningEnabled(false);
                setPipeliningTestResult("unsupported");
            }
        } catch {
            setStatPipeliningEnabled(false);
            setPipeliningTestResult("unsupported");
        } finally {
            setIsTestingPipelining(false);
        }
    }, [host, port, useSsl, user, pass]);

    const handleSave = useCallback(() => {
        // Nickname is effectively required: an empty field auto-fills with the
        // deduped host so every saved provider carries an explicit unique name.
        const trimmedNickname = nickname.trim() || defaultNickname;
        onSave({
            Type: type,
            Host: host,
            Port: parseInt(port, 10),
            UseSsl: useSsl,
            User: user,
            Pass: pass,
            MaxConnections: parseInt(maxConnections, 10),
            StatPipeliningEnabled: statPipeliningEnabled,
            Nickname: trimmedNickname === "" ? undefined : trimmedNickname,
            ByteLimit: gbStringToBytes(dataCapGb),
            // Preserve the usage-counter bookkeeping across edits so saving a
            // provider doesn't rewind its gauge; new providers start at zero.
            // The card's "Reset" button is the surface for changing these.
            BytesUsedOffset: provider?.BytesUsedOffset ?? 0,
            BytesUsedResetAt: provider?.BytesUsedResetAt ?? 0,
        });
    }, [type, host, port, useSsl, user, pass, maxConnections, statPipeliningEnabled, nickname, defaultNickname, dataCapGb, provider, onSave]);

    const handleOverlayClick = useCallback((e: React.MouseEvent) => {
        if (e.target === e.currentTarget) {
            onClose();
        }
    }, [onClose]);

    const isFormValid = host.trim() !== ""
        && isPositiveInteger(port)
        && user.trim() !== ""
        && pass.trim() !== ""
        && isPositiveInteger(maxConnections)
        && isValidDataCap(dataCapGb)
        && !nicknameTaken;

    const canSave = isFormValid && (connectionTested || type == ProviderType.Disabled);

    if (!show) return null;

    return (
        <div className={styles["modal-overlay"]} onClick={handleOverlayClick}>
            <div className={styles["modal-container"]}>
                <div className={styles["modal-header"]}>
                    <h2 className={styles["modal-title"]}>
                        {provider ? "Edit Provider" : "Add Provider"}
                    </h2>
                    <button className={styles["modal-close"]} onClick={onClose} aria-label="Close">
                        ×
                    </button>
                </div>

                <div className={styles["modal-body"]}>
                    <div className={styles["form-grid"]}>
                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-nickname" className={styles["form-label"]}>
                                Nickname
                            </label>
                            <input
                                type="text"
                                id="provider-nickname"
                                className={`${styles["form-input"]} ${nicknameTaken ? styles.error : ""}`}
                                placeholder={defaultNickname || "e.g. Main provider"}
                                value={nickname}
                                onChange={(e) => setNickname(e.target.value)}
                            />
                            {nicknameTaken ? (
                                <div className={styles["form-hint-error"]}>
                                    Name already in use.
                                </div>
                            ) : (
                                <div className={styles["form-hint"]}>
                                    Unique label shown in place of the hostname. Leave blank to
                                    use {defaultNickname ? `"${defaultNickname}"` : "the hostname"}.
                                </div>
                            )}
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-data-cap" className={styles["form-label"]}>
                                Data cap (GB, optional)
                            </label>
                            <input
                                type="text"
                                inputMode="decimal"
                                id="provider-data-cap"
                                className={`${styles["form-input"]} ${!isValidDataCap(dataCapGb) ? styles.error : ""}`}
                                placeholder="Leave blank for no cap"
                                value={dataCapGb}
                                onChange={(e) => setDataCapGb(e.target.value)}
                            />
                            <div className={styles["form-hint"]}>
                                For block accounts: total GB purchased. The provider pauses
                                at ~95% of this to absorb in-flight requests. Empty or 0 = no cap.
                            </div>
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-host" className={styles["form-label"]}>
                                Host
                            </label>
                            <input
                                type="text"
                                id="provider-host"
                                className={styles["form-input"]}
                                placeholder="news.provider.com"
                                value={host}
                                onChange={(e) => {
                                    setHost(e.target.value);
                                    invalidateConnectionTests();
                                }}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-port" className={styles["form-label"]}>
                                Port
                            </label>
                            <input
                                type="text"
                                id="provider-port"
                                className={`${styles["form-input"]} ${!isPositiveInteger(port) && port !== "" ? styles.error : ""}`}
                                placeholder="563"
                                value={port}
                                onChange={(e) => {
                                    setPort(e.target.value);
                                    invalidateConnectionTests();
                                }}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-user" className={styles["form-label"]}>
                                Username
                            </label>
                            <input
                                type="text"
                                id="provider-user"
                                className={styles["form-input"]}
                                placeholder="username"
                                value={user}
                                onChange={(e) => {
                                    setUser(e.target.value);
                                    invalidateConnectionTests();
                                }}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-pass" className={styles["form-label"]}>
                                Password
                            </label>
                            <div className={styles["password-input-wrapper"]}>
                                <input
                                    type={showPass ? "text" : "password"}
                                    id="provider-pass"
                                    className={styles["form-input"]}
                                    placeholder="password"
                                    value={pass}
                                    onChange={(e) => {
                                        setPass(e.target.value);
                                        invalidateConnectionTests();
                                    }}
                                />
                                <button
                                    type="button"
                                    className={styles["password-toggle"]}
                                    onClick={() => setShowPass(v => !v)}
                                    aria-label={showPass ? "Hide password" : "Show password"}
                                    title={showPass ? "Hide password" : "Show password"}
                                >
                                    {showPass ? (
                                        // Eye-off icon (password revealed; click to hide)
                                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                            <path d="M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-11-8-11-8a18.45 18.45 0 0 1 5.06-5.94M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19m-6.72-1.07a3 3 0 1 1-4.24-4.24" />
                                            <line x1="1" y1="1" x2="23" y2="23" />
                                        </svg>
                                    ) : (
                                        // Eye icon (password hidden; click to reveal)
                                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                            <path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z" />
                                            <circle cx="12" cy="12" r="3" />
                                        </svg>
                                    )}
                                </button>
                            </div>
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-max-connections" className={styles["form-label"]}>
                                Max Connections
                            </label>
                            <input
                                type="text"
                                id="provider-max-connections"
                                className={`${styles["form-input"]} ${!isPositiveInteger(maxConnections) && maxConnections !== "" ? styles.error : ""}`}
                                placeholder="20"
                                value={maxConnections}
                                onChange={(e) => setMaxConnections(e.target.value)}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-type" className={styles["form-label"]}>
                                Type
                            </label>
                            <select
                                id="provider-type"
                                className={styles["form-select"]}
                                value={type}
                                onChange={(e) => setType(parseInt(e.target.value, 10) as ProviderType)}
                            >
                                <option value={ProviderType.Disabled}>Disabled</option>
                                <option value={ProviderType.Pooled}>Pooled Providers</option>
                                <option value={ProviderType.BackupAndStats}>Backup &amp; Health Checks</option>
                                <option value={ProviderType.BackupOnly}>Backup Only</option>
                            </select>
                        </div>

                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <div className={styles["form-checkbox-wrapper"]}>
                                <input
                                    type="checkbox"
                                    id="provider-ssl"
                                    className={styles["form-checkbox"]}
                                    checked={useSsl}
                                    onChange={(e) => {
                                        setUseSsl(e.target.checked);
                                        invalidateConnectionTests();
                                    }}
                                />
                                <label htmlFor="provider-ssl" className={styles["form-checkbox-label"]}>
                                    Use SSL
                                </label>
                            </div>
                        </div>

                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <div className={styles["form-checkbox-wrapper"]}>
                                <input
                                    type="checkbox"
                                    id="provider-pipelining"
                                    className={styles["form-checkbox"]}
                                    checked={statPipeliningEnabled}
                                    disabled={!pipeliningMasterEnabled}
                                    onChange={(e) => {
                                        setStatPipeliningEnabled(e.target.checked);
                                        setPipeliningTestResult(null);
                                    }}
                                />
                                <label htmlFor="provider-pipelining" className={styles["form-checkbox-label"]}>
                                    Enable STAT pipelining for this provider
                                </label>
                            </div>
                            <div style={{ marginTop: '8px', display: 'flex', alignItems: 'center', gap: '12px' }}>
                                <Button
                                    variant="secondary"
                                    size="sm"
                                    onClick={handleTestPipelining}
                                    disabled={!pipeliningMasterEnabled || !isFormValid || isTestingPipelining}
                                >
                                    {isTestingPipelining ? "Testing..." : "Test pipelining support"}
                                </Button>
                                {pipeliningTestResult === "supported" && (
                                    <span style={{ color: 'var(--bs-success, #28a745)' }}>✓ Supported</span>
                                )}
                                {pipeliningTestResult === "unsupported" && (
                                    <span style={{ color: 'var(--bs-danger, #dc3545)' }}>✗ Not supported</span>
                                )}
                            </div>
                            <div style={{ marginTop: '8px', opacity: 0.7, fontSize: '0.85em' }}>
                                {pipeliningMasterEnabled
                                    ? "Pipelining sends many STAT checks at once for faster health checks. For backup providers, this tickbox also opts the provider into carrying health checks when “Use Backup Providers for health checks” is enabled on the SABnzbd tab (providers of type “Backup & Health Checks” carry them even without pipelining, one STAT at a time) — STAT commands download no article data, though their protocol traffic can count toward block quotas (~0.7 MB per 10 GB of content checked). Test that this provider handles it correctly before enabling."
                                    : "To tick “Enable STAT pipelining for this provider”, first turn on the NNTP pipelining master switch on the SABnzbd tab. It’s disabled globally right now."}
                            </div>
                        </div>
                    </div>

                    {testError && (
                        <div className={`${styles.alert} ${styles["alert-danger"]}`} style={{ marginTop: '16px' }}>
                            {testError}
                        </div>
                    )}

                    {connectionTested && (
                        <div className={`${styles.alert} ${styles["alert-success"]}`} style={{ marginTop: '16px' }}>
                            Connection test successful!
                        </div>
                    )}
                </div>

                <div className={styles["modal-footer"]}>
                    <div className={styles["modal-footer-left"]}></div>
                    <div className={styles["modal-footer-right"]}>
                        <Button variant="secondary" onClick={onClose}>
                            Cancel
                        </Button>
                        {!canSave ? (
                            <Button
                                variant="primary"
                                onClick={handleTestConnection}
                                disabled={!isFormValid || isTestingConnection}
                            >
                                {isTestingConnection ? "Testing..." : "Test Connection"}
                            </Button>
                        ) : (
                            <Button variant="primary" onClick={handleSave} disabled={!canSave}>
                                Save Provider
                            </Button>
                        )}
                    </div>
                </div>
            </div>
        </div>
    );
}

export function isUsenetSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["usenet.providers"] !== newConfig["usenet.providers"]
}

export function isPositiveInteger(value: string) {
    const num = Number(value);
    return Number.isInteger(num) && num > 0 && value.trim() === num.toString();
}