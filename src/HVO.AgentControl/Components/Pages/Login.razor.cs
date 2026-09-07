using Microsoft.AspNetCore.Components;

namespace HVO.AgentControl.Components.Pages;

public partial class Login
{
    [SupplyParameterFromQuery] public bool Failed { get; set; }
}
