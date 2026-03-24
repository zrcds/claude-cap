using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Simple side-profile baseball cap in Claude orange — clean silhouette, no gradients, no text.

var sizes   = new[] { 16, 24, 32, 48, 64, 256 };
var outPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".claude", "tools", "claude-usage-tray", "icon.ico");

var pngs = sizes.Select(s =>
{
    using var bmp = DrawCap(s, Color.FromArgb(0xE8, 0x65, 0x0A));
    using var ms  = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}).ToArray();

using var file = new FileStream(outPath, FileMode.Create, FileAccess.Write);
using var bw   = new BinaryWriter(file);
bw.Write((short)0); bw.Write((short)1); bw.Write((short)sizes.Length);

int offset = 6 + 16 * sizes.Length;
for (int i = 0; i < sizes.Length; i++)
{
    int sz = sizes[i];
    bw.Write((byte)(sz == 256 ? 0 : sz));
    bw.Write((byte)(sz == 256 ? 0 : sz));
    bw.Write((byte)0); bw.Write((byte)0);
    bw.Write((short)1); bw.Write((short)32);
    bw.Write((int)pngs[i].Length);
    bw.Write((int)offset);
    offset += pngs[i].Length;
}
foreach (var png in pngs) bw.Write(png);

Console.WriteLine($"Wrote {sizes.Length} sizes → {outPath}");

// ─────────────────────────────────────────────────────────────────────────────

static Bitmap DrawCap(int size, Color color)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode   = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.Clear(Color.Transparent);

    float s = size;

    // ── Crown ─────────────────────────────────────────────────────────────────
    // Left edge (back), right edge (front), top, bottom of crown
    float cx = s * 0.09f;
    float cw = s * 0.60f;
    float cy = s * 0.10f;
    float ch = s * 0.56f;

    using var crown = new GraphicsPath();
    // Dome: top half of ellipse (arc from 9-o'clock sweeping counterclockwise to 3-o'clock)
    crown.AddArc(cx, cy, cw, ch * 0.88f, 180f, -180f);
    // Right side straight down to crown bottom
    crown.AddLine(cx + cw, cy + ch * 0.44f, cx + cw, cy + ch);
    // Bottom straight back to left
    crown.AddLine(cx + cw, cy + ch, cx, cy + ch);
    crown.CloseFigure();

    using var brush = new SolidBrush(color);
    g.FillPath(brush, crown);

    // ── Brim ──────────────────────────────────────────────────────────────────
    // Flat shape extending from ~30% across the crown to the right edge,
    // with a rounded right tip.
    float brimLeft = cx + cw * 0.28f;
    float brimTop  = cy + ch;
    float brimW    = s * 0.88f - brimLeft;
    float brimH    = s * 0.145f;
    float tipR     = brimH * 0.55f;

    using var brim = new GraphicsPath();
    brim.AddLine(brimLeft, brimTop, brimLeft + brimW - tipR, brimTop);
    brim.AddArc(brimLeft + brimW - tipR * 2f, brimTop, tipR * 2f, brimH, -90f, 180f);
    brim.AddLine(brimLeft + brimW - tipR, brimTop + brimH, brimLeft, brimTop + brimH);
    brim.CloseFigure();
    g.FillPath(brush, brim);

    return bmp;
}
