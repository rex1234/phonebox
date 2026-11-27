using PhoneBox.Agent;
using Xunit;

/// <summary>The Govee light widget's messages and the addresses it will talk to. Nothing here touches the network.</summary>
public sealed class GoveeLanTests
{
    [Fact]
    public void AStatusAnswerIsRead() =>
        Assert.Equal(new GoveeLightState(true, 40, 3300), GoveeLan.ParseStatus(
            "{\"msg\":{\"cmd\":\"devStatus\",\"data\":{\"onOff\":1,\"brightness\":40,\"color\":{\"r\":0,\"g\":0,\"b\":0},\"colorTemInKelvin\":3300}}}"));

    [Fact]
    public void AnOffLightWithoutAColourTemperatureIsRead()
    {
        Assert.Equal(new GoveeLightState(false, 98, null), GoveeLan.ParseStatus("{\"msg\":{\"cmd\":\"devStatus\",\"data\":{\"onOff\":0,\"brightness\":98}}}"));
    }

    [Fact]
    public void ABrightnessOutOfRangeIsClamped() =>
        Assert.Equal(100, GoveeLan.ParseStatus("{\"msg\":{\"cmd\":\"devStatus\",\"data\":{\"onOff\":1,\"brightness\":150}}}")!.Brightness);

    [Theory]
    [InlineData("{\"msg\":{\"cmd\":\"scan\",\"data\":{}}}")]          // another message of the protocol
    [InlineData("{\"msg\":{\"cmd\":\"devStatus\",\"data\":{\"brightness\":5}}}")] // no on/off state
    [InlineData("{\"msg\":{\"cmd\":\"devStatus\"}}")]                  // no data
    [InlineData("not json")]
    [InlineData("")]
    public void AnythingElseIsNotAStatus(string packet) => Assert.Null(GoveeLan.ParseStatus(packet));

    [Fact]
    public void TheOnAndOffCommandsAreTheDocumentedLanMessages()
    {
        Assert.Equal("{\"msg\":{\"cmd\":\"turn\",\"data\":{\"value\":1}}}", GoveeLan.Command("on", null));
        Assert.Equal("{\"msg\":{\"cmd\":\"turn\",\"data\":{\"value\":0}}}", GoveeLan.Command("off", null));
    }

    [Theory]
    [InlineData(50, "\"value\":50")]
    [InlineData(0, "\"value\":1")]      // the light's brightness starts at 1
    [InlineData(250, "\"value\":100")]
    public void BrightnessIsSentWithinItsRange(int value, string expected) =>
        Assert.Contains(expected, GoveeLan.Command("brightness", value));

    [Theory]
    [InlineData(3300, "\"colorTemInKelvin\":3300}}}")]
    [InlineData(100, "\"colorTemInKelvin\":2000}}}")]
    [InlineData(20000, "\"colorTemInKelvin\":9000}}}")]
    public void TheColourTemperatureIsSentWithinItsRange(int value, string expected)
    {
        var command = GoveeLan.Command("kelvin", value)!;
        Assert.EndsWith(expected, command);
        using var json = System.Text.Json.JsonDocument.Parse(command);
        Assert.Equal("colorwc", json.RootElement.GetProperty("msg").GetProperty("cmd").GetString());
    }

    [Theory]
    [InlineData("on", false, false)]   // the old state the light still reports right after the command
    [InlineData("on", true, true)]
    [InlineData("off", true, false)]
    [InlineData("off", false, true)]
    public void AnAnswerCountsOnceItShowsTheSwitch(string op, bool on, bool reflects) =>
        Assert.Equal(reflects, GoveeLan.Reflects(op, null, new GoveeLightState(on, 50, 3300)));

    [Theory]
    [InlineData("brightness", 50, true)]
    [InlineData("brightness", 60, false)]
    [InlineData("kelvin", 3300, true)]
    [InlineData("kelvin", 4000, false)]
    public void AnAnswerCountsOnceItShowsTheLevel(string op, int value, bool reflects) =>
        Assert.Equal(reflects, GoveeLan.Reflects(op, value, new GoveeLightState(true, 50, 3300)));

    [Theory]
    [InlineData("kelvin", null)]
    [InlineData("explode", null)]
    [InlineData("brightness", null)]
    [InlineData(null, null)]
    public void AnUnknownOperationSendsNothing(string? op, int? value) => Assert.Null(GoveeLan.Command(op, value));

    [Theory]
    [InlineData("192.168.0.69")]
    [InlineData(" 10.0.0.12 ")]
    public void AnOrdinaryLanAddressIsUsed(string text) => Assert.NotNull(GoveeLan.DeviceAddress(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("lamp.local")]        // names are not resolved: an address only
    [InlineData("127.0.0.1")]         // this computer
    [InlineData("0.0.0.0")]
    [InlineData("239.255.255.250")]   // multicast
    [InlineData("192.168.0.255")]     // broadcast
    [InlineData("::1")]               // IPv6 is not used by the Govee protocol
    public void AnythingElseIsRefused(string? text) => Assert.Null(GoveeLan.DeviceAddress(text));
}
