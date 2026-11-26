using BiliIndexTranslate.Core.Settings;

namespace BiliIndexTranslate.Core.Translation;

/// <summary>Short names of the configured translation service, for captions, diagnostics and log lines.</summary>
public static class ServiceLabels
{
    /// <summary>What answers the requests: the model, or the name of a format that has no model.</summary>
    public static string ModelLabel(AppSettings s) => s.ApiFormat switch
    {
        ApiFormat.Deeplx => DeeplxProtocol.ModelLabel,
        ApiFormat.Custom => s.CustomTemplate.Label,
        _ => s.Model,
    };

    /// <summary>The host the requests go to, or "(invalid)" when the settings do not resolve to an endpoint.</summary>
    public static string HostOf(AppSettings s)
        => EndpointResolver.TryResolve(s.ApiFormat, s.BaseUrl, s.UseRelay, s.RelayPrefix, s.ApiKey, out var ep, out _) ? ep!.DisplayHost : "(invalid)";
}
