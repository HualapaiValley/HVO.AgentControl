using Microsoft.AspNetCore.Components;

namespace HVO.AgentControl.Components.Layout;

public partial class ControlHeader
{
    [Parameter, EditorRequired] public string Title { get; set; } = "";
    [Parameter, EditorRequired] public string Description { get; set; } = "";
}
