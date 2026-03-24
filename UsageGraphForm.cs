using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ClaudeCap;

class UsageGraphForm : Form
{
    readonly List<DailyUsage> _history;
    readonly double _totalDollars;

    static readonly Color BgColor      = Color.FromArgb(18, 18, 30);
    static readonly Color AccentBlue   = Color.FromArgb(99, 149, 255);
    static readonly Color AccentOrange = Color.FromArgb(255, 165, 0);
    static readonly Color GridColor    = Color.FromArgb(45, 255, 255, 255);

    public UsageGraphForm(List<DailyUsage> history, double totalDollars)
    {
        _history      = history;
        _totalDollars = totalDollars > 0 ? totalDollars : 250;

        Text            = "Claude Plan — Usage Trend";
        Size            = new Size(720, 440);
        MinimumSize     = new Size(520, 360);
        BackColor       = BgColor;
        ForeColor       = Color.White;
        StartPosition   = FormStartPosition.CenterScreen;
        ResizeRedraw    = true;

        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        panel.Paint += OnChartPaint;
        Controls.Add(panel);
    }

    void OnChartPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode       = SmoothingMode.AntiAlias;
        g.TextRenderingHint   = TextRenderingHint.ClearTypeGridFit;

        int w = ((Control)sender!).Width;
        int h = ((Control)sender!).Height;

        const int padL = 64, padR = 24, padT = 42, padB = 56;
        var chart = new Rectangle(padL, padT, w - padL - padR, h - padT - padB);

        // ── Title ────────────────────────────────────────────────────────────────
        using var titleFont = new Font("Segoe UI", 10, FontStyle.Bold);
        using var centerSf  = new StringFormat { Alignment = StringAlignment.Center };
        g.DrawString($"Claude Plan — {DateTime.Today:MMMM yyyy}",
            titleFont, Brushes.White, w / 2f, 13, centerSf);

        // ── No data ──────────────────────────────────────────────────────────────
        if (_history.Count == 0)
        {
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("No history yet.\nData will appear after the next fetch.",
                new Font("Segoe UI", 11), new SolidBrush(Color.DimGray),
                new RectangleF(0, 0, w, h), sf);
            return;
        }

        // ── Date range ───────────────────────────────────────────────────────────
        var today      = DateTime.Today;
        var firstDate  = DateTime.Parse(_history.Min(d => d.Timestamp)).Date;
        var endDate    = new DateTime(today.Year, today.Month,
                             DateTime.DaysInMonth(today.Year, today.Month));
        int totalDays  = Math.Max((endDate - firstDate).Days + 1, 2);
        double maxY    = _totalDollars * 1.08;

        PointF ToScreen(DateTime date, double dollars)
        {
            float x = chart.Left + (float)((date - firstDate).TotalDays / (totalDays - 1) * chart.Width);
            float y = chart.Bottom - (float)(Math.Max(0, dollars) / maxY * chart.Height);
            return new PointF(x, y);
        }

        // ── Grid lines ───────────────────────────────────────────────────────────
        using var gridPen   = new Pen(GridColor);
        using var labelFont = new Font("Segoe UI", 8);
        using var rightSf   = new StringFormat { Alignment = StringAlignment.Far };

        for (int i = 0; i <= 5; i++)
        {
            double val = _totalDollars * i / 5;
            float  y   = chart.Bottom - (float)(val / maxY * chart.Height);
            g.DrawLine(gridPen, chart.Left, y, chart.Right, y);
            g.DrawString($"${val:F0}", labelFont, new SolidBrush(Color.DimGray),
                chart.Left - 4, y - 7, rightSf);
        }

        // ── Plan limit line ───────────────────────────────────────────────────────
        float limitY = chart.Bottom - (float)(_totalDollars / maxY * chart.Height);
        using var limitPen = new Pen(Color.FromArgb(160, Color.OrangeRed), 1f) { DashStyle = DashStyle.Dash };
        g.DrawLine(limitPen, chart.Left, limitY, chart.Right, limitY);
        using var smallFont = new Font("Segoe UI", 8);
        g.DrawString("plan limit", smallFont, new SolidBrush(Color.OrangeRed),
            chart.Right - 62, limitY + 2);

        // ── Actual data points ────────────────────────────────────────────────────
        var pts = _history
            .Select(d => new { Date = DateTime.Parse(d.Timestamp), d.UsedDollars })
            .OrderBy(d => d.Date)
            .ToList();

        // Dots shown only at the last reading of each calendar day (avoids clutter)
        var dotPts = pts
            .GroupBy(p => p.Date.Date)
            .Select(g => g.Last())
            .ToList();

