using PhoneBox.Agent;
using Xunit;

/// <summary>Desktop wallpaper behind the panel: which file may be served, and which never.</summary>
public sealed class WallpaperTests
{
    private const string Transcoded = @"C:\Users\me\AppData\Roaming\Microsoft\Windows\Themes\TranscodedWallpaper";
    private static Func<string, long?> Sizes(params (string Path, long? Size)[] files) =>
        p => files.FirstOrDefault(f => f.Path == p).Size;

    [Fact]
    public void TheWallpaperFromTheRegistryIsPreferred()
    {
        const string picture = @"C:\Users\me\Desktop\wall.jpeg";
        Assert.Equal(picture, Wallpaper.Pick(picture, Transcoded, Sizes((picture, 200_000), (Transcoded, 150_000))));
    }

    [Theory]
    [InlineData(@"C:\Users\me\Desktop\notes.txt")]      // not a picture
    [InlineData(@"C:\Users\me\.ssh\id_rsa")]            // no extension: only the transcoded copy may be extension-less
    [InlineData(@"C:\Windows\System32\config\SAM")]
    [InlineData(@"C:\Users\me\Desktop\payload.exe")]
    [InlineData(@"wall.jpg")]                            // not an absolute path
    [InlineData(@"..\..\wall.jpg")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingButAnAbsolutePathToAPictureFallsBackToTheTranscodedCopy(string? configured)
    {
        var sizes = Sizes((@"C:\Users\me\Desktop\notes.txt", 10), (@"C:\Users\me\.ssh\id_rsa", 10), (@"C:\Windows\System32\config\SAM", 10),
            (@"C:\Users\me\Desktop\payload.exe", 10), ("wall.jpg", 10), (@"..\..\wall.jpg", 10), (Transcoded, 150_000));
        Assert.Equal(Transcoded, Wallpaper.Pick(configured, Transcoded, sizes));
    }

    [Fact]
    public void AMissingEmptyOrHugeFileIsNotServed()
    {
        const string picture = @"C:\Users\me\Desktop\wall.png";
        Assert.Null(Wallpaper.Pick(picture, null, Sizes()));                                              // missing
        Assert.Null(Wallpaper.Pick(picture, null, Sizes((picture, 0))));                                  // empty
        Assert.Null(Wallpaper.Pick(picture, null, Sizes((picture, Wallpaper.MaxBytes + 1))));             // too large
        Assert.Equal(picture, Wallpaper.Pick(picture, null, Sizes((picture, Wallpaper.MaxBytes))));       // exactly the limit
        // a huge picture falls back to the transcoded copy, which Windows sized for the screen
        Assert.Equal(Transcoded, Wallpaper.Pick(picture, Transcoded, Sizes((picture, Wallpaper.MaxBytes + 1), (Transcoded, 1000))));
    }

    [Fact]
    public void WithNoWallpaperAtAllThereIsNothing() =>
        Assert.Null(Wallpaper.Pick(null, Transcoded, Sizes()));

    [Theory]
    [InlineData(@"C:\a\b.JPG", "image/jpeg")]
    [InlineData(@"C:\a\b.jpeg", "image/jpeg")]
    [InlineData(@"C:\a\b.png", "image/png")]
    [InlineData(@"C:\a\b.webp", "image/webp")]
    [InlineData(Transcoded, "image/jpeg")]               // no extension: a JPEG
    public void TheMediaTypeFollowsTheExtension(string path, string type) => Assert.Equal(type, Wallpaper.ContentType(path));
}
