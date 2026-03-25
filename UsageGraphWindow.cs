using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Globalization;

namespace ClaudeCap;

class UsageGraphWindow : Window
{
    public UsageGraphWindow(List<DailyUsage> history, double totalDollars)
    {
        Title                 = "Claude Plan — Usage Trend";
        Width                 = 720;
        Height                = 440;
        MinWidth              = 520;
        MinHeight             = 360;
        Background            = new SolidColorBrush(Color.FromRgb(18, 18, 30));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content               = new ChartControl(history, totalDollars);
    }
}

class ChartControl : Control
{
    readonly List<DailyUsage> _history;
    readonly double           _totalDollars;

    static readonly Color AccentBlue   = Color.FromArgb(255,  99, 149, 255);
    static readonly Color AccentOrange = Color.FromArgb(255, 255, 165,   0);

    public ChartControl(List<DailyUsage> history, double totalDollars)
    {
        _history      = history;
        _totalDollars = totalDollars > 0 ? totalDollars : 250;
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width;
        double h = Bounds.Height;

        IBrush whiteBrush  = new SolidColorBrush(Colors.White);
        IBrush dimBrush    = new SolidColorBrush(Colors.DimGray);
        IBrush blueBrush   = new SolidColorBrush(AccentBlue);

        var normalTypeface = new Typeface("Segoe UI");
        var boldTypeface   = new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold);

        // ── Title ─────────────────────────────────────────────────────────────
        var titleFt = new FormattedText(
            $"Claude Plan — {DateTime.Today:MMMM yyyy}",
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight, boldTypeface, 10, whiteBrush);
        ctx.DrawText(titleFt, new Point(w / 2 - titleFt.Width / 2, 13));

        // ── No data ───────────────────────────────────────────────────────────
        if (_history.Count == 0)
        {
            var noDataFt = new FormattedText(
                "No history yet.\nData will appear after the next fetch.",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, normalTypeface, 11, dimBrush);
            ctx.DrawText(noDataFt, new Point(w / 2 - noDataFt.Width / 2, h / 2 - noDataFt.Height / 2));
            return;
        }

        const double padL = 64, padR = 24, padT = 42, padB = 56;
        var chart = new Rect(padL, padT, w - padL - padR, h - padT - padB);

        // ── Date range ────────────────────────────────────────────────────────
        var today     = DateTime.Today;
        var firstDate = DateTime.Parse(_history.Min(d => d.Timestamp)).Date;
        var endDate   = new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
        int totalDays = Math.Max((endDate - firstDate).Days + 1, 2);
        double maxY   = _totalDollars * 1.08;

        Point ToScreen(DateTime date, double dollars)
        {
            double x = chart.Left + (date - firstDate).TotalDays / (totalDays - 1) * chart.Width;
            double y = chart.Bottom - Math.Max(0, dollars) / maxY * chart.Height;
            return new Point(x, y);
        }

