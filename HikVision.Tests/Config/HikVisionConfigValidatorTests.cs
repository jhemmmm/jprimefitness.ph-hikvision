using HikVision.Config;
using Microsoft.Extensions.Configuration;

namespace HikVision.Tests.Config;

public class HikVisionConfigValidatorTests
{
    [Fact]
    public void LoadAndValidate_accepts_only_simplified_config()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["HikVision:Username"] = "admin",
            ["HikVision:Password"] = "secret",
            ["HikVision:ForwardUrl"] = "https://example.test/callback",
            ["HikVision:ForwardToken"] = "token",
            ["HikVision:Debug"] = "true"
        });

        var config = HikVisionConfigValidator.LoadAndValidate(configuration);

        Assert.Equal("admin", config.Username);
        Assert.True(config.Debug);
    }

    [Fact]
    public void LoadAndValidate_rejects_legacy_device_ip_config()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["HikVision:Username"] = "admin",
            ["HikVision:Password"] = "secret",
            ["HikVision:ForwardUrl"] = "https://example.test/callback",
            ["HikVision:ForwardToken"] = "token",
            ["HikVision:DeviceIp"] = "192.168.1.100"
        });

        var error = Assert.Throws<InvalidOperationException>(
            () => HikVisionConfigValidator.LoadAndValidate(configuration));

        Assert.Contains("DeviceIp", error.Message);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
