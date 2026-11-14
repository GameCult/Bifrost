using Microsoft.AspNetCore.Mvc;

namespace Bifrost.Web.ViewComponents;

/// <summary>Skeleton: the layout's merge notice. Filled in by the cut's next commits.</summary>
public sealed class MergeNotice : ViewComponent
{
    public IViewComponentResult Invoke() => Content(string.Empty);
}
