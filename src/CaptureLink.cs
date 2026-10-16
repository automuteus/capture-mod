using System;

namespace CaptureMod;

// A Capture Link as the bot posts it for a new game: aucapture://host:port/CODE, with ?insecure when Galactus isn't
// behind TLS. Parsed the way the Capture App parses it, so any link that works there works here.
public sealed record CaptureLink(Uri Galactus, string ConnectCode)
{
    const int ConnectCodeLength = 8;

    public static CaptureLink? Parse(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "aucapture") return null;

        var code = uri.AbsolutePath.Trim('/');
        if (code.Length != ConnectCodeLength) return null;

        var insecure = uri.Query.Contains("insecure") && !uri.Query.Contains("insecure=false");
        return new CaptureLink(new Uri((insecure ? "ws://" : "wss://") + uri.Authority), code);
    }
}
