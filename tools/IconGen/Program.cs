using System.Buffers.Binary;
using SkiaSharp;
using Svg.Skia;

// Usage: dotnet run --project tools/IconGen -- <input.svg> <outputDir>
var svgPath = args.Length > 0 ? args[0] : "src/SolutionDeployer.App/Assets/app-icon.svg";
var outDir = args.Length > 1 ? args[1] : "src/SolutionDeployer.App/Assets";
Directory.CreateDirectory(outDir);

using var svg = new SKSvg();
if (svg.Load(svgPath) is null || svg.Picture is null)
{
    Console.Error.WriteLine($"Failed to load SVG: {svgPath}");
    return 1;
}

var picture = svg.Picture;
var cull = picture.CullRect;
var source = Math.Max(cull.Width, cull.Height);

byte[] RenderPng(int size)
{
    var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
    using var surface = SKSurface.Create(info);
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);
    var scale = size / source;
    canvas.Scale(scale);
    canvas.DrawPicture(picture);
    canvas.Flush();
    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

// --- app-icon.png (256, for the Avalonia window icon) ---
var png256 = RenderPng(256);
File.WriteAllBytes(Path.Combine(outDir, "app-icon.png"), png256);

// --- app-icon.ico (Windows: multi-size, PNG-encoded entries) ---
int[] icoSizes = [16, 24, 32, 48, 64, 128, 256];
var icoImages = icoSizes.Select(s => (Size: s, Png: RenderPng(s))).ToArray();
WriteIco(Path.Combine(outDir, "app-icon.ico"), icoImages);

// --- app-icon.icns (macOS: PNG-encoded entries) ---
(string Type, int Size)[] icnsEntries =
[
    ("ic11", 32), ("ic12", 64), ("ic07", 128), ("ic13", 256), ("ic08", 256), ("ic09", 512),
];
WriteIcns(Path.Combine(outDir, "app-icon.icns"), icnsEntries.Select(e => (e.Type, RenderPng(e.Size))).ToArray());

Console.WriteLine($"Wrote app-icon.png / .ico / .icns to {Path.GetFullPath(outDir)}");
return 0;

static void WriteIco(string path, (int Size, byte[] Png)[] images)
{
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);

    w.Write((ushort)0);              // reserved
    w.Write((ushort)1);              // type: icon
    w.Write((ushort)images.Length);  // image count

    var offset = 6 + images.Length * 16;
    foreach (var (size, png) in images)
    {
        w.Write((byte)(size >= 256 ? 0 : size)); // width  (0 = 256)
        w.Write((byte)(size >= 256 ? 0 : size)); // height (0 = 256)
        w.Write((byte)0);    // palette count
        w.Write((byte)0);    // reserved
        w.Write((ushort)1);  // colour planes
        w.Write((ushort)32); // bits per pixel
        w.Write((uint)png.Length);
        w.Write((uint)offset);
        offset += png.Length;
    }

    foreach (var (_, png) in images)
        w.Write(png);
}

static void WriteIcns(string path, (string Type, byte[] Png)[] entries)
{
    var body = new List<byte>();
    var header = new byte[8];
    foreach (var (type, png) in entries)
    {
        for (var i = 0; i < 4; i++)
            header[i] = (byte)type[i];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)(png.Length + 8));
        body.AddRange(header);
        body.AddRange(png);
    }

    var total = 8 + body.Count;
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);
    w.Write([(byte)'i', (byte)'c', (byte)'n', (byte)'s']);
    Span<byte> len = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(len, (uint)total);
    w.Write(len.ToArray());
    w.Write(body.ToArray());
}
