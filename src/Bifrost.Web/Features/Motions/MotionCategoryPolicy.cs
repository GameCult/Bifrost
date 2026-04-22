using Bifrost.Web.Domain;

namespace Bifrost.Web.Features.Motions;

public static class MotionCategoryPolicy
{
    public static decimal GetThreshold(MotionCategory category) =>
        category switch
        {
            MotionCategory.Bugs => 0.15m,
            MotionCategory.Cosmetics => 0.30m,
            MotionCategory.BalanceChanges => 0.40m,
            MotionCategory.Features => 0.50m,
            MotionCategory.NewContent => 0.50m,
            MotionCategory.FundamentalDesignChanges => 0.66m,
            _ => 0.50m
        };

    public static string GetLabel(MotionCategory category) =>
        category switch
        {
            MotionCategory.Bugs => "Bugs",
            MotionCategory.Cosmetics => "Cosmetics",
            MotionCategory.BalanceChanges => "Balance changes",
            MotionCategory.Features => "Features",
            MotionCategory.NewContent => "New content",
            MotionCategory.FundamentalDesignChanges => "Fundamental design changes",
            _ => category.ToString()
        };
}