        // Filled area
        if (pts.Count >= 2)
        {
            var path = new GraphicsPath();
            path.AddLine(ToScreen(pts[0].Date, 0), ToScreen(pts[0].Date, pts[0].UsedDollars));
            for (int i = 1; i < pts.Count; i++)
                path.AddLine(ToScreen(pts[i - 1].Date, pts[i - 1].UsedDollars),
                             ToScreen(pts[i].Date,     pts[i].UsedDollars));
            path.AddLine(ToScreen(pts[^1].Date, pts[^1].UsedDollars),
                         ToScreen(pts[^1].Date, 0));
            path.CloseFigure();
            using var fillBrush = new SolidBrush(Color.FromArgb(45, AccentBlue));
            g.FillPath(fillBrush, path);
        }

        // Line
        if (pts.Count >= 2)
        {
            using var linePen = new Pen(AccentBlue, 2.5f);
            g.DrawLines(linePen, pts.Select(p => ToScreen(p.Date, p.UsedDollars)).ToArray());
        }

        // Dots — one per day (last reading of each day) to avoid clutter
        foreach (var p in dotPts)
        {
            var pt = ToScreen(p.Date, p.UsedDollars);
            g.FillEllipse(new SolidBrush(AccentBlue), pt.X - 3.5f, pt.Y - 3.5f, 7, 7);
            g.FillEllipse(Brushes.White, pt.X - 1.5f, pt.Y - 1.5f, 3, 3);
        }

        // ── Trend line (linear regression) ───────────────────────────────────────
        string summaryText  = "";
        Color  summaryColor = Color.LightGreen;

        if (pts.Count >= 2)
        {
            double[] xs = pts.Select(p => (p.Date - firstDate).TotalDays).ToArray();
            double[] ys = pts.Select(p => p.UsedDollars).ToArray();
            double n    = xs.Length;
            double sumX = xs.Sum(), sumY = ys.Sum();
            double sumXY = xs.Zip(ys, (x, y) => x * y).Sum();
            double sumXX = xs.Select(x => x * x).Sum();
            double denom = n * sumXX - sumX * sumX;

            if (Math.Abs(denom) > 0.001)
            {
                double slope     = (n * sumXY - sumX * sumY) / denom;
                double intercept = (sumY - slope * sumX) / n;

                double trendStartX = (pts[^1].Date - firstDate).TotalDays;
                double trendStartY = slope * trendStartX + intercept;
                double trendEndX   = (endDate - firstDate).TotalDays;
                double trendEndY   = slope * trendEndX + intercept;

                var trendPt1 = ToScreen(pts[^1].Date, trendStartY);
                var trendPt2 = ToScreen(endDate, Math.Min(trendEndY, maxY));
                using var trendPen = new Pen(Color.FromArgb(200, AccentOrange), 1.5f) { DashStyle = DashStyle.Dash };
                g.DrawLine(trendPen, trendPt1, trendPt2);

                if (slope > 0 && trendEndY >= _totalDollars)
                {
                    double daysToLimit   = (_totalDollars - intercept) / slope;
                    var    depletionDate = firstDate.AddDays(daysToLimit);
                    summaryText  = depletionDate <= today
                        ? "⚠  Plan may already be exhausted at current rate"
                        : $"⚠  Trend: plan depletes around {depletionDate:MMMM d}";
                    summaryColor = Color.Orange;
                }
                else
                {
                    summaryText  = $"On track — projected month-end: ${trendEndY:F0} of ${_totalDollars:F0}";
                    summaryColor = Color.FromArgb(100, 210, 120);
                }
            }
        }

        // ── X axis labels ─────────────────────────────────────────────────────────
        using var axisPen = new Pen(Color.FromArgb(70, 255, 255, 255));
        g.DrawLine(axisPen, chart.Left, chart.Top,    chart.Left,  chart.Bottom);
        g.DrawLine(axisPen, chart.Left, chart.Bottom, chart.Right, chart.Bottom);

        void DrawDateLabel(DateTime date, StringAlignment align)
        {
            var pt = ToScreen(date, 0);
            using var sf = new StringFormat { Alignment = align };
            g.DrawString(date.ToString("MMM d"), labelFont,
                new SolidBrush(Color.DimGray), pt.X, chart.Bottom + 5, sf);
        }
        DrawDateLabel(firstDate, StringAlignment.Near);
        var midDate = firstDate.AddDays(totalDays / 2);
        if ((midDate - firstDate).TotalDays > 3 && (endDate - midDate).TotalDays > 3)
            DrawDateLabel(midDate, StringAlignment.Center);
        DrawDateLabel(endDate, StringAlignment.Far);

        // ── Today marker ──────────────────────────────────────────────────────────
        if (today >= firstDate && today <= endDate)
        {
            var todayPt = ToScreen(today, 0);
            using var todayPen = new Pen(Color.FromArgb(60, 255, 255, 255), 1f) { DashStyle = DashStyle.Dot };
            g.DrawLine(todayPen, todayPt.X, chart.Top, todayPt.X, chart.Bottom);
        }

        // ── Summary label ─────────────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(summaryText))
        {
            using var summaryFont = new Font("Segoe UI", 9, FontStyle.Bold);
            g.DrawString(summaryText, summaryFont, new SolidBrush(summaryColor),
                w / 2f, chart.Bottom + 32, centerSf);
        }
    }
}
