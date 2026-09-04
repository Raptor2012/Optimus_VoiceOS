namespace Optimus.Core.Phone;

/// <summary>
/// One destination as the phone sees it: enough to choose and to know why it cannot send.
/// </summary>
/// <remarks>
/// Binding a destination to an exact window stays on the PC, where the window list actually is.
/// The phone chooses among destinations and is told the readiness; it never picks a window, so
/// it cannot guess a target.
/// </remarks>
public sealed record PhoneDestination(string Id, string Name, bool Ready, string Detail);
