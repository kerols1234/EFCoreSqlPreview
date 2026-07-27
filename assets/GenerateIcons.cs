#:package System.Drawing.Common@10.*
#:property PublishAot=false
#:property Nullable=disable

// Regenerates the extension icon and preview image.
//
//   dotnet run --file assets/GenerateIcons.cs
//
// The artwork is committed, so this only needs running when the design changes. Everything is drawn at
// 1024 px and downsampled, because the 32 px icon is far too small to draw directly without the curves
// turning to mush.

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

const int Master = 1024;

var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
if (!Directory.Exists(Path.Combine(repoRoot, "EFCoreSqlPreview")))
{
    // A file-based app's base directory is a build-cache path, so fall back to the working directory.
    repoRoot = Directory.GetCurrentDirectory();
    while (repoRoot is not null && !Directory.Exists(Path.Combine(repoRoot, "EFCoreSqlPreview")))
    {
        repoRoot = Path.GetDirectoryName(repoRoot);
    }
}

if (repoRoot is null)
{
    Console.Error.WriteLine("Could not locate the repository root.");
    return 1;
}

var resources = Path.Combine(repoRoot, "EFCoreSqlPreview", "Resources");
Directory.CreateDirectory(resources);

using var master = Draw(Master);

Save(master, Path.Combine(resources, "icon.png"), 32);
Save(master, Path.Combine(resources, "preview.png"), 200);
Save(master, Path.Combine(repoRoot, "assets", "logo.png"), 512);

Console.WriteLine("Wrote icon.png (32), preview.png (200) and assets/logo.png (512).");
return 0;

/// <summary>Renders the logo at an arbitrary size.</summary>
static Bitmap Draw(int size)
{
    var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bitmap);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.Clear(Color.Transparent);

    float S(double fraction) => (float)(fraction * size);

    // Rounded-square plate in the .NET purple-to-blue range, so it reads as a .NET tool at a glance.
    var plate = new RectangleF(0, 0, size, size);
    using (var path = RoundedRect(plate, S(0.22)))
    using (var brush = new LinearGradientBrush(plate, Color.FromArgb(0x6D, 0x3B, 0xE8), Color.FromArgb(0x24, 0x86, 0xE0), 55f))
    {
        g.FillPath(brush, path);
    }

    // Database cylinder, offset up and left to leave the lower-right corner for the magnifier.
    var cx = S(0.395);
    var discWidth = S(0.42);
    var discHeight = S(0.135);
    var top = S(0.17);
    var bottom = S(0.55);

    var left = cx - (discWidth / 2f);
    var bodyTop = top + (discHeight / 2f);
    var bodyHeight = bottom - top;

    using (var white = new SolidBrush(Color.White))
    {
        g.FillRectangle(white, left, bodyTop, discWidth, bodyHeight);
        g.FillEllipse(white, left, bottom, discWidth, discHeight);
        g.FillEllipse(white, left, top, discWidth, discHeight);
    }

    // Two ribs, drawn in the plate colour so they read as separations rather than drawn-on lines.
    using (var rib = new Pen(Color.FromArgb(0x50, 0x3E, 0xB0, 0xE8), S(0.028)))
    {
        rib.StartCap = LineCap.Round;
        rib.EndCap = LineCap.Round;
        g.DrawArc(rib, left, top + (bodyHeight / 3f), discWidth, discHeight, 20f, 140f);
        g.DrawArc(rib, left, top + (2f * bodyHeight / 3f), discWidth, discHeight, 20f, 140f);
    }

    // Magnifier. A plate-coloured halo is stroked first so the glass stays legible where it crosses the
    // cylinder - without it the white ring vanishes into the white body.
    var glassCentre = new PointF(S(0.635), S(0.615));
    var radius = S(0.235);
    var glass = new RectangleF(glassCentre.X - radius, glassCentre.Y - radius, radius * 2f, radius * 2f);
    var handleFrom = new PointF(glassCentre.X + (radius * 0.72f), glassCentre.Y + (radius * 0.72f));
    var handleTo = new PointF(S(0.885), S(0.865));

    using (var halo = new Pen(Color.FromArgb(0x2B, 0x74, 0xDC), S(0.145)))
    {
        halo.StartCap = LineCap.Round;
        halo.EndCap = LineCap.Round;
        g.DrawEllipse(halo, glass);
        g.DrawLine(halo, handleFrom, handleTo);
    }

    // The lens is filled opaque rather than tinted: a translucent one lets the cylinder's hard edge cut
    // straight through the glass, which reads as a rendering mistake at icon sizes.
    using (var lens = new SolidBrush(Color.FromArgb(0x3F, 0x8B, 0xE4)))
    {
        g.FillEllipse(lens, glass);
    }

    // A single highlight streak is what makes it read as glass rather than a flat disc.
    using (var shine = new Pen(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF), S(0.05)))
    {
        shine.StartCap = LineCap.Round;
        shine.EndCap = LineCap.Round;
        g.DrawArc(shine, glass.X + S(0.055), glass.Y + S(0.055), glass.Width - S(0.11), glass.Height - S(0.11), 160f, 70f);
    }

    using (var ring = new Pen(Color.White, S(0.072)))
    {
        ring.StartCap = LineCap.Round;
        ring.EndCap = LineCap.Round;
        g.DrawLine(ring, handleFrom, handleTo);
        g.DrawEllipse(ring, glass);
    }

    return bitmap;
}

static void Save(Bitmap master, string path, int size)
{
    using var scaled = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(scaled))
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.Clear(Color.Transparent);
        g.DrawImage(master, new Rectangle(0, 0, size, size));
    }

    Directory.CreateDirectory(Path.GetDirectoryName(path));
    scaled.Save(path, ImageFormat.Png);
    Console.WriteLine($"  {size,4} px  {path}");
}

static GraphicsPath RoundedRect(RectangleF bounds, float radius)
{
    var d = radius * 2f;
    var path = new GraphicsPath();
    path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
    path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
    path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
    path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
    path.CloseFigure();
    return path;
}
