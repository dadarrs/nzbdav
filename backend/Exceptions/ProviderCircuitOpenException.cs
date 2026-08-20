namespace NzbWebDAV.Exceptions;

/// <summary>
/// The provider circuit breaker is cooling down or already has a half-open probe in flight.
/// Callers may immediately try another provider without recording another provider failure.
/// </summary>
public sealed class ProviderCircuitOpenException(string providerName)
    : Exception($"Provider {providerName} is temporarily unavailable.");
