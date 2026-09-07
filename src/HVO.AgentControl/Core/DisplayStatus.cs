namespace HVO.AgentControl.Core;

public static class DisplayStatus
{
    public static string Worker(WorkerRecord worker) => worker.Archived ? "Archived" : worker.Stale ? "Reconnecting" : worker.Activity switch
    {
        "Idle" => "Ready",
        "WaitingPermission" => "Approval needed",
        "WaitingQuestion" => "Answer needed",
        "Unknown" => "Checking status",
        _ => worker.Activity
    };
    public static string Outcome(string value) => value switch
    {
        "NeedsReview" => "Not yet reviewed",
        "ReportedComplete" => "Agent reported completion",
        "VerifiedComplete" => "Completion verified",
        "None" or "Unknown" or "" => "No reviewed result",
        _ => value
    };
    public static string DeliveryState(string value) => value switch
    {
        Delivery.Dispatching => "Sending",
        Delivery.Accepted => "Sent",
        Delivery.Running => "In progress",
        Delivery.Unknown => "Delivery uncertain",
        Delivery.Finished => "Finished",
        _ => value
    };
}
