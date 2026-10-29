namespace Inngest.Soak.Memory;

internal static class Trend
{
    // The least-squares slope of y over x.
    public static double Slope(IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count < 2)
        {
            return 0;
        }

        var meanX = points.Average(static point => point.X);
        var meanY = points.Average(static point => point.Y);
        var numerator = points.Sum(point => (point.X - meanX) * (point.Y - meanY));
        var denominator = points.Sum(point => (point.X - meanX) * (point.X - meanX));
        return denominator == 0 ? 0 : numerator / denominator;
    }
}
