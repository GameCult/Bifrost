using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bifrost.Web.Pages.Membership;

/// <summary>Skeleton: the member's own merge page. Filled in by the cut's next commits.</summary>
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MergeStatusModel : PageModel
{
    public void OnGet()
    {
    }
}
