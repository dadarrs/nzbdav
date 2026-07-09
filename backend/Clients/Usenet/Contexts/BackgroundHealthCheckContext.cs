namespace NzbWebDAV.Clients.Usenet.Contexts;

/// <summary>
/// Marker context set on the background health-check service's cancellation token so the
/// STAT fan-out can tell background checks apart from on-add checks: backup providers may be
/// scoped to on-add checks only, keeping periodic library re-scans off block accounts.
/// </summary>
public record BackgroundHealthCheckContext;
