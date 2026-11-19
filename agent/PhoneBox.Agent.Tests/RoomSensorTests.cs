using System.Net;
using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

/// <summary>Room temperature widget: reading the Environment tile of a home-monitor page, and what the endpoint answers.</summary>
public sealed class RoomSensorTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"phonebox-room-{Guid.NewGuid():N}.json");
    public void Dispose() { File.Delete(_path); File.Delete(_path + ".bak"); }

    // the tile of the real page (vaponitor, Tabler markup), trimmed
    private const string Tile = """
        <div class="col">
            <div class="font-weight-medium">
                Environment
            </div>
            <div class="text-secondary">
                <span>
                    <span>23.3</span>°C,
                    <span>41.2</span>% RH
                </span>
            </div>
        </div>
        """;

    [Fact]
    public void TheTemperatureAndHumidityOfTheRealTileAreRead()
    {
        var r = RoomSensor.Parse("<html><body>" + Tile + "</body></html>");
        Assert.NotNull(r);
        Assert.Equal(23.3, r!.Temp);
        Assert.Equal(41.2, r.Humidity);
    }

    [Fact]
    public void OtherTilesBeforeTheEnvironmentOneAreNotMistakenForIt()
    {
        var other = "<div>CPU Temp</div><div><span>62.0</span>°C</div><div>CPU Usage</div><div><span>15.2</span>%</div>";
        var r = RoomSensor.Parse(other + Tile);
        Assert.Equal(23.3, r!.Temp);
        Assert.Equal(41.2, r.Humidity);
    }

    [Fact]
    public void ADecimalCommaAndAnHtmlDegreeSignAreUnderstood()
    {
        var r = RoomSensor.Parse(">Environment<<div>23,5&deg;C, 41,0 % RH</div>");
        Assert.Equal(23.5, r!.Temp);
        Assert.Equal(41.0, r.Humidity);
    }

    [Fact]
    public void ANegativeTemperatureIsRead()
    {
        Assert.Equal(-4.5, RoomSensor.Parse(">Environment<<span>-4.5</span>°C, <span>80</span>% RH")!.Temp);
    }

    [Fact]
    public void AMissingHumidityIsJustLeftOut()
    {
        var r = RoomSensor.Parse(">Environment<<div><span>21.0</span>°C</div>");
        Assert.Equal(21.0, r!.Temp);
        Assert.Null(r.Humidity);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html>no tile here <span>23.3</span>°C</html>")]
    [InlineData(">Environment<<div>no numbers</div>")]
    [InlineData(">Environment<<span>500</span>°C")]       // not a room temperature
    public void APageWithoutAUsableTileGivesNothing(string? html) => Assert.Null(RoomSensor.Parse(html));

    [Fact]
    public void AnImplausibleHumidityIsDropped()
    {
        var r = RoomSensor.Parse(">Environment<<span>22</span>°C, <span>140</span>% RH");
        Assert.Equal(22, r!.Temp);
        Assert.Null(r.Humidity);
    }

    // ---- the endpoint

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(answer(request));
        }
    }

    // a second, enabled page: a layout with every page disabled gets its first page switched on again
    private ConfigStore Store(string? url, bool pageEnabled = true)
    {
        var options = url is null ? "{}" : JsonSerializer.Serialize(new { url });
        File.WriteAllText(_path, $$"""
            {"pages":[{"id":"p","title":"P","enabled":{{(pageEnabled ? "true" : "false")}},"widgets":[{"id":"w","type":"roomtemp","x":0,"y":0,"w":3,"h":2,"options":{{options}}}]},{"id":"q","title":"Q","widgets":[{"id":"c","type":"clock","x":0,"y":0,"w":4,"h":3}]}]}
            """);
        return new ConfigStore(_path);
    }

    private static HttpResponseMessage Page(string html) => new(HttpStatusCode.OK) { Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html") };

    private static JsonElement Json(object o) => JsonSerializer.SerializeToElement(o);
    private static string State(object o) => Json(o).GetProperty("state").GetString()!;

    private static RoomSensor Sensor(ConfigStore store, Stub stub, Func<long>? now = null) =>
        new(store, _ => new HttpClient(stub), now);

    [Fact]
    public async Task TheWidgetsPageIsFetchedAndTheReadingReturned()
    {
        var stub = new Stub(_ => Page(Tile));
        var answer = Json(await Sensor(Store("http://192.168.0.110:8080/"), stub).Read(null, "p/w", default));
        Assert.Equal("ok", answer.GetProperty("state").GetString());
        Assert.Equal(23.3, answer.GetProperty("temp").GetDouble());
        Assert.Equal(41.2, answer.GetProperty("humidity").GetDouble());
        Assert.Equal(1, stub.Calls);
    }

    [Fact]
    public async Task ASecondAskWithinTheCacheTimeCostsNoRequest()
    {
        var stub = new Stub(_ => Page(Tile));
        var clock = 1_000L;
        var sensor = Sensor(Store("http://192.168.0.110:8080/"), stub, () => clock);
        await sensor.Read(null, "p/w", default);
        clock += RoomSensor.CacheMs - 1;
        Assert.Equal("ok", State(await sensor.Read(null, "p/w", default)));
        Assert.Equal(1, stub.Calls);
        clock += 2;
        await sensor.Read(null, "p/w", default);
        Assert.Equal(2, stub.Calls);
    }

    [Fact]
    public async Task OnlyAWidgetOnAnEnabledPageIsAnswered()
    {
        var stub = new Stub(_ => Page(Tile));
        Assert.Equal("unknown", State(await Sensor(Store("http://192.168.0.110/"), stub).Read(null, "p/nope", default)));
        Assert.Equal("unknown", State(await Sensor(Store("http://192.168.0.110/", pageEnabled: false), stub).Read(null, "p/w", default)));
        Assert.Equal("unknown", State(await Sensor(Store("http://192.168.0.110/"), stub).Read(null, null, default)));
        Assert.Equal(0, stub.Calls);
    }

    [Fact]
    public async Task NoAddressAndAWrongAddressAreSaidSo()
    {
        var stub = new Stub(_ => Page(Tile));
        Assert.Equal("pick", State(await Sensor(Store(null), stub).Read(null, "p/w", default)));
        Assert.Equal("pick", State(await Sensor(Store("  "), stub).Read(null, "p/w", default)));
        Assert.Equal("badurl", State(await Sensor(Store("ftp://192.168.0.110/"), stub).Read(null, "p/w", default)));
        Assert.Equal("badurl", State(await Sensor(Store("http://user:pass@192.168.0.110/"), stub).Read(null, "p/w", default)));
        Assert.Equal(0, stub.Calls);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8765/")]                // this computer: the agent itself
    [InlineData("http://169.254.169.254/latest/meta-data")] // link-local (cloud metadata)
    public async Task AnAddressThatIsThisComputerOrLinkLocalIsNeverFetched(string url)
    {
        var stub = new Stub(_ => Page(Tile));
        Assert.Equal("unreachable", State(await Sensor(Store(url), stub).Read(null, "p/w", default)));
        Assert.Equal(0, stub.Calls);
    }

    [Fact]
    public async Task AnErrorOrAPageWithoutATileGivesAStateAndNoDetails()
    {
        var down = new Stub(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var answer = Json(await Sensor(Store("http://192.168.0.110:8080/"), down).Read(null, "p/w", default));
        Assert.Equal("unreachable", answer.GetProperty("state").GetString());
        Assert.Single(answer.EnumerateObject());          // only the state: no address, no error text

        var other = new Stub(_ => Page("<html>some other page</html>"));
        Assert.Equal("nodata", State(await Sensor(Store("http://192.168.0.110:8080/"), other).Read(null, "p/w", default)));
    }
}
