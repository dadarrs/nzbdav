import { Form } from "react-bootstrap";
import { type Dispatch, type SetStateAction, useEffect } from "react";
import styles from "./system.module.css";

type SystemSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

type ThemeDef = {
    id: string,
    name: string,
    description: string,
    // swatch preview colors: [background, surface, accent, text]
    swatch: [string, string, string, string],
};

const THEMES: ThemeDef[] = [
    {
        id: "midnight", name: "Midnight",
        description: "The original deep-blue dark theme.",
        swatch: ["hsl(198,100%,4%)", "hsl(196,55%,9%)", "#4db8ff", "#ffffff"],
    },
    {
        id: "carbon", name: "Carbon",
        description: "Neutral near-black, easy on OLED.",
        swatch: ["hsl(0,0%,7%)", "hsl(0,0%,13%)", "hsl(160,60%,45%)", "#f2f2f2"],
    },
    {
        id: "tangerine", name: "Tangerine",
        description: "Charcoal dark with a bright orange accent.",
        swatch: ["hsl(220,14%,11%)", "hsl(220,12%,17%)", "hsl(24,95%,55%)", "#f6f1ec"],
    },
    {
        id: "claude", name: "Claude",
        description: "Warm parchment light theme.",
        swatch: ["hsl(48,28%,94%)", "hsl(46,24%,91%)", "hsl(16,63%,52%)", "hsl(30,18%,16%)"],
    },
    {
        id: "napster", name: "Napster",
        description: "Retro light grey, circa-2000.",
        swatch: ["hsl(0,0%,78%)", "hsl(0,0%,83%)", "hsl(224,64%,33%)", "hsl(0,0%,12%)"],
    },
];

const LIGHT_THEMES = new Set(["claude", "napster"]);

export function SystemSettings({ config, setNewConfig }: SystemSettingsProps) {
    const selected = config["ui.theme"] || "midnight";

    // Live preview: apply the pending theme to the document as the user clicks,
    // so they see it before saving. The root loader makes it permanent on the
    // next navigation once saved; this just keeps the current page in sync.
    useEffect(() => {
        if (typeof document === "undefined") return;
        document.documentElement.dataset.theme = selected;
        document.documentElement.setAttribute(
            "data-bs-theme", LIGHT_THEMES.has(selected) ? "light" : "dark");
    }, [selected]);

    return (
        <div className={styles.container}>
            <Form.Group>
                <Form.Label>Theme</Form.Label>
                <Form.Text className={styles.help} muted>
                    Recolors the whole app. Applies immediately as a preview; click Save to keep it.
                </Form.Text>
                <div className={styles.grid}>
                    {THEMES.map(theme => (
                        <button
                            type="button"
                            key={theme.id}
                            className={`${styles.card} ${selected === theme.id ? styles.cardSelected : ""}`}
                            onClick={() => setNewConfig({ ...config, "ui.theme": theme.id })}
                            aria-pressed={selected === theme.id}
                        >
                            <div className={styles.swatch} style={{ background: theme.swatch[0] }}>
                                <span className={styles.swatchSurface} style={{ background: theme.swatch[1] }} />
                                <span className={styles.swatchAccent} style={{ background: theme.swatch[2] }} />
                                <span className={styles.swatchText} style={{ background: theme.swatch[3] }} />
                            </div>
                            <div className={styles.cardBody}>
                                <div className={styles.cardName}>
                                    {theme.name}
                                    {selected === theme.id && <span className={styles.check}>✓</span>}
                                </div>
                                <div className={styles.cardDesc}>{theme.description}</div>
                            </div>
                        </button>
                    ))}
                </div>
            </Form.Group>
        </div>
    );
}

export function isSystemSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["ui.theme"] !== newConfig["ui.theme"];
}
