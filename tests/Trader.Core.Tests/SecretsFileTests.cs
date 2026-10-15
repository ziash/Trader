using Trader.Core.Config;

namespace Trader.Core.Tests;

public class SecretsFileTests
{
    private static readonly string[] ValidLines =
    [
        "# comment",
        "",
        "CTRADER_CLIENT_ID=id_1234567",
        "CTRADER_CLIENT_SECRET=\"secret-abcdef\"",
        "export CTRADER_ACCESS_TOKEN=access-xyz",
        "CTRADER_REFRESH_TOKEN='refresh-xyz'",
        "CTRADER_ENV=demo",
    ];

    [Fact]
    public void Parse_reads_values_strips_quotes_and_export()
    {
        var s = SecretsFile.Parse(ValidLines);

        Assert.Equal("id_1234567", s.ClientId);
        Assert.Equal("secret-abcdef", s.ClientSecret);
        Assert.Equal("access-xyz", s.AccessToken);
        Assert.Equal("refresh-xyz", s.RefreshToken);
        Assert.Equal(ApiEnvironment.Demo, s.Environment);
    }

    [Fact]
    public void Missing_env_defaults_to_demo()
    {
        var s = SecretsFile.Parse(ValidLines.Where(l => !l.StartsWith("CTRADER_ENV")));
        Assert.Equal(ApiEnvironment.Demo, s.Environment);
    }

    [Theory]
    [InlineData("live")]
    [InlineData("LIVE")]
    [InlineData("prod")]
    public void Non_demo_env_is_refused(string env)
    {
        var lines = ValidLines.Select(l => l.StartsWith("CTRADER_ENV") ? $"CTRADER_ENV={env}" : l);
        Assert.Throws<SecretsException>(() => SecretsFile.Parse(lines));
    }

    [Fact]
    public void Missing_key_error_names_key_but_not_values()
    {
        var lines = ValidLines.Where(l => !l.StartsWith("CTRADER_CLIENT_SECRET"));
        var ex = Assert.Throws<SecretsException>(() => SecretsFile.Parse(lines));

        Assert.Contains("CTRADER_CLIENT_SECRET", ex.Message);
        Assert.DoesNotContain("id_1234567", ex.Message);
        Assert.DoesNotContain("access-xyz", ex.Message);
    }

    [Fact]
    public void ToString_masks_every_secret()
    {
        var text = SecretsFile.Parse(ValidLines).ToString();

        Assert.DoesNotContain("id_1234567", text);
        Assert.DoesNotContain("secret-abcdef", text);
        Assert.DoesNotContain("access-xyz", text);
        Assert.DoesNotContain("refresh-xyz", text);
        Assert.Contains("id_1…", text);
    }

    [Theory]
    [InlineData(null, "(not set)")]
    [InlineData("", "(not set)")]
    [InlineData("abc", "…")]
    [InlineData("abcdefgh", "abcd…")]
    public void Mask_shows_at_most_four_chars(string? value, string expected) =>
        Assert.Equal(expected, TraderSecrets.Mask(value));

    [Fact]
    public void Load_refuses_file_readable_by_others()
    {
        if (OperatingSystem.IsWindows())
            return;

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path, ValidLines);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            Assert.Throws<SecretsException>(() => SecretsFile.Load(path));

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Equal("id_1234567", SecretsFile.Load(path).ClientId);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
