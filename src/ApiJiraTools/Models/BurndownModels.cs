namespace ApiJiraTools.Models;

public sealed class BurndownData
{
    public DateTime SprintStart { get; set; }
    public DateTime SprintEnd { get; set; }
    public double TotalSp { get; set; }
    public List<BurndownPoint> DataPoints { get; set; } = new();
}

public sealed class BurndownPoint
{
    public DateTime Date { get; set; }
    public double RemainingIdeal { get; set; }
    public double? RemainingActual { get; set; }
}
