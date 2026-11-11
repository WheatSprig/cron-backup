using CronBackup.Core.Configuration;

namespace CronBackup.Core.Tests.Configuration;

public sealed class EnvInterpolatorTests
{
    private static EnvInterpolator Create(params (string Key, string? Value)[] values)
    {
        var environment = values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        return new EnvInterpolator(environment);
    }

    [Fact]
    public void Interpolate_ShouldReplaceVariable()
    {
        var interpolator = Create(("BUCKET", "backups"));

        var result = interpolator.Interpolate("s3://${BUCKET}/photos", "dest");

        Assert.Equal("s3://backups/photos", result);
    }

    [Fact]
    public void Interpolate_ShouldUseDefault_WhenVariableIsMissing()
    {
        var interpolator = Create();

        var result = interpolator.Interpolate("${REGION:-us-east-1}", "dest");

        Assert.Equal("us-east-1", result);
    }

    [Fact]
    public void Interpolate_ShouldUseDefault_WhenVariableIsEmpty()
    {
        var interpolator = Create(("REGION", ""));

        var result = interpolator.Interpolate("${REGION:-us-east-1}", "dest");

        Assert.Equal("us-east-1", result);
    }

    [Fact]
    public void Interpolate_ShouldKeepEscapedPlaceholderLiteral()
    {
        var interpolator = Create(("BUCKET", "backups"));

        var result = interpolator.Interpolate("$${BUCKET}", "dest");

        Assert.Equal("${BUCKET}", result);
    }

    [Fact]
    public void Interpolate_ShouldNotExpandDollarWithoutBraces()
    {
        var interpolator = Create(("BUCKET", "backups"));

        var result = interpolator.Interpolate("$BUCKET", "dest");

        Assert.Equal("$BUCKET", result);
    }

    [Fact]
    public void Interpolate_ShouldThrowWithPathAndVariableName_WhenMissing()
    {
        var interpolator = Create();

        var exception = Assert.Throws<ConfigException>(
            () => interpolator.Interpolate("${MISSING}", "jobs[0].destinations[1].password"));

        Assert.Contains("jobs[0].destinations[1].password", exception.Message);
        Assert.Contains("MISSING", exception.Message);
    }

    [Fact]
    public void Interpolate_ShouldThrow_WhenPlaceholderIsUnterminated()
    {
        var interpolator = Create(("BUCKET", "backups"));

        Assert.Throws<ConfigException>(() => interpolator.Interpolate("${BUCKET", "dest"));
    }

    [Fact]
    public void Interpolate_ShouldThrow_WhenVariableNameIsInvalid()
    {
        var interpolator = Create();

        Assert.Throws<ConfigException>(() => interpolator.Interpolate("${bad-name:-x}", "dest"));
    }
}
