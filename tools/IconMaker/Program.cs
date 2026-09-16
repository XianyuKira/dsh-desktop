using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Renders a multi-resolution application icon. Everything is drawn at run time
// so the repository carries no binary art. Small sizes are stored as BMP
// entries and larger ones as PNG entries, which is what Windows expects.

var output = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "app.ico");

var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
var images = sizes.Select(Render).ToArray();

try
{
    using (var stream = File.Create(output))
    using (var writer = new BinaryWriter(stream))
    {
        writer.Write((ushort)0);              // reserved
        writer.Write((ushort)1);              // type: icon
        writer.Write((ushort)images.Length);  // image count

        var offset = 6 + (16 * images.Length);
        foreach (var image in images)
        {
            var payload = image.Payload;
            writer.Write((byte)(image.Size >= 256 ? 0 : image.Size)); // width (0 means 256)
            writer.Write((byte)(image.Size >= 256 ? 0 : image.Size)); // height
            writer.Write((byte)0);                                     // palette colours
            writer.Write((byte)0);                                     // reserved
            writer.Write((ushort)1);                                   // colour planes
            writer.Write((ushort)32);                                  // bits per pixel
            writer.Write(payload.Length);
            writer.Write(offset);
            offset += payload.Length;
        }

        foreach (var image in images) writer.Write(image.Payload);
    }

    // Verify every entry parses. System.Drawing.Icon refuses PNG-compressed
    // entries (a framework limitation, not a file defect), so those are checked
    // by decoding the embedded PNG directly.
    var offsetCheck = 6 + (16 * images.Length);
    foreach (var (size, payload) in images)
    {
        if (size < 128)
        {
            using var probe = new Icon(output, size, size);
            if (probe.Width != size)
            {
                Console.Error.WriteLine($"FAIL: requested {size} but got {probe.Width}x{probe.Height}");
                return 1;
            }
            using var bitmap = probe.ToBitmap();
            _ = bitmap.GetPixel(size / 2, size / 2);
        }
        else
        {
            if (payload.Length < 8 || payload[0] != 0x89 || payload[1] != 0x50)
            {
                Console.Error.WriteLine($"FAIL: entry {size} is not a PNG payload");
                return 1;
            }
            using var pngStream = new MemoryStream(payload);
            using var decoded = new Bitmap(pngStream);
            if (decoded.Width != size)
            {
                Console.Error.WriteLine($"FAIL: PNG entry {size} decodes as {decoded.Width}px");
                return 1;
            }
        }
        offsetCheck += payload.Length;
    }

    var expected = new FileInfo(output).Length;
    if (expected != offsetCheck)
    {
        Console.Error.WriteLine($"FAIL: file is {expected} bytes but entries claim {offsetCheck}");
        return 1;
    }

    Console.WriteLine($"wrote {output} ({expected} bytes, {sizes.Length} sizes, all frames verified)");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("FAIL: " + ex.Message);
    return 1;
}

static (int Size, byte[] Payload) Render(int size)
{
    using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bitmap))
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        var pad = size * 0.04f;
        var box = new RectangleF(pad, pad, size - (pad * 2), size - (pad * 2));
        var radius = size * 0.24f;

        using (var path = RoundedRect(box, radius))
        using (var brush = new LinearGradientBrush(box, Color.FromArgb(93, 124, 255), Color.FromArgb(54, 74, 190), 55f))
        {
            g.FillPath(brush, path);
        }

        // A soft inner highlight keeps small sizes from reading as a flat blob.
        using (var pen = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1f, size * 0.045f)))
        using (var path = RoundedRect(box, radius))
        {
            g.DrawPath(pen, path);
        }

        using (var font = new Font("Segoe UI", size * 0.56f, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        using (var textBrush = new SolidBrush(Color.White))
        {
            g.DrawString("D", font, textBrush, new RectangleF(0, size * 0.015f, size, size), format);
        }
    }

    if (size >= 128)
    {
        using var pngStream = new MemoryStream();
        bitmap.Save(pngStream, ImageFormat.Png);
        return (size, pngStream.ToArray());
    }

    return (size, EncodeBmp(bitmap));
}

/// <summary>Bottom-up 32bpp BGRA bitmap in the layout ICO expects for a BMP entry.</summary>
static byte[] EncodeBmp(Bitmap bitmap)
{
    var size = bitmap.Width;
    var stride = size * 4;
    var maskStride = ((size + 31) / 32) * 4;
    var pixelBytes = stride * size;
    var maskBytes = maskStride * size;

    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);

    writer.Write(40);                 // biSize
    writer.Write(size);               // biWidth
    writer.Write(size * 2);           // biHeight: XOR image plus AND mask
    writer.Write((ushort)1);          // biPlanes
    writer.Write((ushort)32);         // biBitCount
    writer.Write(0);                  // biCompression: BI_RGB
    writer.Write(pixelBytes + maskBytes); // biSizeImage
    writer.Write(0);                  // biXPelsPerMeter
    writer.Write(0);                  // biYPelsPerMeter
    writer.Write(0);                  // biClrUsed
    writer.Write(0);                  // biClrImportant

    for (var y = size - 1; y >= 0; y--)
    {
        for (var x = 0; x < size; x++)
        {
            var c = bitmap.GetPixel(x, y);
            writer.Write(c.B);
            writer.Write(c.G);
            writer.Write(c.R);
            writer.Write(c.A);
        }
    }

    writer.Write(new byte[maskBytes]); // AND mask: ignored for 32bpp, present for format completeness
    writer.Flush();
    return stream.ToArray();
}

static GraphicsPath RoundedRect(RectangleF rect, float radius)
{
    var path = new GraphicsPath();
    var d = radius * 2;
    path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
    path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
    path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
    path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
    path.CloseFigure();
    return path;
}
