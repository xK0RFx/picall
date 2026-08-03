using ImageMagick;

var source = args[0];
var jxl = args[1];
var decoded = args[2];
var format = MagickNET.SupportedFormats.FirstOrDefault(x => x.Format == MagickFormat.Jxl);
Console.WriteLine($"JXL read={format?.SupportsReading} write={format?.SupportsWriting}");
using (var image = new MagickImage(source)) image.Write(jxl, MagickFormat.Jxl);
using (var image = new MagickImage(jxl))
{
    image.Thumbnail(480, 320);
    image.Write(decoded, MagickFormat.Png);
    Console.WriteLine($"Decoded {image.Width}x{image.Height}");
}
