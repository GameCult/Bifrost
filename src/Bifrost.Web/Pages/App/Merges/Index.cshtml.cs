using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bifrost.Web.Pages.App.Merges;

public sealed class IndexModel : PageModel
{
    public IActionResult OnGet() => NotFound();
}
