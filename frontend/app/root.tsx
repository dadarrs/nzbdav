import {
  Links,
  Meta,
  Outlet,
  Scripts,
  ScrollRestoration,
  useLocation,
  useNavigation,
  useRouteLoaderData,
} from "react-router";

// Light themes need bootstrap's light component styling; dark themes its dark.
const LIGHT_THEMES = new Set(["claude", "napster"]);
export const AVAILABLE_THEMES = ["midnight", "claude", "tangerine", "napster", "carbon"] as const;

import 'bootstrap/dist/css/bootstrap.min.css';
import "./app.css";
import type { Route } from "./+types/root";
import { IS_FRONTEND_AUTH_DISABLED } from "~/auth/authentication.server";
import { backendClient } from "~/clients/backend-client.server";
import { TopNavigation } from "./routes/_index/components/top-navigation/top-navigation";
import { LeftNavigation } from "./routes/_index/components/left-navigation/left-navigation";
import { PageLayout } from "./routes/_index/components/page-layout/page-layout";
import { Loading } from "./routes/_index/components/loading/loading";

export async function loader({ request }: Route.LoaderArgs) {
  let path = new URL(request.url).pathname;
  if (path === "/login") return { useLayout: false };
  if (path === "/onboarding") return { useLayout: false };

  const configItems = await backendClient.getConfig(["webdav.active-stream-tracker", "ui.theme"]);
  const isActiveStreamTrackerEnabled = configItems
    .find(i => i.configName === "webdav.active-stream-tracker")?.configValue !== "false";
  const theme = configItems.find(i => i.configName === "ui.theme")?.configValue || "midnight";
  return {
    useLayout: true,
    version: process.env.NZBDAV_VERSION,
    isFrontendAuthDisabled: IS_FRONTEND_AUTH_DISABLED,
    isActiveStreamTrackerEnabled,
    theme,
  };
}


export function Layout({ children }: { children: React.ReactNode }) {
  const rootData = useRouteLoaderData("root") as { theme?: string } | undefined;
  const theme = rootData?.theme ?? "midnight";
  const bsTheme = LIGHT_THEMES.has(theme) ? "light" : "dark";
  return (
    <html lang="en" data-theme={theme} data-bs-theme={bsTheme}>
      <head>
        <meta charSet="utf-8" />
        <meta name="viewport" content="width=device-width, initial-scale=1" />
        <link rel="icon" href="/logo.svg" />
        <Meta />
        <Links />
      </head>
      <body>
        {children}
        <ScrollRestoration />
        <Scripts />
      </body>
    </html>
  );
}

export default function App({ loaderData }: Route.ComponentProps) {
  const { useLayout, version, isFrontendAuthDisabled, isActiveStreamTrackerEnabled } = loaderData;
  const location = useLocation();
  const navigation = useNavigation();
  const isNavigating = Boolean(navigation.location);

  // display loading animiation during top-level page transitions,
  // but allow the `/explore` page to handle it's own loading screen.
  const isCurrentExplorePage = location.pathname.startsWith("/explore");
  const isNextExplorePage = navigation.location?.pathname?.startsWith("/explore");
  const showLoading = isNavigating && !(isCurrentExplorePage && isNextExplorePage);

  if (useLayout) {
    return (
      <PageLayout
        topNavComponent={TopNavigation}
        bodyChild={showLoading ? <Loading /> : <Outlet />}
        leftNavChild={
          <LeftNavigation
            version={version}
            isFrontendAuthDisabled={isFrontendAuthDisabled}
            isActiveStreamTrackerEnabled={isActiveStreamTrackerEnabled} />
        } />
    );
  }

  return <Outlet />;
}