using Microsoft.Extensions.Configuration;

namespace HikVision.Config;

public static class HikVisionConfigValidator
{
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(HikVisionConfig.Username),
        nameof(HikVisionConfig.Password),
        nameof(HikVisionConfig.ForwardUrl),
        nameof(HikVisionConfig.ForwardToken),
        nameof(HikVisionConfig.Debug)
    };

    public static HikVisionConfig LoadAndValidate(IConfiguration configuration)
    {
        var section = configuration.GetSection("HikVision");
        var unknownKeys = section
            .GetChildren()
            .Select(child => child.Key)
            .Where(key => !AllowedKeys.Contains(key))
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (unknownKeys.Length > 0)
        {
            throw new InvalidOperationException(
                "Unsupported HikVision configuration key(s): " +
                string.Join(", ", unknownKeys) +
                ". Only ForwardUrl, ForwardToken, Username, Password, and Debug are configurable.");
        }

        var config = section.Get<HikVisionConfig>()
            ?? throw new InvalidOperationException("HikVision configuration not found in appsettings.json.");

        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(config.ForwardUrl))
            missing.Add(nameof(HikVisionConfig.ForwardUrl));
        else if (!Uri.TryCreate(config.ForwardUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("HikVision:ForwardUrl must be an absolute URL.");

        if (string.IsNullOrWhiteSpace(config.ForwardToken))
            missing.Add(nameof(HikVisionConfig.ForwardToken));

        if (string.IsNullOrWhiteSpace(config.Username))
            missing.Add(nameof(HikVisionConfig.Username));

        if (string.IsNullOrWhiteSpace(config.Password))
            missing.Add(nameof(HikVisionConfig.Password));

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Missing required HikVision configuration key(s): " + string.Join(", ", missing) + ".");
        }

        return config;
    }
}