        // ── Grid lines ────────────────────────────────────────────────────────
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), 1);

        for (int i = 0; i <= 5; i++)
        {
            double val = _totalDollars * i / 5;
            double y   = chart.Bottom - val / maxY * chart.Height;
            ctx.DrawLine(gridPen, new Point(chart.Left, y), new Point(chart.Right, y));

            var labelFt = new FormattedText($"${val:F0}",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, normalTypeface, 8, dimBrush);
            ctx.DrawText(labelFt, new Point(chart.Left - 4 - labelFt.Width, y - labelFt.Height / 2));
        }

        // ── Plan limit line ───────────────────────────────────────────────────
        double limitY  = chart.Bottom - _totalDollars / maxY * chart.Height;
        var limitPen   = new Pen(new SolidColorBrush(Color.FromArgb(160, 255, 69, 0)), 1,
                             new DashStyle(new double[] { 4, 2 }, 0));
        ctx.DrawLine(limitPen, new Point(chart.Left, limitY), new Point(chart.Right, limitY));

        var limitLabelFt = new FormattedText("plan limit",
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight, normalTypeface, 8,
            new SolidColorBrush(Colors.OrangeRed));
        ctx.DrawText(limitLabelFt, new Point(chart.Right - limitLabelFt.Width, limitY + 2));

        // ── Data points ───────────────────────────────────────────────────────
        var pts = _history
            .Select(d => new { Date = DateTime.Parse(d.Timestamp), d.UsedDollars })
            .OrderBy(d => d.Date)
            .ToList();

        var dotPts = pts
            .GroupBy(p => p.Date.Date)
            .Select(g => g.Last())
            .ToList();

        // Filled area
        if (pts.Count >= 2)
        {
            var geo = new StreamGeometry();
            using var geoCtx = geo.Open();
            geoCtx.BeginFigure(ToScreen(pts[0].Date, 0), true);
            geoCtx.LineTo(ToScreen(pts[0].Date, pts[0].UsedDollars));
            for (int i = 1; i < pts.Count; i++)
                geoCtx.LineTo(ToScreen(pts[i].Date, pts[i].UsedDollars));
            geoCtx.LineTo(ToScreen(pts[^1].Date, 0));
            geoCtx.EndFigure(true);
            ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(45, 99, 149, 255)), null, geo);
        }

        // Line
        if (pts.Count >= 2)
        {
            var geo = new StreamGeometry();
            using var geoCtx = geo.Open();
            geoCtx.BeginFigure(ToScreen(pts[0].Date, pts[0].UsedDollars), false);
            for (int i = 1; i < pts.Count; i++)
                geoCtx.LineTo(ToScreen(pts[i].Date, pts[i].UsedDollars));
            geoCtx.EndFigure(false);
            ctx.DrawGeometry(null, new Pen(blueBrush, 2.5), geo);
        }

        // Dots — one per calendar day
        foreach (var p in dotPts)
        {
            var pt = ToScreen(p.Date, p.UsedDollars);
            ctx.DrawEllipse(blueBrush,  null, new Rect(pt.X - 3.5, pt.Y - 3.5, 7, 7));
            ctx.DrawEllipse(whiteBrush, null, new Rect(pt.X - 1.5, pt.Y - 1.5, 3, 3));
        }

        // ── Trend line (linear regression) ────────────────────────────────────
        string summaryText  = "";
        Color  summaryColor = Color.FromRgb(100, 210, 120);

        if (pts.Count >= 2)
        {
            double[] xs  = pts.Select(p => (p.Date - firstDate).TotalDays).ToArray();
            double[] ys  = pts.Select(p => p.UsedDollars).ToArray();
            double n     = xs.Length;
            double sumX  = xs.Sum(), sumY = ys.Sum();
            double sumXY = xs.Zip(ys, (x, y) => x * y).Sum();
            double sumXX = xs.Select(x => x * x).Sum();
            double denom = n * sumXX - sumX * sumX;

            if (Math.Abs(denom) > 0.001)
            {
                double slope     = (n * sumXY - sumX * sumY) / denom;
                double intercept = (sumY - slope * sumX) / n;
                double trendEndX = (endDate - firstDate).TotalDays;
                double trendEndY = slope * trendEndX + intercept;

                var trendPen = new Pen(
                    new SolidColorBrush(Color.FromArgb(200, 255, 165, 0)), 1.5,
                    new DashStyle(new double[] { 4, 2 }, 0));
                ctx.DrawLine(trendPen,
                    ToScreen(pts[^1].Date, slope * (pts[^1].Date - firstDate).TotalDays + intercept),
                    ToScreen(endDate, Math.Min(trendEndY, maxY)));

                if (slope > 0 && trendEndY >= _totalDollars)
                {
                    double daysToLimit   = (_totalDollars - intercept) / slope;
                    var    depletionDate = firstDate.AddDays(daysToLimit);
                    summaryText  = depletionDate <= today
                        ? "⚠  Plan may already be exhausted at current rate"
                        : $"⚠  Trend: plan depletes around {depletionDate:MMMM d}";
                    summaryColor = Colors.Orange;
                }
                else
                {
                    summaryText  = $"On track — projected month-end: ${trendEndY:F0} of ${_totalDollars:F0}";
                    summaryColor = Color.FromRgb(100, 210, 120);
                }
            }
        }

        // ── X axis ────────────────────────────────────────────────────────────
        var axisPen = new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 1);
        ctx.DrawLine(axisPen, new Point(chart.Left,  chart.Top),    new Point(chart.Left,  chart.Bottom));
        ctx.DrawLine(axisPen, new Point(chart.Left,  chart.Bottom), new Point(chart.Right, chart.Bottom));

        void DrawDateLabel(DateTime date, double alignFraction)
        {
            var pt = ToScreen(date, 0);
            var ft = new FormattedText(date.ToString("MMM d"),
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, normalTypeface, 8, dimBrush);
            ctx.DrawText(ft, new Point(pt.X - ft.Width * alignFraction, chart.Bottom + 5));
        }

        DrawDateLabel(firstDate, 0);
        var midDate = firstDate.AddDays(totalDays / 2);
        if ((midDate - firstDate).TotalDays > 3 && (endDate - midDate).TotalDays > 3)
            DrawDateLabel(midDate, 0.5);
        DrawDateLabel(endDate, 1);

        // ── Today marker ──────────────────────────────────────────────────────
        if (today >= firstDate && today <= endDate)
        {
            var todayPen = new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 1,
                               new DashStyle(new double[] { 1, 2 }, 0));
            var todayPt = ToScreen(today, 0);
            ctx.DrawLine(todayPen, new Point(todayPt.X, chart.Top), new Point(todayPt.X, chart.Bottom));
        }

        // ── Summary label ─────────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(summaryText))
        {
            var summaryFt = new FormattedText(summaryText,
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, boldTypeface, 9,
                new SolidColorBrush(summaryColor));
            ctx.DrawText(summaryFt, new Point(w / 2 - summaryFt.Width / 2, chart.Bottom + 32));
        }
    }
}
