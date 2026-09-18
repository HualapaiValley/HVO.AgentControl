using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;

namespace HVO.AgentControl.Components.Pages;

/// <summary>
/// Catch-all fallback for unknown portal paths. Static SSR lets the component
/// set the 404 status after routing has already selected it; API and static
/// endpoints are more specific and never reach this page.
/// </summary>
public partial class NotFoundPage
{
    [Parameter]
    public string? Path { get; set; }

    [CascadingParameter]
    private HttpContext? HttpContext { get; set; }

    protected override void OnInitialized()
    {
        if (HttpContext is not null)
        {
            // Defer to OnStarting: assigning a 4xx status before the static SSR
            // renderer writes its body makes the framework suppress that body.
            // Setting it in the first-write callback keeps the friendly HTML,
            // then commits the correct 404 header.
            HttpContext.Response.OnStarting(static state =>
            {
                ((HttpResponse)state).StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }, HttpContext.Response);
        }
    }
}
