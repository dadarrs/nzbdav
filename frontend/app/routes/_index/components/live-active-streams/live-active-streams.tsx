import { useEffect, useState } from "react";
import styles from "./live-active-streams.module.css";
import { receiveMessage } from "~/utils/websocket-util";
import { useNavigate } from "react-router";

const activeStreamsTopic = {'as': 'state'};

interface ActiveStream {
    id: string;
    fileName: string;
    bytesTransferred: number;
    speedBytesPerSec: number;
    activeConnections: number;
}

function parseStreams(message: string): ActiveStream[] {
    if (!message) return [];
    return message.split(";").map(s => {
        const [id, fileName, bytesTransferred, speedBytesPerSec, activeConnections] = s.split("|");
        return {
            id,
            fileName,
            bytesTransferred: Number(bytesTransferred),
            speedBytesPerSec: Number(speedBytesPerSec),
            activeConnections: Number(activeConnections),
        };
    });
}

function formatSpeed(bytesPerSec: number): string {
    if (bytesPerSec >= 1_000_000_000) return `${(bytesPerSec / 1_000_000_000).toFixed(1)} GB/s`;
    if (bytesPerSec >= 1_000_000) return `${(bytesPerSec / 1_000_000).toFixed(1)} MB/s`;
    if (bytesPerSec >= 1_000) return `${(bytesPerSec / 1_000).toFixed(0)} KB/s`;
    return `${bytesPerSec} B/s`;
}

function truncate(str: string, max: number): string {
    return str.length > max ? str.slice(0, max - 1) + "\u2026" : str;
}

export function LiveActiveStreams() {
    const navigate = useNavigate();
    const [streams, setStreams] = useState<ActiveStream[] | null>(null);

    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;
        function connect() {
            ws = new WebSocket(window.location.origin.replace(/^http/, 'ws'));
            ws.onmessage = receiveMessage((_, message) => setStreams(parseStreams(message)));
            ws.onopen = () => ws.send(JSON.stringify(activeStreamsTopic));
            ws.onerror = () => { ws.close() };
            ws.onclose = onClose;
            return () => { disposed = true; ws.close(); }
        }
        function onClose(e: CloseEvent) {
            if (e.code == 1008) navigate('/login');
            !disposed && setTimeout(() => connect(), 1000);
            setStreams(null);
        }
        return connect();
    }, [setStreams]);

    return (
        <div className={styles.container}>
            <div className={styles.title}>
                Active Streams
            </div>
            {streams === null && (
                <div className={styles.caption}>Loading...</div>
            )}
            {streams !== null && streams.length === 0 && (
                <div className={styles.caption}>No active streams</div>
            )}
            {streams !== null && streams.length > 0 && (
                // compact summary: per-stream details live on the overview page's live-reads
                // panel; clicking through navigates there.
                <div
                    className={styles.list}
                    onClick={() => navigate("/overview")}
                    style={{ cursor: "pointer" }}
                    title="Open the overview page for per-stream details"
                >
                    <div className={styles.stream}>
                        <div className={styles.fileName}>
                            {streams.length === 1
                                ? truncate(streams[0].fileName, 30)
                                : `${streams.length} files streaming`}
                        </div>
                        <div className={styles.meta}>
                            <span className={styles.speed}>
                                {formatSpeed(streams.reduce((sum, s) => sum + s.speedBytesPerSec, 0))}
                            </span>
                            <span className={styles.connections}>
                                {streams.reduce((sum, s) => sum + s.activeConnections, 0)} conn
                            </span>
                        </div>
                    </div>
                </div>
            )}
        </div>
    );
}
